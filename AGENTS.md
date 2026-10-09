# Навигация для агентов

## Назначение

Windows-агент Fleet Manager: служба, отправляющая heartbeat и инвентаризацию на сервер, tray-приложение и элевированная панель управления, плюс EXE-установщик (Inno Setup).

## Карта проекта

- `src/FleetManager.Agent.Core` — конфигурация, machine-id, состояние, журнал, Named Pipe, API-клиент, сбор инвентаризации, версия агента (`AgentVersion.cs`). Общая библиотека для остальных проектов. `AgentStatusPresenter.cs` — единственный источник текстов состояния («Подключено к серверу», «Нет связи с сервером»…) для Tray и Control; причину сбоя служба передаёт в `AgentStatus.Issue` (`AgentIssues.Classify`).
- `src/FleetManager.Agent.Service` — фоновая служба Windows: периодический сбор железа/ПО, heartbeat, обработка локальных команд.
- `src/FleetManager.Agent.Ui` — общий WinForms-слой Tray и Control в стиле Windows 11: тема (светлая/тёмная/высокий контраст, акцентный цвет из системы), шрифты Segoe UI Variable и Fluent-иконки, карточки, кнопки, поле ввода, рендерер меню, иконка трея с индикатором проблем. Попадает в `publish/win-x64` как `FleetManager.Agent.Ui.dll`, установщик забирает его маской.
- `src/FleetManager.Agent.Tray` — неэлевированный процесс в трее (один на сессию); открывает Control через `runas`, а если панель уже открыта — выводит её на передний план без повторного UAC. Подсказку `NotifyIcon.Text` (лимит 127 символов, иначе исключение) строить только через `AgentStatusPresenter.Tooltip`.
- `src/FleetManager.Agent.Control` — WinForms-панель с manifest `requireAdministrator`: состояние, адрес сервера, сведения о ПК, журнал; при остановленной службе предлагает её запустить (`sc.exe start`).
- `installer/` — `FleetManagerAgent.iss` (Inno Setup script; единственный источник логики установки — весь install-код генерируется и исполняется прямо в `CurStepChanged`: OpenSSH Server на порту 22, firewall и `sshd_config` пускают только адрес сервера (`/SshSourceAddress`, по умолчанию хост из `ServerUrl`), служба, автозапуск tray) и `uninstall.ps1` (отдельный скрипт для ручного/удалённого удаления вне пакета). Отдельного `install.ps1` больше нет — не создавайте его заново, любые изменения install-логики вносите в `.iss`.
- Подготовка ПК из образа ([AutoDomain](https://github.com/Xorizo-n/AutoDomain)): установщик запускается до перезагрузки, применяющей новое имя ПК, с `/DeferStart=1` (служба не стартует до перезагрузки) и `/ResetSshHostKeys=1` (новые SSH-ключи хоста вместо ключей эталона). Чистая установка (нет `AgentToken`) сбрасывает `machine-id`, а служба не регистрируется, пока ожидается переименование (`ComputerRename.IsPending`) — иначе хост попал бы на сервер под старым именем образа.
- Установка поверх уже зарегистрированного агента — режим обновления: `ServerUrl` и `EnrollmentToken` можно не передавать, `AgentId`/`AgentToken`/`SshPublicKey`/`SshLogin` переносятся из существующего `agent.json`, поэтому агент не перерегистрируется и не получает новый SSH-ключ. Именно так сервер обновляет агентов удалённо. `agent.json` читается регистронезависимо: установщик пишет PascalCase, служба перезаписывает файл camelCase.
- Named Pipe обслуживает до 8 клиентов одновременно: `sync` держит соединение до конца синхронизации, и с одним экземпляром трей в это время не мог получить статус (`AgentPipeTests`).
- `tests/FleetManager.Agent.Core.Tests` — dotnet-тесты Core; `installer/` также покрыт Pester-тестами (`tests/install_script.tests.ps1`).
- `build-installer.ps1` — локальный пайплайн: dotnet publish (Service/Tray/Control) → Inno Setup → `dist/FleetManagerAgent-Setup.exe`. Требует Windows, .NET SDK 8+, Inno Setup 6 (с ISPP). Один и тот же `-AppVersion` уходит и в `-p:Version` сборок, и в `/DAppVersion` установщика — на этом держится учёт версий на сервере, не разводите их.

## Связанные репозитории

- [FleetManager-Server](https://github.com/Xorizo-n/FleetManager-Server) — принимает heartbeat/инвентаризацию (`routers/agent.py`) и автоматически подтягивает свежий инсталлятор из GitHub Releases этого репозитория (`backend/services/agent_installer_sync.py`, почасово через Celery beat + кнопка «Проверить обновление агента» в UI). Он же хранит установленную версию агента по хостам и обновляет агентов удалённо (`backend/services/agent_update.py`): хост скачивает установщик сам через `GET /api/agent/installer` своим agent-токеном.
- [RTF_OOD_AnsiblePlaybooks](https://github.com/kozlov174/RTF_OOD_AnsiblePlaybooks) — плейбуки, которые сервер запускает на хостах с этим агентом.

## Ветвление и CI/CD

- `main` защищён: прямой пуш запрещён (в том числе для админа), обязателен Pull Request. Force-push и удаление ветки заблокированы.
- `develop` — интеграционная ветка для повседневной работы. Рабочий цикл: коммиты в `develop` (или feature-ветку от неё) → PR в `main` → мердж.
- `.github/workflows/build-installer.yml`: при пуше в `main`, затрагивающем `src/**`, `installer/**` или `build-installer.ps1`, GitHub Actions на `windows-latest` собирает три .NET-проекта + Inno Setup и публикует `FleetManagerAgent-Setup.exe` как GitHub Release с тегом `v<год>.<месяц>.<день>.<run_number>`.
- `.github/workflows/pr-check.yml`: на каждый PR в `main` — `dotnet test`, Pester-тесты установщика и полная сборка `build-installer.ps1` (компилирует Pascal-код `.iss`). Ничего не публикует.
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
