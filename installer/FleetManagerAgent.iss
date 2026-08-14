; FleetManagerAgent.iss — Inno Setup 6 installer for FleetManager Agent (Windows x64)
;
; The compiled EXE is fully self-contained: all agent binaries are embedded inside it.
; Transfer the single EXE to any Windows host and run — no extra files needed.
;
; Silent install:
;   FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://fleet.example.com /EnrollmentToken=abc123
;   FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://... /EnrollmentToken=... /SshLogin=DOMAIN\user /DIR="C:\Custom\Path"
;   FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://... /EnrollmentToken=... /AllowPort22=1
;
; Silent upgrade over an existing installation (this is what the server runs
; remotely — see FleetManager-Server, services/agent_update.py):
;   FleetManagerAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; ServerUrl and EnrollmentToken may be omitted: server address, agent id, agent
; token and SSH key are read from the existing agent.json and written back, so
; the agent keeps its registration instead of enrolling again.
;
; By default only port 5022 (the Ansible management port) is opened; sshd is
; configured to listen solely on 5022 and any pre-existing inbound firewall
; rule that allows port 22 is detected and removed. Pass /AllowPort22=1 to
; skip that check and leave whatever port-22 firewall state already exists.
;
; Silent uninstall (includes remote server cleanup):
;   "%ProgramFiles%\FleetManager Agent\unins000.exe" /VERYSILENT
;
; Build: run build-installer.ps1 (requires .NET SDK 8+ and Inno Setup 6 with ISPP).
; This is the single source of truth for install steps — there is no separate
; install.ps1; the PowerShell below is generated and run in CurStepChanged.

#ifndef AppVersion
  #define AppVersion "1.0"
#endif

#define AppName      "FleetManager Agent"
#define AppPublisher "FleetManager"

[Setup]
AppId={{6B3A8F42-D3E1-4C8B-9F23-A7E5D1B42C87}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\FleetManager Agent
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=FleetManagerAgent-Setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
CloseApplications=yes
CloseApplicationsFilter=FleetManager.Agent.Tray.exe,FleetManager.Agent.Control.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; ALL binaries are compressed and embedded into the setup EXE at compile time.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Code]

// NL() returns CR+LF without using #13/#10 char literals (ISPP misreads those as directives).
function NL: string;
begin
  Result := Chr(13) + Chr(10);
end;

// Minimal JSON escaping for values written into agent.json.
function JsonEscape(const S: string): string;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if      C = '"'       then Result := Result + '\"'
    else if C = '\'       then Result := Result + '\\'
    else if C = Chr(10)   then Result := Result + '\n'
    else if C = Chr(13)   then Result := Result + '\r'
    else if C = Chr(9)    then Result := Result + '\t'
    else                       Result := Result + C;
  end;
end;

// Write a self-contained PowerShell script to a temp file and execute it.
// Using -File avoids the quoting and length limits of -Command "...".
procedure RunPowerShellScript(const Script: string);
var
  TempFile: string;
  ResultCode: Integer;
begin
  TempFile := ExpandConstant('{tmp}') + '\fm-agent-step.ps1';
  SaveStringToFile(TempFile, Script, False);
  Exec(ExpandConstant('{sys}') + '\WindowsPowerShell\v1.0\powershell.exe',
    '-NonInteractive -ExecutionPolicy Bypass -File "' + TempFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DeleteFile(TempFile);
end;

var
  GServerUrl:       string;
  GEnrollmentToken: string;
  GSshLogin:        string;
  GAllowPort22:     Boolean;
  GExistingConfig:  string;   // agent.json предыдущей установки (пусто при первой установке)
  GUpgrade:         Boolean;  // поверх уже зарегистрированного агента

function DataRootDir: string;
begin
  Result := ExpandConstant('{commonappdata}') + '\FleetManagerAgent';
end;

// Читает agent.json предыдущей установки. Пустая строка — файла нет.
function ReadExistingConfig: string;
var
  Raw: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(DataRootDir + '\agent.json', Raw) then
    Result := String(Raw);
end;

// Значение строкового ключа из плоского JSON (agent.json именно такой).
// Поиск ключа регистронезависим: установщик пишет PascalCase, а сам агент
// перезаписывает файл camelCase-именами (JsonSerializerDefaults.Web).
function JsonString(const Json, Key: string): string;
var
  Position, Index: Integer;
  Pattern: string;
  Current: Char;
begin
  Result := '';
  if Json = '' then Exit;

  Pattern := '"' + Key + '"';
  Position := Pos(Lowercase(Pattern), Lowercase(Json));
  if Position = 0 then Exit;

  Index := Position + Length(Pattern);
  while (Index <= Length(Json)) and (Json[Index] <> ':') do Index := Index + 1;
  Index := Index + 1;
  while (Index <= Length(Json)) and ((Json[Index] = ' ') or (Json[Index] = Chr(9))) do Index := Index + 1;
  // null и числа строковым значением не считаем — вызывающий подставит умолчание.
  if (Index > Length(Json)) or (Json[Index] <> '"') then Exit;

  Index := Index + 1;
  while Index <= Length(Json) do
  begin
    Current := Json[Index];
    if (Current = '\') and (Index < Length(Json)) then
    begin
      Current := Json[Index + 1];
      if      Current = 'n' then Result := Result + Chr(10)
      else if Current = 'r' then Result := Result + Chr(13)
      else if Current = 't' then Result := Result + Chr(9)
      else                       Result := Result + Current;
      Index := Index + 2;
      Continue;
    end;
    if Current = '"' then Break;
    Result := Result + Current;
    Index := Index + 1;
  end;
end;

// Целое значение ключа из плоского JSON; Default — если ключа нет или он не число.
function JsonInteger(const Json, Key: string; Default: Integer): Integer;
var
  Position, Index: Integer;
  Pattern, Digits: string;
begin
  Result := Default;
  if Json = '' then Exit;

  Pattern := '"' + Key + '"';
  Position := Pos(Lowercase(Pattern), Lowercase(Json));
  if Position = 0 then Exit;

  Index := Position + Length(Pattern);
  while (Index <= Length(Json)) and (Json[Index] <> ':') do Index := Index + 1;
  Index := Index + 1;
  while (Index <= Length(Json)) and ((Json[Index] = ' ') or (Json[Index] = Chr(9))) do Index := Index + 1;

  Digits := '';
  while (Index <= Length(Json)) and (Json[Index] >= '0') and (Json[Index] <= '9') do
  begin
    Digits := Digits + Json[Index];
    Index := Index + 1;
  end;
  if Digits <> '' then Result := StrToIntDef(Digits, Default);
end;

// Пара "Key":"Value" для agent.json; пустые значения не пишем, чтобы не затирать
// поля, которые агент заполняет сам.
function JsonPair(const Key, Value: string): string;
begin
  if Trim(Value) = '' then
    Result := ''
  else
    Result := '"' + Key + '":"' + JsonEscape(Value) + '",';
end;

function InitializeSetup(): Boolean;
var
  InstallRootParam, AllowPort22Param: string;
begin
  GServerUrl        := Trim(ExpandConstant('{param:ServerUrl|}'));
  GEnrollmentToken  := Trim(ExpandConstant('{param:EnrollmentToken|}'));
  GSshLogin         := Trim(ExpandConstant('{param:SshLogin|}'));

  // Режим обновления: агент уже установлен и зарегистрирован на сервере.
  // Тогда ни ServerUrl, ни EnrollmentToken передавать не нужно — они берутся
  // из agent.json, и регистрация не повторяется (см. удалённое обновление в
  // FleetManager-Server, services/agent_update.py).
  GExistingConfig := ReadExistingConfig;
  if GServerUrl = '' then
    GServerUrl := Trim(JsonString(GExistingConfig, 'ServerUrl'));
  GUpgrade := (Trim(JsonString(GExistingConfig, 'AgentToken')) <> '') and (GServerUrl <> '');

  // /AllowPort22=1 skips the default port-22 firewall check/close step.
  AllowPort22Param := Trim(ExpandConstant('{param:AllowPort22|}'));
  GAllowPort22 := (AllowPort22Param <> '') and (CompareText(AllowPort22Param, '0') <> 0)
    and (CompareText(AllowPort22Param, 'false') <> 0);

  // /InstallRoot=... accepted as an alias for /DIR=...
  InstallRootParam := Trim(ExpandConstant('{param:InstallRoot|}'));
  if InstallRootParam <> '' then
    WizardForm.DirEdit.Text := InstallRootParam;

  if GServerUrl = '' then
  begin
    if not WizardSilent() then
      MsgBox('ServerUrl is required.' + Chr(13) + Chr(10) + 'Example: /ServerUrl=http://fleet.example.com', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if (Pos('http://', GServerUrl) <> 1) and (Pos('https://', GServerUrl) <> 1) then
  begin
    if not WizardSilent() then
      MsgBox('ServerUrl must start with http:// or https://', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if (GEnrollmentToken = '') and (not GUpgrade) then
  begin
    if not WizardSilent() then
      MsgBox('EnrollmentToken is required for a first-time install.' + Chr(13) + Chr(10) + 'Example: /EnrollmentToken=your-token', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  InstallRoot, DataRoot, ServiceExe, TrayExe: string;
  Config, Script, AllowPort22Literal: string;
  UserDomain, UserName, ComputerName: string;
begin
  if CurStep = ssInstall then
  begin
    // Stop the service AND kill Tray/Control BEFORE Setup copies [Files]. All
    // three projects publish into the same output folder (build-installer.ps1),
    // so Tray/Control keep the shared runtime DLLs locked even once the service
    // is stopped — confirmed on a production host where CloseApplicationsFilter
    // left both still running after a failed update. Setup's own Restart-Manager
    // based CloseApplications isn't reliable enough here either (same host), so
    // this kills them directly, the same way uninstall.ps1 already does, instead
    // of depending on it.
    //
    // Without this, an in-place upgrade fails with a fatal "file in use" error
    // (exit code 5) before ever reaching ssPostInstall below, where the service
    // used to get stopped — too late for the copy that already failed. This is
    // what a remote update (FleetManager-Server, services/agent_update.py) always
    // hits, since the very definition of an in-place update is that the old
    // service (and often Tray/Control) is still running when it starts.
    ForceDirectories(DataRootDir + '\logs');
    RunPowerShellScript(
      '$logFile = ''' + DataRootDir + '\logs\install.log''' + NL +
      'function Log { param($m) "$(Get-Date -f ''yyyy-MM-dd HH:mm:ss'')  $m" | Tee-Object -FilePath $logFile -Append | Write-Host }' + NL +
      'if (Get-Service FleetManagerAgent -ErrorAction SilentlyContinue) {' + NL +
      '    Log ''Stopping running service before file copy (in-place upgrade)...''' + NL +
      '    Stop-Service FleetManagerAgent -Force -ErrorAction SilentlyContinue' + NL +
      '}' + NL +
      'foreach ($p in @(''FleetManager.Agent.Tray'', ''FleetManager.Agent.Control'')) {' + NL +
      '    Get-Process -Name $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue' + NL +
      '}' + NL +
      '$deadline = (Get-Date).AddSeconds(15)' + NL +
      'while ((Get-Process -Name ''FleetManager.Agent.Service'',''FleetManager.Agent.Tray'',''FleetManager.Agent.Control'' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }' + NL +
      'Log ''Service and Tray/Control stopped, proceeding with file copy''' + NL);
    Exit;
  end;

  if CurStep <> ssPostInstall then Exit;

  InstallRoot := ExpandConstant('{app}');
  DataRoot    := DataRootDir;
  ServiceExe  := InstallRoot + '\FleetManager.Agent.Service.exe';
  TrayExe     := InstallRoot + '\FleetManager.Agent.Tray.exe';

  // Determine default SSH login: the one from the previous install, otherwise
  // the current interactive domain\user, or local user.
  if GSshLogin = '' then
    GSshLogin := Trim(JsonString(GExistingConfig, 'SshLogin'));
  if GSshLogin = '' then
  begin
    UserDomain   := GetEnv('USERDOMAIN');
    UserName     := GetEnv('USERNAME');
    ComputerName := GetEnv('COMPUTERNAME');
    if (UserDomain <> '') and (CompareText(UserDomain, ComputerName) <> 0) then
      GSshLogin := UserDomain + '\' + UserName
    else
      GSshLogin := UserName;
  end;

  if GAllowPort22 then AllowPort22Literal := 'true' else AllowPort22Literal := 'false';

  // Write agent.json (must happen before the PowerShell script so logs dir exists).
  // On an upgrade the registration (AgentId/AgentToken/SshPublicKey) is carried
  // over: without it the agent would re-register and the server would issue a new
  // token and SSH key on every update.
  ForceDirectories(DataRoot + '\logs');
  Config :=
    '{' +
    JsonPair('ServerUrl',       GServerUrl) +
    JsonPair('EnrollmentToken', GEnrollmentToken) +
    JsonPair('AgentId',         JsonString(GExistingConfig, 'AgentId')) +
    JsonPair('AgentToken',      JsonString(GExistingConfig, 'AgentToken')) +
    JsonPair('SshPublicKey',    JsonString(GExistingConfig, 'SshPublicKey')) +
    JsonPair('SshLogin',        GSshLogin) +
    '"SyncIntervalMinutes":' + IntToStr(JsonInteger(GExistingConfig, 'SyncIntervalMinutes', 5)) +
    '}';
  SaveStringToFile(DataRoot + '\agent.json', Config, False);

  // Build and run the install PowerShell script.
  // Key design decisions:
  //   - Paths assigned to variables at the top to avoid inline quoting issues.
  //   - OpenSSH block wrapped in try/catch: agent must not fail if OpenSSH is
  //     already installed or temporarily unavailable.
  //   - Every step logs to install.log for post-mortem diagnosis.
  //   - sc.exe invoked via '& sc.exe ... "`"$var`"" ' so PowerShell expands
  //     backtick-escaped quotes into real quotes before passing to sc.exe.
  Script :=
    '$ErrorActionPreference = ''Stop''' + NL +
    '$logFile = ''' + DataRoot + '\logs\install.log''' + NL +
    'function Log { param($m) "$(Get-Date -f ''yyyy-MM-dd HH:mm:ss'')  $m" | Tee-Object -FilePath $logFile -Append | Write-Host }' + NL +
    '$serviceExe  = ''' + ServiceExe + '''' + NL +
    '$trayExe     = ''' + TrayExe + '''' + NL +
    '$installRoot = ''' + InstallRoot + '''' + NL +
    '$allowPort22 = $' + AllowPort22Literal + NL +
    'Log ''=== FleetManager Agent install script started ==''' + NL +
    '' + NL +
    '# OpenSSH Server — non-fatal: agent works even if this step is skipped' + NL +
    'try {' + NL +
    '    $s = Get-WindowsCapability -Online -Name OpenSSH.Server*' + NL +
    '    if ($s.State -ne ''Installed'') {' + NL +
    '        Log ''Installing OpenSSH Server...''' + NL +
    '        Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null' + NL +
    '    }' + NL +
    '    # sshd listens ONLY on the Ansible management port 5022 by default.' + NL +
    '    # The rewrite+restart is skipped when sshd already listens on 5022 only:' + NL +
    '    # a remote update runs over that very SSH session, and restarting sshd' + NL +
    '    # would drop it before the installer reports its result.' + NL +
    '    $sshdConfig = ''C:\ProgramData\ssh\sshd_config''' + NL +
    '    $sshdNeedsUpdate = $true' + NL +
    '    if (Test-Path $sshdConfig) {' + NL +
    '        $ports = @(@(Get-Content $sshdConfig) | Where-Object { $_ -match ''^\s*Port\s+\d+\s*$'' })' + NL +
    '        if (($ports.Count -eq 1) -and ($ports[0] -match ''^\s*Port\s+5022\s*$'')) { $sshdNeedsUpdate = $false }' + NL +
    '    }' + NL +
    '    if ($sshdNeedsUpdate) {' + NL +
    '        if (Test-Path $sshdConfig) {' + NL +
    '            $lines = @(Get-Content $sshdConfig) | Where-Object { $_ -notmatch ''^\s*Port\s+\d+\s*$'' }' + NL +
    '            Set-Content $sshdConfig -Value (@(''Port 5022'') + $lines)' + NL +
    '        } else {' + NL +
    '            Set-Content $sshdConfig ''Port 5022''' + NL +
    '        }' + NL +
    '        Restart-Service sshd -Force -ErrorAction SilentlyContinue' + NL +
    '        Log ''sshd reconfigured for port 5022''' + NL +
    '    } else {' + NL +
    '        Log ''sshd already listens on 5022 only; restart skipped''' + NL +
    '    }' + NL +
    '    Set-Service -Name sshd -StartupType Automatic' + NL +
    '    # Firewall: Ansible SSH port 5022 — the only SSH port opened by default' + NL +
    '    if (-not (Get-NetFirewallRule -Name FleetManager-Agent-SSH-5022 -ErrorAction SilentlyContinue)) {' + NL +
    '        New-NetFirewallRule -Name FleetManager-Agent-SSH-5022 -DisplayName ''FleetManager Agent SSH (5022)'' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 5022 | Out-Null' + NL +
    '    }' + NL +
    '    # Port 22: closed by default (removes any pre-existing allow rule, incl.' + NL +
    '    # the OpenSSH capability''s own default rule). Skippable with /AllowPort22=1.' + NL +
    '    if ($allowPort22) {' + NL +
    '        Log ''Port 22 check skipped (/AllowPort22)''' + NL +
    '    } else {' + NL +
    '        $port22Rules = @(Get-NetFirewallRule -Direction Inbound -Action Allow -ErrorAction SilentlyContinue |' + NL +
    '            Where-Object { $_.Enabled -eq ''True'' -and (($_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue).LocalPort -contains ''22'') })' + NL +
    '        if ($port22Rules) {' + NL +
    '            $port22Rules | Remove-NetFirewallRule -ErrorAction SilentlyContinue' + NL +
    '            Log "Port 22 closed ($($port22Rules.Count) firewall rule(s) removed)"' + NL +
    '        } else {' + NL +
    '            Log ''Port 22 already closed''' + NL +
    '        }' + NL +
    '    }' + NL +
    '    Log ''OpenSSH OK (port 5022)''' + NL +
    '} catch {' + NL +
    '    Log "OpenSSH warning (non-fatal): $_"' + NL +
    '}' + NL +
    '' + NL +
    '# Windows service (fatal if it fails)' + NL +
    'try {' + NL +
    '    if (Get-Service FleetManagerAgent -ErrorAction SilentlyContinue) {' + NL +
    '        Log ''Removing previous service instance...''' + NL +
    '        Stop-Service FleetManagerAgent -Force -ErrorAction SilentlyContinue' + NL +
    '        & sc.exe delete FleetManagerAgent | Out-Null' + NL +
    '        Start-Sleep -Milliseconds 500' + NL +
    '    }' + NL +
    '    Log "Creating service: $serviceExe"' + NL +
    '    & sc.exe create FleetManagerAgent binPath= "`"$serviceExe`"" start= auto obj= LocalSystem DisplayName= "FleetManager Agent" | Out-Null' + NL +
    '    & sc.exe description FleetManagerAgent "Fleet Manager inventory and heartbeat agent" | Out-Null' + NL +
    '    & sc.exe start FleetManagerAgent | Out-Null' + NL +
    '    Log ''Service created and started''' + NL +
    '} catch {' + NL +
    '    Log "Service FAILED: $_"' + NL +
    '    throw' + NL +
    '}' + NL +
    '' + NL +
    '# Tray autorun registry key' + NL +
    'try {' + NL +
    '    New-ItemProperty -Path ''HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'' -Name FleetManagerAgentTray -Value "`"$trayExe`"" -PropertyType String -Force | Out-Null' + NL +
    '    Log ''Run key set''' + NL +
    '} catch {' + NL +
    '    Log "Run key warning: $_"' + NL +
    '}' + NL +
    '' + NL +
    '# Launch Tray in the interactive user session (not the elevated installer context)' + NL +
    'try {' + NL +
    '    $sh = New-Object -ComObject Shell.Application' + NL +
    '    $sh.ShellExecute($trayExe, $null, $installRoot, ''open'', 1)' + NL +
    '    Log ''Tray launched''' + NL +
    '} catch {' + NL +
    '    Log "Tray launch warning (starts at next logon): $_"' + NL +
    '}' + NL +
    '' + NL +
    'Log ''=== Install script completed ==''' + NL;

  RunPowerShellScript(Script);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Script, DataRoot: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    DataRoot := ExpandConstant('{commonappdata}') + '\FleetManagerAgent';

    // Stop processes, call remote /api/agent/uninstall, clean SSH key — before files are removed.
    // IMPORTANT: every multi-parameter cmdlet call must be on ONE LINE — a bare newline in a
    // PowerShell script file ends the statement, so splitting Invoke-RestMethod across lines
    // via NL would silently skip -Uri/-Headers/-Body and make an incomplete (failing) request.
    Script :=
      '$logFile = "$env:TEMP\fm-uninstall.log"' + NL +
      'function Log { param($m) "$(Get-Date -f ''yyyy-MM-dd HH:mm:ss'')  $m" | Tee-Object -FilePath $logFile -Append | Write-Host }' + NL +
      '$dataRoot      = ''' + DataRoot + '''' + NL +
      '$configPath    = Join-Path $dataRoot ''agent.json''' + NL +
      '$machineIdPath = Join-Path $dataRoot ''machine-id''' + NL +
      '$authKeys      = ''C:\ProgramData\ssh\administrators_authorized_keys''' + NL +
      'Log ''=== FleetManager Agent uninstall script started ==''' + NL +
      '' + NL +
      '# Stop service' + NL +
      'Stop-Service FleetManagerAgent -Force -ErrorAction SilentlyContinue' + NL +
      'Log ''Service stopped''' + NL +
      '' + NL +
      '# Stop Tray and Control, wait up to 10 s each' + NL +
      'foreach ($p in @(''FleetManager.Agent.Tray'', ''FleetManager.Agent.Control'')) {' + NL +
      '    Get-Process -Name $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue' + NL +
      '    $deadline = (Get-Date).AddSeconds(10)' + NL +
      '    while ((Get-Process -Name $p -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }' + NL +
      '}' + NL +
      '& sc.exe delete FleetManagerAgent | Out-Null' + NL +
      'Log ''Service deleted''' + NL +
      '' + NL +
      '# Remote server cleanup' + NL +
      '$config = $null' + NL +
      'if (Test-Path $configPath) {' + NL +
      '    try { $config = Get-Content $configPath -Raw | ConvertFrom-Json } catch { Log "agent.json parse error: $_" }' + NL +
      '}' + NL +
      'Log "AgentToken present: $(-not [string]::IsNullOrWhiteSpace($config.AgentToken))"' + NL +
      'Log "machine-id present: $(Test-Path $machineIdPath)"' + NL +
      'if ($config -and $config.AgentToken -and (Test-Path $machineIdPath)) {' + NL +
      '    $machineId = (Get-Content $machineIdPath -Raw).Trim()' + NL +
      '    if ($machineId) {' + NL +
      '        try {' + NL +
      '            $headers = @{ Authorization = "Bearer $($config.AgentToken)" }' + NL +
      '            $body    = @{ machine_id = $machineId } | ConvertTo-Json -Compress' + NL +
      '            $resp    = Invoke-RestMethod -Method Post -Uri "$($config.ServerUrl.TrimEnd(''/''))/api/agent/uninstall" -Headers $headers -Body $body -ContentType ''application/json'' -TimeoutSec 15' + NL +
      '            Log "Remote cleanup OK: $($resp.status)"' + NL +
      '        } catch {' + NL +
      '            $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }' + NL +
      '            Log "Remote cleanup FAILED (HTTP $code): $($_.Exception.Message)"' + NL +
      '        }' + NL +
      '    } else { Log ''machine-id is empty'' }' + NL +
      '} else { Log ''Skipping remote cleanup: AgentToken or machine-id missing'' }' + NL +
      '' + NL +
      '# Remove SSH public key from administrators_authorized_keys' + NL +
      'if ($config -and $config.SshPublicKey -and (Test-Path $authKeys)) {' + NL +
      '    $remaining = Get-Content $authKeys | Where-Object { $_.Trim() -ne $config.SshPublicKey.Trim() }' + NL +
      '    Set-Content $authKeys -Value $remaining' + NL +
      '    Log ''SSH key removed from administrators_authorized_keys''' + NL +
      '}' + NL +
      '' + NL +
      '# Remove Tray autorun key' + NL +
      'Remove-ItemProperty -Path ''HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'' -Name FleetManagerAgentTray -ErrorAction SilentlyContinue' + NL +
      'Log ''=== Uninstall script completed ==''' + NL;

    RunPowerShellScript(Script);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Remove data directory after Inno Setup has finished deleting installed files
    DataRoot := ExpandConstant('{commonappdata}') + '\FleetManagerAgent';
    DelTree(DataRoot, True, True, True);
  end;
end;
