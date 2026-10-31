# Chalk

Short-lived records for agents. Boards with a schema, upsert by key, history kept, searchable, wiped on a timer.
`https://chalk.andrewmorgan.ai`

Chalk is not memory. It is the ledger an agent keeps between runs: what it saw, when, and what changed since.

- `src/Chalk.Api` — .NET 10. REST at `/api/v1`, MCP (Streamable HTTP) at `/mcp`, OpenAPI at `/openapi/v1.json`,
  the read+delete UI from `wwwroot/`, and the expiry sweep. Postgres (pgvector) schema in `Db/schema.sql`,
  applied idempotently at startup. The Chalk skill in `Skill/SKILL.md` is served as `skill://chalk/SKILL.md`.
- `Dockerfile` — multi-stage build; image `192.168.0.140:5555/chalk/api`.
- Deploy manifests live in the lab repo: `/mnt/lab/k3s/clusters/attic/applications/chalk/`.

## Run locally

```bash
docker network create chalk-test
docker run -d --name chalk-pg --network chalk-test -e POSTGRES_USER=chalk -e POSTGRES_PASSWORD=chalk -e POSTGRES_DB=chalk pgvector/pgvector:pg17
docker build -t chalk-api:dev .
docker run -d --name chalk-api --network chalk-test -p 18080:8080 \
  -e ConnectionStrings__Chalk='Host=chalk-pg;Username=chalk;Password=chalk;Database=chalk' \
  -e Chalk__BootstrapApiKey=chk_localdev -e Embeddings__OllamaUrl=http://192.168.69.21:11434 chalk-api:dev
curl -s -H 'X-Api-Key: chk_localdev' localhost:18080/api/v1/me
```

Configuration (env): `ConnectionStrings__Chalk`, `Chalk__BootstrapApiKey` (seeds the first admin key when none exist),
`Chalk__BootstrapUser` (default `andy`), `Chalk__PublicUrl`, `Embeddings__OllamaUrl`, `Sweep__IntervalSeconds` (default 300).

## Design

The design note lives in Obsidian (`Personal/Projects/Chalk/Notes/Chalk — design.md`). Decided and built here:
keys are paths from the board's pointer list; bulk atomic upsert with touch-on-identical; events table behind
`changes_since`; API-key auth with `chalk:read|write|delete|admin` scopes; UI is read-only plus delete.
Deferred: OAuth via Keycloak, shares, CLI, cross-board search, value editing, MCP Skills extension handshake.
