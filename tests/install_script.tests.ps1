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
Write-Output 'INSTALL_SCRIPT_TESTS_OK'
