[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ServerUrl,
    [string] $InstallRoot = "$env:ProgramFiles\FleetManager Agent",
    [string] $PackageRoot = "$PSScriptRoot\..\publish\win-x64",
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string] $EnrollmentToken,
    [string] $SshLogin
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run installer from an elevated PowerShell session.'
}

$ServerUrl = $ServerUrl.TrimEnd('/')
$EnrollmentToken = $EnrollmentToken.Trim()
$defaultSshLogin = if (-not [string]::IsNullOrWhiteSpace($env:USERDOMAIN) -and $env:USERDOMAIN -ne $env:COMPUTERNAME) { "$env:USERDOMAIN\$env:USERNAME" } else { $env:USERNAME }
if ([string]::IsNullOrWhiteSpace($SshLogin)) { $SshLogin = $defaultSshLogin }
$SshLogin = $SshLogin.Trim()
if ($ServerUrl -notmatch '^https?://') { throw 'ServerUrl must start with http:// or https://' }
if ([string]::IsNullOrWhiteSpace($EnrollmentToken)) { throw 'EnrollmentToken must not be empty.' }
if ([string]::IsNullOrWhiteSpace($SshLogin)) { throw 'SshLogin must not be empty.' }
if (-not (Test-Path -LiteralPath $PackageRoot)) { throw "Publish directory not found: $PackageRoot" }

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
Copy-Item -Path (Join-Path $PackageRoot '*') -Destination $InstallRoot -Recurse -Force
$dataRoot = Join-Path $env:ProgramData 'FleetManagerAgent'
New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot 'logs') | Out-Null
$config = @{ ServerUrl = $ServerUrl; EnrollmentToken = $EnrollmentToken; SshLogin = $SshLogin; SyncIntervalMinutes = 5 } | ConvertTo-Json
Set-Content -LiteralPath (Join-Path $dataRoot 'agent.json') -Value $config -Encoding UTF8

$ssh = Get-WindowsCapability -Online -Name 'OpenSSH.Server*'
if ($ssh.State -ne 'Installed') {
    Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
}
Set-Service -Name sshd -StartupType Automatic
Start-Service sshd
if (-not (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
}

$serviceExe = Join-Path $InstallRoot 'FleetManager.Agent.Service.exe'
if (Get-Service -Name FleetManagerAgent -ErrorAction SilentlyContinue) {
    Stop-Service FleetManagerAgent -Force -ErrorAction SilentlyContinue
    & sc.exe delete FleetManagerAgent | Out-Null
    Start-Sleep -Milliseconds 500
}
& sc.exe create FleetManagerAgent binPath= "`"$serviceExe`"" start= auto obj= LocalSystem DisplayName= "FleetManager Agent" | Out-Null
& sc.exe description FleetManagerAgent "Fleet Manager inventory and heartbeat agent" | Out-Null
Start-Service FleetManagerAgent

$runKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$trayExe = Join-Path $InstallRoot 'FleetManager.Agent.Tray.exe'
$quotedTrayExe = '"' + $trayExe + '"'
New-ItemProperty -Path $runKey -Name FleetManagerAgentTray -Value $quotedTrayExe -PropertyType String -Force | Out-Null
try {
    # ShellExecute through the existing Explorer process starts tray in the
    # interactive user's context instead of inheriting the installer's UAC token.
    $shell = New-Object -ComObject Shell.Application
    $shell.ShellExecute($trayExe, $null, $InstallRoot, 'open', 1)
} catch {
    Write-Warning "Tray will start at next logon: $($_.Exception.Message)"
}
Write-Host 'FleetManager Agent installed. Service and OpenSSH Server are running.'
