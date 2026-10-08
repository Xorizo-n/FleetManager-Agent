using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FleetManager.Agent.Core;

/// <summary>
/// Pending computer rename. Rename-Computer and a domain join with a new name only
/// change the name that applies after the next reboot; until then Environment.MachineName
/// still returns the old one, and an agent registered now would show up under it.
/// </summary>
public static class ComputerRename
{
    private const string ActiveKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName";
    private const string PendingKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName";

    public static bool IsPending()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return IsPending(ReadName(ActiveKey), ReadName(PendingKey));
    }

    public static bool IsPending(string? activeName, string? pendingName) =>
        !string.IsNullOrWhiteSpace(activeName) &&
        !string.IsNullOrWhiteSpace(pendingName) &&
        !string.Equals(activeName.Trim(), pendingName.Trim(), StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static string? ReadName(string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue("ComputerName") as string;
    }
}
