#!/usr/bin/env bash
# Сборка desktop-версии ЭЖЛ для Windows 10 x86 (работает на macOS).
# Результат: artifacts/EJL-win-x86.zip — один exe + руководство.
#
# Требуется один раз: .NET SDK (brew install --cask dotnet-sdk), Node.js.
set -euo pipefail
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null || export PATH="$HOME/.dotnet:$PATH"

echo "==> [1/4] Фронтенд (React SPA)"
( cd web && npm ci --no-audit --no-fund && npm run build )

echo "==> [2/4] Встраивание SPA в exe"
rsync -a --delete web/dist/ desktop/EJL.App/wwwroot/
# EmbeddedResource требует, чтобы файлы существовали на момент компиляции
touch desktop/EJL.App/wwwroot/index.html

echo "==> [3/4] dotnet publish (win-x86, self-contained, один файл)"
dotnet publish desktop/EJL.App/EJL.App.csproj \
  -c Release -r win-x86 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=embedded \
  -o artifacts/win-x86

# один exe в пакете: убираем технические файлы publish
find artifacts/win-x86 -type f ! -name 'EJL.exe' -delete
find artifacts/win-x86 -type d -empty -delete

echo "==> [4/4] Архив"
cp desktop/РУКОВОДСТВО_ЛАБОРАНТА.txt artifacts/win-x86/ 2>/dev/null || true
rm -f artifacts/EJL-win-x86.zip
( cd artifacts/win-x86 && zip -q -r ../EJL-win-x86.zip . )

echo
echo "Готово: $(pwd)/artifacts/EJL-win-x86.zip"
ls -lh artifacts/EJL-win-x86.zip artifacts/win-x86/EJL.exe | awk '{print $5, $9}'
