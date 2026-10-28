using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FleetManager.Agent.Core;

/// <summary>
/// Everything on the PC that decides whether the server can manage it over SSH, besides sshd itself.
/// <list type="bullet">
/// <item><see cref="FirewallRule"/>: "ok", "missing", "disabled", "mismatch" (port, profile or source
/// differ from the installer's rule), "unknown" (no source address to compare with) or null — not checked.</item>
/// <item><see cref="FirewallIgnoredOn"/>: profiles where Group Policy ignores local rules — inbound SSH
/// then follows the GPO only, and the agent's rule does nothing.</item>
/// <item><see cref="LoginIsAdmin"/>: the server's SSH login is a local administrator (its key lives in
/// administrators_authorized_keys); null — could not be determined.</item>
/// <item><see cref="DomainTrust"/>: "ok", "broken" (secure channel to the domain fails — logon of domain
/// accounts is refused), "unreachable" (no domain controller) or null — not in a domain / not checked.</item>
/// <item><see cref="LocalAdmin"/>: the local administrator the server falls back to when the domain logon fails
/// (same key, no domain needed): "ok", "missing", "disabled", "not_admin" or null — not checked.</item>
/// </list>
/// A broken trust is first repaired with <c>nltest /sc_reset</c> (a new secure channel, no password needed); when the
/// machine password itself no longer matches, only a domain admin can rejoin the PC — the server then signs in locally.
/// </summary>
public sealed record SshHealthReport
{
    public string? FirewallRule { get; init; }
    public IReadOnlyList<string> FirewallIgnoredOn { get; init; } = Array.Empty<string>();
    public bool? FirewallGpoAllows { get; init; }
    public IReadOnlyList<string> FirewallBlocking { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NetworkProfiles { get; init; } = Array.Empty<string>();
    public string? FirewallError { get; init; }
    public bool? ConfigValid { get; init; }
    public int? ConfigPort { get; init; }
    /// <summary>sshd_config has the installer's "only the Fleet Manager server" block; null — no sshd_config.</summary>
    public bool? SourceRestricted { get; init; }
    /// <summary>SSH sessions start PowerShell (the server drives hosts with ansible_shell_type=powershell); null — no sshd.</summary>
    public bool? DefaultShellOk { get; init; }
    public bool? PathOk { get; init; }
    public bool? LoginIsAdmin { get; init; }
    public string? DomainTrust { get; init; }
    public string? LocalAdmin { get; init; }
    public IReadOnlyList<string> HostKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Repaired { get; init; } = Array.Empty<string>();
}

public sealed class SshHealthChecker
{
    // Logon checks ask a domain controller; they change rarely.
    internal static readonly TimeSpan LogonCheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(90);
    private const string SshDirectory = @"C:\ProgramData\ssh";

    private readonly AgentLogger _logger;
    private DateTimeOffset _logonCheckedAt = DateTimeOffset.MinValue;
    private bool? _loginIsAdmin;
    private string? _domainTrust;
    private string? _localAdmin;

    public SshHealthChecker(AgentLogger logger) => _logger = logger;

    public async Task<SshHealthReport?> CheckAsync(AgentOptions options, bool repair, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var repaired = new List<string>();

        if (repair && MachinePath.Repair(_logger)) repaired.Add("path");
        var pathOk = MachinePath.IsHealthy();

        var logonDue = DateTimeOffset.UtcNow - _logonCheckedAt >= LogonCheckInterval;
        var sources = FirewallSources(options.SshSourceAddress, options.ServerUrl);
        FirewallState? firewall = null;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(ScriptTimeout);
            try
            {
                var output = await WindowsInventoryCollector.RunPowerShellAsync(Script(sources, repair, logonDue ? options.SshLogin : null, logonDue, options.LocalAdminName), timeout.Token);
                firewall = ParseScriptOutput(output);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                _logger.Warn($"Unable to check the SSH firewall rule: {ex.Message}");
            }
        }
        if (firewall?.Repaired is { } was)
        {
            repaired.Add("firewall_rule");
            _logger.Warn($"Firewall rule for SSH from the Fleet Manager server was {was}; restored.");
        }
        if (logonDue && firewall is not null)
        {
            _logonCheckedAt = DateTimeOffset.UtcNow;
            _loginIsAdmin = firewall.LoginIsAdmin;
            _domainTrust = DomainTrustState(firewall.NltestOutput);
            _localAdmin = firewall.LocalAdmin;
            if (repair && _domainTrust == "broken" && !string.IsNullOrWhiteSpace(firewall.Domain))
            {
                _domainTrust = await ResetSecureChannelAsync(firewall.Domain, cancellationToken);
                if (_domainTrust == "ok") repaired.Add("domain_trust");
            }
        }

        var (valid, port, restricted) = CheckConfig();
        bool? shellOk = null;
        if (restricted is not null)
        {
            if (repair && RepairDefaultShell()) repaired.Add("default_shell");
            shellOk = IsPowerShell(ReadDefaultShell());
        }
        return new SshHealthReport
        {
            // Without a source address the rule's scope cannot be compared
            FirewallRule = sources.Count == 0 && firewall?.Rule == "ok" ? "unknown" : firewall?.Rule,
            FirewallIgnoredOn = firewall?.IgnoredOn ?? Array.Empty<string>(),
            FirewallGpoAllows = firewall?.GpoAllows,
            FirewallBlocking = firewall?.Blocking ?? Array.Empty<string>(),
            NetworkProfiles = firewall?.Profiles ?? Array.Empty<string>(),
            FirewallError = firewall?.Error,
            ConfigValid = valid,
            ConfigPort = port,
            SourceRestricted = restricted,
            DefaultShellOk = shellOk,
            PathOk = pathOk,
            LoginIsAdmin = _loginIsAdmin,
            DomainTrust = _domainTrust,
            LocalAdmin = _localAdmin,
            HostKeys = ReadHostKeys(),
            Repaired = repaired,
        };
    }

    /// <summary>
    /// Where SSH is allowed from — the same rule as the installer: /SshSourceAddress kept in agent.json,
    /// else the address of the ServerUrl host. Empty — nothing to compare with, the rule is not touched.
    /// </summary>
    public static IReadOnlyList<string> FirewallSources(string? sshSourceAddress, string? serverUrl)
    {
        var configured = (sshSourceAddress ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsIpv4OrCidr)
            .ToArray();
        if (configured.Length > 0) return configured;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return Array.Empty<string>();
        if (IPAddress.TryParse(uri.Host, out var ip)) return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? new[] { ip.ToString() } : Array.Empty<string>();
        try
        {
            return Dns.GetHostAddresses(uri.Host)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToArray();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    internal static bool IsIpv4OrCidr(string value)
    {
        var parts = value.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        return parts.Length == 1 || (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) && prefix is >= 0 and <= 32);
    }

    internal sealed record FirewallState(
        string? Rule,
        string? Repaired,
        IReadOnlyList<string> IgnoredOn,
        bool? GpoAllows,
        IReadOnlyList<string> Blocking,
        IReadOnlyList<string> Profiles,
        string? Error,
        bool? LoginIsAdmin,
        string? NltestOutput,
        string? LocalAdmin = null,
        string? Domain = null);

    /// <summary>
    /// nltest /sc_reset sets up a new secure channel, possibly with another controller: it fixes a channel stuck on
    /// a controller that lost the PC's password replica. Then the trust is verified again.
    /// </summary>
    private async Task<string?> ResetSecureChannelAsync(string domain, CancellationToken cancellationToken)
    {
        var nltest = Path.Combine(Environment.SystemDirectory, "nltest.exe");
        try
        {
            await RunAsync(nltest, "/sc_reset:" + domain, cancellationToken);
            var state = DomainTrustState(await RunAsync(nltest, "/sc_verify:" + domain, cancellationToken));
            if (state == "ok") _logger.Warn($"Trust with the domain {domain} was broken; nltest /sc_reset restored it.");
            else _logger.Warn($"Trust with the domain {domain} is broken and nltest /sc_reset did not help ({state ?? "no answer"}): the PC has to be rejoined; the server signs in as the local administrator.");
            return state ?? "broken";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"Unable to reset the secure channel with {domain}: {ex.Message}");
            return "broken";
        }
    }

    private static async Task<string> RunAsync(string file, string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException(file + " did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        return await output + await error;
    }

    /// <summary>
    /// Checks the installer's firewall rule (and restores it when <paramref name="repair"/> is set),
    /// what Group Policy does to inbound port 22 and, when asked, the logon prerequisites.
    /// RemoteAddress comes back from Windows as "a.b.c.d/255.255.255.0", hence Norm.
    /// Only single quotes: RunPowerShellAsync passes the script inside -Command "…".
    /// </summary>
    internal static string Script(IReadOnlyList<string> sources, bool repair, string? login, bool checkTrust, string? localAdmin = null)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var list = sources.Count == 0 ? "@()" : "@(" + string.Join(",", sources.Select(Quote)) + ")";
        return string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            $"$sources = {list}",
            $"$repair = {(repair ? "$true" : "$false")}",
            $"$login = {(string.IsNullOrWhiteSpace(login) ? "$null" : Quote(login.Trim()))}",
            $"$checkTrust = {(checkTrust ? "$true" : "$false")}",
            $"$localAdmin = {(string.IsNullOrWhiteSpace(localAdmin) ? "$null" : Quote(localAdmin.Trim()))}",
            "$out = [ordered]@{}",
            "function Norm([string]$a) {",
            "  $p = $a.Trim().Split('/', 2)",
            "  if ($p.Count -eq 1) { return $p[0] + '/32' }",
            "  if ($p[1] -match '^\\d+$') { return $p[0] + '/' + $p[1] }",
            "  $bits = (([System.Net.IPAddress]::Parse($p[1])).GetAddressBytes() | ForEach-Object { [Convert]::ToString($_, 2) }) -join ''",
            "  return $p[0] + '/' + @($bits.ToCharArray() | Where-Object { $_ -eq '1' }).Count",
            "}",
            "try {",
            "  $name = 'FleetManager-Agent-SSH'",
            "  $title = 'FleetManager Agent SSH (22, Fleet Manager server only)'",
            "  $rule = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue",
            "  $state = 'ok'",
            "  if (-not $rule) { $state = 'missing' }",
            "  elseif ([string]$rule.Enabled -ne 'True' -or [string]$rule.Action -ne 'Allow' -or [string]$rule.Direction -ne 'Inbound') { $state = 'disabled' }",
            "  else {",
            "    $port = $rule | Get-NetFirewallPortFilter",
            "    $addr = $rule | Get-NetFirewallAddressFilter",
            "    $have = @(@($addr.RemoteAddress) | ForEach-Object { Norm $_ } | Sort-Object)",
            "    $want = @(@($sources) | ForEach-Object { Norm $_ } | Sort-Object)",
            "    if ([string]$port.Protocol -ne 'TCP' -or @($port.LocalPort) -notcontains '22' -or [string]$rule.Profile -ne 'Any') { $state = 'mismatch' }",
            "    elseif ($want.Count -gt 0 -and (Compare-Object $have $want)) { $state = 'mismatch' }",
            "  }",
            "  if ($repair -and $state -ne 'ok' -and $sources.Count -gt 0) {",
            "    if ($rule) { Set-NetFirewallRule -Name $name -NewDisplayName $title -Enabled True -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 22 -RemoteAddress $sources }",
            "    else { New-NetFirewallRule -Name $name -DisplayName $title -Enabled True -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 22 -RemoteAddress $sources | Out-Null }",
            "    $out.repaired = $state",
            "    $state = 'ok'",
            "  }",
            "  $out.rule = $state",
            "  $out.ignored_on = @(Get-NetFirewallProfile -PolicyStore ActiveStore | Where-Object { [string]$_.Enabled -eq 'True' -and [string]$_.AllowLocalFirewallRules -eq 'False' } | ForEach-Object { [string]$_.Name })",
            "  $port22 = { @($_.LocalPort) -contains '22' }",
            "  $out.gpo_allows = @(Get-NetFirewallPortFilter -All -PolicyStore RSOP -ErrorAction SilentlyContinue | Where-Object $port22 | Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { [string]$_.Direction -eq 'Inbound' -and [string]$_.Action -eq 'Allow' -and [string]$_.Enabled -eq 'True' }).Count -gt 0",
            "  $out.blocking = @(Get-NetFirewallPortFilter -All -PolicyStore ActiveStore | Where-Object $port22 | Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { [string]$_.Direction -eq 'Inbound' -and [string]$_.Action -eq 'Block' -and [string]$_.Enabled -eq 'True' } | ForEach-Object { [string]$_.DisplayName })",
            "  $out.profiles = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.NetworkCategory })",
            "} catch { $out.error = [string]$_.Exception.Message }",
            // S4U logon token: includes nested domain groups and local group membership — what sshd sees
            "if ($login) {",
            "  try {",
            "    $account = New-Object System.Security.Principal.NTAccount($login)",
            "    $sid = $account.Translate([System.Security.Principal.SecurityIdentifier])",
            "    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')",
            "    $group = $admins.Translate([System.Security.Principal.NTAccount]).Value.Split('\\')[-1]",
            "    $members = @(([ADSI]('WinNT://./' + $group + ',group')).psbase.Invoke('Members') | ForEach-Object { (New-Object System.Security.Principal.SecurityIdentifier($_.GetType().InvokeMember('objectSid', 'GetProperty', $null, $_, $null), 0)).Value })",
            "    if ($members -contains $sid.Value) { $out.login_is_admin = $true }",
            // A local account can only be a direct member: local groups do not nest
            "    elseif ($sid.Translate([System.Security.Principal.NTAccount]).Value -like ($env:COMPUTERNAME + '\\*')) { $out.login_is_admin = $false }",
            "    else {",
            "      $upn = ([ADSISearcher]('(objectSid=' + $sid.Value + ')')).FindOne().Properties['userprincipalname'] | Select-Object -First 1",
            "      if ($upn) { $out.login_is_admin = @((New-Object System.Security.Principal.WindowsIdentity([string]$upn)).Groups | ForEach-Object { $_.Value }) -contains 'S-1-5-32-544' }",
            "    }",
            "  } catch { }",
            "}",
            "if ($checkTrust) {",
            "  try {",
            "    $cs = Get-CimInstance Win32_ComputerSystem",
            "    if ($cs.PartOfDomain) { $out.domain = [string]$cs.Domain; $out.nltest = (& (Join-Path $env:SystemRoot 'System32\\nltest.exe') ('/sc_verify:' + $cs.Domain) 2>&1 | Out-String) }",
            "  } catch { }",
            // The fallback login: exists, enabled, a direct member of Administrators (ADSI — Get-LocalGroupMember fails on orphaned SIDs)
            "  if ($localAdmin) {",
            "    try {",
            "      $user = [ADSI]('WinNT://./' + $localAdmin + ',user')",
            "      $flags = [int]$user.UserFlags.Value",
            "      $usid = (New-Object System.Security.Principal.SecurityIdentifier($user.objectSid.Value, 0)).Value",
            "      $adminsGroup = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([System.Security.Principal.NTAccount]).Value.Split('\\')[-1]",
            "      $adminSids = @(([ADSI]('WinNT://./' + $adminsGroup + ',group')).psbase.Invoke('Members') | ForEach-Object { (New-Object System.Security.Principal.SecurityIdentifier($_.GetType().InvokeMember('objectSid', 'GetProperty', $null, $_, $null), 0)).Value })",
            "      if ($flags -band 2) { $out.local_admin = 'disabled' } elseif ($adminSids -contains $usid) { $out.local_admin = 'ok' } else { $out.local_admin = 'not_admin' }",
            "    } catch { if ([string]$_.Exception.Message -match '2221|800708AD|not found|найти') { $out.local_admin = 'missing' } }",
            "  }",
            "}",
            "ConvertTo-Json -Compress -InputObject ([pscustomobject]$out)");
    }

    internal static FirewallState? ParseScriptOutput(string? output)
    {
        var line = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new FirewallState(
                String(root, "rule"),
                String(root, "repaired"),
                Strings(root, "ignored_on"),
                Bool(root, "gpo_allows"),
                Strings(root, "blocking"),
                Strings(root, "profiles"),
                String(root, "error"),
                Bool(root, "login_is_admin"),
                String(root, "nltest"),
                String(root, "local_admin"),
                String(root, "domain"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// nltest /sc_verify prints "Trusted DC Connection Status Status = 1311 0x51f ERROR_NO_LOGON_SERVERS"
    /// and "Trust Verification Status = 0 0x0 NERR_Success". No DC — "unreachable", not a broken trust:
    /// a laptop at home must not be reported as a PC that left the domain.
    /// </summary>
    public static string? DomainTrustState(string? nltestOutput)
    {
        if (string.IsNullOrWhiteSpace(nltestOutput)) return null;
        var connection = StatusCode(nltestOutput, "Connection Status Status");
        var verification = StatusCode(nltestOutput, "Trust Verification Status");
        if (connection is null && verification is null) return null;
        if (connection is 0 && verification is null or 0) return "ok";
        int[] unreachable = { 1311, 1722, 1355, 53, 1727, 1908 };
        if (connection is { } c && unreachable.Contains(c)) return "unreachable";
        return "broken";
    }

    private static int? StatusCode(string text, string label)
    {
        var match = Regex.Match(text, Regex.Escape(label) + @"\s*=\s*(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    /// <summary>"Port 2222" in sshd_config (the last such line wins in sshd); null — default 22 or no file.</summary>
    public static int? ConfiguredPort(IEnumerable<string> configLines)
    {
        int? port = null;
        foreach (var raw in configLines)
        {
            var line = raw.Trim();
            // Settings inside a Match block do not move the listener
            if (line.StartsWith("Match ", StringComparison.OrdinalIgnoreCase)) break;
            var match = Regex.Match(line, @"^Port\s+(\d+)\s*(#.*)?$", RegexOptions.IgnoreCase);
            if (match.Success && port is null) port = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return port;
    }

    /// <summary>"ssh-ed25519 AAAA… comment" → "ssh-ed25519 AAAA…"; null — not a public key line.</summary>
    public static string? PublicKeyOf(string? line)
    {
        var parts = (line ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !(parts[0].StartsWith("ssh-", StringComparison.Ordinal) || parts[0].StartsWith("ecdsa-", StringComparison.Ordinal))) return null;
        return parts[0] + " " + parts[1];
    }

    private static IReadOnlyList<string> ReadHostKeys()
    {
        try
        {
            if (!Directory.Exists(SshDirectory)) return Array.Empty<string>();
            return Directory.EnumerateFiles(SshDirectory, "ssh_host_*_key.pub")
                .Select(file => PublicKeyOf(File.ReadAllText(file).Trim()))
                .OfType<string>()
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The block the installer appends: logins from anywhere but the server are denied.</summary>
    public const string SourceRestrictionMarker = "# BEGIN FleetManager SSH source restriction";
    private const string OpenSshKey = @"SOFTWARE\OpenSSH";
    private static readonly string PowerShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe");

    public static bool HasSourceRestriction(IEnumerable<string> configLines) =>
        configLines.Any(line => line.Trim() == SourceRestrictionMarker);

    /// <summary>
    /// OpenSSH's DefaultShell. Without it sessions start cmd.exe and every PowerShell command of the server
    /// fails — what an MSI installed by hand after the agent left on SU5-H205-W01…W06.
    /// </summary>
    public static bool IsPowerShell(string? defaultShell)
    {
        var name = Path.GetFileName(defaultShell?.Trim().Trim('"') ?? string.Empty);
        return name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadDefaultShell()
    {
        using var key = Registry.LocalMachine.OpenSubKey(OpenSshKey);
        return key?.GetValue("DefaultShell") as string;
    }

    /// <summary>Same values as the installer. true — the shell was not PowerShell and has been set.</summary>
    [SupportedOSPlatform("windows")]
    private bool RepairDefaultShell()
    {
        try
        {
            if (IsPowerShell(ReadDefaultShell()) || !File.Exists(PowerShellPath)) return false;
            using var key = Registry.LocalMachine.CreateSubKey(OpenSshKey, writable: true);
            key.SetValue("DefaultShell", PowerShellPath, RegistryValueKind.String);
            key.SetValue("DefaultShellCommandOption", "-c", RegistryValueKind.String);
            _logger.Warn("OpenSSH default shell was not PowerShell; set to Windows PowerShell.");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _logger.Warn($"Unable to set the OpenSSH default shell: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private (bool? Valid, int? Port, bool? Restricted) CheckConfig()
    {
        var config = Path.Combine(SshDirectory, "sshd_config");
        int? port = null;
        bool? restricted = null;
        try
        {
            if (File.Exists(config))
            {
                var lines = File.ReadAllLines(config);
                port = ConfiguredPort(lines);
                restricted = HasSourceRestriction(lines);
            }
        }
        catch (IOException) { }
        var exe = SshdExecutable();
        if (exe is null || !File.Exists(config)) return (null, port, restricted);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, $"-t -f \"{config}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return (null, port, restricted);
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000)) { process.Kill(); return (null, port, restricted); }
            if (process.ExitCode != 0) _logger.Warn($"sshd_config is rejected by sshd -t: {error.Trim()}");
            return (process.ExitCode == 0, port, restricted);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, port, restricted);
        }
    }

    /// <summary>sshd.exe from the service registration: System32\OpenSSH for the capability, Program Files for the MSI.</summary>
    [SupportedOSPlatform("windows")]
    private static string? SshdExecutable()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{SshService.ServiceName}");
        var image = key?.GetValue("ImagePath", null, RegistryValueOptions.None) as string;
        if (string.IsNullOrWhiteSpace(image)) return null;
        image = image.Trim();
        var path = image.StartsWith('"') ? image[1..image.IndexOf('"', 1)] : image.Split(' ')[0];
        return Environment.ExpandEnvironmentVariables(path);
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    // Windows PowerShell 5.1 turns a one-element array into a scalar and sometimes wraps arrays as {"value":[…]}
    private static IReadOnlyList<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return Array.Empty<string>();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner)) value = inner;
        return value.ValueKind switch
        {
            JsonValueKind.String => new[] { value.GetString()! },
            JsonValueKind.Array => value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray(),
            _ => Array.Empty<string>(),
        };
    }
}
