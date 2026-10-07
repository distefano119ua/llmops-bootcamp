-- Базова схема. Студент розширює під власні потреби.

CREATE TABLE IF NOT EXISTS requests (
    request_id      UUID PRIMARY KEY,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    model           TEXT NOT NULL,
    provider        TEXT,
    prompt_version  TEXT,
    latency_ms      INTEGER,
    prompt_tokens   INTEGER,
    completion_tokens INTEGER,
    cost_usd        NUMERIC(10, 6),
    status          TEXT,
    finish_reason   TEXT
);

CREATE TABLE IF NOT EXISTS prompts (
    name        TEXT NOT NULL,
    version     TEXT NOT NULL,
    body        TEXT NOT NULL,
    active      BOOLEAN NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (name, version)
);

INSERT INTO prompts (name, version, body, active) VALUES
  ('support', 'v1', 'You are an assistant.', false),
  ('support', 'v2', 'You are a support assistant. Be concise and helpful.', true);

CREATE TABLE IF NOT EXISTS prompt_activations (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    prompt_name   TEXT NOT NULL,
    version       TEXT NOT NULL,
    actor         TEXT NOT NULL,
    activated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    FOREIGN KEY (prompt_name, version) REFERENCES prompts (name, version)
);

INSERT INTO prompt_activations (prompt_name, version, actor)
VALUES ('support', 'v2', 'system-seed');

CREATE INDEX IF NOT EXISTS idx_requests_created_at ON requests (created_at);
CREATE INDEX IF NOT EXISTS idx_requests_model ON requests (model);

-- Ціни в USD за 1000 токенів. "user" передає той, хто змінює запис.
BEGIN;

CREATE TABLE IF NOT EXISTS model_prices (
    model                    TEXT PRIMARY KEY CHECK (btrim(model) <> ''),
    completion_tokens_price  NUMERIC(18, 8) NOT NULL CHECK (completion_tokens_price >= 0),
    prompt_tokens_price      NUMERIC(18, 8) NOT NULL CHECK (prompt_tokens_price >= 0),
    updated                  TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    "user"                   TEXT NOT NULL CHECK (btrim("user") <> '')
);

-- Без FK до model_prices: історія зберігається навіть після видалення моделі.
CREATE TABLE IF NOT EXISTS model_price_history (
    id                           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    model                        TEXT NOT NULL,
    action                       TEXT NOT NULL CHECK (action IN ('insert', 'update')),
    old_completion_tokens_price  NUMERIC(18, 8),
    old_prompt_tokens_price      NUMERIC(18, 8),
    new_completion_tokens_price  NUMERIC(18, 8) NOT NULL,
    new_prompt_tokens_price      NUMERIC(18, 8) NOT NULL,
    updated                      TIMESTAMPTZ NOT NULL,
    "user"                       TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_model_price_history_model_updated
    ON model_price_history (model, updated DESC, id DESC);

CREATE OR REPLACE FUNCTION set_model_price_updated()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated := clock_timestamp();
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION audit_model_prices()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO model_price_history (
            model, action, new_completion_tokens_price, new_prompt_tokens_price, updated, "user"
        ) VALUES (
            NEW.model, 'insert', NEW.completion_tokens_price, NEW.prompt_tokens_price,
            NEW.updated, NEW."user"
        );
    ELSE
        INSERT INTO model_price_history (
            model, action, old_completion_tokens_price, old_prompt_tokens_price,
            new_completion_tokens_price, new_prompt_tokens_price, updated, "user"
        ) VALUES (
            NEW.model, 'update', OLD.completion_tokens_price, OLD.prompt_tokens_price,
            NEW.completion_tokens_price, NEW.prompt_tokens_price, NEW.updated, NEW."user"
        );
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS model_prices_set_updated ON model_prices;
CREATE TRIGGER model_prices_set_updated
    BEFORE INSERT OR UPDATE ON model_prices
    FOR EACH ROW EXECUTE FUNCTION set_model_price_updated();

DROP TRIGGER IF EXISTS model_prices_audit ON model_prices;
CREATE TRIGGER model_prices_audit
    AFTER INSERT OR UPDATE ON model_prices
    FOR EACH ROW EXECUTE FUNCTION audit_model_prices();

COMMIT;

-- Загальний бюджет сервісу в USD та історія його змін.
BEGIN;

CREATE TABLE IF NOT EXISTS budget (
    id       SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    budget   NUMERIC(18, 6) NOT NULL CHECK (budget >= 0),
    updated  TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    "user"   TEXT NOT NULL CHECK (btrim("user") <> '')
);

CREATE TABLE IF NOT EXISTS budget_history (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    action      TEXT NOT NULL CHECK (action IN ('insert', 'update')),
    old_budget  NUMERIC(18, 6),
    new_budget  NUMERIC(18, 6) NOT NULL,
    updated     TIMESTAMPTZ NOT NULL,
    "user"      TEXT NOT NULL CHECK (btrim("user") <> '')
);

CREATE INDEX IF NOT EXISTS idx_budget_history_updated
    ON budget_history (updated DESC, id DESC);

CREATE OR REPLACE FUNCTION set_budget_updated()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated := clock_timestamp();
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION audit_budget()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO budget_history (action, new_budget, updated, "user")
        VALUES ('insert', NEW.budget, NEW.updated, NEW."user");
    ELSE
        INSERT INTO budget_history (action, old_budget, new_budget, updated, "user")
        VALUES ('update', OLD.budget, NEW.budget, NEW.updated, NEW."user");
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS budget_set_updated ON budget;
CREATE TRIGGER budget_set_updated
    BEFORE INSERT OR UPDATE ON budget
    FOR EACH ROW EXECUTE FUNCTION set_budget_updated();

DROP TRIGGER IF EXISTS budget_audit ON budget;
CREATE TRIGGER budget_audit
    AFTER INSERT OR UPDATE ON budget
    FOR EACH ROW EXECUTE FUNCTION audit_budget();

COMMIT;

INSERT INTO model_prices (model, prompt_tokens_price, completion_tokens_price, "user") VALUES
        ('mock-mini',   0.00015, 0.0006, 'seed'),
        ('mock-strong', 0.0025,  0.01,   'seed')
    ON CONFLICT (model) DO NOTHING;
    INSERT INTO budget (id, budget, "user") VALUES (1, 5.00, 'seed')
    ON CONFLICT (id) DO NOTHING;