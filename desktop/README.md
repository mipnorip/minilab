# ЭЖЛ Desktop — один exe для Windows 10 x86

Desktop-версия «Электронных журналов лаборатории»: весь web-стек
(React SPA + 6 backend-сервисов + PostgreSQL) свёрнут в **одно приложение**:

```
EJL.exe
├── Kestrel (127.0.0.1, свободный порт)  — вместо nginx-gateway
│   ├── /api/auth|forms|records|materials|notifications|audit  — было 6 сервисов FastAPI
│   └── / — React SPA, вшитая в exe как ресурсы
├── SQLite (EF Core, WAL)                — вместо PostgreSQL
├── планировщик в фоне                   — вместо scheduler-svc
└── окно приложения: Edge --app=...      — вместо браузера
```

Контракт API и фронтенд — **без изменений**: тот же same-origin `/api/*`,
тот же JWT, тот же формат ошибок `{"detail": "..."}`. Смоук-сценарий
`scripts/smoke.sh` проходит против desktop-версии.

## Данные

Всё живёт в одном каталоге (переменная `EJL_DATA`, по умолчанию):

| ОС | Каталог |
|---|---|
| Windows | `%LOCALAPPDATA%\EJL` |
| macOS | `~/.local/share/EJL` |

```
ejl.db          база SQLite (вся система)
secret.key      секрет JWT (генерируется при первом запуске)
ejl.log         журнал приложения
backup/         резервные копии (кнопка 💾 в интерфейсе)
server.lock     блокировка «один экземпляр»
server.url      адрес запущенного сервера
```

При первом запуске создаются схема и демо-данные (пользователи `admin`,
`head`, `qm`, `eng1`, `lab1`, `lab2`, пароль `demo123`; 12 пресетов форм,
5 журналов MVP, материалы).

## Команды

```bash
# сборка exe для Windows 10 x86 — прямо на macOS (нужен .NET SDK)
bash scripts/build-desktop.sh        # → artifacts/EJL-win-x86.zip

# dev-запуск на своей машине (браузер откроется сам)
bash scripts/run-desktop.sh

# приёмочный тест: 24 проверки демо-сценария против локального приложения
bash scripts/smoke-desktop.sh
```

Требования к машине сборки (один раз): .NET SDK 11+ и Node.js.
Установка SDK (на выбор):

```bash
brew install --cask dotnet-sdk          # через Homebrew
# либо официально, без прав администратора (в ~/.dotnet):
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 11.0
```

Скрипты сами найдут `dotnet` из PATH или в `~/.dotnet`.
Кросс-компиляция в `win-x86` — штатная возможность .NET, Windows не нужна.

## Переменные окружения

| Переменная | Назначение |
|---|---|
| `EJL_DATA` | каталог данных (БД, лог, копии) |
| `EJL_PORT` | фиксированный порт сервера (для тестов) |
| `EJL_NO_BROWSER=1` | не открывать окно |
| `EJL_NO_LIVENESS=1` | не завершаться при закрытии окна (тесты) |
| `EJL_DEMO_PASSWORD` | пароль демо-пользователей (по умолчанию `demo123`) |

## Как живёт приложение

1. exe занимает lock-файл (второй запуск просто открывает уже работающее окно);
2. поднимает Kestrel на случайном свободном порту, пишет адрес в `server.url`;
3. открывает окно приложения (Edge в `--app`-режиме; на macOS/Linux — браузер по умолчанию);
4. SPA каждые 5 секунд шлёт `POST /api/ping`; когда окно закрывают — пульс
   пропадает, сервер корректно закрывает SQLite и завершается (≤45 сек);
5. планировщик раз в минуту проверяет сроки годности, ежедневные записи и
   периодичность проверок журналов.

## Перенос с Python-микросервисов

| Было (web) | Стало (desktop) |
|---|---|
| `services/auth` | `EJL.App/Modules/AuthModule.cs` |
| `services/forms` (+presets.py) | `FormsModule.cs` + `Resources/presets.json` |
| `services/records` (+seed.py) | `RecordsModule.cs` + `Seeder.cs` |
| `services/materials` | `MaterialsModule.cs` |
| `services/scheduler` | `NotificationsModule.cs` + `SchedulerService.cs` |
| `services/audit` | `AuditModule.cs` + `Audit.Emit()` (прямой вызов вместо REST) |
| PostgreSQL | SQLite через EF Core (JSON → TEXT, даты → UTC TEXT) |

Бизнес-правила перенесены дословно: исправления «с причиной» (V/O/I) и ревизии,
запрет удаления, роли и право на списание прекурсоров, FEFO-списание, dedupe
уведомлений, append-only аудит. Безопасный вычислитель формул — порт `safe_eval`
(`Domain/Formula.cs`), форматирование чисел `5.0` как в Python — `Util/Json.cs`.
