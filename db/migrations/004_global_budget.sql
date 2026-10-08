-- Перехід від бюджетів моделей до одного бюджету сервісу.
-- Старі значення зберігаються в архіві. Нову суму задайте через PUT /budget.
BEGIN;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'budget' AND column_name = 'model'
    ) THEN
        LOCK TABLE budget, budget_history IN ACCESS EXCLUSIVE MODE;
        DROP TRIGGER IF EXISTS budget_set_updated ON budget;
        DROP TRIGGER IF EXISTS budget_audit ON budget;
        ALTER TABLE budget RENAME TO budget_model_legacy;
        ALTER TABLE budget_model_legacy
            RENAME CONSTRAINT budget_pkey TO budget_model_legacy_pkey;
        ALTER TABLE budget_history RENAME TO budget_history_model_legacy;
        ALTER TABLE budget_history_model_legacy
            RENAME CONSTRAINT budget_history_pkey TO budget_history_model_legacy_pkey;
    END IF;
END;
$$;

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
