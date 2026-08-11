using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FleetManager.Agent.Core;

public interface IInventoryCollector
{
    Task<HardwareSnapshot> CollectHardwareAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SoftwareEntry>> CollectSoftwareAsync(CancellationToken cancellationToken);
}

public sealed class WindowsInventoryCollector : IInventoryCollector
{
    public async Task<HardwareSnapshot> CollectHardwareAsync(CancellationToken cancellationToken)
    {
        const string script = "Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,TotalPhysicalMemory | ConvertTo-Json -Compress; Get-CimInstance Win32_BIOS | Select-Object SerialNumber | ConvertTo-Json -Compress; Get-CimInstance Win32_OperatingSystem | Select-Object Caption | ConvertTo-Json -Compress; Get-CimInstance Win32_Processor | Select-Object -First 1 Name | ConvertTo-Json -Compress";
        var output = await RunPowerShellAsync(script, cancellationToken);
        var objects = ParseJsonObjects(output);
        var computer = objects.ElementAtOrDefault(0);
        var bios = objects.ElementAtOrDefault(1);
        var os = objects.ElementAtOrDefault(2);
        var cpu = objects.ElementAtOrDefault(3);
        var hardware = new HardwareSnapshot(
            GetString(computer, "Manufacturer"), GetString(computer, "Model"), GetString(bios, "SerialNumber"),
            GetString(os, "Caption"), GetString(cpu, "Name"), GetLong(computer, "TotalPhysicalMemory"), null);
        return hardware with { Fingerprint = Fingerprint(hardware) };
    }

    public async Task<IReadOnlyList<SoftwareEntry>> CollectSoftwareAsync(CancellationToken cancellationToken)
    {
        // Registry uninstall keys cover MSI/EXE installers, while the AppX and package
        // providers cover Store, winget and Chocolatey installations when available.
        const string script = @"
$items = @()
$paths = @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*')
$items += Get-ItemProperty $paths -ErrorAction SilentlyContinue | Where-Object DisplayName | Select-Object @{N='Name';E={$_.DisplayName}},@{N='Version';E={$_.DisplayVersion}},@{N='Publisher';E={$_.Publisher}},@{N='Source';E={'registry'}}
if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) { $items += Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Select-Object @{N='Name';E={$_.Name}},@{N='Version';E={$_.Version.ToString()}},@{N='Publisher';E={$_.Publisher}},@{N='Source';E={'appx'}} }
if (Get-Command choco -ErrorAction SilentlyContinue) { $items += choco list --local-only --limit-output 2>$null | ForEach-Object { $p=$_.Split('|'); if($p.Count -ge 2){ [pscustomobject]@{Name=$p[0];Version=$p[1];Publisher=$null;Source='chocolatey'} } } }
if (Get-Command winget -ErrorAction SilentlyContinue) { $items += winget list --accept-source-agreements --disable-interactivity 2>$null | Select-Object -Skip 2 | ForEach-Object { if($_ -match '^(.+?)\s{2,}([^\s]+)\s{2,}(.+?)\s{2,}(.+)$'){ [pscustomobject]@{Name=$matches[1].Trim();Version=$matches[2].Trim();Publisher=$null;Source='winget'} } } }
$items | Where-Object Name | Sort-Object Name,Version -Unique | ConvertTo-Json -Compress
";
        var output = await RunPowerShellAsync(script, cancellationToken);
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<SoftwareEntry>();
        try
        {
            using var document = JsonDocument.Parse(output);
            var values = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : new[] { document.RootElement };
            return values.Select(x => new SoftwareEntry(
                GetString(x, "Name") ?? string.Empty, GetString(x, "Version"), GetString(x, "Publisher"), GetString(x, "Source") ?? "unknown"))
                .Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<SoftwareEntry>();
        }
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"& { " + script.Replace("\"", "\\\"") + " }\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output;
    }

    private static IReadOnlyList<JsonElement> ParseJsonObjects(string output)
    {
        try
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        }
        catch (JsonException) { return Array.Empty<JsonElement>(); }
    }

    private static string? GetString(JsonElement? element, string name) =>
        element is { } value && value.TryGetProperty(name, out var property) ? property.ToString() : null;

    private static string? GetString(JsonDocument? element, string name) => element?.RootElement.ToString();
    private static long? GetLong(JsonElement? element, string name) => long.TryParse(GetString(element, name), out var result) ? result : null;

    private static string Fingerprint(HardwareSnapshot snapshot)
    {
        var value = string.Join('|', snapshot.Manufacturer, snapshot.Model, snapshot.SerialNumber, snapshot.OperatingSystem, snapshot.Processor, snapshot.TotalMemoryBytes);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }
}
