# Ціни моделей і загальний бюджет сервісу

`model_prices` зберігає поточні ціни в USD за **1000 токенів**:
`model`, `completion_tokens_price`, `prompt_tokens_price`, `updated`, `"user"`.
Назва моделі унікальна, ціни не можуть бути від'ємними. Поле `"user"` —
ім'я ініціатора, яке потрібно передавати під час кожного запису; це не підтверджена
особа користувача. Подвійні лапки обов'язкові через спеціальне слово `user` у SQL.

`model_price_history` зберігає події `insert` і `update`, попередні та нові ціни,
час та ініціатора. Під час додавання моделі попередні ціни — `NULL`.
Postgres сам оновлює `updated` і додає історію через тригери.
Будь-який `UPDATE`, зокрема повторний запис тих самих цін, створює подію.
Зміна поточного запису та запис історії виконуються в одній транзакції:
якщо одна операція не вдалася, обидві відкочуються.

## HTTP API: створення та оновлення цін

`PUT /model-prices/{model}` створює ціни моделі або замінює обидві наявні ціни.
Одиниця — **USD за 1000 токенів**, як у міграції 002.
Поля запиту й відповіді: `completion_tokens_price`, `prompt_tokens_price`, `user`.
Відповідь HTTP 200 також містить `model` і встановлений базою `updated`.

```bash
# Навчальні значення, а не тарифи провайдера.
curl -X PUT http://localhost:8080/model-prices/mock-mini \
  -H 'Content-Type: application/json' \
  -d '{"completion_tokens_price":0.002,"prompt_tokens_price":0.001,"user":"student"}'

# Оновлення тієї самої моделі.
curl -X PUT http://localhost:8080/model-prices/mock-mini \
  -H 'Content-Type: application/json' \
  -d '{"completion_tokens_price":0.003,"prompt_tokens_price":0.0015,"user":"student"}'
```

Обидві ціни обов'язкові: від нуля до `9999999999.99999999`, щонайбільше вісім знаків
після крапки. Порожні `model`/`user`, відсутність ціни, від'ємні значення,
переповнення та зайві значущі дробові знаки дають HTTP 400 без запису в БД.
Пробіли на початку й у кінці `model`/`user` видаляються. Недоступна БД дає HTTP 503 з `request_id`.
Кожен успішний виклик створює запис історії через тригери міграції 002,
зокрема повторний запис тих самих цін. Перед використанням з наявною
базою застосуйте міграцію 002 (команда нижче).

Після успішного збереження сервіс пише `LogInformation`: `model_price.created`
під час створення або `model_price.updated` під час оновлення. У плоскому JSON є `model`,
обидві ціни, `user`, `operation` і `request_id`; `message` містить лише короткий опис.
Помилки запису та HTTP 400 не створюють подію успішного збереження.

`POST /chat` читає поточну ціну вибраної маршрутизатором моделі перед викликом
gateway. Вартість зберігається в `requests.cost_usd`:

```csharp
Math.Round(promptTokens / 1000m * pr.PromptTokensPrice
    + completionTokens / 1000m * pr.CompletionTokensPrice, 6)
```

Розрахунок використовує `decimal`, USD і округлення до шести знаків
(`Math.Round` за замовчуванням округлює середину до парного).
Якщо цін моделі або повного `usage` немає, вартість залишається `NULL`.
Нульові ціни дають нульову вартість. Оновлені ціни використовуються в наступному
запиті; вартість раніше збережених запитів не перераховується.

## Застосування до наявної бази

З кореня проєкту:

```bash
docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U llmops -d llmops < db/migrations/002_model_prices.sql
```

Під час створення нової бази таблиці й тригери встановлюються з `schema.sql`.
Застосування міграції не створює записи про ціни, які існували до встановлення тригерів.

## Приклади SQL

Цифри нижче — навчальні значення, а не тарифи реального провайдера.

```sql
-- Додавання моделі: у журналі з'явиться insert.
INSERT INTO model_prices (model, completion_tokens_price, prompt_tokens_price, "user")
VALUES ('mock-mini', 0.002, 0.001, 'student');

-- Зміна ціни: у журналі з'явиться update з попередніми та новими значеннями.
-- Завжди передавайте ініціатора поточної зміни.
UPDATE model_prices
SET completion_tokens_price = 0.003,
    prompt_tokens_price = 0.0015,
    "user" = 'student'
WHERE model = 'mock-mini';

SELECT * FROM model_prices;
SELECT * FROM model_price_history
WHERE model = 'mock-mini'
ORDER BY updated DESC, id DESC;
```

## Загальний бюджет сервісу

### HTTP API: створення та оновлення

`PUT /budget` створює загальний бюджет сервісу або замінює наявну суму.
В обох випадках повертається HTTP 200 зі збереженими `budget`, `updated`, `user`.
Модель не передається й не повертається. `updated` встановлює база;
історію зберігають тригери. Перший виклик задає нову суму загального бюджету.

```bash
curl -X PUT http://localhost:8080/budget \
  -H 'Content-Type: application/json' \
  -d '{"budget":10.00,"user":"student"}'

# Повторний виклик з новою сумою оновить той самий запис.
curl -X PUT http://localhost:8080/budget \
  -H 'Content-Type: application/json' \
  -d '{"budget":15.00,"user":"student"}'
```

Сума обов'язкова: допускається нуль, максимум `999999999999.999999`, до шести
знаків після крапки. Порожній `user`, відсутність суми та неприпустимі
значення дають HTTP 400 без запису в БД. Пробіли на початку й у кінці `user` видаляються.
Недоступна БД дає HTTP 503 з `request_id` через наявний middleware.
`user` передається клієнтом і не підтверджує особу. Кожен повторний PUT
створює запис історії, навіть якщо сума не змінилася, як передбачено міграцією.
Перед використанням з наявною базою застосуйте міграцію 004 (команда нижче).

Після успішного збереження сервіс пише `LogInformation`: `budget.created`
або `budget.updated`, з полями `budget`, `user`, `operation` і `request_id`.
Подія з'являється після фіксації транзакції; помилки запису не логуються як успіх.

### HTTP API: витрати та бюджет

`GET /cost` повертає `today_usd` і `budget_usd`. Витрати обчислюються через
`SELECT coalesce(sum(cost_usd), 0) FROM requests WHERE created_at::date = CURRENT_DATE`
і округлюються до чотирьох знаків через `Math.Round(today, 4)`.
Поточна дата визначається часовим поясом сесії PostgreSQL.
Порожня історія або лише невідомі (`NULL`) вартості дають `today_usd: 0`.
`budget_usd` — поточна сума єдиного бюджету, без округлення; якщо бюджет ще
не задано, повертається `null`. Недоступна БД дає HTTP 503 з `request_id`.

```bash
curl http://localhost:8080/cost
```

### Зберігання та SQL

`budget` зберігає `id`, `budget` (сума в USD), `updated`, `"user"`.
Технічний ключ `id = 1` та обмеження `CHECK (id = 1)` допускають лише один
запис для всього сервісу. Ключ не входить до HTTP-відповіді чи логів.
Бюджет не може бути від'ємним. Період бюджету поки не задано.
`budget_history` автоматично зберігає додавання та оновлення:
дію, попередній і новий бюджет, час зміни та ініціатора.
Під час додавання `old_budget` дорівнює `NULL`. Будь-який `UPDATE` створює подію,
зокрема повторний запис тієї самої суми. Зміна бюджету та запис історії
виконуються в одній транзакції; час встановлює Postgres.
Під час кожного запису передавайте ім'я ініціатора в `"user"`.
Це вказане клієнтом ім'я, а не підтверджена особа.

Для наявної бази проєкту `llmops-bootcamp`:

```bash
docker compose -p llmops-bootcamp exec -T postgres psql -v ON_ERROR_STOP=1 -U llmops -d llmops < db/migrations/004_global_budget.sql
docker compose -p llmops-bootcamp up -d --build service
```

Міграція 004 зберігає старі бюджети моделей у `budget_model_legacy`, а їхню
історію — у `budget_history_model_legacy`. Загальний бюджет залишається порожнім до
першого `PUT /budget`: старі суми не підсумовуються й не переносяться автоматично.
Архівні таблиці доступні для читання; їхні старі тригери запису вимкнені.
Повторне застосування 004 не скидає вже встановлений загальний бюджет.
Для нової бази достатньо `schema.sql`. Міграція 003 залишається попередньою версією
схеми й застосовується лише перед 004 під час послідовного встановлення міграцій.
Старий маршрут `/budgets/{model}` видалено.

Приклади SQL:

```sql
-- Новий загальний бюджет.
INSERT INTO budget (budget, "user") VALUES (10.00, 'student');

-- Зміна наявного бюджету.
UPDATE budget SET budget = 15.00, "user" = 'student' WHERE id = 1;

-- Одна команда для додавання нового запису або зміни наявного.
INSERT INTO budget (id, budget, "user") VALUES (1, 50.00, 'student')
ON CONFLICT (id) DO UPDATE
SET budget = EXCLUDED.budget, "user" = EXCLUDED."user";

SELECT * FROM budget;
SELECT * FROM budget_history
ORDER BY updated DESC, id DESC;
```
