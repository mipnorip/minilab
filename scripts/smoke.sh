#!/usr/bin/env bash
# Смоук-тест ЭЖЛ: полный демо-сценарий через API шлюза.
# Запускать при поднятом стеке:  bash scripts/smoke.sh
# Дополнительно: BASE=http://localhost:8080 DEMO_PASSWORD=demo123 bash scripts/smoke.sh
set -uo pipefail

BASE="${BASE:-http://localhost:8080}"
PASS="${DEMO_PASSWORD:-demo123}"
BODY_FILE="${TMPDIR:-/tmp}/ejl_smoke_body.json"
OK=0
BAD=0

note() { printf '\n%s\n' "$*"; }
pass() { OK=$((OK + 1)); printf '  OK  %s\n' "$1"; }
bad() { BAD=$((BAD + 1)); printf '  FAIL %s\n' "$1"; }

req() { # req METHOD PATH [TOKEN] [JSON] -> HTTP-код, тело ответа в $BODY_FILE
  local method="$1" path="$2" token="${3:-}" data="${4:-}"
  local args=(-s -o "$BODY_FILE" -w '%{http_code}' -X "$method" "$BASE$path")
  [ -n "$token" ] && args+=(-H "Authorization: Bearer $token")
  [ -n "$data" ] && args+=(-H 'Content-Type: application/json' -d "$data")
  curl "${args[@]}"
}
body() { cat "$BODY_FILE"; }
py() { python3 -c "$1" 2>/dev/null || echo ""; }

probe() { # probe PATH: 0, когда сервис отвечает (не 502) — до 60 секунд
  local path="$1" i code
  for i in $(seq 1 30); do
    code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE$path")
    [ "$code" != "502" ] && return 0
    sleep 2
  done
  return 1
}
login() {
  req POST /api/auth/login "" "{\"login\":\"$1\",\"password\":\"$PASS\"}" >/dev/null
  body | py "import sys,json;print(json.load(sys.stdin).get('token',''))"
}

note "== 0. Стек =="
SPA=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/")
[ "$SPA" = "200" ] && pass "SPA отдаётся: $BASE" || bad "SPA недоступна (HTTP $SPA)"
for p in /api/auth/me /api/forms/templates /api/records/journals /api/materials/items /api/notifications/list /api/audit/events; do
  probe "$p" && pass "сервис отвечает: $p" || bad "сервис недоступен: $p"
done

note "== 1. Вход =="
ADMIN=$(login admin)
LAB1=$(login lab1)
LAB2=$(login lab2)
ENG1=$(login eng1)
QM=$(login qm)
for t in ADMIN LAB1 LAB2 ENG1 QM; do
  [ -n "${!t}" ] && pass "вход: $t" || bad "вход: $t"
done

note "== 2. Журналы и сиды =="
req GET /api/records/journals "$ADMIN" >/dev/null
NJ=$(body | py "import sys,json;print(len(json.load(sys.stdin)))")
[ "$NJ" -ge 16 ] && pass "журналов в реестре: $NJ (16 из согласованного перечня)" || bad "журналов: $NJ"
req GET /api/forms/templates "$ADMIN" >/dev/null
NT=$(body | py "import sys,json;print(len(json.load(sys.stdin)))")
[ "$NT" -ge 12 ] && pass "шаблонов форм: $NT (12 пресетов)" || bad "шаблонов: $NT"
CODE=$(req GET "/api/records/journals/code/Ж-04/01" "$LAB1")
[ "$CODE" = "200" ] && pass "журнал по шифру «Ж-04/01» (слэш в коде)" || bad "журнал по шифру: HTTP $CODE"

note "== 3. Записи: формула, исправления, история =="
TODAY=$(date +%F)
CODE=$(req POST /api/records/journals/2/records "$LAB1" \
  "{\"data\":{\"date\":\"$TODAY\",\"sample\":1,\"method\":\"ГОСТ 4159-72 (хлориды)\",\"mass_g\":0.25,\"volume_ml\":50,\"norm\":\"до 350 мг/л\",\"verdict\":\"соответствует\"}}")
REC=$(body | py "import sys,json;print(json.load(sys.stdin).get('id',''))")
[ "$CODE" = "201" ] && pass "запись внесена в Ж-04/02 (id=$REC)" || bad "внесение записи: HTTP $CODE — $(body)"
req GET "/api/records/records/$REC" "$LAB1" >/dev/null
R=$(body | py "import sys,json;print(json.load(sys.stdin)['data']['result'])")
[ "$R" = "5.0" ] && pass "расчётная колонка: 0.25*1000/50 = $R" || bad "формула дала: $R"

CODE=$(req PATCH "/api/records/records/$REC" "$LAB1" \
  "{\"data\":{\"date\":\"$TODAY\",\"sample\":1,\"method\":\"ГОСТ 4159-72 (хлориды)\",\"mass_g\":0.5,\"volume_ml\":50,\"norm\":\"до 350 мг/л\",\"verdict\":\"соответствует\"},\"reason_code\":\"V\",\"reason_comment\":\"пересчёт навески\"}")
[ "$CODE" = "200" ] && pass "исправление с причиной (V)" || bad "исправление: HTTP $CODE — $(body)"
req GET "/api/records/records/$REC/history" "$LAB1" >/dev/null
REV=$(body | py "import sys,json;print(len(json.load(sys.stdin)['revisions']))")
[ "$REV" -ge 1 ] && pass "история: $REV ревизия, прежнее значение сохранено" || bad "ревизий: $REV"

CODE=$(req POST /api/records/journals/1/records "$LAB1" '{"data":{}}')
[ "$CODE" = "422" ] && pass "валидация обязательных полей (422)" || bad "валидация: HTTP $CODE"

note "== 4. Права =="
CODE=$(req GET /api/audit/events "$LAB1")
[ "$CODE" = "403" ] && pass "лаборанту закрыт журнал аудита (403)" || bad "аудит для лаборанта: HTTP $CODE"
CODE=$(req POST /api/records/journals/2/records "$LAB2" \
  "{\"data\":{\"date\":\"$TODAY\",\"sample\":1,\"method\":\"ГОСТ 4159-72 (хлориды)\",\"mass_g\":1,\"volume_ml\":100}}")
[ "$CODE" = "403" ] && pass "внесение записи не-участником журнала отклонено (403)" || bad "не-участник: HTTP $CODE"

note "== 5. Материалы: реактивы и прекурсоры =="
req GET /api/materials/items "$LAB1" >/dev/null
NITRATE=$(body | py "import sys,json;items=json.load(sys.stdin);print(next((i['id'] for i in items if 'итрат' in i['name']),0))")
PRECURSOR=$(body | py "import sys,json;items=json.load(sys.stdin);print(next((i['id'] for i in items if i['type']=='precursor'),0))")

CODE=$(req POST /api/materials/writeoffs "$LAB1" "{\"item_id\":$NITRATE,\"qty\":10,\"note\":\"на анализ\"}")
[ "$CODE" = "201" ] && pass "лаборант списал реактив (10 мл)" || bad "списание реактива: HTTP $CODE — $(body)"

CODE=$(req POST /api/materials/writeoffs "$LAB1" "{\"item_id\":$PRECURSOR,\"qty\":5}")
[ "$CODE" = "403" ] && pass "прекурсор без отдельного права — отказ (403)" || bad "прекурсор lab1: HTTP $CODE — $(body)"

CODE=$(req POST /api/materials/writeoffs "$ENG1" \
  "{\"item_id\":$PRECURSOR,\"qty\":5,\"target_journal_code\":\"Ж-04/02\",\"target_record_id\":$REC}")
[ "$CODE" = "201" ] && pass "инженер с правом списал прекурсор (со ссылкой на запись)" || bad "прекурсор eng1: HTTP $CODE — $(body)"

req GET "/api/materials/operations?item_type=precursor" "$QM" >/dev/null
NOPS=$(body | py "import sys,json;print(len(json.load(sys.stdin)))")
[ "$NOPS" -ge 3 ] && pass "журнал учёта прекурсоров (Ж-04/03): операций $NOPS" || bad "операций: $NOPS"

note "== 6. Проверки журналов =="
CODE=$(req POST /api/records/journals/1/review "$QM" '{"note":"проверено, замечаний нет"}')
[ "$CODE" = "201" ] && pass "менеджер по качеству отметил проверку Ж-04/01" || bad "проверка: HTTP $CODE — $(body)"

note "== 7. Напоминания =="
CODE=$(req POST /api/notifications/tick "$ADMIN")
CREATED=$(body | py "import sys,json;print(json.load(sys.stdin).get('created',''))")
[ "$CODE" = "200" ] && pass "ручной тик планировщика: создано уведомлений $CREATED" || bad "тик: HTTP $CODE"
req GET /api/notifications/list "$LAB1" >/dev/null
NN=$(body | py "import sys,json;print(len(json.load(sys.stdin)))")
[ "$NN" -ge 1 ] && pass "у lab1 уведомлений: $NN (сроки годности / запись 9.5)" || bad "уведомлений у lab1: $NN"

note "== 8. Аудит =="
req GET "/api/audit/events?limit=100" "$QM" >/dev/null
NEV=$(body | py "import sys,json;print(len(json.load(sys.stdin)))")
[ "$NEV" -ge 10 ] && pass "событий в журнале аудита: $NEV" || bad "событий аудита: $NEV"

note "== 9. Конструктор форм (регрессия: колонки с null-полями) =="
req GET /api/forms/templates "$ADMIN" >/dev/null
TID=$(body | py "import sys,json;ts=json.load(sys.stdin);print(next((t['id'] for t in ts if t['code']=='T-SMOKE'),0))")
[ -z "$TID" ] && TID=0
if [ "$TID" = "0" ]; then
  CODE=$(req POST /api/forms/templates "$ADMIN" '{"code":"T-SMOKE","title":"Смоук-шаблон","is_preset":false,"columns":[{"key":"name","title":"Наименование","type":"text","required":true,"options":null,"unit":null},{"key":"qty","title":"Количество","type":"number","unit":"шт","options":null}]}')
  TID=$(body | py "import sys,json;print(json.load(sys.stdin).get('id',''))")
  [ "$CODE" = "201" ] && pass "пользовательский шаблон T-SMOKE создан" || bad "создание шаблона: HTTP $CODE — $(body)"
else
  pass "шаблон T-SMOKE уже есть (повторный прогон)"
fi
CODE=$(req POST "/api/forms/templates/$TID/versions" "$ADMIN" '{"columns":[{"key":"name","title":"Наименование","type":"text","required":true,"options":null,"unit":null},{"key":"qty","title":"Количество","type":"number","unit":"шт","options":null},{"key":"note","title":"Примечание","type":"text","options":null}]}')
[ "$CODE" = "201" ] && pass "новая версия шаблона сохранена (null-поля колонок)" || bad "версия шаблона: HTTP $CODE — $(body)"

note "================================"
note "Пройдено: $OK   Провалено: $BAD"
[ "$BAD" -eq 0 ] && note "СМОУК-ТЕСТ ПРОЙДЕН ПОЛНОСТЬЮ"
exit $BAD
