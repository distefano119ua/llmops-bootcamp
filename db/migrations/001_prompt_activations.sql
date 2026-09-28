-- Для існуючої бази: створити audit trail без видалення даних.
CREATE TABLE IF NOT EXISTS prompt_activations (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    prompt_name   TEXT NOT NULL,
    version       TEXT NOT NULL,
    actor         TEXT NOT NULL,
    activated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    FOREIGN KEY (prompt_name, version) REFERENCES prompts (name, version)
);
