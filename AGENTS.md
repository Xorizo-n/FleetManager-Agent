# Навигация для агентов

## Назначение

Windows-агент Fleet Manager: служба, отправляющая heartbeat и инвентаризацию на сервер, tray-приложение и элевированная панель управления, плюс EXE-установщик (Inno Setup).

## Карта проекта

- `src/FleetManager.Agent.Core` — конфигурация, machine-id, состояние, журнал, Named Pipe, API-клиент, сбор инвентаризации. Общая библиотека для остальных трёх проектов.
- `src/FleetManager.Agent.Service` — фоновая служба Windows: периодический сбор железа/ПО, heartbeat, обработка локальных команд.
- `src/FleetManager.Agent.Tray` — неэлевированный процесс в трее; открывает Control через `runas`.
- `src/FleetManager.Agent.Control` — WinForms-панель с manifest `requireAdministrator`.
- `installer/` — `FleetManagerAgent.iss` (Inno Setup script; единственный источник логики установки — весь install-код генерируется и исполняется прямо в `CurStepChanged`: OpenSSH Server, firewall/порт 5022, служба, автозапуск tray) и `uninstall.ps1` (отдельный скрипт для ручного/удалённого удаления вне пакета). Отдельного `install.ps1` больше нет — не создавайте его заново, любые изменения install-логики вносите в `.iss`.
- `tests/FleetManager.Agent.Core.Tests` — dotnet-тесты Core; `installer/` также покрыт Pester-тестами (`tests/install_script.tests.ps1`).
- `build-installer.ps1` — локальный пайплайн: dotnet publish (Service/Tray/Control) → Inno Setup → `dist/FleetManagerAgent-Setup.exe`. Требует Windows, .NET SDK 8+, Inno Setup 6 (с ISPP).

## Связанные репозитории

- [FleetManager-Server](https://github.com/Xorizo-n/FleetManager-Server) — принимает heartbeat/инвентаризацию (`routers/agent.py`) и автоматически подтягивает свежий инсталлятор из GitHub Releases этого репозитория (`backend/services/agent_installer_sync.py`, почасово через Celery beat + кнопка «Проверить обновление агента» в UI).
- [RTF_OOD_AnsiblePlaybooks](https://github.com/kozlov174/RTF_OOD_AnsiblePlaybooks) — плейбуки, которые сервер запускает на хостах с этим агентом.

## Ветвление и CI/CD

- `main` защищён: прямой пуш запрещён (в том числе для админа), обязателен Pull Request. Force-push и удаление ветки заблокированы.
- `develop` — интеграционная ветка для повседневной работы. Рабочий цикл: коммиты в `develop` (или feature-ветку от неё) → PR в `main` → мердж.
- `.github/workflows/build-installer.yml`: при пуше в `main`, затрагивающем `src/**`, `installer/**` или `build-installer.ps1`, GitHub Actions на `windows-latest` собирает три .NET-проекта + Inno Setup и публикует `FleetManagerAgent-Setup.exe` как GitHub Release с тегом `v<год>.<месяц>.<день>.<run_number>`.
- Изменения, не затрагивающие эти пути (документация, README, workflow-файлы сами по себе), сборку **не запускают** — путь не совпадает с path-фильтром триггера.
- Версия в `installer/FleetManagerAgent.iss` (`AppVersion "1.0"`) — это только дефолт для локальной сборки; CI всегда передаёт актуальную версию через `/DAppVersion=...`, поэтому дефолт менять не нужно.
- Релизы создаются только через CI. Ручной `workflow_dispatch` допустим для тестового прогона, но публикует настоящий публичный Release — не запускать без явного запроса пользователя.

## Перед пушем

```powershell
dotnet test tests/FleetManager.Agent.Core.Tests/FleetManager.Agent.Core.Tests.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File tests/install_script.tests.ps1
```

## Запреты

- Не пушить напрямую в `main` (защита ветки всё равно отклонит, но не полагайтесь на это — работайте через `develop`/feature-ветку и PR).
- Не запускать `workflow_dispatch` для сборки/публикации Release без прямого запроса пользователя — это публичное действие.
- Не коммитить содержимое `bin/`, `obj/`, `publish/`, `dist/`, `.vs/`, `TestResults/` — все они в `.gitignore`.
