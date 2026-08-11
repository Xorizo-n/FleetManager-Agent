using System.Diagnostics;

namespace FleetManager.Agent.Core;

public static class WindowsSshKeyInstaller
{
    public const string AdministratorsAuthorizedKeys = @"C:\ProgramData\ssh\administrators_authorized_keys";

    public static void Install(string publicKey)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(publicKey)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(AdministratorsAuthorizedKeys)!);
        var lines = File.Exists(AdministratorsAuthorizedKeys)
            ? File.ReadAllLines(AdministratorsAuthorizedKeys).ToList()
            : new List<string>();
        if (!lines.Any(line => string.Equals(line.Trim(), publicKey.Trim(), StringComparison.Ordinal)))
        {
            lines.Add(publicKey.Trim());
            File.WriteAllLines(AdministratorsAuthorizedKeys, lines);
        }
        SetAdministratorsOnlyAcl();
    }

    public static void Remove(string? publicKey)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(publicKey) || !File.Exists(AdministratorsAuthorizedKeys)) return;
        var lines = File.ReadAllLines(AdministratorsAuthorizedKeys)
            .Where(line => !string.Equals(line.Trim(), publicKey.Trim(), StringComparison.Ordinal))
            .ToArray();
        File.WriteAllLines(AdministratorsAuthorizedKeys, lines);
        SetAdministratorsOnlyAcl();
    }

    private static void SetAdministratorsOnlyAcl()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = $"\"{AdministratorsAuthorizedKeys}\" /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null) throw new InvalidOperationException("Unable to start icacls.exe.");
        if (!process.WaitForExit(10_000)) throw new TimeoutException("icacls.exe did not finish while securing the SSH authorized keys file.");
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException($"icacls.exe failed ({process.ExitCode}): {error}");
        }
    }
}
