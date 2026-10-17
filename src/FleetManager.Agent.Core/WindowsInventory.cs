using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FleetManager.Agent.Core;

public interface IInventoryCollector
{
    Task<HardwareSnapshot> CollectHardwareAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SoftwareEntry>> CollectSoftwareAsync(CancellationToken cancellationToken);
    /// <summary>Активация Windows и Office; null — собрать не удалось.</summary>
    Task<ActivationSnapshot?> CollectActivationAsync(CancellationToken cancellationToken);
}

public sealed class WindowsInventoryCollector : IInventoryCollector
{
    private readonly LicenseActivationCollector _activation = new();

    public Task<ActivationSnapshot?> CollectActivationAsync(CancellationToken cancellationToken) => _activation.CollectAsync(cancellationToken);

    public async Task<HardwareSnapshot> CollectHardwareAsync(CancellationToken cancellationToken)
    {
        // Название ОС берётся из реестра, а не из Win32_OperatingSystem.Caption: Caption
        // локализуется по языку интерфейса и приходил то «Microsoft Windows 11 Enterprise»,
        // то «Майкрософт Windows 11 Корпоративная». Название входит в отпечаток железа,
        // поэтому каждая смена языка отправляла на сервер ложный алерт hardware_changed.
        const string script = "Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,TotalPhysicalMemory | ConvertTo-Json -Compress; Get-CimInstance Win32_BIOS | Select-Object SerialNumber | ConvertTo-Json -Compress; $v = Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion' -ErrorAction SilentlyContinue; [pscustomobject]@{ ProductName = $v.ProductName; CurrentBuildNumber = $v.CurrentBuildNumber; Caption = (Get-CimInstance Win32_OperatingSystem).Caption } | ConvertTo-Json -Compress; Get-CimInstance Win32_Processor | Select-Object -First 1 Name | ConvertTo-Json -Compress";
        var output = await RunPowerShellAsync(script, cancellationToken);
        var objects = ParseJsonObjects(output);
        var computer = objects.ElementAtOrDefault(0);
        var bios = objects.ElementAtOrDefault(1);
        var os = objects.ElementAtOrDefault(2);
        var cpu = objects.ElementAtOrDefault(3);
        var hardware = new HardwareSnapshot(
            GetString(computer, "Manufacturer"), GetString(computer, "Model"), GetString(bios, "SerialNumber"),
            OperatingSystemName(GetString(os, "ProductName"), GetString(os, "CurrentBuildNumber"), GetString(os, "Caption")),
            GetString(cpu, "Name"), GetLong(computer, "TotalPhysicalMemory"), null);
        var adapters = ParseNetworkAdapters(await RunPowerShellAsync(NetworkAdaptersScript, cancellationToken));
        var components = await HardwareComponentsCollector.CollectAsync(cancellationToken);
        return hardware with { Fingerprint = Fingerprint(hardware), NetworkAdapters = adapters, Disks = CollectDisks(), Components = components };
    }

    /// <summary>Локальные несъёмные диски; флешки, сетевые и не готовые (BitLocker) пропускаются.</summary>
    internal static IReadOnlyList<DiskInfo> CollectDisks()
    {
        var disks = new List<DiskInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0) continue;
                disks.Add(new DiskInfo(
                    drive.Name.TrimEnd('\\'), NullIfBlank(drive.VolumeLabel), NullIfBlank(drive.DriveFormat),
                    drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return disks;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Физические адаптеры с MAC, IPv4/префиксом и скоростью линка (Speed, бит/с; без линка — пусто). -InputObject — чтобы и один адаптер
    // пришёл массивом.
    private const string NetworkAdaptersScript =
        "ConvertTo-Json -Compress -Depth 3 -InputObject @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | ForEach-Object { " +
        "[pscustomobject]@{ Name = $_.Name; Description = $_.InterfaceDescription; MacAddress = $_.MacAddress; Status = [string]$_.Status; MediaType = [string]$_.PhysicalMediaType; Speed = $_.Speed; " +
        "Ipv4 = @(Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { $_.IPAddress + '/' + $_.PrefixLength }) } })";

    /// <summary>
    /// Разбирает вывод NetworkAdaptersScript. Bluetooth (PAN) и адаптеры без MAC отбрасываются.
    /// Windows PowerShell 5.1 иногда сериализует вложенный массив как {"value":[…],"Count":n} —
    /// поэтому Ipv4 принимается и строкой, и массивом, и таким объектом.
    /// </summary>
    internal static IReadOnlyList<NetworkAdapterInfo> ParseNetworkAdapters(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<NetworkAdapterInfo>();
        try
        {
            using var document = JsonDocument.Parse(output);
            var items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : new[] { document.RootElement };
            return items
                .Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new NetworkAdapterInfo(
                    GetString(x, "Name"), GetString(x, "Description"), (GetString(x, "MacAddress") ?? string.Empty).Trim(),
                    StringList(x, "Ipv4"), GetString(x, "Status"), GetString(x, "MediaType"), LinkSpeed(x)))
                .Where(x => x.MacAddress.Length > 0 && !(x.MediaType ?? string.Empty).Contains("bluetooth", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<NetworkAdapterInfo>();
        }
    }

    // Без линка Speed пустой или 0; некоторые драйверы отдают «неизвестно» как Int64.MaxValue
    private static long? LinkSpeed(JsonElement element) =>
        element.TryGetProperty("Speed", out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var speed) && speed > 0 && speed < 1_000_000_000_000 ? speed : null;

    private static IReadOnlyList<string> StringList(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return Array.Empty<string>();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner)) value = inner;
        return value.ValueKind switch
        {
            JsonValueKind.String => new[] { value.GetString()! },
            JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>
    /// Название ОС в одном виде независимо от языка интерфейса: «Microsoft Windows 11 Enterprise».
    /// ProductName в реестре не локализуется, но на Windows 11 по-прежнему начинается
    /// с «Windows 10» — её отличает номер сборки (22000 и выше). Без ProductName
    /// остаётся Caption, как раньше.
    /// </summary>
    internal static string? OperatingSystemName(string? productName, string? buildNumber, string? caption)
    {
        if (string.IsNullOrWhiteSpace(productName)) return caption;
        var name = productName.Trim();
        if (name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(buildNumber, out var build) && build >= 22000)
        {
            name = "Windows 11" + name["Windows 10".Length..];
        }
        return name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ? name : "Microsoft " + name;
    }

    internal const string UninstallKeys =
        @"'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'";

    // Программы из Uninstall-записей. Значения читаются по одному через RegistryKey.GetValue,
    // а не Get-ItemProperty по маске: тот читает все значения всех записей, и одно чужое битое
    // значение роняет весь вызов. Установщик NetBeans пишет NoModify как REG_DWORD из 8 байт,
    // .NET отдаёт его как Int64, провайдер реестра PowerShell падает с InvalidCastException,
    // и на таком ПК (SU5-VM-SBX) из реестра не попадало ни одной программы. Нечитаемая запись
    // пропускается, остальные — нет. $keys — список путей PowerShell (подменяется в тестах).
    internal const string RegistrySoftwareScript =
        @"(Get-ChildItem $keys -ErrorAction SilentlyContinue | ForEach-Object { try { $name = [string]$_.GetValue('DisplayName'); if ($name) { [pscustomobject]@{ Name = $name; Version = [string]$_.GetValue('DisplayVersion'); Publisher = [string]$_.GetValue('Publisher'); Source = 'registry' } } } catch {} })";

    // Registry uninstall keys cover MSI/EXE installers, while the AppX and package
    // providers cover Store, winget and Chocolatey installations when available.
    internal const string SoftwareScript = @"
$items = @()
$keys = @(" + UninstallKeys + @")
$items += " + RegistrySoftwareScript + @"
if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) { $items += Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Select-Object @{N='Name';E={$_.Name}},@{N='Version';E={$_.Version.ToString()}},@{N='Publisher';E={$_.Publisher}},@{N='Source';E={'appx'}} }
if (Get-Command choco -ErrorAction SilentlyContinue) { $items += choco list --local-only --limit-output 2>$null | ForEach-Object { $p=$_.Split('|'); if($p.Count -ge 2){ [pscustomobject]@{Name=$p[0];Version=$p[1];Publisher=$null;Source='chocolatey'} } } }
if (Get-Command winget -ErrorAction SilentlyContinue) { $items += winget list --accept-source-agreements --disable-interactivity 2>$null | Select-Object -Skip 2 | ForEach-Object { if($_ -match '^(.+?)\s{2,}([^\s]+)\s{2,}(.+?)\s{2,}(.+)$'){ [pscustomobject]@{Name=$matches[1].Trim();Version=$matches[2].Trim();Publisher=$null;Source='winget'} } } }
$items | Where-Object Name | Sort-Object Name,Version -Unique | ConvertTo-Json -Compress
";

    public async Task<IReadOnlyList<SoftwareEntry>> CollectSoftwareAsync(CancellationToken cancellationToken)
    {
        var output = await RunPowerShellAsync(SoftwareScript, cancellationToken);
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

    // Windows PowerShell 5.1 пишет в перенаправленный stdout в OEM-кодировке консоли
    // (cp866 на русской Windows), а читаем мы UTF-8: кириллица в названиях ОС и ПО
    // превращалась в «�». Переключаем вывод на UTF-8 до запуска скрипта; заодно
    // choco и winget, которых скрипт вызывает, наследуют кодовую страницу консоли.
    internal const string Utf8OutputPrefix = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ";

    internal static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"& { " + (Utf8OutputPrefix + script).Replace("\"", "\\\"") + " }\"",
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
