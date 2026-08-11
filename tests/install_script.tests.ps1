$ErrorActionPreference = 'Stop'
$installer = Get-Content (Join-Path $PSScriptRoot '..\installer\install.ps1') -Raw
$uninstaller = Get-Content (Join-Path $PSScriptRoot '..\installer\uninstall.ps1') -Raw
$worker = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Service\AgentWorker.cs') -Raw
$apiClient = Get-Content (Join-Path $PSScriptRoot '..\src\FleetManager.Agent.Core\FleetManagerApiClient.cs') -Raw

if ($installer -notmatch '\[Parameter\(Mandatory\s*=\s*\$true\)\][\s\S]{0,120}\[string\]\s*\$EnrollmentToken') {
    throw 'Installer must require EnrollmentToken.'
}
if ($installer -notmatch '\$quotedTrayExe\s*=\s*''"''\s*\+\s*\$trayExe\s*\+\s*''"''') {
    throw 'Installer must quote the tray executable path in the Run key.'
}
if ($installer -notmatch 'FleetManager\.Agent\.Tray\.exe') {
    throw 'Installer must reference the tray executable.'
}
if ($uninstaller -notmatch 'Get-Process\s+.*FleetManager\.Agent\.Tray') {
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
if ($installer -notmatch '\[string\]\s*\$SshLogin') {
    throw 'Installer must allow configuring the SSH login.'
}
if ($apiClient -notmatch 'ssh_login') {
    throw 'Agent registration must send the configured SSH login.'
}
if ($apiClient -notmatch 'SendHeartbeatAsync[\s\S]{0,1200}var payload = new\s*\{[\s\S]{0,900}ssh_login') {
    throw 'Agent heartbeat must send the configured SSH login for existing registrations.'
}
Write-Output 'INSTALL_SCRIPT_TESTS_OK'
