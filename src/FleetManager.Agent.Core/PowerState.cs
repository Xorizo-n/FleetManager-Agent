using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace FleetManager.Agent.Core;

/// <summary>
/// Power state for the heartbeat (<c>power</c>): when Windows booted, how the previous run ended and whether
/// Windows is installing updates.
/// <para>Half of the PCs that drop offline do it silently — the agent has no chance to say "offline" (163 of
/// 333 in October 2026). The System log tells why: <see cref="PreviousShutdown"/> is "clean" (shut down),
/// "restart", "power_loss" (power button held or power cut — Kernel-Power 41 without a bugcheck, EventLog
/// 6008), "crash" (Kernel-Power 41 with a bugcheck) or "unknown". The server shows it on the host card, so
/// "switched off" is told apart from "vanished while running".</para>
/// <para><see cref="UpdatesInstalling"/>: TiWorker (Windows Modules Installer) runs — the server holds waiting
/// tasks, as the update ends with a reboot that would cut a playbook off midway.</para>
/// </summary>
public sealed record PowerReport
{
    public DateTimeOffset? BootedAt { get; init; }
    public string? PreviousShutdown { get; init; }
    public DateTimeOffset? PreviousShutdownAt { get; init; }
    public bool UpdatesInstalling { get; init; }
    public bool RebootPending { get; init; }
}

/// <summary>A System log event for <see cref="PowerState.Classify"/>.</summary>
public sealed record PowerEvent(int Id, DateTimeOffset Time, long Bugcheck = 0);

public static class PowerState
{
    // A restart: the OS went down and came back within this time
    private static readonly TimeSpan RestartGap = TimeSpan.FromMinutes(10);
    // Events of the boot itself (41, 6008) are written a minute or two after the boot time
    private static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(15);

    private static (DateTimeOffset? Boot, string? Kind, DateTimeOffset? At)? _previous;

    /// <summary>
    /// How the run before <paramref name="boot"/> ended. <paramref name="lastBeforeBoot"/> — the last System
    /// log entry before the boot: after a power cut it is the closest thing to the moment the PC went dark.
    /// </summary>
    public static (string Kind, DateTimeOffset? At) Classify(DateTimeOffset boot, IEnumerable<PowerEvent> events, DateTimeOffset? lastBeforeBoot)
    {
        var list = events.ToList();
        var afterBoot = list.Where(e => e.Time >= boot.AddMinutes(-1) && e.Time <= boot + BootWindow).ToList();
        var kernelPower = afterBoot.FirstOrDefault(e => e.Id == 41);
        if (kernelPower is not null) return (kernelPower.Bugcheck != 0 ? "crash" : "power_loss", lastBeforeBoot);
        if (afterBoot.Any(e => e.Id == 6008)) return ("power_loss", lastBeforeBoot);
        // 13 — the OS is shutting down, 6006 — the event log stopped, 1074 — someone asked for it
        var down = list.Where(e => e.Id is 13 or 6006 or 1074 && e.Time < boot).OrderByDescending(e => e.Time).FirstOrDefault();
        if (down is null) return ("unknown", lastBeforeBoot);
        return (boot - down.Time <= RestartGap ? "restart" : "clean", down.Time);
    }

    /// <summary>Current report; the slow part (boot and the previous shutdown) is read once per boot.</summary>
    public static async Task<PowerReport?> CollectAsync(AgentLogger logger, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (_previous is not { } previous || previous.Boot is not { } known || Math.Abs((known - boot).TotalMinutes) > 2)
        {
            try { previous = await ReadPreviousShutdownAsync(cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.Warn($"Unable to read the previous shutdown from the System log: {ex.Message}");
                previous = (boot, null, null);
            }
            _previous = previous;
        }
        return new PowerReport
        {
            BootedAt = previous.Boot ?? boot,
            PreviousShutdown = previous.Kind,
            PreviousShutdownAt = previous.At,
            UpdatesInstalling = UpdatesInstalling(),
            RebootPending = RebootPending(),
        };
    }

    public static bool UpdatesInstalling()
    {
        try { return Process.GetProcessesByName("TiWorker").Length > 0; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool RebootPending()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return cbs is not null || wu is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    // Only single quotes: RunPowerShellAsync passes the script inside -Command "…"
    private const string Script =
        "$ErrorActionPreference = 'Stop'\n" +
        "$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime\n" +
        "$events = @(Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,6008,13,6006,1074; StartTime=$boot.AddDays(-30)} -MaxEvents 200 -ErrorAction SilentlyContinue | ForEach-Object {\n" +
        "  $bug = 0; if ($_.Id -eq 41) { try { $bug = [long]$_.Properties[0].Value } catch { } }\n" +
        "  [pscustomobject]@{ id = $_.Id; time = $_.TimeCreated.ToUniversalTime().ToString('o'); bugcheck = $bug } })\n" +
        "$last = Get-WinEvent -FilterHashtable @{LogName='System'; EndTime=$boot} -MaxEvents 1 -ErrorAction SilentlyContinue\n" +
        "ConvertTo-Json -Compress -Depth 3 -InputObject ([pscustomobject]@{ boot = $boot.ToUniversalTime().ToString('o'); last = $(if ($last) { $last.TimeCreated.ToUniversalTime().ToString('o') }); events = $events })";

    private static async Task<(DateTimeOffset? Boot, string? Kind, DateTimeOffset? At)> ReadPreviousShutdownAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = await WindowsInventoryCollector.RunPowerShellAsync(Script, timeout.Token);
        return Parse(output);
    }

    internal static (DateTimeOffset? Boot, string? Kind, DateTimeOffset? At) Parse(string? output)
    {
        var line = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return (null, null, null);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var boot = Time(root, "boot");
        if (boot is null) return (null, null, null);
        var events = new List<PowerEvent>();
        if (root.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && Time(item, "time") is { } time)
                    events.Add(new PowerEvent(number, time, item.TryGetProperty("bugcheck", out var bug) && bug.TryGetInt64(out var code) ? code : 0));
            }
        }
        var (kind, at) = Classify(boot.Value, events, Time(root, "last"));
        return (boot, kind, at);
    }

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
