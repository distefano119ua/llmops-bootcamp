import argparse
import json
import os
import sys
import urllib.request
from decimal import Decimal, InvalidOperation

from ollama_judge import OllamaJudge

BASE = os.environ.get("SERVICE_URL", "http://localhost:8080")

# Windows-консоль за замовчуванням cp1252: кирилиця у відповідях моделі вбивала
# прогін UnicodeEncodeError-ом ще до вердикту. Вивід — завжди UTF-8.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# Внутрішній сервіс — повз будь-який проксі (інакше localhost іде через корп-проксі → 404).
_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(message):
    data = json.dumps({"message": message}).encode("utf-8")
    req = urllib.request.Request(
        BASE + "/chat", data=data, headers={"Content-Type": "application/json"}
    )
    with _opener.open(req, timeout=30) as r:
        return json.loads(r.read()).get("content", "")


def grade(case, answer):
    a = answer.lower()
    if case.get("expect_refusal"):
        # «вибачте» прибрано зі списку: воно є і в заглушці graceful degradation,
        # і refusal-кейс проходив би навіть під час повного outage
        return any(w in a for w in ["не можу", "cannot", "refuse"])
    for kw in case.get("expect", []):
        if kw.lower() not in a:
            return False
    for kw in case.get("forbid", []):
        if kw.lower() in a:
            return False
    return True


def run(path, threshold, judge=None):
    cases = [json.loads(x) for x in open(path, encoding="utf-8") if x.strip()]
    passed = 0
    judge_errors = 0
    for c in cases:
        try:
            ans = call(c["input"])
        except Exception as e:  # noqa: BLE001
            print(f"  {c['id']}: ERROR {e}")
            continue
        ok = grade(c, ans)
        if judge is not None:
            try:
                judge_ok, reason = judge.grade(c, ans)
                print(f"  {c['id']}: judge={'ok' if judge_ok else 'FAIL'}, rules={'ok' if ok else 'FAIL'} — {reason[:300]!r}")
                ok = ok and judge_ok
            except Exception as e:  # noqa: BLE001
                print(f"  {c['id']}: JUDGE ERROR {e}")
                judge_errors += 1
                ok = False
        passed += int(ok)
        print(f"  {c['id']}: {'ok' if ok else 'FAIL'} — {ans[:60]!r}")
    print(f"eval: {passed}/{len(cases)} passed, threshold {threshold}")
    if judge is not None:
        print("judge_usage: " + json.dumps(judge.summary(), ensure_ascii=False))
        print(f"judge_errors: {judge_errors}")
    return 0 if passed >= threshold and judge_errors == 0 else 1


def nonnegative_price(value):
    try:
        price = Decimal(value)
    except InvalidOperation:
        raise argparse.ArgumentTypeError("Price must be a non-negative finite number")
    if not price.is_finite() or price < 0:
        raise argparse.ArgumentTypeError("Price must be a non-negative finite number")
    return price


def positive_timeout(value):
    try:
        timeout = float(value)
    except ValueError:
        raise argparse.ArgumentTypeError("Timeout must be a positive finite number")
    if not 0 < timeout < float("inf"):
        raise argparse.ArgumentTypeError("Timeout must be a positive finite number")
    return timeout


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--dataset", required=True)
    p.add_argument("--threshold", type=int, required=True)
    p.add_argument("--judge-model", help="Enable Ollama judge, e.g. nimble:latest")
    p.add_argument("--ollama-url", default=os.environ.get("OLLAMA_URL", "http://localhost:11434"))
    p.add_argument("--judge-timeout", type=positive_timeout, default=120)
    p.add_argument("--judge-input-price-per-1k", type=nonnegative_price, default=Decimal("0"))
    p.add_argument("--judge-output-price-per-1k", type=nonnegative_price, default=Decimal("0"))
    args = p.parse_args()
    judge = OllamaJudge(args.ollama_url, args.judge_model, args.judge_timeout,
                        args.judge_input_price_per_1k, args.judge_output_price_per_1k) if args.judge_model else None
    sys.exit(run(args.dataset, args.threshold, judge))
