#!/usr/bin/env bash
# Приёмочный тест desktop-версии: полный смоук-сценарий (24 проверки)
# на локально собранном приложении — как scripts/smoke.sh для docker-стека.
#
#   bash scripts/smoke-desktop.sh
set -euo pipefail
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null || export PATH="$HOME/.dotnet:$PATH"

DATA_DIR="$(mktemp -d /tmp/ejl-smoke.XXXXXX)"
APP_PID=""

cleanup() {
  [ -n "$APP_PID" ] && kill "$APP_PID" 2>/dev/null || true
  rm -rf "$DATA_DIR"
}
trap cleanup EXIT

echo "==> Сборка"
( cd web && npm run build >/dev/null )
rsync -a --delete web/dist/ desktop/EJL.App/wwwroot/
dotnet build desktop/EJL.App/EJL.App.csproj -o desktop/.build --nologo -v q

echo "==> Запуск приложения (данные: $DATA_DIR)"
EJL_DATA="$DATA_DIR" EJL_NO_BROWSER=1 EJL_NO_LIVENESS=1 \
  dotnet desktop/.build/EJL.dll &
APP_PID=$!

# ждём адрес сервера
URL=""
for _ in $(seq 1 120); do
  [ -f "$DATA_DIR/server.url" ] && { URL="$(cat "$DATA_DIR/server.url" | tr -d '[:space:]' | sed 's:/*$::')"; break; }
  sleep 0.5
done
if [ -z "$URL" ]; then
  echo "FAIL: приложение не поднялось (лог: $DATA_DIR/ejl.log)"
  exit 1
fi
echo "==> Приложение: $URL"

BASE="$URL" bash scripts/smoke.sh
