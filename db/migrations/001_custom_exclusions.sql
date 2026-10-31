-- Stage 1: custom exclusion rules and review flag. Only needed for databases created before this change;
-- fresh installs get all of this from db/init.sql.
BEGIN;

ALTER TABLE exclusion_rules RENAME COLUMN rule TO pattern;
ALTER TABLE exclusion_rules
    ADD COLUMN IF NOT EXISTS match_type TEXT NOT NULL DEFAULT 'phrase',
    ADD COLUMN IF NOT EXISTS is_active  BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();

ALTER TABLE leads
    ADD COLUMN IF NOT EXISTS needs_review  BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS review_reason TEXT;

ALTER TABLE source_connections
    ADD COLUMN IF NOT EXISTS rate_limited_until TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS last_fetch_at      TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS collection_cursors (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id UUID NOT NULL REFERENCES search_profiles(id) ON DELETE CASCADE,
    source_id  UUID NOT NULL REFERENCES source_connections(id),
    cursor     TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_cursor_profile_source UNIQUE (profile_id, source_id)
);

COMMIT;
