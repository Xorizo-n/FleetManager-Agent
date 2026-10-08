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
- `installer` — `FleetManagerAgent.iss` (Inno Setup; единственный источник логики установки — OpenSSH Server на порту 22 только для сервера, firewall, служба, автозапуск tray) и `uninstall.ps1` (ручное локальное удаление вне пакета).

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

### SSH: порт 22, только для сервера

Ansible управляет хостом по SSH на порту **22**, и хост отвечает только серверу Fleet Manager:

- правило брандмауэра `FleetManager-Agent-SSH` разрешает TCP 22 только с адреса сервера;
- все остальные разрешающие входящие правила для порта 22 и `sshd.exe` (например, `OpenSSH-Server-In-TCP`, открытое всем) отключаются — иначе ограничение не действует, Windows пропускает соединение по любому подходящему правилу;
- в `sshd_config` добавляется блок `Match Address *,!<адрес сервера>` с `DenyUsers *` — он ограничивает вход, даже если групповая политика отменяет локальные правила брандмауэра;
- sshd с портом 5022 (ранние сборки установщика) переводится обратно на 22;
- PowerShell назначается оболочкой SSH по умолчанию (сервер работает с `ansible_shell_type=powershell`).

Адрес сервера определяется по хосту из `ServerUrl`. Если SSH идёт с другого адреса или нужно разрешить ещё и подсеть администраторов, передайте список IPv4/CIDR — он сохранится в `agent.json` и будет использоваться при обновлениях:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://fleet.example.com /EnrollmentToken=your-token /SshSourceAddress=10.40.240.154,10.40.0.0/24
```

Перезапуск sshd выполняется в конце установки; при удалённом обновлении (установщик запущен внутри SSH-сессии) он откладывается на 2 минуты через одноразовую задачу планировщика, чтобы не оборвать сессию самого обновления.

### Подготовка ПК из образа (AutoDomain)

[AutoDomain](https://github.com/Xorizo-n/AutoDomain) ставит агента сразу после ввода ПК в домен, до перезагрузки, которая применяет новое имя:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://fleet.example.com /EnrollmentToken=your-token /SshLogin=DOMAIN\user /DeferStart=1 /ResetSshHostKeys=1
```

- `/DeferStart=1` — служба создаётся с отложенным автозапуском, но не запускается, Tray тоже не стартует. Агент регистрируется после перезагрузки, уже под новым именем.
- `/ResetSshHostKeys=1` — удаляет SSH-ключи хоста, унаследованные от эталонного ПК (если sshd на нём запускался), и sshd создаёт собственные. Внутри SSH-сессии не выполняется.
- Чистая установка (в `agent.json` нет `AgentToken`) всегда сбрасывает оставшийся `machine-id`: иначе клоны образа делили бы один идентификатор, и сервер считал бы их одним хостом.

Служба и сама не регистрируется, пока переименование ПК ожидает перезагрузки, а пока регистрации нет — повторяет попытку раз в минуту, а не раз в интервал синхронизации.

## Тесты

```powershell
dotnet test tests/FleetManager.Agent.Core.Tests/FleetManager.Agent.Core.Tests.csproj
```
