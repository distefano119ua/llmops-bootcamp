# Інцидент-демо: збій → fallback → відновлення

## Мета та передумови

Демонструємо HTTP 503 від mock-провайдера, переходи до fallback,
відкриття circuit breaker та відновлення відповідей після успішної проби.
Маркер `__fail_503` ламає **обидві** моделі ланцюга: fallback тут є спробою
резервного маршруту, а не гарантією успішної відповіді під час загального збою.

Потрібні Docker Compose із підтримкою `!override` у `demo-ports.yml`, curl,
Python 3 і Bash. Усі блоки команд виконуються послідовно в **одному Bash-сеансі**
з кореня репозиторію. Реальних API-ключів не потрібно.

Використовуємо окремий Compose-проєкт `llmops-hw6-demo` та файл `demo-ports.yml`:
service — `http://localhost:8180`, gateway — `http://localhost:4100`,
UI — `http://localhost:4300`, PostgreSQL — порт `25432`.
Ці порти мають бути вільні. Власний робочий стек курсу не зупиняємо й не змінюємо.
Окремий проєкт має власний том PostgreSQL; Redis для сценарію не потрібен,
service користується наявним резервним кешем у пам'яті.

## 1. Запуск та перевірка готовності

```bash
export SERVICE_URL=http://localhost:8180
export GATEWAY_URL=http://localhost:4100
export DEMO_RUN=$(python3 -c 'import uuid; print(uuid.uuid4().hex)')
export DEMO_DIR=$(mktemp -d /tmp/llmops-hw6.XXXXXX)

MODEL=mock docker compose -p llmops-hw6-demo \
  -f docker-compose.yml -f demo-ports.yml up -d --build

service_ready=0
for attempt in $(seq 1 40); do
  if curl -fsS "$SERVICE_URL/health"; then
    service_ready=1
    break
  fi
  sleep 2
done
test "$service_ready" = 1 || { echo 'Service не готовий; зупиніть сценарій.'; exit 1; }

for model in mock-mini mock-azure-gpt-5; do
  gateway_ready=0
  for attempt in $(seq 1 40); do
    if curl -fsS "$GATEWAY_URL/v1/chat/completions" \
      -H 'Content-Type: application/json' \
      -d "{\"model\":\"$model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello warmup $DEMO_RUN\"}]}"; then
      gateway_ready=1
      break
    fi
    sleep 2
  done
  test "$gateway_ready" = 1 || { echo "Gateway не готовий для $model; зупиніть сценарій."; exit 1; }
done
```

`/health` перевіряє лише живість service. Окремий прогрів gateway потрібен:
холодний LiteLLM може повернути помилку на перший виклик.

## 2. Робочий промпт і базова відповідь

У демо-БД має бути робоча v2. Її активація не впливає на основний стек.

```bash
curl -fsS -X POST "$SERVICE_URL/prompts/v2/activate" -H 'X-Actor: hw6-demo'
curl -fsS "$SERVICE_URL/cost"
curl -fsS "$SERVICE_URL/chat" -H 'Content-Type: application/json' \
  -d "{\"message\":\"Як скинути пароль? Демо $DEMO_RUN baseline\"}" \
  | tee "$DEMO_DIR/baseline-chat.json"

python3 - <<'PY'
import json, os
from pathlib import Path
r = json.loads((Path(os.environ['DEMO_DIR']) / 'baseline-chat.json').read_text())
assert 'пароль' in r['content'].lower() and 'email' in r['content'].lower(), r
assert r.get('blocked') is not True, r
print('Базова відповідь коректна.')
PY

curl -fsS "$SERVICE_URL/observability" | tee "$DEMO_DIR/baseline-metrics.json"
curl -fsS "$SERVICE_URL/providers"
```

Очікуємо інструкцію зі скидання пароля; активний маршрут — `mock-mini`.
Якщо базова відповідь неправильна, не продовжуйте демо. Перевірте v2,
доступність БД та моделі. У `/cost` витрати мають бути нижчі за 80% бюджету,
інакше бюджетна політика може вибрати інший маршрут. Перший запуск окремого
проєкту використовує seed із бюджетом 5 USD; повторний запуск зберігає його БД.

## 3. Відтворення інциденту

Надсилаємо три різні повідомлення з маркером збою:

```bash
for attempt in 1 2 3; do
  curl -fsS "$SERVICE_URL/chat" -H 'Content-Type: application/json' \
    -d "{\"message\":\"__fail_503 hello incident $DEMO_RUN attempt-$attempt\"}" \
    | tee "$DEMO_DIR/incident-$attempt.json"
  printf '\n'
done

curl -fsS "$SERVICE_URL/observability" | tee "$DEMO_DIR/incident-metrics.json"
curl -fsS "$SERVICE_URL/providers"

python3 - <<'PY'
import json, os
from pathlib import Path
p = Path(os.environ['DEMO_DIR'])
before = json.loads((p / 'baseline-metrics.json').read_text())
after = json.loads((p / 'incident-metrics.json').read_text())
assert after['fallback_events'] > before['fallback_events'], (before, after)
assert after['error_rate_pct'] > before['error_rate_pct'], (before, after)
print('Зафіксовано зростання fallback_events та error_rate_pct.')
PY
```

Очікуємо повідомлення «тимчасово не можемо обробити ваш запит».
Клієнт отримує HTTP 200 з повідомленням про деградацію, але `requests.status`
містить статус збою, тому error rate зростає.

Ланцюг: `mock-mini` → `mock-azure-gpt-5`. Після трьох послідовних врахованих
збоїв breaker маршруту переходить в open; `/providers` показує `down`.
Якщо серія через затримки gateway тривала понад 5 секунд, між запитами вже
можлива half-open проба. Для підтвердження переходів використовуйте логи,
а не припущення про точний стан на момент наступного GET.

## 4. Відновлення новим запитом

Маркер збою діє лише на повідомлення, яке його містить. Фізично вмикати
провайдера не потрібно. Зачекаємо ранню пробу breaker й надішлемо **нове**
питання з новим суфіксом. Воно відрізняється від baseline та кожної попередньої
спроби, тому не може повернутися з нашого кешу.

```bash
sleep 6
recovered=0
for attempt in $(seq 1 12); do
  if curl -fsS "$SERVICE_URL/chat" -H 'Content-Type: application/json' \
    -d "{\"message\":\"Як скинути пароль? Демо $DEMO_RUN recovery-$attempt\"}" \
    -o "$DEMO_DIR/recovery-chat.json"; then
    cat "$DEMO_DIR/recovery-chat.json"
    printf '\n'
    if python3 - <<'PY'
import json, os
from pathlib import Path
r = json.loads((Path(os.environ['DEMO_DIR']) / 'recovery-chat.json').read_text())
text = r.get('content', '').lower()
raise SystemExit(0 if 'пароль' in text and 'email' in text and not r.get('blocked') else 1)
PY
    then
      curl -fsS "$SERVICE_URL/observability" -o "$DEMO_DIR/recovery-metrics.json"
      if python3 - <<'PYMETRICS'
import json, os
from pathlib import Path
p = Path(os.environ['DEMO_DIR'])
incident = json.loads((p / 'incident-metrics.json').read_text())
recovery = json.loads((p / 'recovery-metrics.json').read_text())
raise SystemExit(0 if recovery['error_rate_pct'] < incident['error_rate_pct'] else 1)
PYMETRICS
      then
        recovered=1
        break
      fi
    fi
  fi
  sleep 5
done
test "$recovered" = 1 || { echo 'Відновлення не підтверджене; збережіть логи.'; exit 1; }

curl -fsS "$SERVICE_URL/observability" | tee "$DEMO_DIR/recovery-metrics.json"
curl -fsS "$SERVICE_URL/providers"

python3 - <<'PY'
import json, os
from pathlib import Path
p = Path(os.environ['DEMO_DIR'])
incident = json.loads((p / 'incident-metrics.json').read_text())
recovery = json.loads((p / 'recovery-metrics.json').read_text())
assert recovery['fallback_events'] >= incident['fallback_events'], (incident, recovery)
assert recovery['error_rate_pct'] < incident['error_rate_pct'], (incident, recovery)
print('Відновлення підтверджено: error rate знизився; fallback залишається накопичувальним.')
PY
```

Повторні спроби враховують можливий cooldown самого gateway. Відновлення
підтверджуємо змістом відповіді, а не лише HTTP 200 або низькою latency.
Якщо кілька спроб відновлення теж були невдалими, відсоток може спочатку
зрости; цикл продовжує надсилати нові питання до успішної відповіді та
зниження error rate відносно збереженого incident-знімка. Якщо ліміту
12 спроб недостатньо, збережіть результати як невдале демо та дослідіть логи.
`mock-mini` після успішної проби має стати `ok`. Резервна модель може залишатися
`down`, доки до неї не дійде власний успішний виклик; це не означає, що основний
маршрут не відновився.

Якщо `circuit-breaker-half-open=false`, ранньої проби через 5 секунд немає:
потрібно дочекатися повного періоду open, 30 секунд, і нового виклику.
Наведений цикл допускає це очікування, але типовий Compose має `true`.

## 5. Логи, консоль і докази

```bash
docker compose -p llmops-hw6-demo -f docker-compose.yml -f demo-ports.yml \
  logs --no-color --tail 500 service > "$DEMO_DIR/service.log"
python3 - <<'PY'
import os
from pathlib import Path
for line in (Path(os.environ['DEMO_DIR']) / 'service.log').read_text().splitlines():
    if any(event in line for event in ('gateway.fallback', 'gateway.failed', 'circuit.open', 'circuit.half_open', 'circuit.closed')):
        print(line)
print('Артефакти:', os.environ['DEMO_DIR'])
PY
```

Очікувані події: `gateway.fallback`, `gateway.failed`, `circuit.open`,
потім `circuit.half_open` та `circuit.closed`, якщо увімкнено half-open.
Якщо проба вже відбулася в середині серії, порядок і кількість подій можуть
відрізнятися. `request_id` пов'язує записи одного звернення.

Відкрийте `http://localhost:4300` → Console. Картки мають показувати збільшений
`fallback_events`, знижений після відновлення `error_rate_pct` і `ok` для
відновленого маршруту. `fallback_events` ніколи не зменшується — це накопичувальний
лічильник процесу. Метрики requests/error rate/p95 рахуються за сьогодні в БД;
не проводьте порівняння через межу доби PostgreSQL і не перезапускайте service
між знімками, бо його лічильники скинуться.

До опису PR додайте результати власного прогону: знімки метрик до інциденту,
під час збою й після відновлення, відповідь нового контрольного запиту та
відповідні події логів. Цей runbook описує очікування; він не є звітом про
вже виконаний прогін.

## 6. Завершення демо

```bash
docker compose -p llmops-hw6-demo -f docker-compose.yml -f demo-ports.yml down
```

Том демо-БД та збережені файли не видаляються. Основний проєкт курсу
залишається без змін.
