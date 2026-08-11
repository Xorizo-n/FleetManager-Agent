[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallRoot = "$env:ProgramFiles\FleetManager Agent",
    [switch] $SkipRemoteCleanup
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run uninstall from an elevated PowerShell session.'
}

$dataRoot = Join-Path $env:ProgramData 'FleetManagerAgent'
$configPath = Join-Path $dataRoot 'agent.json'
$machineIdPath = Join-Path $dataRoot 'machine-id'
$config = $null
if (Test-Path -LiteralPath $configPath) {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
}

if (Get-Service FleetManagerAgent -ErrorAction SilentlyContinue) {
    Stop-Service FleetManagerAgent -Force -ErrorAction SilentlyContinue
    & sc.exe delete FleetManagerAgent | Out-Null
}

# The tray and control panel are independent per-user processes that otherwise
# keep the published directory locked. Stop every instance before removing files.
foreach ($procName in @('FleetManager.Agent.Tray', 'FleetManager.Agent.Control')) {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Name $procName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process -Name $procName -ErrorAction SilentlyContinue) {
        throw "$procName is still running. Close it and run uninstall again."
    }
}

$remoteCleanupCompleted = $false
if (-not $SkipRemoteCleanup) {
    if ($null -eq $config) { throw "Agent configuration not found: $configPath" }
    $machineId = if (Test-Path -LiteralPath $machineIdPath) { (Get-Content -LiteralPath $machineIdPath -Raw).Trim() } else { $null }
    if ([string]::IsNullOrWhiteSpace($config.ServerUrl) -or [string]::IsNullOrWhiteSpace($config.AgentToken) -or [string]::IsNullOrWhiteSpace($machineId)) {
        throw 'Remote cleanup requires ServerUrl, AgentToken and machine-id in the agent data directory. Use -SkipRemoteCleanup only for an intentional local-only removal.'
    }
    try {
        $headers = @{ Authorization = "Bearer $($config.AgentToken)" }
        $body = @{ machine_id = $machineId } | ConvertTo-Json -Compress
        $response = Invoke-RestMethod -Method Post -Uri "$($config.ServerUrl.TrimEnd('/'))/api/agent/uninstall" -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 15
        if ($response.status -ne 'uninstalled') { throw "Unexpected cleanup response status: $($response.status)" }
        $remoteCleanupCompleted = $true
        Write-Host 'Remote agent cleanup completed.'
    } catch {
        $httpStatus = $null
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) { $httpStatus = [int]$_.Exception.Response.StatusCode }
        $suffix = if ($httpStatus) { "HTTP $httpStatus" } else { 'no HTTP status' }
        throw "Remote agent cleanup failed ($suffix): $($_.Exception.Message)"
    }
}

if ($config -and $config.SshPublicKey) {
    $authorizedKeys = 'C:\ProgramData\ssh\administrators_authorized_keys'
    if (Test-Path -LiteralPath $authorizedKeys) {
        $remaining = Get-Content -LiteralPath $authorizedKeys | Where-Object { $_.Trim() -ne $config.SshPublicKey.Trim() }
        Set-Content -LiteralPath $authorizedKeys -Value $remaining
    }
}

Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name FleetManagerAgentTray -ErrorAction SilentlyContinue
if ($PSCmdlet.ShouldProcess($InstallRoot, 'Remove FleetManager Agent files')) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force -ErrorAction Stop
}
if ($remoteCleanupCompleted -or $SkipRemoteCleanup) {
    Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'FleetManager Agent removed. OpenSSH Server was intentionally left enabled for Ansible.'
