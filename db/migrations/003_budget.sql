-- Бюджет моделі в USD та історія його змін.
BEGIN;

CREATE TABLE IF NOT EXISTS budget (
    model    TEXT PRIMARY KEY CHECK (btrim(model) <> ''),
    budget   NUMERIC(18, 6) NOT NULL CHECK (budget >= 0),
    updated  TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    "user"   TEXT NOT NULL CHECK (btrim("user") <> '')
);

CREATE TABLE IF NOT EXISTS budget_history (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    model       TEXT NOT NULL,
    action      TEXT NOT NULL CHECK (action IN ('insert', 'update')),
    old_budget  NUMERIC(18, 6),
    new_budget  NUMERIC(18, 6) NOT NULL,
    updated     TIMESTAMPTZ NOT NULL,
    "user"      TEXT NOT NULL CHECK (btrim("user") <> '')
);

CREATE INDEX IF NOT EXISTS idx_budget_history_model_updated
    ON budget_history (model, updated DESC, id DESC);

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
        INSERT INTO budget_history (model, action, new_budget, updated, "user")
        VALUES (NEW.model, 'insert', NEW.budget, NEW.updated, NEW."user");
    ELSE
        INSERT INTO budget_history (model, action, old_budget, new_budget, updated, "user")
        VALUES (NEW.model, 'update', OLD.budget, NEW.budget, NEW.updated, NEW."user");
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
