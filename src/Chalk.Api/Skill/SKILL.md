---
name: chalk
description: Keep run-to-run state in Chalk (chalk.andrewmorgan.ai) instead of hand-rolled JSON files — seen-lists, watermarks, per-host or per-check observations, anything keyed with a shelf life. Use whenever a scheduled agent or routine needs to remember what it saw, dedup against earlier runs, or ask "what changed since my last run". Not for free-form notes or lessons; those stay in files.
---

# Chalk — short-lived records for agents

Chalk is the ledger an agent keeps between runs: what it saw, when, and what changed since. It is not memory.
Free-form notes and lessons stay in files; Chalk holds the structured, expiring part.

## Model in one paragraph

A **board** is a named set of JSON **records** under a JSON Schema. Each record is keyed by values inside it:
the board's **key pointers**, joined with `/` (one pointer → the raw value, e.g. a URL; several → a path like
`office/office-1/disk`). Every write is an **upsert**: a changed value adds a revision and a `changed` event, an
identical value only refreshes expiry (`touched`). Records **expire** after the board's TTL unless rewritten.
The server sweeps; no model is ever in the loop for tidying.

## When to write a record

Write when you observe something you will need to recognise or compare against next run:

- a story, repo, model or post you have already reported (`news-seen`, key `/url`, ttl 7d)
- a version watermark per package (`sdk-releases`, key `/package`, ttl 90d)
- a per-day, per-host metric (`fetch-blocks`, key `["/date","/host"]`, ttl 180d)
- a file you have processed (`voice-processed`, key `/file`, ttl 30d)
- a check result per node (`lab-health`, key `["/cluster","/node","/check"]`, ttl 14d)

Do not write prose, summaries of what you did, or lessons learned. If a value wants chunking, it is a document, not a record.

## The run shape

1. **Start:** `changes_since(board, lastRunStart)` tells you what was created, changed, expired or deleted since
   you last ran, with `from`/`to` values. Expired and deleted entries carry the last value so you can report
   "X was failing yesterday and is gone now".
2. **Dedup:** `get(board, keys[])` returns only the keys that still exist. Anything missing is new to you.
3. **Near-duplicates:** `search(board, text, minScore?)` returns scored candidates (hybrid when the board embeds; vector hits below `embed.minScore`, default 0.6 cosine, are dropped). Each result carries `similarity` (cosine) and/or `rank` (full-text) so you can judge;
   Chalk never refuses a write because something similar exists.
4. **Write:** one `upsert(board, records[])` per run with every observation. Batches are atomic; one schema
   violation fails the call with the item index. Include `observedAt` when the observation time is not now.
5. **Never tidy.** No `.bak`, no archive files, no pruning. TTL does it.

## Keys

- Derive nothing yourself: the server derives the key from the value using the board's pointers.
- A key is an exact string match. Give any pointer with many surface forms a **normaliser** on the board
  (`create_board … normalize: {"/url": "url"}` or `update_board`): `url` (https, lower-case host, no `www.`,
  no fragment, no `utm_*`/`ref`/`fbclid`-style params, no trailing slash), `lower`, `trim`. The server applies it
  on every write and on every key you pass to `get`, `history` and `delete`, so you can pass URLs as found.
  Without one, keep key fields canonical yourself.
- `upsert` returns `counts: { created, changed, touched }`. On a dedup board a run that expected mostly
  `touched` but got all `created` has a key mismatch; say so in the run summary.
- Prefix queries (`list`, `search`, `changes_since`, `delete`) take a key path prefix such as `office/` or `2026-10-15/`.

## Per-record expiry

`upsert` items accept `ttl` (relative to this write, e.g. `2d`) or `expiresAt` (absolute). Omit both for the board default.

## Access

- MCP: `https://chalk.andrewmorgan.ai/mcp` (Streamable HTTP). REST: `https://chalk.andrewmorgan.ai/api/v1`, OpenAPI at `/openapi/v1.json`.
- Auth: `X-Api-Key: chk_…` or `Authorization: Bearer chk_…`. Keys are minted by a signed-in person on the Chalk UI (account menu → API keys) and act as that person on their boards; the admin key lives in the DonkeyWork Vault under `chalk`. Keys may carry a board list and an expiry; a 401 means the key was revoked or expired, not that Chalk is down.
- From Bash: `curl -s -H "X-Api-Key: $CHALK_API_KEY" https://chalk.andrewmorgan.ai/api/v1/boards`.

### REST shapes

```
POST /api/v1/boards/{board}/records          { "records": [ { "value": {...}, "ttl": "2d" } ] }
POST /api/v1/boards/{board}/records/lookup   { "keys": ["…"] }
GET  /api/v1/boards/{board}/changes?since=2026-10-14T06:00:00Z&prefix=office/
DELETE /api/v1/boards/{board}?confirm={board}      (owner only; drops every record)
GET  /api/v1/boards/{board}/search?q=disk+full&prefix=office/&minScore=0.6
GET  /api/v1/boards/{board}/records?prefix=2026-10-15/&filter=/status=warn
```

Every response is `{ data, cursor, meta }`; errors are `{ error: { code, message, path, index } }`.
