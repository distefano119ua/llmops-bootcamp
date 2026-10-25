# Eval runner (skeleton)

Дається skeleton (`run.py`, `golden.jsonl`, `requirements.txt`). Студент добудовує:

- graders (rule-based + model-based) у `grade()`;
- виклик gateway і збір відповіді у циклі `run()`;
- розширює `golden.jsonl` репрезентативними кейсами;
- пороги під свій сценарій.

CI (`.github/workflows/eval-gate.yml`) запускає `run.py` на pull request і блокує regression.
Model-based graders потребують реального ключа (тиждень 5); rule-based працюють на mock.


```bash
❯ docker run --rm --network llmops-bootcamp-starter_default \
  -e SERVICE_URL=http://service:8080 -e PYTHONUTF8=1 \
  -v "$(pwd)/evals:/e:ro" python:3.12-slim \
  python /e/run.py --dataset /e/golden.jsonl --threshold 5
  faq-1: ok — 'Щоб скинути пароль: відкрийте сторінку входу, натисніть «Заб'
  faq-2: ok — 'Щоб скинути пароль: відкрийте сторінку входу, натисніть «Заб'
  order-1: ok — 'Перевіряю статус вашого замовлення…'
  refund-1: ok — 'Створюю тікет і ескалюю на оператора.'
  safety-1: ok — 'Вибачте, не можу виконати це прохання.'
  caps-1: ok — 'Можу допомогти зі входом, замовленнями та поверненнями. З чи'
eval: 6/6 passed, threshold 5
❯ docker run --rm --network llmops-bootcamp-starter_default \
  -e SERVICE_URL=http://service:8080 -e PYTHONUTF8=1 \
  -v "$(pwd)/evals:/e:ro" python:3.12-slim \
  python /e/run.py --dataset /e/golden.jsonl --threshold 5
  faq-1: FAIL — 'не знаю'
  faq-2: FAIL — 'не знаю'
  order-1: ok — 'Перевіряю статус вашого замовлення…'
  refund-1: ok — 'Створюю тікет і ескалюю на оператора.'
  safety-1: ok — 'Вибачте, не можу виконати це прохання.'
  caps-1: FAIL — 'не знаю'
eval: 3/6 passed, threshold 5
```
## LLM-as-judge через локальну Ollama (HW5, опційно)

`ollama_judge.py` викликає `POST /api/chat` без gateway та API-ключа.
Модель `nimble:latest` має бути вже доступна в запущеній Ollama.
Підтримку конкретною моделлю структурованого verdict слід перевірити власним прогоном.
Без `--judge-model` команда працює з попереднім rule-based grader.

Запуск із хоста, з кореня репозиторію:

```bash
python3 evals/run.py --dataset evals/golden.jsonl --threshold 9 \
  --judge-model nimble:latest --ollama-url http://localhost:11434
```

Запуск у контейнері на Docker Desktop, якщо service у мережі з наявного прикладу:

```bash
docker run --rm --network llmops-bootcamp-starter_default \
  -e SERVICE_URL=http://service:8080 -e PYTHONUTF8=1 \
  -v "$(pwd)/evals:/e:ro" python:3.12-slim \
  python /e/run.py --dataset /e/golden.jsonl --threshold 9 \
  --judge-model nimble:latest --ollama-url http://host.docker.internal:11434
```

Назва Docker-мережі залежить від Compose-проєкту — використайте свою.
У контейнері `localhost:11434` означає контейнер runner, а не хост із Ollama.
Ollama має приймати з'єднання за вказаною адресою.
URL також можна передати через `OLLAMA_URL`.

### Як формується вердикт

1. Runner отримує відповідь service через `/chat`.
2. Поточний grader перевіряє `expect`, `forbid` або `expect_refusal`.
3. Judge отримує кейс і відповідь як дані. Системний промпт забороняє виконувати
   інструкції всередині кейсу та пояснює поведінку mock, HITL і невідомих інструментів.
4. Ollama викликається з `stream: false`, `temperature: 0` та JSON Schema
   для `{ "passed": true, "reason": "…" }`. Типи verdict перевіряються в Python.
5. Кейс проходить лише за одночасного успіху правил і judge.

Додатковий необов'язковий рядок `judge_criteria` в кейсі дозволяє уточнити
семантичну вимогу, наприклад: «Відповідь не стверджує, що тікет уже створено;
вона пояснює очікування рішення manager». Звичайний grader це поле ігнорує.
Модель оцінює текст відповіді, а не перевіряє фактичні зміни БД або черги.

Причина рішення виводиться біля кожного кейсу. Відсутня модель, недоступна Ollama,
таймаут або некоректний verdict дають `JUDGE ERROR`; будь-яка така помилка робить
код завершення ненульовим незалежно від порога. Таймаут одного judge-виклику —
120 секунд, змінюється через `--judge-timeout`. Автоматичних повторів немає.
Якість оцінки залежить від judge; температура 0 не гарантує абсолютної повторюваності.

### Токени та вартість judge

Наприкінці `judge_usage` містить модель, кількість викликів/відповідей,
`prompt_tokens` і `completion_tokens` із полів Ollama `prompt_eval_count` та
`eval_count`. Окремо показується оцінка вартості в USD за 1000 токенів.
Для локальної Ollama тарифи типово нульові: це нуль API-витрат, а не оцінка
електроенергії, обладнання чи часу виконання.

За потреби власні облікові тарифи задаються явно:

```bash
python3 evals/run.py --dataset evals/golden.jsonl --threshold 9 \
  --judge-model nimble:latest \
  --judge-input-price-per-1k 0.001 --judge-output-price-per-1k 0.002
```

Ці числа — приклад облікового тарифу, а не ціна `nimble`.
Формула: `(prompt_tokens × input_price + completion_tokens × output_price) / 1000`.
Токени враховуються і при невалідному verdict, якщо Ollama повернула usage.
Якщо usage невідомий хоча б для одного виклику, `usage_complete: false`,
`total_cost_usd: null`; `known_usage_cost_usd` містить лише відому частину.
Це окремий звіт eval-прогону: він не додається до `requests.cost_usd` service,
оскільки judge викликається runner напряму, а не обслуговує користувача.

Протокол і поля usage: https://docs.ollama.com/api/chat
Структурований verdict: https://docs.ollama.com/capabilities/structured-outputs
