# FleetManager Agent

Windows-агент для Fleet Manager. Вариант 1 состоит из службы Windows, tray-приложения и отдельной панели управления, которая запускается только через UAC.

## Связанные репозитории

- [FleetManager-Server](https://github.com/Xorizo-n/FleetManager-Server) — backend/frontend, принимает регистрацию, heartbeat и инвентаризацию от агента (`routers/agent.py`).
- [RTF_OOD_AnsiblePlaybooks](https://github.com/kozlov174/RTF_OOD_AnsiblePlaybooks) — плейбуки, которые сервер запускает на хостах с установленным агентом.

## Структура

- `src/FleetManager.Agent.Core` — конфигурация, machine-id, состояние, журнал, Named Pipe, API-клиент и сбор инвентаризации.
- `src/FleetManager.Agent.Service` — фоновая служба: периодический сбор железа/ПО, heartbeat и обработка локальных команд.
- `src/FleetManager.Agent.Tray` — неэле­вированный процесс в области уведомлений; открывает Control через `runas`.
- `src/FleetManager.Agent.Control` — WinForms-панель с manifest `requireAdministrator`.
- `installer` — `FleetManagerAgent.iss` (Inno Setup; единственный источник логики установки — OpenSSH Server, firewall, порт 5022, служба, автозапуск tray) и `uninstall.ps1` (ручное локальное удаление вне пакета).

## Сборка

```powershell
dotnet publish src/FleetManager.Agent.Service/FleetManager.Agent.Service.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
dotnet publish src/FleetManager.Agent.Tray/FleetManager.Agent.Tray.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
dotnet publish src/FleetManager.Agent.Control/FleetManager.Agent.Control.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Установка выполняется только через собранный Inno Setup EXE-установщик (см. ниже) — отдельного `install.ps1` в репозитории нет, вся логика установки встроена в `installer\FleetManagerAgent.iss`. Конфигурация и логи находятся в `%ProgramData%\FleetManagerAgent`.

Сервис регистрируется через enrollment-токен и шлёт heartbeat (железо + ПО + версия агента) на `/api/agent/heartbeat` сервера Fleet Manager.

## Установщик (Inno Setup)

`build-installer.ps1` собирает все три проекта и упаковывает их в единый EXE-установщик:

```powershell
.\build-installer.ps1
.\build-installer.ps1 -AppVersion 1.2
.\build-installer.ps1 -SkipPublish   # пересобрать инсталлятор без dotnet publish
```

Требует .NET SDK 8+ и Inno Setup 6 (с ISPP). Результат — `dist\FleetManagerAgent-Setup.exe`:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://fleet.example.com /EnrollmentToken=your-token
```

### Обновление поверх установленного агента

Тот же EXE ставится поверх существующей установки — в этом режиме `ServerUrl` и `EnrollmentToken` не нужны:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Адрес сервера, идентификатор агента, agent-токен и SSH-ключ переносятся из существующего `%ProgramData%\FleetManagerAgent\agent.json`, поэтому агент остаётся зарегистрированным на том же хосте. Fleet Manager запускает эту же команду удалённо по SSH («Обновить агент» в реестре хостов), а версия из сборки уходит на сервер в heartbeat.

По умолчанию открывается только порт **5022** (используется Ansible для управления хостом): sshd настраивается слушать исключительно его, а любое уже существующее разрешающее правило firewall для порта 22 обнаруживается и удаляется. Чтобы пропустить эту проверку и оставить состояние порта 22 как есть, добавьте `/AllowPort22=1`:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://fleet.example.com /EnrollmentToken=your-token /AllowPort22=1
```

## Тесты

```powershell
dotnet test tests/FleetManager.Agent.Core.Tests/FleetManager.Agent.Core.Tests.csproj
```
