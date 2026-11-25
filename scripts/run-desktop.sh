#!/usr/bin/env bash
# Dev-запуск desktop-приложения на macOS/любой ОС:
# собирает фронтенд, встраивает его и стартует сервер с окном браузера.
# Тот же код, что уходит в exe: на Windows откроется Edge в app-режиме.
#
# Переменные окружения:
#   EJL_DATA=...          каталог данных (по умолчанию ~/.local/share/EJL или %LOCALAPPDATA%\EJL)
#   EJL_NO_BROWSER=1      не открывать окно
#   EJL_DEMO_PASSWORD=... пароль демо-пользователей (по умолчанию demo123)
set -euo pipefail
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null || export PATH="$HOME/.dotnet:$PATH"

echo "==> Сборка фронтенда"
( cd web && npm run build )
rsync -a --delete web/dist/ desktop/EJL.App/wwwroot/

echo "==> Запуск (адрес также пишется в <каталог данных>/server.url)"
dotnet run --project desktop/EJL.App/EJL.App.csproj
