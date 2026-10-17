-- Навчальні ціни в USD за 1000 токенів, а не тарифи Azure.
-- На момент додавання обидві ціни у 2,5 раза вищі за відповідні максимуми.
INSERT INTO model_prices (model, completion_tokens_price, prompt_tokens_price, "user")
SELECT
    'mock-azure-gpt-5',
    GREATEST(COALESCE(MAX(completion_tokens_price), 0), 0.01) * 2.5,
    GREATEST(COALESCE(MAX(prompt_tokens_price), 0), 0.0025) * 2.5,
    'seed'
FROM model_prices
ON CONFLICT (model) DO NOTHING;
