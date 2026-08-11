#Requires -Version 5.1
<#
.SYNOPSIS
    Full pipeline: .NET source → published binaries → Inno Setup EXE installer.

.DESCRIPTION
    Steps performed:
      1. Clean publish\win-x64\ to avoid stale binaries.
      2. dotnet publish -c Release -r win-x64 --self-contained for all three projects
         (Service, Tray, Control) into publish\win-x64\.
      3. Locate Inno Setup 6 compiler (ISCC.exe).
      4. Compile installer\FleetManagerAgent.iss into dist\FleetManagerAgent-Setup.exe.

    The resulting EXE embeds all binaries — transfer it alone to any Windows host:
      FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://... /EnrollmentToken=abc123

.PARAMETER AppVersion
    Version string embedded in the installer (default: 1.0).

.PARAMETER SkipPublish
    Skip dotnet publish (use only when the binaries in publish\win-x64\ are already current).

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -AppVersion 1.2
    .\build-installer.ps1 -SkipPublish

.NOTES
    Requires:
      - .NET SDK 8 or later  (dotnet --version)
      - Inno Setup 6 with ISPP  (https://jrsoftware.org/isinfo.php)
    Run from FleetManager-agent\ or from the repository root.
#>
[CmdletBinding()]
param(
    [string] $AppVersion  = '1.0',
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 0. Resolve FleetManager-agent root
# ---------------------------------------------------------------------------
$AgentRoot = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'src')) {
    $PSScriptRoot                                         # invoked from FleetManager-agent\
} elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'FleetManager-agent\src')) {
    Join-Path $PSScriptRoot 'FleetManager-agent'          # invoked from repo root
} else {
    throw 'Run build-installer.ps1 from FleetManager-agent\ or from the repository root.'
}

$PublishOut = Join-Path $AgentRoot 'publish\win-x64'
$IssFile    = Join-Path $AgentRoot 'installer\FleetManagerAgent.iss'
$DistDir    = Join-Path $AgentRoot 'dist'

Write-Host ''
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host "  FleetManager Agent — build installer v$AppVersion"   -ForegroundColor Cyan
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host "  Agent root : $AgentRoot"
Write-Host "  Publish dir: $PublishOut"
Write-Host "  Output dir : $DistDir"
Write-Host ''

# ---------------------------------------------------------------------------
# 1. Clean publish directory (skip if -SkipPublish)
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host '[1/3] Cleaning publish directory ...' -ForegroundColor Cyan
    if (Test-Path -LiteralPath $PublishOut) {
        Remove-Item -LiteralPath $PublishOut -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $PublishOut | Out-Null
} else {
    Write-Host '[1/3] Skipping clean (-SkipPublish)' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 2. dotnet publish — Service, Tray, Control
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host '[2/3] Publishing .NET projects ...' -ForegroundColor Cyan

    $Projects = @(
        'src\FleetManager.Agent.Service\FleetManager.Agent.Service.csproj',
        'src\FleetManager.Agent.Tray\FleetManager.Agent.Tray.csproj',
        'src\FleetManager.Agent.Control\FleetManager.Agent.Control.csproj'
    )

    foreach ($proj in $Projects) {
        $projPath = Join-Path $AgentRoot $proj
        Write-Host "    dotnet publish $proj" -ForegroundColor Gray
        dotnet publish $projPath `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --output $PublishOut `
            /p:PublishSingleFile=false
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $proj (exit $LASTEXITCODE)"
        }
    }

    Write-Host "    Published to: $PublishOut" -ForegroundColor Gray
} else {
    Write-Host '[2/3] Skipping dotnet publish (-SkipPublish)' -ForegroundColor Yellow
}

# Verify binaries exist before compiling the installer
$RequiredBinaries = @(
    'FleetManager.Agent.Service.exe',
    'FleetManager.Agent.Tray.exe',
    'FleetManager.Agent.Control.exe'
)
foreach ($bin in $RequiredBinaries) {
    $binPath = Join-Path $PublishOut $bin
    if (-not (Test-Path -LiteralPath $binPath)) {
        throw "Required binary not found after publish: $binPath"
    }
}

# ---------------------------------------------------------------------------
# 3. Locate Inno Setup 6 compiler (ISCC.exe)
# ---------------------------------------------------------------------------
Write-Host '[3/3] Compiling installer ...' -ForegroundColor Cyan

$IsccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    'C:\Program Files (x86)\Inno Setup 5\ISCC.exe',
    'C:\Program Files\Inno Setup 5\ISCC.exe'
)
$Iscc = $null
foreach ($p in $IsccCandidates) {
    if (Test-Path -LiteralPath $p) { $Iscc = $p; break }
}
if (-not $Iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { $Iscc = $cmd.Source }
}
if (-not $Iscc) {
    throw @'
Inno Setup compiler (ISCC.exe) not found.
Install Inno Setup 6 (with ISPP) from https://jrsoftware.org/isinfo.php then re-run.
'@
}
Write-Host "    Compiler: $Iscc" -ForegroundColor Gray

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
& $Iscc "/DAppVersion=$AppVersion" $IssFile
if ($LASTEXITCODE -ne 0) {
    throw "ISCC compilation failed (exit $LASTEXITCODE)"
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
$Output = Join-Path $DistDir 'FleetManagerAgent-Setup.exe'
if (-not (Test-Path -LiteralPath $Output)) {
    throw "Expected output not found: $Output"
}

$size = [math]::Round((Get-Item $Output).Length / 1MB, 1)
Write-Host ''
Write-Host '======================================================' -ForegroundColor Green
Write-Host "  Installer ready: $Output"                             -ForegroundColor Green
Write-Host "  Size: ${size} MB"                                     -ForegroundColor Green
Write-Host ''
Write-Host '  Silent install:'                                       -ForegroundColor Green
Write-Host '    FleetManagerAgent-Setup.exe /VERYSILENT \'          -ForegroundColor White
Write-Host '      /ServerUrl=http://fleet.example.com \'            -ForegroundColor White
Write-Host '      /EnrollmentToken=your-token'                       -ForegroundColor White
Write-Host '======================================================' -ForegroundColor Green
Write-Host ''
