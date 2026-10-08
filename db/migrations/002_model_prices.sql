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
