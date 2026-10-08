# App service (.NET) — skeleton

Дається .NET-skeleton із HTTP-ендпоінтом `/chat` і підключенням до gateway та Postgres.
Це control plane — тут студент реалізує:

- **routing** — яку модель обрати під задачу (faq / escalation);
- **fallback** — порядок провайдерів при 429 / збої;
- **cost** — облік tokens і вартості на запит, budget-alert;
- **tool-хендлери** — lookup_order, create_ticket, з human approval перед незворотною дією;
- **guardrails** — PII, prompt injection, policy logs;
- **обзервабіліті** — розділ поверх логів у Postgres.

LiteLLM викликається лише для самого запиту до обраної моделі.

Skeleton готовий: `Program.cs` (`/chat` + API-контракт `/observability` `/cost` `/prompts` `/health` `/approvals`), `Service.csproj`, `Dockerfile`. Місця для дороблення позначені `TODO(student)`.

## Системні JSON-логи

HTTP middleware і логування операцій PostgreSQL розташовані в [`middleware/`](middleware/README.md).
Налаштування рівнів, полів і виключених HTTP-шляхів — у
[`middleware/logging_config.json`](middleware/logging_config.json).
Одна подія — один плоский JSON-рядок у stdout; HTTP і БД пов'язані через `request_id`.

## Політика бюджету

[`BudgetPolicy/`](BudgetPolicy/README.md) перевіряє сьогоднішні витрати перед
відправленням `/chat` у gateway. При досягненні 80% загального бюджету обирається
дешевша модель з `model_prices` за сумою цін вхідних і вихідних токенів;
перехід через поріг дає один Warning у JSON-логах.
