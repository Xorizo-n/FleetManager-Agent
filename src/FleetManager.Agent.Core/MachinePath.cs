using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FleetManager.Agent.Core;

/// <summary>
/// Machine PATH. Ansible starts every command with chcp.com and PowerShell by name, so a PATH
/// without System32 cuts the PC off from all playbooks while SSH itself still works — the MultiClet
/// SDK installer left SU7-M301-W01 like that (October 2026). A REG_SZ value breaks it the same way:
/// %SystemRoot% is then not expanded.
/// </summary>
public static class MachinePath
{
    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    internal const string Base = @"%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem;%SystemRoot%\System32\WindowsPowerShell\v1.0;%SystemRoot%\System32\OpenSSH";

    public static bool IsHealthy(string? raw, RegistryValueKind kind) =>
        kind == RegistryValueKind.ExpandString && (raw ?? string.Empty).Contains("system32", StringComparison.OrdinalIgnoreCase);

    /// <summary>The value to write back; null — nothing to fix. Existing entries are kept after the base ones.</summary>
    public static string? Repaired(string? raw, RegistryValueKind kind)
    {
        if (IsHealthy(raw, kind)) return null;
        raw ??= string.Empty;
        if (raw.Contains("system32", StringComparison.OrdinalIgnoreCase)) return raw;
        return raw.Trim(';').Length == 0 ? Base : Base + ";" + raw.Trim(';');
    }

    public static bool? IsHealthy()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var (raw, kind) = Read();
        return kind is null ? null : IsHealthy(raw, kind.Value);
    }

    /// <returns>true — PATH was broken and has been restored.</returns>
    public static bool Repair(AgentLogger logger)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var (raw, kind) = Read();
            if (kind is null) return false;
            var value = Repaired(raw, kind.Value);
            if (value is null) return false;
            using var key = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true);
            if (key is null) return false;
            key.SetValue("Path", value, RegistryValueKind.ExpandString);
            logger.Warn($"Machine PATH was broken ({kind}, {raw?.Length ?? 0} characters); System32 restored.");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            logger.Warn($"Unable to check the machine PATH: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static (string? Raw, RegistryValueKind? Kind) Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(EnvironmentKey);
        if (key is null) return (null, null);
        var names = key.GetValueNames();
        if (!names.Contains("Path", StringComparer.OrdinalIgnoreCase)) return (null, RegistryValueKind.ExpandString);
        return (key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, key.GetValueKind("Path"));
    }
}
