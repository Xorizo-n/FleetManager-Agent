# Протокол обмена с сервером

Агент ходит на [FleetManager-Server](https://github.com/Xorizo-n/FleetManager-Server) по HTTP(S): базовый адрес — `ServerUrl` из `agent.json`, префикс API — `/api/agent` (`AgentConfiguration.DefaultApiPath`). Реализация на стороне сервера — `backend/routers/agent.py`, схемы — `backend/schemas/agent.py`. Агенты ходят на порт **8080** (HTTP): `HttpClient` и `Invoke-WebRequest` при удалённом обновлении теряют заголовок `Authorization` при перенаправлении, поэтому перенаправлять агентов на HTTPS нельзя.

Аутентификация — заголовок `Authorization: Bearer <AgentToken>` (кроме `register`, где передаётся токен регистрации в теле). Все тела — JSON, поля в `snake_case`.

## Эндпоинты, которые использует агент

| Метод и путь | Кто вызывает | Назначение |
|---|---|---|
| `POST /register` | служба | Регистрация по токену регистрации. |
| `POST /heartbeat` | служба | Периодический отчёт: железо, ПО, активация, обои, SSH. Ответ несёт задания серверу → агенту. |
| `POST /alerts` | служба | Алерт `hardware_changed` при смене отпечатка железа. |
| `POST /offline` | служба при остановке | Сервер сразу помечает ПК выключенным. |
| `POST /uninstall` | деинсталлятор, `uninstall.ps1` | Сервер удаляет агентскую учётку и (для хоста, созданного агентом) сам хост. |
| `GET /installer` | служба (самообновление) | Установщик последней версии, скачивается токеном агента. |
| `GET /wallpapers/{id}` | служба | Картинка каталога обоев, токеном агента. |

Остальные эндпоинты сервера (токены регистрации, версии, обновление, статус) — для администраторов и интерфейса, агент их не вызывает.

## `POST /register`

Запрос:

```json
{
  "enrollment_token": "…",
  "machine_id": "GUID из machine-id",
  "hostname": "Environment.MachineName",
  "ssh_login": "ДОМЕН\\пользователь",
  "ip_address": null,
  "os": "windows_11 | windows_10",
  "agent_version": "2026.10.28.28"
}
```

Ответ: `agent_id`, `agent_token`, `host_id`, `hostname`, `ip_address`, `ssh_public_key`, `ssh_login`. Агент сохраняет всё в `agent.json`, стирает `EnrollmentToken`, ставит `ssh_public_key` в `C:\ProgramData\ssh\administrators_authorized_keys`. Приватный ключ остаётся у сервера в зашифрованном хранилище ключей.

Что делает сервер:

- токен регистрации неверный, отключён или истёк → `401`;
- `machine_id` уже принадлежит хосту, который ещё присылает heartbeat под другим именем (клон образа без сброса идентификатора) → `409` с подсказкой удалить `C:\ProgramData\FleetManagerAgent\machine-id` и перезапустить службу;
- хост без агента с тем же именем или адресом привязывается к этому агенту;
- чистая переустановка занимает старую запись того же имени, если от неё больше 15 минут нет heartbeat (история, группа, комментарий и задачи остаются при ПК, ключи хоста и ожидающее самообновление сбрасываются);
- по имени ПК создаётся группа в дереве (корпус → этаж → аудитория).

## `POST /heartbeat`

Верхний уровень тела:

| Поле | Содержимое |
|---|---|
| `machine_id`, `hostname`, `ssh_login`, `ip_address` (всегда `null`: адрес сервер берёт из `network_adapters`, а не из источника запроса, — иначе при VPN он был бы из пула VPN), `os`, `status: "online"`, `agent_version` | идентификация |
| `hardware` | железо (ниже) |
| `software[]` | `name`, `version`, `publisher`, `source` (`registry`, `appx`, `chocolatey`, `winget`) |
| `activation` | `collected_at`, `client_machine_id`, `kms_probe{host,port,reachable,error}`, `products[]` (`kind`, `id`, `name`, `description`, `channel`, `partial_product_key`, `license_status`, `status_reason`, `grace_minutes`, `kms_machine`, `kms_port`, `discovered_kms_machine`, `discovered_kms_port`, `last_request{at,kms,result}`); `null`, если не собралась |
| `wallpaper` | `desktop`/`lock_screen` (`path`, `sha256`, `source`: `policy`/`csp`/`user`), `applied_desktop_id`, `applied_lock_screen_id`, `error`, `thumbnails[]` (`sha256`, `jpeg_base64`) |
| `ssh` | состояние SSH (ниже) |

`hardware`: `manufacturer`, `model`, `serial_number`, `operating_system`, `processor`, `total_memory_bytes`, `fingerprint`, а также

- `network_adapters[]`: `name`, `description`, `mac_address`, `ipv4[]` (`10.152.5.56/25`), `status`, `media_type` (`802.3` — проводной), `link_speed_bps`;
- `disks[]`: `name` (`C:`), `label`, `file_system`, `size_bytes`, `free_bytes`;
- `components`: `baseboard`, `bios_version`, `memory_modules[]`, `physical_disks[]`, `gpus[]`, `monitors[]`, `input_devices[]`, `usb_devices[]`.

`ssh`: `installed`, `start_type` (`auto`/`manual`/`disabled`/`null`), `listening`, `responding`, `install_error`, `repaired[]`, `firewall_rule` (`ok`/`missing`/`disabled`/`mismatch`/`unknown`), `firewall_ignored_on[]`, `firewall_gpo_allows`, `firewall_blocking[]`, `firewall_error`, `network_profiles[]`, `config_valid`, `config_port`, `source_restricted`, `default_shell_ok`, `path_ok`, `login_is_admin`, `domain_trust` (`ok`/`broken`/`unreachable`/`null`), `host_keys[]`.

Старые агенты присылают лишь часть полей, поэтому на сервере всё необязательно: отсутствие данных не затирает сохранённое (так, список дисков от старого агента не стирает известный).

Что делает сервер с отчётом: обновляет статус, `last_seen_at`, IP (приоритет у адреса физического адаптера), версию агента; сравнивает железо с прошлым отчётом **той же версии агента** (после смены версии снимок принимается заново, другая версия могла собирать поля иначе) и пишет журнал и алерты; считает заполнение дисков (алерты от порога); применяет правила тегов; хранит `license_activation`, `wallpaper_state`, `ssh_state`; обновляет ПО диффом (без перезаписи всего списка); отпускает ожидающие задачи ПК — кроме случая, когда агент сам сообщает, что SSH закрыт.

### Ответ

```json
{
  "status": "accepted",
  "host_id": "…",
  "last_seen_at": "…",
  "software_count": 123,
  "wallpaper": { "desktop": {…}, "lock_screen": {…}, "thumbnails": ["sha256", …] },
  "agent_update": { "version": "2026.10.28.28", "sha256": "…64 hex…", "repair": false, "url": "https://…" },
  "pin_routes": [ { "host": "10.152.240.10", "port": 8088 } ]
}
```

Всё, кроме первых четырёх полей, необязательно. Агент разбирает ответ терпимо: неверная форма поля означает «сервер ничего не просит».

- **`wallpaper`** — назначенные обои. Объект отсутствует → сервер о них не сообщил (старая версия), агент ничего не меняет. `desktop` или `lock_screen` отсутствует/`null` → сервер этим не управляет, агент возвращает значение образа. Элемент: `id`, `sha256` (64 hex), `style` (`WallpaperStyle`, 1–2 цифры, по умолчанию `4` — заполнение), `file_name`. `thumbnails` — до 8 хэшей обоев, миниатюры которых сервер хочет получить в следующем heartbeat.
- **`agent_update`** — предложение обновиться без SSH. `version` — числовая версия, `sha256` — хэш установщика. `repair: true` — переустановить даже ту же версию (установщик доставит то, чего на ПК не хватает: OpenSSH Server). `url` — подписанная ссылка на тот же установщик на точке раздачи (абсолютный `http`/`https`).
- **`pin_routes`** — до 4 хостов с портом, до которых нужно сохранить маршрут мимо VPN (сейчас — точка раздачи). Отсутствие поля снимает маршруты.

## Самообновление

Агент ставит предложение, только если выполнено всё:

1. версия предложения новее текущей, либо она равна текущей и `repair: true` (старее — никогда);
2. для этой версии было меньше трёх попыток (`MaxAttempts`) и последняя была больше часа назад (`RetryInterval`); счётчик в `self-update.json` растёт до загрузки, чтобы зависший установщик или установщик, убивающий службу, не повторялся бесконечно;
3. сначала качается `url` (если он есть); при любой ошибке или несовпадении SHA-256 установщик берётся с сервера — `GET /api/agent/installer` токеном агента (таймаут 15 минут). Ссылка точки раздачи токен не получает (таймаут 5 минут): права даёт сама подписанная ссылка, а токен не должен уходить на другой сервер;
4. SHA-256 скачанного файла совпал с объявленным сервером; иначе файл удаляется и установка не запускается.

Установщик запускается как `FleetManagerAgent-Setup-<версия>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG="…\updates\install-<версия>.log"` в режиме обновления: `agent.json` переносится, перерегистрации нет. Установщик останавливает службу, процесс самообновления при этом выживает.

Сервер предлагает обновление в двух случаях:

- задача «Обновить агент» не достучалась до ПК по SSH — запоминается цель (`hosts.agent_update_target`) и предложение приходит в следующем heartbeat;
- отчёт `ssh` говорит, что SSH настроен не установщиком (OpenSSH Server нет или в `sshd_config` нет блока «только адрес сервера») — предлагается ремонт, то есть та же версия с `repair: true`.

Агенты старше `2026.10.24.24` предложение не понимают — на них нужна одна установка на месте.

## Совместимость версий

Контракт расширяется только необязательными полями: новый агент работает со старым сервером (лишние поля игнорируются), старый агент — с новым (недостающие поля не затирают сохранённое). Известные границы:

| Версия агента | Что появилось |
|---|---|
| 2026.10.10.10 | Название ОС в отпечатке железа не зависит от языка интерфейса; до этой версии смена языка давала ложный алерт `hardware_changed` (сервер теперь принимает такой алерт, но не сохраняет) |
| 2026.10.15 | `hardware.disks`, `link_speed_bps` |
| 2026.10.24 | Отчёт `ssh`: служба sshd |
| 2026.10.24.24 | Остальные поля отчёта `ssh`, самообновление по ответу на heartbeat (`SELF_UPDATE_SINCE` на сервере) |

Сервер ожидает от `ssh` ещё необязательное поле `local_admin` (`ok`/`missing`/`disabled`/`not_admin`) — состояние локального администратора для запасного входа (`services/local_admin.py`). Агент его пока не присылает (серверная часть ещё в разработке, см. [ssh-and-connectivity.md](ssh-and-connectivity.md#запасной-вход-под-локальным-администратором-в-разработке-на-сервере)).

## Что видно на стороне сервера

- Реестр хостов: колонка «Агент» (версия), фильтр «Версия устарела»; «Администрирование → Агент и токены» — токены регистрации и доступная версия.
- Установщик сервер сам подтягивает из GitHub Releases этого репозитория (`services/agent_installer_sync.py`, почасово через Celery beat и кнопкой «Проверить обновление агента») в `soft_share_dir`; тег релиза — доступная версия. Копия на точке раздачи — зеркало этой папки.
- Для скачивания пользователем сервер выдаёт обёртку (NSIS) над тем же установщиком: она запускает его молча с `/ServerUrl` и `/EnrollmentToken`; журнал Inno с замаскированным токеном остаётся в `C:\ProgramData\FleetManagerAgent\logs\setup.log`.
