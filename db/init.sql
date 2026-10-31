-- AI Lead Generation System: schema per SRS section 8
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

CREATE TABLE users (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name          TEXT NOT NULL,
    email         TEXT NOT NULL UNIQUE,
    password_hash TEXT NOT NULL,
    role          TEXT NOT NULL DEFAULT 'owner',
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE services (
    id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id  UUID NOT NULL REFERENCES users(id),
    name     TEXT NOT NULL,
    category TEXT NOT NULL   -- WEB_DEVELOPMENT, ERP_DEVELOPMENT, MOBILE_DEVELOPMENT, ECOMMERCE, CRM_DEVELOPMENT, CUSTOM_SOFTWARE, AI_DEVELOPMENT, API_INTEGRATION, DESKTOP_DEVELOPMENT, MAINTENANCE
);

CREATE TABLE search_profiles (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id       UUID NOT NULL REFERENCES users(id),
    name          TEXT NOT NULL,
    countries     TEXT[] NOT NULL DEFAULT '{}',
    cities        TEXT[] NOT NULL DEFAULT '{}',
    languages     TEXT[] NOT NULL DEFAULT '{ar,en}',
    min_score     INT NOT NULL DEFAULT 60,
    is_active     BOOLEAN NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    schedule_mode             TEXT NOT NULL DEFAULT 'off',   -- off | interval | daily
    schedule_interval_minutes INT  NOT NULL DEFAULT 360,
    schedule_daily_time       TIME NOT NULL DEFAULT '09:00',
    schedule_time_zone        TEXT NOT NULL DEFAULT 'UTC',
    schedule_paused           BOOLEAN NOT NULL DEFAULT FALSE,
    next_run_at               TIMESTAMPTZ,
    last_run_at               TIMESTAMPTZ,
    last_run_status           TEXT,
    last_run_error            TEXT,
    retry_attempts            INT NOT NULL DEFAULT 0,
    running_since             TIMESTAMPTZ,   -- run claim: only one run per profile at a time
    run_token                 TEXT
);
CREATE INDEX idx_profiles_due ON search_profiles (next_run_at) WHERE schedule_mode <> 'off';

CREATE TABLE search_keywords (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id UUID NOT NULL REFERENCES search_profiles(id) ON DELETE CASCADE,
    keyword    TEXT NOT NULL,
    match_type TEXT NOT NULL DEFAULT 'phrase'   -- phrase | all_words | any_word
);

CREATE TABLE exclusion_rules (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id UUID NOT NULL REFERENCES search_profiles(id) ON DELETE CASCADE,
    pattern    TEXT NOT NULL,
    match_type TEXT NOT NULL DEFAULT 'phrase',   -- phrase | keyword
    is_active  BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE source_connections (
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_type        TEXT NOT NULL UNIQUE,            -- x
    status             TEXT NOT NULL DEFAULT 'disabled',-- enabled | disabled | error
    last_error         TEXT,
    rate_limited_until TIMESTAMPTZ,
    last_fetch_at      TIMESTAMPTZ,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Per profile + source: newest post already seen, so the next run does not refetch old posts.
CREATE TABLE collection_cursors (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id UUID NOT NULL REFERENCES search_profiles(id) ON DELETE CASCADE,
    source_id  UUID NOT NULL REFERENCES source_connections(id),
    cursor     TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_cursor_profile_source UNIQUE (profile_id, source_id)
);

CREATE TABLE collection_jobs (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_id   UUID NOT NULL REFERENCES source_connections(id),
    profile_id  UUID REFERENCES search_profiles(id),
    status      TEXT NOT NULL DEFAULT 'queued',  -- queued | running | done | failed
    fetched     INT NOT NULL DEFAULT 0,
    error       TEXT,
    started_at  TIMESTAMPTZ,
    finished_at TIMESTAMPTZ
);

CREATE TABLE raw_signals (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_id    UUID NOT NULL REFERENCES source_connections(id),
    external_id  TEXT NOT NULL,
    content      TEXT NOT NULL,
    url          TEXT NOT NULL,
    published_at TIMESTAMPTZ NOT NULL,
    collected_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    content_hash TEXT NOT NULL,
    CONSTRAINT uq_signal_source_external UNIQUE (source_id, external_id)
);
CREATE INDEX ix_signals_hash ON raw_signals(content_hash);

CREATE TABLE ai_analysis_results (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    signal_id      UUID NOT NULL REFERENCES raw_signals(id) ON DELETE CASCADE,
    intent         TEXT NOT NULL,
    category       TEXT,
    confidence     NUMERIC(4,3) NOT NULL DEFAULT 0,
    json_result    JSONB NOT NULL,
    model_version  TEXT NOT NULL,
    rules_version  TEXT NOT NULL,
    analyzed_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE leads (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    signal_id   UUID NOT NULL UNIQUE REFERENCES raw_signals(id) ON DELETE CASCADE,
    service_id  UUID REFERENCES services(id),
    category    TEXT,
    score       INT NOT NULL,
    priority    TEXT NOT NULL,                   -- high | medium | low
    status      TEXT NOT NULL DEFAULT 'new',     -- new reviewed contacted responded negotiation converted rejected
    needs_review  BOOLEAN NOT NULL DEFAULT FALSE,-- an excluded word appeared inside a clear purchase request
    review_reason TEXT,
    country     TEXT,
    city        TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ix_leads_status_score ON leads(status, score DESC);

CREATE TABLE lead_score_factors (
    id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    lead_id  UUID NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
    factor   TEXT NOT NULL,
    score    INT NOT NULL,
    weight   INT NOT NULL
);

CREATE TABLE lead_activities (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    lead_id       UUID NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
    activity_type TEXT NOT NULL,
    notes         TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE notifications (
    id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    lead_id UUID NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
    read_at TIMESTAMPTZ
);

CREATE TABLE audit_logs (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id    UUID REFERENCES users(id),
    action     TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO source_connections (source_type, status) VALUES ('x', 'disabled');
