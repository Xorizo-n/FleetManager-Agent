# FleetManager Agent

Windows-агент для [Fleet Manager](https://github.com/Xorizo-n/FleetManager-Server). Вариант 1 состоит из службы Windows, tray-приложения и отдельной панели управления, которая запускается только через UAC.

## Каталоги

- `src/FleetManager.Agent.Core` — конфигурация, machine-id, состояние, журнал, Named Pipe, API-клиент и сбор инвентаризации.
- `src/FleetManager.Agent.Service` — фоновая служба: периодический сбор железа/ПО, heartbeat и обработка локальных команд.
- `src/FleetManager.Agent.Tray` — неэле­вированный процесс в области уведомлений; открывает Control через `runas`.
- `src/FleetManager.Agent.Control` — WinForms-панель с manifest `requireAdministrator`.
- `installer` — PowerShell-установка/удаление, OpenSSH Server, firewall, служба и автозапуск tray.

## Сборка

```powershell
dotnet test tests/FleetManager.Agent.Core.Tests/FleetManager.Agent.Core.Tests.csproj
dotnet publish src/FleetManager.Agent.Service/FleetManager.Agent.Service.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
dotnet publish src/FleetManager.Agent.Tray/FleetManager.Agent.Tray.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
dotnet publish src/FleetManager.Agent.Control/FleetManager.Agent.Control.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

После публикации запустите `installer\install.ps1 -ServerUrl https://fleet.example` из PowerShell от имени администратора. Конфигурация и логи находятся в `%ProgramData%\FleetManagerAgent`.

Сервис отправляет POST на `/api/agent/heartbeat`; этот endpoint нужно добавить на сервере Fleet Manager отдельным изменением backend.

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
