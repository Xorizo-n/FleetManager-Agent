; FleetManagerAgent.iss — Inno Setup 6 installer for FleetManager Agent (Windows x64)
;
; The compiled EXE is fully self-contained: all agent binaries are embedded inside it.
; Transfer the single EXE to any Windows host and run — no extra files needed.
;
; Silent install:
;   FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://fleet.example.com /EnrollmentToken=abc123
;   FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://... /EnrollmentToken=... /SshLogin=DOMAIN\user /DIR="C:\Custom\Path"
;   FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=http://... /EnrollmentToken=... /SshSourceAddress=10.40.240.154,10.40.0.0/24
;
; Silent upgrade over an existing installation (this is what the server runs
; remotely — see FleetManager-Server, services/agent_update.py):
;   FleetManagerAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; ServerUrl and EnrollmentToken may be omitted: server address, agent id, agent
; token and SSH key are read from the existing agent.json and written back, so
; the agent keeps its registration instead of enrolling again.
;
; SSH (Ansible management channel) listens on port 22 and answers only the
; Fleet Manager server: one firewall rule allows TCP 22 from the source address
; only, every other inbound allow rule for port 22 / sshd.exe is disabled, and
; sshd_config denies logins from any other address. The source is
; /SshSourceAddress (IPv4 or CIDR, comma-separated), else the value kept in
; agent.json, else the address(es) of the ServerUrl host.
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

// Single-quoted PowerShell string literal.
function PsLiteral(const S: string): string;
var
  Escaped: string;
begin
  Escaped := S;
  StringChangeEx(Escaped, '''', '''''', True);
  Result := '''' + Escaped + '''';
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
  GSshSource:       string;   // откуда разрешён SSH; пусто — адрес хоста из ServerUrl
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
  InstallRootParam: string;
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

  // /SshSourceAddress=... — с каких адресов разрешён SSH (IPv4 или CIDR через
  // запятую). Без параметра берётся значение прошлой установки (его же писал
  // старый install.ps1), а если нет и его — скрипт установки разрешит адрес
  // хоста из ServerUrl.
  GSshSource := Trim(ExpandConstant('{param:SshSourceAddress|}'));
  if GSshSource = '' then
    GSshSource := Trim(JsonString(GExistingConfig, 'SshSourceAddress'));

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
  Config, Script: string;
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
    JsonPair('SshSourceAddress', GSshSource) +
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
    '$dataRoot    = ''' + DataRoot + '''' + NL +
    '$serverUrl   = ' + PsLiteral(GServerUrl) + NL +
    '$sshSourceParam = ' + PsLiteral(GSshSource) + NL +
    '$sshdRestartNeeded = $false' + NL +
    'Log ''=== FleetManager Agent install script started ==''' + NL +
    '' + NL +
    '# OpenSSH Server on port 22, answering only the Fleet Manager server.' + NL +
    '# Non-fatal: the agent itself works even if this step fails.' + NL +
    'try {' + NL +
    '    # Allowed source: /SshSourceAddress (or agent.json), else the ServerUrl host.' + NL +
    '    # Nothing is touched when it cannot be determined: better unmanaged than' + NL +
    '    # SSH open to everyone.' + NL +
    '    $sshSource = @($sshSourceParam -split '','' | ForEach-Object { $_.Trim() } | Where-Object { $_ })' + NL +
    '    if (-not $sshSource) {' + NL +
    '        $serverHost = ([Uri]$serverUrl).Host' + NL +
    '        $parsed = $null' + NL +
    '        if ([System.Net.IPAddress]::TryParse($serverHost, [ref]$parsed)) {' + NL +
    '            $sshSource = @($parsed.IPAddressToString)' + NL +
    '        } else {' + NL +
    '            $sshSource = @([System.Net.Dns]::GetHostAddresses($serverHost) | Where-Object { $_.AddressFamily -eq ''InterNetwork'' } | ForEach-Object { $_.IPAddressToString })' + NL +
    '        }' + NL +
    '    }' + NL +
    '    if (-not $sshSource) { throw "cannot determine the SSH source address from $serverUrl - pass /SshSourceAddress=" }' + NL +
    '    foreach ($entry in $sshSource) {' + NL +
    '        $parts = $entry.Split(''/'', 2); $ip = $null; $prefix = 32' + NL +
    '        $valid = [System.Net.IPAddress]::TryParse($parts[0], [ref]$ip) -and $ip.AddressFamily -eq ''InterNetwork''' + NL +
    '        if ($valid -and $parts.Count -eq 2) { $valid = [int]::TryParse($parts[1], [ref]$prefix) -and $prefix -ge 0 -and $prefix -le 32 }' + NL +
    '        if (-not $valid) { throw "invalid SSH source address: $entry (expected IPv4 or IPv4/CIDR)" }' + NL +
    '    }' + NL +
    '    Log "SSH source restricted to: $($sshSource -join '', '')"' + NL +
    '' + NL +
    '    # Hosts set up by the old install.ps1 run the Win32-OpenSSH MSI build;' + NL +
    '    # adding the Windows capability on top of it would register a second sshd.' + NL +
    '    if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {' + NL +
    '        Log ''Installing OpenSSH Server...''' + NL +
    '        Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null' + NL +
    '    }' + NL +
    '    Set-Service -Name sshd -StartupType Automatic' + NL +
    '    # The first start generates sshd_config and the host keys.' + NL +
    '    if ((Get-Service sshd).Status -ne ''Running'') { Start-Service sshd }' + NL +
    '    $sshdConfig = Join-Path $env:ProgramData ''ssh\sshd_config''' + NL +
    '    for ($i = 0; ($i -lt 20) -and -not (Test-Path $sshdConfig); $i++) { Start-Sleep -Milliseconds 500 }' + NL +
    '    if (-not (Test-Path $sshdConfig)) { throw "sshd_config not found: $sshdConfig" }' + NL +
    '' + NL +
    '    # PowerShell as the SSH default shell: the server drives hosts with' + NL +
    '    # ansible_shell_type=powershell (raw module); under cmd.exe every command fails.' + NL +
    '    if (-not (Test-Path ''HKLM:\SOFTWARE\OpenSSH'')) { New-Item -Path ''HKLM:\SOFTWARE\OpenSSH'' | Out-Null }' + NL +
    '    Set-ItemProperty -Path ''HKLM:\SOFTWARE\OpenSSH'' -Name DefaultShell -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"' + NL +
    '    Set-ItemProperty -Path ''HKLM:\SOFTWARE\OpenSSH'' -Name DefaultShellCommandOption -Value ''-c''' + NL +
    '' + NL +
    '    # sshd_config: port 22 only (older builds of this installer moved it to' + NL +
    '    # 5022), and logins from anywhere but $sshSource are denied - this holds' + NL +
    '    # even where Group Policy overrides the local firewall rules below.' + NL +
    '    $original = [System.IO.File]::ReadAllText($sshdConfig)' + NL +
    '    $lines = @($original -split "`r?`n")' + NL +
    '    if (@($lines | Where-Object { $_ -match ''^\s*Port\s+\d+\s*$'' -and $_ -notmatch ''^\s*Port\s+22\s*$'' }).Count -gt 0) {' + NL +
    '        $lines = @(''Port 22'') + @($lines | Where-Object { $_ -notmatch ''^\s*Port\s+\d+\s*$'' })' + NL +
    '        Log ''sshd: port moved to 22''' + NL +
    '    }' + NL +
    '    $begin = ''# BEGIN FleetManager SSH source restriction''' + NL +
    '    $end = ''# END FleetManager SSH source restriction''' + NL +
    '    $kept = New-Object System.Collections.Generic.List[string]' + NL +
    '    $inside = $false' + NL +
    '    foreach ($line in $lines) {' + NL +
    '        if ($line.Trim() -eq $begin) { $inside = $true; continue }' + NL +
    '        if ($line.Trim() -eq $end) { $inside = $false; continue }' + NL +
    '        if (-not $inside) { $kept.Add($line) }' + NL +
    '    }' + NL +
    '    while (($kept.Count -gt 0) -and [string]::IsNullOrWhiteSpace($kept[$kept.Count - 1])) { $kept.RemoveAt($kept.Count - 1) }' + NL +
    '    $matchList = (@(''*'') + @($sshSource | ForEach-Object { "!$_" })) -join '',''' + NL +
    '    foreach ($line in @('''', $begin, "Match Address $matchList", ''    DenyUsers *'', $end)) { $kept.Add($line) }' + NL +
    '    $updated = ($kept -join "`r`n") + "`r`n"' + NL +
    '    if ($updated -ne $original) {' + NL +
    '        $utf8 = New-Object System.Text.UTF8Encoding($false)' + NL +
    '        Copy-Item $sshdConfig "$sshdConfig.fleetmanager.bak" -Force' + NL +
    '        [System.IO.File]::WriteAllText($sshdConfig, $updated, $utf8)' + NL +
    '        try {' + NL +
    '            $svcPath = (Get-CimInstance Win32_Service -Filter "Name=''sshd''").PathName' + NL +
    '            $sshdExe = if ($svcPath -match ''^\s*"([^"]+)"'') { $Matches[1] } else { ($svcPath -replace ''\s+-.*$'', '''').Trim() }' + NL +
    '            if (-not $sshdExe -or -not (Test-Path $sshdExe)) { throw "sshd.exe not found (service path: $svcPath)" }' + NL +
    '            $ErrorActionPreference = ''Continue''' + NL +
    '            $check = & $sshdExe -t -f $sshdConfig 2>&1 | Out-String' + NL +
    '            $checkCode = $LASTEXITCODE' + NL +
    '            $ErrorActionPreference = ''Stop''' + NL +
    '            if ($checkCode -ne 0) { throw "sshd -t exit code $checkCode : $check" }' + NL +
    '        } catch {' + NL +
    '            $ErrorActionPreference = ''Stop''' + NL +
    '            [System.IO.File]::WriteAllText($sshdConfig, $original, $utf8)' + NL +
    '            throw "new sshd_config rejected, previous config restored: $_"' + NL +
    '        }' + NL +
    '        $sshdRestartNeeded = $true' + NL +
    '        Log ''sshd_config updated (sshd restarts at the end of the install)''' + NL +
    '    } else {' + NL +
    '        Log ''sshd_config already up to date''' + NL +
    '    }' + NL +
    '' + NL +
    '    # Firewall: one allow rule for TCP 22 limited to $sshSource. Windows admits' + NL +
    '    # a connection if ANY enabled allow rule matches, so every other inbound' + NL +
    '    # allow rule for port 22 or sshd.exe (e.g. the OpenSSH-Server-In-TCP rule' + NL +
    '    # open to everyone) is disabled - otherwise the restriction does nothing,' + NL +
    '    # which is exactly what hosts set up by the old install.ps1 ended up with.' + NL +
    '    $ruleName = ''FleetManager-Agent-SSH''' + NL +
    '    $ruleTitle = ''FleetManager Agent SSH (22, Fleet Manager server only)''' + NL +
    '    if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) {' + NL +
    '        Set-NetFirewallRule -Name $ruleName -NewDisplayName $ruleTitle -Enabled True -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 22 -RemoteAddress $sshSource' + NL +
    '    } else {' + NL +
    '        New-NetFirewallRule -Name $ruleName -DisplayName $ruleTitle -Enabled True -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 22 -RemoteAddress $sshSource | Out-Null' + NL +
    '    }' + NL +
    '    Remove-NetFirewallRule -Name FleetManager-Agent-SSH-5022 -ErrorAction SilentlyContinue' + NL +
    '    $byPort = @(Get-NetFirewallPortFilter -All | Where-Object { $_.Protocol -eq ''TCP'' -and @($_.LocalPort) -contains ''22'' } | Get-NetFirewallRule)' + NL +
    '    $byProgram = @(Get-NetFirewallApplicationFilter -All | Where-Object { $_.Program -match ''\\sshd\.exe$'' } | Get-NetFirewallRule)' + NL +
    '    $foreign = @(@($byPort) + @($byProgram) | Where-Object { $_ -and $_.Name -ne $ruleName -and $_.Direction -eq ''Inbound'' -and $_.Action -eq ''Allow'' -and $_.Enabled -eq ''True'' } | Sort-Object Name -Unique)' + NL +
    '    foreach ($rule in $foreign) {' + NL +
    '        Disable-NetFirewallRule -Name $rule.Name' + NL +
    '        Log "Firewall rule disabled (let SSH in from anywhere): $($rule.Name) / $($rule.DisplayName)"' + NL +
    '    }' + NL +
    '    $gpoOpen = @(Get-NetFirewallRule -PolicyStore RSOP -Direction Inbound -Action Allow -Enabled True -ErrorAction SilentlyContinue | Where-Object { @(($_ | Get-NetFirewallPortFilter).LocalPort) -contains ''22'' })' + NL +
    '    if ($gpoOpen) { Log "WARNING: Group Policy opens port 22 ($(@($gpoOpen | ForEach-Object { $_.DisplayName }) -join ''; '')); only the sshd_config restriction applies" }' + NL +
    '    $ignoredOn = @(Get-NetFirewallProfile -PolicyStore ActiveStore | Where-Object { $_.Enabled -eq ''True'' -and $_.AllowLocalFirewallRules -eq ''False'' } | ForEach-Object { $_.Name })' + NL +
    '    if ($ignoredOn) { Log "WARNING: Group Policy ignores local firewall rules on $($ignoredOn -join '', ''); inbound SSH follows the GPO, which needs: TCP 22 from $($sshSource -join '','')" }' + NL +
    '    Log ''OpenSSH OK (port 22, restricted source)''' + NL +
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
    '# Launch Tray in the interactive user session (not the elevated installer context),' + NL +
    '# detached via cmd /c start rather than ShellExecute so it does not inherit this' + NL +
    '# script''s own stdio handles: on a remote update this whole install runs over a' + NL +
    '# non-interactive SSH exec channel, and Tray keeps running indefinitely, so if it' + NL +
    '# inherits that channel''s stdout/stderr pipe, sshd never sees EOF and the SSH' + NL +
    '# client (Ansible on the FleetManager server) hangs waiting for the command to' + NL +
    '# return even though installation already finished — confirmed on a production' + NL +
    '# host where the service was already running the new version while the update' + NL +
    '# task was still stuck "running" minutes later. Start-Process -RedirectStandard*' + NL +
    '# was tried first but rejects NUL (relative NUL 404s; \\.\NUL collides because' + NL +
    '# stdout/stderr can''t redirect to the same resolved path) — cmd /c start sidesteps' + NL +
    '# all of that by detaching the child the same way any other backgrounded cmd job does.' + NL +
    'try {' + NL +
    '    Start-Process -FilePath ''cmd.exe'' -WorkingDirectory $installRoot -WindowStyle Hidden ' +
             '-ArgumentList "/c start `"`" /B `"$trayExe`""' + NL +
    '    Log ''Tray launched''' + NL +
    '} catch {' + NL +
    '    Log "Tray launch warning (starts at next logon): $_"' + NL +
    '}' + NL +
    '' + NL +
    '# sshd reads sshd_config only when it starts. A remote update runs this very' + NL +
    '# script over an SSH session of that sshd, and restarting it there could kill' + NL +
    '# the installer before it finishes, so inside an SSH session the restart is' + NL +
    '# deferred to a one-shot scheduled task that removes itself.' + NL +
    'if ($sshdRestartNeeded) {' + NL +
    '    try {' + NL +
    '        if ($env:SSH_CONNECTION -or $env:SSH_CLIENT) {' + NL +
    '            $taskName = ''FleetManager-Agent-sshd-restart''' + NL +
    '            $restartScript = Join-Path $dataRoot ''sshd-restart.ps1''' + NL +
    '            Set-Content -Path $restartScript -Value "Restart-Service sshd -Force; Unregister-ScheduledTask -TaskName $taskName -Confirm:`$false; Remove-Item -LiteralPath `$PSCommandPath -Force"' + NL +
    '            $action = New-ScheduledTaskAction -Execute ''powershell.exe'' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$restartScript`""' + NL +
    '            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2)' + NL +
    '            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -User ''SYSTEM'' -RunLevel Highest -Force | Out-Null' + NL +
    '            Log ''sshd restart scheduled in 2 minutes (install runs inside an SSH session)''' + NL +
    '        } else {' + NL +
    '            Restart-Service sshd -Force' + NL +
    '            Log ''sshd restarted''' + NL +
    '        }' + NL +
    '    } catch {' + NL +
    '        Log "sshd restart warning (new config applies at the next sshd start): $_"' + NL +
    '    }' + NL +
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
