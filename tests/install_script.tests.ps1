$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $PSScriptRoot '..\installer\install.ps1'
if (Test-Path -LiteralPath $installRoot) {
    throw 'install.ps1 must stay removed; install logic lives only in FleetManagerAgent.iss.'
}

$iss = Get-Content (Join-Path $PSScriptRoot '..\installer\FleetManagerAgent.iss') -Raw
$uninstaller = Get-Content (Join-Path $PSScriptRoot '..\installer\uninstall.ps1') -Raw
$worker = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Service\AgentWorker.cs') -Raw
$apiClient = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Core\FleetManagerApiClient.cs') -Raw

if ($iss -notmatch '\(GEnrollmentToken = ''''\) and \(not GUpgrade\)[\s\S]{0,300}Result := False') {
    throw 'Installer must require EnrollmentToken for a first-time install.'
}

# Обновление поверх установленного агента: регистрация переносится из старого
# agent.json, иначе сервер выдавал бы новый токен и SSH-ключ при каждом апдейте.
foreach ($field in @('AgentId', 'AgentToken', 'SshPublicKey')) {
    if ($iss -notmatch "JsonPair\('$field',") {
        throw "Installer must preserve $field from the existing agent.json on upgrade."
    }
}
if ($iss -notmatch 'Lowercase\(Pattern\), Lowercase\(Json\)') {
    throw 'Installer must read agent.json case-insensitively (the agent rewrites it in camelCase).'
}
if ($iss -notmatch 'FleetManager\.Agent\.Tray\.exe') {
    throw 'Installer must reference the tray executable.'
}
if ($iss -notmatch '\{param:SshLogin\|\}') {
    throw 'Installer must allow configuring the SSH login.'
}

# Политика SSH: порт 22, отвечает только серверу Fleet Manager. Все рабочие
# хосты в проде доступны именно по 22 (правило брандмауэра с адресом сервера),
# а 5022 не доходил ни до одного.
$sshBlock = [regex]::Match($iss, '# OpenSSH Server on port 22[\s\S]*?# Windows service').Value
if (-not $sshBlock) {
    throw 'Installer must contain the OpenSSH block before the Windows service block.'
}
if (-not $iss.Contains('{param:SshSourceAddress|}') -or $iss -notmatch "JsonPair\('SshSourceAddress',") {
    throw 'Installer must accept /SshSourceAddress and keep it in agent.json for upgrades.'
}
if (-not $sshBlock.Contains('([Uri]$serverUrl).Host')) {
    throw 'Without /SshSourceAddress the allowed SSH source must default to the ServerUrl host.'
}
if ($sshBlock -notmatch 'New-NetFirewallRule -Name \$ruleName [^'']*-LocalPort 22 -RemoteAddress \$sshSource' -or
    $sshBlock -notmatch 'Set-NetFirewallRule -Name \$ruleName [^'']*-LocalPort 22 -RemoteAddress \$sshSource') {
    throw 'The FleetManager SSH firewall rule must allow TCP 22 only from the SSH source address.'
}
# Windows пропускает соединение, если совпало ЛЮБОЕ разрешающее правило: старый
# install.ps1 добавлял правило с адресом сервера, но оставлял OpenSSH-Server-In-TCP
# (открыт всем) — проверено с рабочей станции, порт 22 отвечал всем.
if (-not $sshBlock.Contains('Disable-NetFirewallRule') -or -not $sshBlock.Contains('-contains ''''22''''')) {
    throw 'Installer must disable every other inbound allow rule for port 22, otherwise the source restriction has no effect.'
}
if ($iss.Contains('New-NetFirewallRule -Name FleetManager-Agent-SSH-5022') -or $iss.Contains('{param:AllowPort22|}')) {
    throw 'Port 5022 and /AllowPort22 are retired: SSH runs on 22 restricted to the server.'
}
if (-not $sshBlock.Contains('Match Address $matchList') -or -not $sshBlock.Contains('DenyUsers *')) {
    throw 'sshd_config must deny logins from other addresses (holds even when GPO overrides local firewall rules).'
}
if (-not $sshBlock.Contains('-t -f $sshdConfig') -or -not $sshBlock.Contains('WriteAllText($sshdConfig, $original')) {
    throw 'A rewritten sshd_config must be validated with sshd -t and restored if rejected.'
}
if (-not $sshBlock.Contains('DefaultShell') -or -not $sshBlock.Contains('WindowsPowerShell\v1.0\powershell.exe')) {
    throw 'Installer must make PowerShell the OpenSSH default shell: the server uses ansible_shell_type=powershell.'
}
if ($sshBlock.Contains('Get-WindowsCapability') -or -not $sshBlock.Contains('if (-not (Get-Service sshd')) {
    throw 'OpenSSH capability must be installed only when no sshd service exists (old hosts run the MSI build).'
}
# 0x800f0954 (WSUS без источника компонентов) на SU5-H205-W12/W14: без запасного MSI
# ПК оставался без SSH. Сбой Add-WindowsCapability не должен обрывать блок до MSI.
if ($sshBlock -notmatch 'try \{ Add-WindowsCapability[^\r\n]*\} catch \{') {
    throw 'A failed Add-WindowsCapability must be caught so the bundled MSI can be tried.'
}
if (-not $sshBlock.Contains('openssh\OpenSSH-Win64.msi') -or -not $sshBlock.Contains('ADDLOCAL=Server') -or -not $sshBlock.Contains('/qn')) {
    throw 'Installer must fall back to the bundled OpenSSH MSI (server feature only, silent).'
}
if ($iss -notmatch 'Source: "\.\.\\publish\\openssh\\OpenSSH-Win64\.msi"; DestDir: "\{app\}\\openssh"') {
    throw 'The OpenSSH MSI must be packed into the setup as {app}\openssh\OpenSSH-Win64.msi.'
}
$build = Get-Content -Raw (Join-Path $PSScriptRoot '..\build-installer.ps1')
if ($build -notmatch '\$OpenSshSha256\s*=\s*''[0-9a-f]{64}''' -or -not $build.Contains('Get-AuthenticodeSignature')) {
    throw 'build-installer.ps1 must pin the OpenSSH MSI by SHA-256 and check its Microsoft signature.'
}
# Удалённое обновление идёт по SSH-сессии этого же sshd — перезапуск посреди
# установки мог бы убить установщик. Поэтому sshd перезапускается только в
# конце, а внутри SSH-сессии — отложенно, через одноразовую задачу планировщика.
if ($sshBlock.Contains('Restart-Service sshd')) {
    throw 'sshd must not be restarted inside the OpenSSH block, only at the end of the install.'
}
if ($iss -notmatch 'if \(\$sshdRestartNeeded\)[\s\S]{0,300}SSH_CONNECTION[\s\S]{0,1500}Register-ScheduledTask[\s\S]{0,300}Restart-Service sshd -Force') {
    throw 'Inside an SSH session the sshd restart must be deferred to a scheduled task.'
}
if ($iss -notmatch 'if \(\$sshdRestartNeeded\)[\s\S]*Install script completed') {
    throw 'The sshd restart must run after the service and tray steps.'
}
$options = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Core\AgentOptions.cs') -Raw
if ($options -notmatch 'public string\? SshSourceAddress') {
    throw 'AgentOptions must keep SshSourceAddress, or the service drops it when it rewrites agent.json.'
}

# Подготовка ПК из образа (AutoDomain): агент ставится до перезагрузки, которая
# применяет новое имя, и должен зарегистрироваться только после неё.
if (-not $iss.Contains('ParamFlag(''DeferStart'')') -or -not $iss.Contains('ParamFlag(''ResetSshHostKeys'')')) {
    throw 'Installer must accept /DeferStart and /ResetSshHostKeys.'
}
if ($iss -notmatch 'if \(\$deferStart\) \{ ''''delayed-auto'''' \}' -or
    $iss -notmatch 'if \(\$deferStart\) \{[^}]*starts after the next reboot[^}]*\} else \{[\s\S]{0,700}?& sc\.exe start FleetManagerAgent') {
    throw '/DeferStart must create the service with delayed auto-start and must not start it now.'
}
if ($iss -notmatch 'if \(\$deferStart\) \{\s*''\s*\+\s*NL\s*\+\s*''\s*Log ''''Tray not launched') {
    throw '/DeferStart must not launch the tray either.'
}
# Клоны образа не должны делить machine-id: сервер находит хост прежде всего по нему.
if ($iss -notmatch 'if not GUpgrade then\s+DeleteFile\(DataRoot \+ ''\\machine-id''\)') {
    throw 'A fresh install (no AgentToken) must drop a leftover machine-id.'
}
$resetBlock = [regex]::Match($iss, 'if \(\$resetSshHostKeys\) \{[\s\S]*?Log "SSH host key reset warning').Value
if (-not $resetBlock -or $resetBlock -notmatch 'SSH_CONNECTION[\s\S]*throw' -or -not $resetBlock.Contains('ssh_host_*') -or -not $resetBlock.Contains('ssh-keygen.exe')) {
    throw '/ResetSshHostKeys must refuse inside an SSH session, delete ssh_host_* and fall back to ssh-keygen -A.'
}
$worker = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Service\AgentWorker.cs') -Raw
if ($worker -notmatch 'ComputerRename\.IsPending\(\)[\s\S]{0,400}return false') {
    throw 'The agent must not register while a computer rename is pending a reboot.'
}

# Файлы [Files] копируются Inno Setup на шаге ssInstall, до ssPostInstall — если
# служба останавливается только в ssPostInstall, in-place обновление падает с
# "fatal error during installation" (exit code 5), потому что сама служба ещё
# держит FleetManager.Agent.Service.exe открытым, а CloseApplicationsFilter
# покрывает только Tray/Control, не сервис.
if ($iss -notmatch 'if CurStep = ssInstall[\s\S]{0,1900}Stop-Service FleetManagerAgent') {
    throw 'Installer must stop the service at ssInstall, before Files are copied — CloseApplicationsFilter does not cover the service exe.'
}
# CloseApplicationsFilter одного Tray/Control недостаточно: подтверждено на
# продакшн-хосте, где оба процесса остались запущены после проваленного
# обновления и держали открытыми общие DLL рантайма (все три проекта
# публикуются в одну и ту же папку) — файлы всё равно не копировались.
# ssInstall должен убивать их напрямую, как уже делает uninstall.ps1.
if ($iss -notmatch 'if CurStep = ssInstall[\s\S]{0,1900}Stop-Process -Force[\s\S]{0,200}FleetManager\.Agent\.Tray[\s\S]{0,200}FleetManager\.Agent\.Control') {
    throw 'Installer must kill Tray/Control at ssInstall too, not just rely on CloseApplicationsFilter — it was observed to leave both running after a failed update.'
}
if ($iss -notmatch 'if CurStep = ssInstall[\s\S]{0,2400}Exit;\s*end;[\s\S]{0,50}if CurStep <> ssPostInstall') {
    throw 'The ssInstall handler must Exit before falling through to the ssPostInstall logic.'
}

# Tray продолжает работать бессрочно; если он унаследует stdout/stderr SSH-канала,
# на котором выполняется удалённое обновление, sshd не увидит EOF и клиент (Ansible
# на сервере) зависнет в ожидании завершения команды даже после успешной установки —
# воспроизведено на продакшн-хосте: служба уже работала на новой версии, пока
# задача обновления всё ещё висела "running".
if (-not $iss.Contains('/c start `"`" /B `"$trayExe`""')) {
    throw 'Tray must be launched via cmd /c start (detached, non-inherited stdio) — otherwise a remote update over SSH hangs waiting for Tray (which never exits) to release the channel.'
}
# Удалённое обновление запускает установщик через WMI из SSH-сессии, то есть в
# служебном сеансе 0: трей, запущенный «в своём контексте», оказывался там, а у
# вошедших пользователей пропадал до следующего входа (SU5-VM-SBX, SU5-K104-W09),
# и открытый рабочий стол не получал обои. Трей запускается в каждом сеансе
# вошедшего пользователя разовой задачей планировщика с группой Users.
$trayBlock = [regex]::Match($iss, '# Launch Tray in every logged-on user session[\s\S]*?# /ResetSshHostKeys').Value
if (-not $trayBlock) {
    throw 'Installer must contain the tray launch block before the /ResetSshHostKeys block.'
}
if ($trayBlock -notmatch 'Get-Process -Name explorer[^\r\n]*SessionId -ne 0') {
    throw 'The tray must be launched only for logged-on user sessions (explorer), never for session 0.'
}
if ($trayBlock -notmatch 'New-ScheduledTaskPrincipal -GroupId ''''S-1-5-32-545'''' -RunLevel Limited' -or
    $trayBlock -notmatch 'Start-ScheduledTask -TaskName \$trayTask' -or
    $trayBlock -notmatch 'finally \{[\s\S]{0,200}Unregister-ScheduledTask -TaskName \$trayTask') {
    throw 'The tray must be started in user sessions by a one-off Users-group scheduled task that is deleted afterwards.'
}
if ($trayBlock -notmatch 'New-ScheduledTaskSettingsSet[^\r\n]*-AllowStartIfOnBatteries[^\r\n]*-ExecutionTimeLimit \(\[TimeSpan\]::Zero\)[^\r\n]*-MultipleInstances Parallel') {
    throw 'The tray task must start on battery, have no time limit and run one instance per session.'
}
if ($trayBlock -notmatch '\(Get-Process -Id \$PID\)\.SessionId -ne 0\) \{[\s\S]{0,300}/c start') {
    throw 'The cmd /c start fallback must never launch the tray in session 0.'
}
if ($iss.Contains('$sh = New-Object -ComObject Shell.Application')) {
    throw 'Tray must not be launched via ShellExecute COM — its stdio inherits this script''s handles, which is what caused the hang cmd /c start fixes.'
}

if ($uninstaller -notmatch "@\('FleetManager\.Agent\.Tray',\s*'FleetManager\.Agent\.Control'\)[\s\S]{0,80}Get-Process") {
    throw 'Uninstaller must stop the tray process before deleting files.'
}
if ($uninstaller -notmatch '/api/agent/uninstall') {
    throw 'Uninstaller must call the remote cleanup endpoint.'
}
if ($uninstaller -notmatch 'throw\s+"Remote agent cleanup failed') {
    throw 'Uninstaller must fail instead of silently ignoring remote cleanup errors.'
}
if ($worker -notmatch 'EnsureSshKeyInstalled') {
    throw 'Agent worker must retry installation of the server SSH key.'
}
$sshInstaller = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Core\WindowsSshKeyInstaller.cs') -Raw
if ($sshInstaller -notmatch 'ExitCode\s*!=\s*0') {
    throw 'SSH key installer must surface icacls failures.'
}
if ($apiClient -notmatch 'ssh_login') {
    throw 'Agent registration must send the configured SSH login.'
}
if ($apiClient -notmatch 'SendHeartbeatAsync[\s\S]{0,1200}var payload = new\s*\{[\s\S]{0,900}ssh_login') {
    throw 'Agent heartbeat must send the configured SSH login for existing registrations.'
}
if ($apiClient -notmatch 'agent_version = AgentVersion\.Current[\s\S]*agent_version = AgentVersion\.Current') {
    throw 'Agent must report its version in both register and heartbeat.'
}

# Версия агента должна попадать в сборку — иначе сервер увидит только 1.0.0.
$buildScript = Get-Content (Join-Path $PSScriptRoot '..\build-installer.ps1') -Raw
if ($buildScript -notmatch '/p:Version=\$AppVersion') {
    throw 'build-installer.ps1 must stamp the assemblies with AppVersion.'
}
if ($buildScript -notmatch '/p:InformationalVersion=\$AppVersion') {
    throw 'build-installer.ps1 must stamp InformationalVersion so the exact version string survives.'
}
# Удалённый рабочий стол: включается при установке (кнопка RDP в карточке хоста),
# с NLA, через встроенную группу правил брандмауэра по идентификатору ресурса (имя
# локализовано); /EnableRdp=0 шаг пропускает.
$rdpBlock = [regex]::Match($iss, '# Remote Desktop for administrators[\s\S]*?# Windows service').Value
if (-not $rdpBlock) {
    throw 'Installer must contain the Remote Desktop block before the Windows service block.'
}
if ($iss -notmatch "ParamFlagDefaultOn\('EnableRdp'\)") {
    throw 'Remote Desktop must be on by default with /EnableRdp=0 as the opt-out.'
}
foreach ($needle in @('fDenyTSConnections -Value 0', 'UserAuthentication -Value 1', "Get-NetFirewallRule -Group ''@FirewallAPI.dll,-28752''")) {
    if (-not $rdpBlock.Contains($needle)) {
        throw "Remote Desktop block must contain: $needle"
    }
}
# TermService, запущенная при выключенном RDP, не открывает 3389 до перезапуска;
# перезапускать можно, только когда за ПК никто не работает.
if (-not $rdpBlock.Contains('Restart-Service TermService') -or -not $rdpBlock.Contains('quser')) {
    throw 'Remote Desktop block must restart TermService when nothing listens on 3389, but only with no users logged on.'
}
if ($rdpBlock -notmatch "try \{[\s\S]*catch \{[\s\S]*Remote Desktop warning \(non-fatal\)") {
    throw 'The Remote Desktop step must be non-fatal.'
}

# Пробуждение по сети (кнопка «Разбудить» на сервере): проводные адаптеры будят ПК,
# быстрый запуск выключен; адаптер не перезапускается — это оборвало бы SSH-сессию
# удалённого обновления; /EnableWol=0 шаг пропускает.
$wolBlock = [regex]::Match($iss, '# Wake-on-LAN for the server[\s\S]*?# Windows service').Value
if (-not $wolBlock) {
    throw 'Installer must contain the Wake-on-LAN block before the Windows service block.'
}
if ($iss -notmatch "ParamFlagDefaultOn\('EnableWol'\)") {
    throw 'Wake-on-LAN must be prepared by default with /EnableWol=0 as the opt-out.'
}
foreach ($needle in @('MSPower_DeviceEnable', 'MSPower_DeviceWakeEnable', 'HiberbootEnabled -Value 0', "PhysicalMediaType -eq ''802.3''")) {
    if (-not $wolBlock.Contains($needle)) {
        throw "Wake-on-LAN block must contain: $needle"
    }
}
foreach ($line in ($wolBlock -split "`n" | Where-Object { $_ -match 'Set-NetAdapter(PowerManagement|AdvancedProperty) ' })) {
    if ($line -notmatch '-NoRestart') {
        throw "Wake-on-LAN must not reset the network adapter (missing -NoRestart): $line"
    }
}
if ($wolBlock -notmatch "try \{[\s\S]*catch \{[\s\S]*Wake-on-LAN warning \(non-fatal\)") {
    throw 'The Wake-on-LAN step must be non-fatal.'
}
if ($apiClient -notmatch 'network_adapters\s*=') {
    throw 'Heartbeat must send the network adapters (MAC for Wake-on-LAN).'
}
if ($apiClient -notmatch 'link_speed_bps\s*=' -or $apiClient -notmatch 'disks\s*=') {
    throw 'Heartbeat must send the adapter link speed and the disk usage.'
}
if ($apiClient -notmatch 'components\s*=\s*ComponentsPayload') {
    throw 'Heartbeat must send the workplace components (monitors, keyboards, mice, USB).'
}
if ($apiClient -notmatch 'activation\s*=\s*ActivationPayload') {
    throw 'Heartbeat must send the Windows/Office activation status.'
}

# Служба поднимается сама: перезапуск после сбоя и повторный запуск после
# загрузки — первый старт после установки с /DeferStart не укладывался в 30 с
# SCM (7009/7000 на SU5-F*), а действия при сбое на таймаут запуска не срабатывают.
$serviceBlock = [regex]::Match($iss, '# Windows service \(fatal if it fails\)[\s\S]*?# Tray autorun registry key').Value
if (-not $serviceBlock) {
    throw 'Installer must contain the Windows service block before the tray autorun block.'
}
foreach ($needle in @('sc.exe failure FleetManagerAgent reset= 86400 actions= restart/', 'sc.exe failureflag FleetManagerAgent 1')) {
    if (-not $serviceBlock.Contains($needle)) {
        throw "Service block must set recovery actions: $needle"
    }
}
# Установка/обновление тоже не укладывалось в 30 с (Smart App Control) и оставляло
# службу остановленной до перезагрузки — запуск повторяется.
if ($serviceBlock -notmatch 'for \(\$attempt = 1; \$attempt -le 3[\s\S]{0,200}sc\.exe start FleetManagerAgent[\s\S]{0,400}StartPending') {
    throw 'Installer must retry the service start when the first attempt times out.'
}
$retryBlock = [regex]::Match($serviceBlock, '# Service start retry after boot[\s\S]*').Value
if (-not $retryBlock) {
    throw 'Installer must register the service start retry task after creating the service.'
}
foreach ($needle in @('New-ScheduledTaskTrigger -AtStartup', "-Argument ''start FleetManagerAgent''", "-UserId ''S-1-5-18''", "-TaskName ''FleetManager-Agent-start-retry''")) {
    if (-not $retryBlock.Contains($needle)) {
        throw "Start retry block must contain: $needle"
    }
}
if ($retryBlock -notmatch "try \{[\s\S]*catch \{[\s\S]*Start retry task warning \(non-fatal\)") {
    throw 'The start retry step must be non-fatal.'
}
$uninstallBlock = [regex]::Match($iss, 'FleetManager Agent uninstall script started[\s\S]*Uninstall script completed').Value
foreach ($script in @($uninstallBlock, $uninstaller)) {
    if ($script -notmatch "Unregister-ScheduledTask -TaskName '{1,2}FleetManager-Agent-start-retry'{1,2}") {
        throw 'Uninstall must remove the service start retry task.'
    }
}

# Служба, остановленная позже загрузки (руками или после исчерпанных действий при сбое),
# молчала до следующей перезагрузки: задача-сторож поднимает её каждые 15 минут, но не
# отключённую и не во время обновления; удаление снимает сторожа до остановки службы.
$keeperBlock = [regex]::Match($iss, '# Service keeper[\s\S]*?Service keeper task warning \(non-fatal\)').Value
if (-not $keeperBlock) {
    throw 'Installer must register the service keeper task (non-fatal).'
}
foreach ($needle in @('-RepetitionInterval (New-TimeSpan -Minutes 15)', "-TaskName ''FleetManager-Agent-keeper''", 'FleetManagerAgent-Setup*', '[int]$s.StartType -ne 4', "-UserId ''S-1-5-18''")) {
    if (-not $keeperBlock.Contains($needle)) {
        throw "Service keeper block must contain: $needle"
    }
}
foreach ($script in @($uninstallBlock, $uninstaller)) {
    if ($script -notmatch "Unregister-ScheduledTask -TaskName '{1,2}FleetManager-Agent-keeper'{1,2}[\s\S]*Stop-Service FleetManagerAgent") {
        throw 'Uninstall must remove the keeper task before stopping the service.'
    }
}
if ($iss -notmatch "GEnrollmentToken := Trim\(JsonString\(GExistingConfig, 'EnrollmentToken'\)\)") {
    throw 'An update must keep the enrollment token from agent.json (re-registration after 401).'
}

# Папка данных наследовала права ProgramData: «Пользователи» читали agent.json с
# токеном агента и могли подкладывать файлы, которые служба читает от SYSTEM.
$aclBlock = [regex]::Match($iss, '// Data folder access[\s\S]*?Data folder permissions warning').Value
if (-not $aclBlock) {
    throw 'Installer must restrict the data folder permissions.'
}
if ($iss -notmatch 'if CurStep = ssInstall[\s\S]*?RestrictDataFolder;\s*Exit;\s*end;[\s\S]{0,50}if CurStep <> ssPostInstall') {
    throw 'Data folder permissions must be restricted at ssInstall, before agent.json with the tokens is written.'
}
foreach ($needle in @(
    "/inheritance:r /grant:r ''*S-1-5-18:(OI)(CI)F'' /grant:r ''*S-1-5-32-544:(OI)(CI)F''",
    "/setowner ''*S-1-5-32-544'' /T",
    "& icacls.exe `$dataRoot /reset",
    "& icacls.exe (Join-Path `$dataRoot ''*'') /reset /T",
    "/grant ''*S-1-5-32-545:(OI)(CI)RX''")) {
    if (-not $aclBlock.Contains($needle)) {
        throw "Data folder permissions block must contain: $needle"
    }
}
# Ссылки уходят до icacls /T: иначе он (и служба от SYSTEM) пошёл бы по ним за пределы папки.
if ($aclBlock -notmatch 'ReparsePoint[\s\S]*Remove-PlantedLinks \$dataRoot[\s\S]*/setowner') {
    throw 'Links planted in the data folder must be removed before icacls /T runs.'
}
if (([regex]::Matches($aclBlock, 'S-1-5-32-545')).Count -ne 1) {
    throw 'Users must get exactly one grant: read on the wallpapers folder.'
}
if ($aclBlock -notmatch "\`$wallpapers = Join-Path \`$dataRoot ''wallpapers''[\s\S]*& icacls.exe \`$wallpapers /grant ''\*S-1-5-32-545") {
    throw 'Only the wallpapers folder may be readable by Users (Explorer opens the wallpaper in the user session).'
}
if ($aclBlock -notmatch 'try \{[\s\S]*\} catch \{') {
    throw 'The data folder permissions step must be non-fatal (the service re-checks them at start).'
}
# Служба проверяет права при каждом старте: агенты, поставленные раньше, остались с правами ProgramData.
$serviceProgram = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Service\Program.cs') -Raw
if ($serviceProgram -notmatch 'DataFolderSecurity\.Repair\(AgentConfiguration\.DefaultDataDirectory\)[\s\S]*Host\.CreateApplicationBuilder') {
    throw 'The service must repair the data folder permissions before anything opens the folder.'
}
# Трей не читает файлы из папки данных — только Named Pipe.
$tray = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Tray\Program.cs') -Raw
if ($tray -match 'AgentOptions\.Load|AgentConfiguration\.(ConfigurationFile|LogFile|MachineIdentityFile|DefaultDataDirectory)|MachineIdentity\.|new AgentLogger') {
    throw 'The tray runs unelevated and cannot read the data folder: get the data over the named pipe.'
}

Write-Output 'INSTALL_SCRIPT_TESTS_OK'
