$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $PSScriptRoot '..\installer\install.ps1'
if (Test-Path -LiteralPath $installRoot) {
    throw 'install.ps1 must stay removed; install logic lives only in FleetManagerAgent.iss.'
}

$iss = Get-Content (Join-Path $PSScriptRoot '..\installer\FleetManagerAgent.iss') -Raw
$uninstaller = Get-Content (Join-Path $PSScriptRoot '..\installer\uninstall.ps1') -Raw
$worker = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Service\AgentWorker.cs') -Raw
$apiClient = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Core\FleetManagerApiClient.cs') -Raw

if ($iss -notmatch 'if GEnrollmentToken = ''''[\s\S]{0,200}Result := False') {
    throw 'Installer must require EnrollmentToken.'
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
Write-Output 'INSTALL_SCRIPT_TESTS_OK'
