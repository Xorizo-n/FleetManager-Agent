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

# Port policy: only 5022 opened by default, port 22 detected and closed unless
# explicitly overridden via /AllowPort22.
if (-not $iss.Contains('FleetManager-Agent-SSH-5022')) {
    throw 'Installer must open the Ansible SSH port (5022).'
}
if ($iss.Contains('New-NetFirewallRule -Name OpenSSH-Server-In-TCP')) {
    throw 'Installer must not unconditionally open port 22 by default.'
}
if (-not $iss.Contains('{param:AllowPort22|}')) {
    throw 'Installer must support an /AllowPort22 flag to skip closing port 22.'
}
if (-not $iss.Contains('Remove-NetFirewallRule')) {
    throw 'Installer must close any pre-existing port 22 firewall rule by default.'
}
if (-not $iss.Contains("Set-Content `$sshdConfig ''Port 5022''")) {
    throw 'Installer must configure sshd to listen on port 5022 only for a fresh sshd_config.'
}
# Удалённое обновление идёт по этой же SSH-сессии — перезапуск sshd на уже
# настроенном хосте оборвал бы её до того, как установщик отчитается.
if (-not $iss.Contains('$sshdNeedsUpdate')) {
    throw 'Installer must skip the sshd rewrite/restart when sshd already listens on 5022 only.'
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
