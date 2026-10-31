-- Chalk schema. Idempotent; run at every startup.
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS users (
  id          text PRIMARY KEY,
  display     text NOT NULL DEFAULT '',
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS api_keys (
  id           uuid PRIMARY KEY,
  user_id      text NOT NULL REFERENCES users(id),
  name         text NOT NULL,
  prefix       text NOT NULL,
  hash         bytea NOT NULL UNIQUE,
  scopes       jsonb NOT NULL,
  enabled      bool NOT NULL DEFAULT true,
  created_at   timestamptz NOT NULL DEFAULT now(),
  last_used_at timestamptz,
  UNIQUE (user_id, name)
);
ALTER TABLE api_keys ADD COLUMN IF NOT EXISTS expires_at timestamptz;
-- A name is unique among live keys only, so a revoked key's name can be reused.
ALTER TABLE api_keys DROP CONSTRAINT IF EXISTS api_keys_user_id_name_key;
CREATE UNIQUE INDEX IF NOT EXISTS api_keys_user_name_live ON api_keys (user_id, name) WHERE enabled;
ALTER TABLE users ADD COLUMN IF NOT EXISTS email text;
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_seen_at timestamptz;

CREATE TABLE IF NOT EXISTS boards (
  id              uuid PRIMARY KEY,
  owner_id        text NOT NULL REFERENCES users(id),
  name            text NOT NULL,
  description     text NOT NULL DEFAULT '',
  schema          jsonb NOT NULL,
  key_pointers    jsonb NOT NULL,
  ttl             interval,
  max_revisions   int NOT NULL DEFAULT 50,
  search          jsonb NOT NULL DEFAULT '{}'::jsonb,
  embed           jsonb,
  max_value_bytes int NOT NULL DEFAULT 65536,
  size_cap        bigint NOT NULL DEFAULT 104857600,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  UNIQUE (owner_id, name)
);

CREATE TABLE IF NOT EXISTS records (
  board_id    uuid NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
  key         text NOT NULL,
  revision    int NOT NULL,
  value       jsonb NOT NULL,
  observed_at timestamptz NOT NULL,
  written_at  timestamptz NOT NULL DEFAULT now(),
  expires_at  timestamptz,
  writer      text NOT NULL,
  tombstone   bool NOT NULL DEFAULT false,
  current     bool NOT NULL DEFAULT true,
  search_a    text NOT NULL DEFAULT '',
  search      tsvector GENERATED ALWAYS AS (
                setweight(to_tsvector('english', search_a), 'A') ||
                setweight(to_tsvector('simple', search_a), 'B') ||
                setweight(jsonb_to_tsvector('english', value, '["string"]'), 'D')) STORED,
  PRIMARY KEY (board_id, key, revision)
);
CREATE UNIQUE INDEX IF NOT EXISTS records_current_uq ON records (board_id, key) WHERE current;
CREATE INDEX IF NOT EXISTS records_live_prefix ON records (board_id, key text_pattern_ops) WHERE current AND NOT tombstone;
CREATE INDEX IF NOT EXISTS records_search_gin ON records USING gin (search) WHERE current AND NOT tombstone;
CREATE INDEX IF NOT EXISTS records_key_trgm ON records USING gin (key gin_trgm_ops) WHERE current AND NOT tombstone;
CREATE INDEX IF NOT EXISTS records_expires ON records (expires_at) WHERE expires_at IS NOT NULL;
CREATE INDEX IF NOT EXISTS records_observed_brin ON records USING brin (observed_at);

CREATE TABLE IF NOT EXISTS embeddings (
  board_id  uuid NOT NULL,
  key       text NOT NULL,
  revision  int NOT NULL,
  model     text NOT NULL,
  dims      int NOT NULL,
  truncated bool NOT NULL DEFAULT false,
  vector    vector(512) NOT NULL,
  PRIMARY KEY (board_id, key, revision),
  FOREIGN KEY (board_id, key, revision) REFERENCES records(board_id, key, revision) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS events (
  id            bigserial PRIMARY KEY,
  board_id      uuid NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
  key           text NOT NULL,
  kind          text NOT NULL,
  at            timestamptz NOT NULL DEFAULT now(),
  from_revision int,
  to_revision   int,
  from_value    jsonb,
  to_value      jsonb,
  writer        text NOT NULL,
  expires_at    timestamptz
);
CREATE INDEX IF NOT EXISTS events_board_at ON events (board_id, at DESC);
CREATE INDEX IF NOT EXISTS events_expires ON events (expires_at);

CREATE TABLE IF NOT EXISTS shares (
  board_id uuid NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
  user_id  text NOT NULL REFERENCES users(id),
  mode     text NOT NULL,
  PRIMARY KEY (board_id, user_id)
);

CREATE TABLE IF NOT EXISTS audit (
  id        bigserial PRIMARY KEY,
  at        timestamptz NOT NULL DEFAULT now(),
  principal text NOT NULL,
  action    text NOT NULL,
  detail    jsonb
);
