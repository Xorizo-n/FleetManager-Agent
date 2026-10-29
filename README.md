# FleetManager Agent

Windows-агент для [Fleet Manager](https://github.com/Xorizo-n/FleetManager-Server) — системы управления парком компьютеров университета. Агент ставится на ПК одним EXE-установщиком, регистрируется на сервере по токену и дальше:

- раз в 5 минут присылает **heartbeat** с инвентаризацией: железо, сетевые адаптеры (MAC и IPv4 для Wake-on-LAN, скорость линка), диски и их заполненность, состав рабочего места (плата, BIOS, модули памяти, физические диски, видеокарты, мониторы по EDID, клавиатуры, мыши, USB), установленное ПО, версию агента;
- раз в час собирает **активацию Windows и Office** (состояние лицензий, последнее обращение к KMS, доступность порта KMS, CMID);
- держит рабочим **SSH-канал**, по которому сервер управляет ПК через Ansible: ставит и настраивает OpenSSH Server, ограничивает порт 22 адресом сервера, чинит остановленный sshd, правило брандмауэра, PATH и оболочку;
- закрепляет маршруты до сервера, точки раздачи установщиков и контроллеров домена **мимо полнотуннельного VPN**;
- применяет **обои рабочего стола и экрана блокировки**, назначенные сервером (через локальную групповую политику, с откатом к значениям образа);
- **обновляет сам себя** по предложению сервера, если по SSH до ПК не достучаться;
- готовит ПК к **RDP** и **Wake-on-LAN**;
- показывает пользователю **значок в трее** и даёт администратору панель управления.

## Состав

| Компонент | Роль |
|---|---|
| Служба `FleetManagerAgent` | Работает от `LocalSystem`; вся работа — здесь |
| Tray | Значок в области уведомлений, по одному на сеанс, без прав администратора |
| Control | Панель управления; запускается только через UAC (`requireAdministrator`) |
| Установщик | `FleetManagerAgent-Setup.exe` (Inno Setup) — единственный способ установки и обновления |

Служба, трей и панель общаются только по Named Pipe; трей и панель не читают файлов агента.

## Документация

| Документ | О чём |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Проекты, цикл синхронизации, что собирается, папка данных, `agent.json`, Named Pipe, интерфейс |
| [docs/server-protocol.md](docs/server-protocol.md) | HTTP-контракт с сервером: `register`, `heartbeat` (поля запроса и ответа), самообновление, совместимость версий |
| [docs/ssh-and-connectivity.md](docs/ssh-and-connectivity.md) | Политика порта 22, самолечение sshd, маршруты мимо VPN, обновление без SSH |
| [installer/USAGE.md](installer/USAGE.md) | Параметры установщика, сценарии (установка, обновление, ПК из образа), шаги, журналы, удаление, сборка |
| [docs/operations.md](docs/operations.md) | Сборка, тесты, ветки и выпуск, диагностика, типичные неполадки, памятка разработчику |
| [AGENTS.md](AGENTS.md) | Карта проекта и подробные договорённости для ИИ-агентов и разработчиков |

## Быстрый старт

Токен регистрации выпускает администратор («Администрирование → Агент и токены» в Fleet Manager, `POST /agent/enrollment-tokens`). Установка от администратора:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=https://fleet.example /EnrollmentToken='<токен>' /SshLogin='ДОМЕН\имя'
```

Обновление поверх установленного агента (регистрация сохраняется, `ServerUrl` и токен не нужны):

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

ПК из образа ([AutoDomain](https://github.com/Xorizo-n/AutoDomain)) ставят до перезагрузки, применяющей новое имя:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=https://fleet.example /EnrollmentToken='<токен>' /SshLogin='ДОМЕН\имя' /DeferStart=1 /ResetSshHostKeys=1
```

Все параметры, шаги установки и удаление — в [installer/USAGE.md](installer/USAGE.md). Файлы устанавливаются в `%ProgramFiles%\FleetManager Agent`, конфигурация, состояние и журналы — в `%ProgramData%\FleetManagerAgent` (папка закрыта для обычных пользователей, в ней токен агента).

## Сборка и тесты

Нужны Windows, .NET SDK 8+ и Inno Setup 6 с ISPP.

```powershell
.\build-installer.ps1 -AppVersion 1.2   # publish → Inno Setup → dist\FleetManagerAgent-Setup.exe
dotnet test tests/FleetManager.Agent.Core.Tests/FleetManager.Agent.Core.Tests.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File tests/install_script.tests.ps1
```

Релизы создаёт только CI при слиянии в `main` (версия `<UTC-дата>.<номер запуска>`, например `2026.10.28.28`); сервер сам подтягивает их из GitHub Releases. Подробнее — в [docs/operations.md](docs/operations.md).

## Структура репозитория

```
src/
  FleetManager.Agent.Core      общая библиотека: конфигурация, API, инвентаризация, SSH, маршруты, обои…
  FleetManager.Agent.Service   служба Windows (AgentWorker, ServerRouteKeeper)
  FleetManager.Agent.Ui        общий WinForms-слой Tray и Control в стиле Windows 11
  FleetManager.Agent.Tray      значок в трее
  FleetManager.Agent.Control   панель управления (UAC)
installer/
  FleetManagerAgent.iss        Inno Setup: вся логика установки
  uninstall.ps1                ручное удаление вне пакета
  USAGE.md                     справочник по установщику
tests/
  FleetManager.Agent.Core.Tests  xUnit-тесты Core
  install_script.tests.ps1       статические проверки установщика
docs/                          подробная документация
build-installer.ps1            локальный и CI-конвейер сборки
.github/workflows/             pr-check и build-installer
```

## Связанные репозитории

- [FleetManager-Server](https://github.com/Xorizo-n/FleetManager-Server) — backend и frontend; принимает регистрацию, heartbeat и инвентаризацию (`routers/agent.py`), раздаёт установщик и обои.
- [RTF_OOD_AnsiblePlaybooks](https://github.com/kozlov174/RTF_OOD_AnsiblePlaybooks) — плейбуки, которые сервер запускает на ПК с агентом.
- [AutoDomain](https://github.com/Xorizo-n/AutoDomain) — подготовка ПК из образа; ставит агента сразу после ввода в домен.
