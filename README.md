# Chalk

Short-lived records for agents. Boards with a schema, upsert by key, history kept, searchable, wiped on a timer.
`https://chalk.andrewmorgan.ai`

Chalk is not memory. It is the ledger an agent keeps between runs: what it saw, when, and what changed since.

## What it does

- **Boards** are named sets of JSON records under a JSON Schema. Each record is keyed by values inside it
  (the board's key pointers, joined with `/`), revisioned on every change, and expires after the board's TTL
  unless rewritten. Per-record `ttl`/`expiresAt` override the default.
- **Upsert** is bulk and atomic. A changed value adds a revision and a `changed` event; an identical value only
  refreshes expiry (`touched`). The response carries `counts: {created, changed, touched}` so a caller can see
  whether its dedup is working.
- **Key normalisers** (`url`, `lower`, `trim`) can be set per key pointer. The server applies them on every write
  and on every key a caller passes to get, history or delete, so `https://WWW.x.com/a/?utm_source=y` and
  `x.com/a` are one record. Setting one on an existing board re-keys its records.
- **Dedup** is `get(keys[])`: absent keys are simply missing from the result.
- **changes_since** replays created / changed / expired / deleted events from a timestamp.
- **Search** is hybrid: Postgres full-text plus pgvector cosine similarity (Ollama embeddings), fused with
  reciprocal rank fusion. Results carry the fused `score` (0–1) and, where available, the raw `similarity` and
  text `rank`; `minScore` floors the vector side (default 0.6).
- **Expiry sweep** tombstones expired records on a timer and emits `expired` events.
- **UI**: browse boards as a key tree, inspect revisions and diffs, see what changed or is expiring, delete, and
  mint API keys.

### MCP tools (`/mcp`, Streamable HTTP)

| tool | purpose |
|---|---|
| `list_boards`, `describe_board` | what exists; schema, key pointers, normalisers, TTL, counts |
| `create_board`, `update_board` | create (idempotent on name); owner edits description, TTL, normalisers |
| `upsert` | write one or many records, atomic |
| `get` | latest live revision for each key; dedup in one call |
| `list` | records under a key prefix, with `/pointer=value` filters and `since` |
| `history` | revisions of a key with diffs |
| `changes_since` | event replay |
| `search` | hybrid text + vector search, scored |
| `delete` | tombstone a key or prefix |

The REST surface mirrors this under `/api/v1` (`GET|POST|PATCH|DELETE /boards[/{board}]`, `/records`,
`/records/lookup`, `/records/{key}[/history]`, `/changes`, `/search`, `/tree`, `/keys`); OpenAPI at `/openapi/v1.json`.
The skill that teaches a model when and how to use all of this is `src/Chalk.Api/Skill/SKILL.md`, served as
`skill://chalk/SKILL.md`.

- `src/Chalk.Api` — .NET 10. REST at `/api/v1`, MCP (Streamable HTTP) at `/mcp`, OpenAPI at `/openapi/v1.json`,
  the read+delete UI from `wwwroot/`, and the expiry sweep. Postgres (pgvector) schema in `Db/schema.sql`,
  applied idempotently at startup. The Chalk skill in `Skill/SKILL.md` is served as `skill://chalk/SKILL.md`.
- `Dockerfile` — multi-stage build. `--build-arg CHALK_VERSION=<sha>` becomes the cache-buster on UI assets.
- Deploy manifests live in a separate GitOps repo (k3s, Postgres with pgvector, Ollama for embeddings).

## Run locally

```bash
docker network create chalk-test
docker run -d --name chalk-pg --network chalk-test -e POSTGRES_USER=chalk -e POSTGRES_PASSWORD=chalk -e POSTGRES_DB=chalk pgvector/pgvector:pg17
docker build -t chalk-api:dev .
docker run -d --name chalk-api --network chalk-test -p 18080:8080 \
  -e ConnectionStrings__Chalk='Host=chalk-pg;Username=chalk;Password=chalk;Database=chalk' \
  -e Chalk__BootstrapApiKey=chk_localdev -e Embeddings__OllamaUrl=http://<ollama-host>:11434 chalk-api:dev
curl -s -H 'X-Api-Key: chk_localdev' localhost:18080/api/v1/me
```

Configuration (env): `ConnectionStrings__Chalk`, `Chalk__BootstrapApiKey` (seeds the first admin key when none exist),
`Chalk__BootstrapUser` (the owner's Keycloak subject), `Chalk__PublicUrl`, `Embeddings__OllamaUrl`, `Sweep__IntervalSeconds` (default 300).

## Auth

Two credentials, one `Smart` scheme that picks by shape:

- **People sign in with Keycloak** (authorization code + PKCE; `OAuth__Enabled=true`, `OAuth__Authority`, `OAuth__Audience`, `OAuth__ClientId`,
  optional `OAuth__InternalAuthority`, `OAuth__Providers`). The UI offers Google / GitHub / Microsoft / Apple via `kc_idp_hint`; the server
  keeps the PKCE verifier in an HttpOnly cookie and hands tokens to the SPA in a URL fragment, which refreshes them in place.
  A signed-in user is `user:<username>` with every scope on their own boards.
- **Agents use API keys** (`X-Api-Key: chk_…` or `Authorization: Bearer chk_…`) minted by a signed-in user on the UI's *API keys* page
  (`POST /api/v1/keys`: name, `read|write|delete|admin`, optional `boards`, optional `expiresIn`/`expiresAt`). Keys act as that user,
  are shown once and stored hashed. A key with `chalk:admin` may also mint keys (the bootstrap path).
- **MCP clients** get RFC 9728 discovery at `/.well-known/oauth-protected-resource` and a `WWW-Authenticate: Bearer resource_metadata=…`
  challenge, so an OAuth-capable client can DCR against Keycloak; a plain Keycloak access token with `aud=chalk` is accepted on `/mcp` too.

## Design

The design note lives in Obsidian (`Personal/Projects/Chalk/Notes/Chalk — design.md`). Decided and built here:
keys are paths from the board's pointer list; bulk atomic upsert with touch-on-identical; events table behind
`changes_since`; Keycloak sign-in for people, user-minted API keys with `chalk:read|write|delete|admin` scopes for agents; UI is read-only plus delete plus key management;
server-side key normalisers after a trailing-slash dedup miss in production.
Deferred: shares, CLI, cross-board search, value editing, MCP Skills extension handshake.
