using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FleetManager.Agent.Core;

/// <summary>
/// State of OpenSSH Server (sshd) — Fleet Manager's only management channel to the PC.
/// <paramref name="StartType"/>: "auto", "manual", "disabled" or null when sshd is not installed.
/// <paramref name="Repaired"/>: what the agent fixed during this sync ("start_type", "started"), empty if nothing.
/// </summary>
public sealed record SshServiceReport(
    bool Installed,
    string? StartType,
    bool Listening,
    IReadOnlyList<string> Repaired);

/// <summary>What <see cref="SshService.Plan"/> decided to fix.</summary>
public sealed record SshServicePlan(bool SetAutomatic, bool Start)
{
    public static readonly SshServicePlan Nothing = new(false, false);
}

/// <summary>
/// Keeps sshd running with automatic start. A Windows cumulative update that services the
/// built-in OpenSSH.Server capability (KB5129195, October 2026) stopped sshd and reset its
/// start type to Manual without logging a change; the PC then dropped out of Ansible after the
/// next reboot while its heartbeats kept arriving. Disabled is left alone: that is a deliberate
/// choice of an administrator or a Group Policy, the agent only reports it.
/// </summary>
public static class SshService
{
    public const string ServiceName = "sshd";
    // The installer pins sshd to this port (FleetManagerAgent.iss).
    public const int Port = 22;

    private const int StartAutomatic = 2;
    private const int StartManual = 3;
    private const int StartDisabled = 4;
    // sc.exe start: the service is already running.
    private const int ErrorServiceAlreadyRunning = 1056;

    /// <param name="start">Start value of the service (2 auto, 3 manual, 4 disabled); null — not installed.</param>
    public static SshServicePlan Plan(int? start, bool listening)
    {
        if (start is null || start == StartDisabled) return SshServicePlan.Nothing;
        return new SshServicePlan(SetAutomatic: start == StartManual, Start: !listening);
    }

    public static string? StartTypeName(int? start) => start switch
    {
        null => null,
        <= StartAutomatic => "auto",
        StartManual => "manual",
        StartDisabled => "disabled",
        _ => start.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>Reports the state and, when <paramref name="repair"/> is set, fixes the start type and starts sshd.</summary>
    public static SshServiceReport Ensure(bool repair, AgentLogger logger)
    {
        if (!OperatingSystem.IsWindows()) return new SshServiceReport(false, null, false, Array.Empty<string>());

        var start = ReadStartValue();
        var listening = IsListening();
        var repaired = new List<string>();
        var plan = repair ? Plan(start, listening) : SshServicePlan.Nothing;
        if (plan.SetAutomatic)
        {
            if (RunSc($"config {ServiceName} start= auto", logger) == 0)
            {
                repaired.Add("start_type");
                logger.Warn($"sshd start type was {StartTypeName(start)}; set to Automatic.");
                start = ReadStartValue();
            }
        }
        if (plan.Start)
        {
            // Already running but not on the port (sshd_config changed by hand) — nothing to start
            if (RunSc($"start {ServiceName}", logger, ErrorServiceAlreadyRunning) == 0)
            {
                repaired.Add("started");
                logger.Warn("sshd was not running; service started.");
                listening = WaitForListener();
            }
        }
        return new SshServiceReport(start is not null, StartTypeName(start), listening, repaired);
    }

    [SupportedOSPlatform("windows")]
    private static int? ReadStartValue()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        return key?.GetValue("Start") is int value ? value : null;
    }

    private static bool IsListening() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == Port);

    private static bool WaitForListener()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (IsListening()) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    // sc.exe, not ServiceController: the change has to go through the SCM (its database is not
    // re-read from the registry), and Core stays without extra packages. Control does the same.
    /// <returns>Exit code of sc.exe; null — it did not run. <paramref name="expected"/> is not logged as a failure.</returns>
    private static int? RunSc(string arguments, AgentLogger logger, int expected = 0)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("sc.exe did not start.");
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000)) throw new TimeoutException($"sc.exe {arguments} did not finish.");
            if (process.ExitCode != 0 && process.ExitCode != expected)
                logger.Warn($"sc.exe {arguments} failed ({process.ExitCode}): {output.Trim()}");
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            logger.Warn($"sc.exe {arguments} failed: {ex.Message}");
            return null;
        }
    }
}
