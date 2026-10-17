using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FleetManager.Agent.Core;

/// <summary>
/// Состав рабочего места: материнская плата и BIOS, модули памяти, физические диски,
/// видеокарты, мониторы, клавиатуры и мыши, прочие USB-устройства. Сервер сравнивает
/// снимки, ведёт журнал изменений оборудования и шлёт алерт, если пропала клавиатура,
/// мышь или монитор. В отпечаток железа не входит: периферию переподключают постоянно.
/// </summary>
public sealed record HardwareComponents(
    BaseboardInfo? Baseboard,
    string? BiosVersion,
    IReadOnlyList<MemoryModuleInfo> MemoryModules,
    IReadOnlyList<PhysicalDiskInfo> PhysicalDisks,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<MonitorInfo> Monitors,
    IReadOnlyList<PeripheralInfo> InputDevices,
    IReadOnlyList<PeripheralInfo> UsbDevices);

public sealed record BaseboardInfo(string? Manufacturer, string? Product, string? SerialNumber);

/// <param name="Slot">DeviceLocator: «Controller0-ChannelA-DIMM0».</param>
public sealed record MemoryModuleInfo(string? Slot, string? Manufacturer, string? PartNumber, string? SerialNumber, long? CapacityBytes, int? SpeedMhz);

/// <param name="BusType">«NVMe» или InterfaceType («IDE», «SCSI»); USB-накопители и виртуальные диски не входят.</param>
public sealed record PhysicalDiskInfo(string? Model, string? SerialNumber, string? BusType, long? SizeBytes);

/// <param name="PnpId">PCI\VEN_8086&amp;DEV_4680&amp;… — по VEN/DEV сервер узнаёт ту же карту после переустановки драйвера.</param>
public sealed record GpuInfo(string? Name, string? DriverVersion, string? PnpId);

/// <param name="Manufacturer">Код производителя из EDID: «AUS», «SAM», «MSI».</param>
/// <param name="Connection">WmiMonitorConnectionParams.VideoOutputTechnology: 0 — VGA, 4 — DVI, 5 — HDMI,
/// 10 — DisplayPort, 0x80000000 — встроенная панель (моноблок, ноутбук).</param>
public sealed record MonitorInfo(string? Manufacturer, string? Model, string? SerialNumber, string? ProductCode, int? Year, long? Connection);

/// <param name="Kind">«keyboard», «mouse» или «usb».</param>
/// <param name="Name">Имя, которое сообщает само устройство («USB Optical Mouse»), иначе имя драйвера.</param>
/// <param name="Serial">Серийный номер USB, если устройство его сообщает; иначе null —
/// тогда два одинаковых устройства различаются только количеством.</param>
public sealed record PeripheralInfo(string Kind, string? Name, string? Vid, string? Pid, string? Serial, string? DeviceClass);

public static class HardwareComponentsCollector
{
    // Мониторы — только активные (Active): отключённый остаётся в WMI до перезагрузки.
    // Клавиатуры и мыши — из Win32_Keyboard / Win32_PointingDevice (≈40 мс). USB-устройства —
    // верхнего уровня (без интерфейсов &MI_), без хабов и накопителей. Имя, которое сообщает
    // само USB-устройство (DEVPKEY_Device_BusReportedDeviceDesc: «USB Optical Mouse»), понятнее
    // драйверного «HID-compliant mouse»; запрашивается по одному устройству: устройство, отключённое
    // во время сбора, роняло общий запрос, и имена не приходили ни у одного. Диски — из
    // Win32_DiskDrive, а не Get-PhysicalDisk: тот будит службу хранилища и после простоя
    // отвечал по 10–12 секунд (SU5, SU7), а сбор идёт каждые 5 минут.
    internal const string Script =
        "$dec = { param($a) if ($a) { (-join ($a | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ })).Trim() } }; " +
        "$board = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue | Select-Object -First 1; " +
        "$bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue | Select-Object -First 1; " +
        "$memory = @(Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Slot = $_.DeviceLocator; Manufacturer = $_.Manufacturer; PartNumber = $_.PartNumber; SerialNumber = $_.SerialNumber; CapacityBytes = [long]$_.Capacity; SpeedMhz = [int]$_.ConfiguredClockSpeed } }); " +
        "$disks = @(Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceType -ne 'USB' -and $_.MediaType -notlike 'Removable*' -and $_.PNPDeviceID -notlike '*VIRTUAL*' } | ForEach-Object { [pscustomobject]@{ Model = $_.Model; SerialNumber = $_.SerialNumber; BusType = $(if ($_.PNPDeviceID -like '*NVME*') { 'NVMe' } else { $_.InterfaceType }); SizeBytes = [long]$_.Size } }); " +
        "$gpus = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.PNPDeviceID -like 'PCI\\*' } | ForEach-Object { [pscustomobject]@{ Name = $_.Name; DriverVersion = $_.DriverVersion; PnpId = $_.PNPDeviceID } }); " +
        "$conn = @{}; Get-CimInstance -Namespace root\\wmi WmiMonitorConnectionParams -ErrorAction SilentlyContinue | ForEach-Object { $conn[$_.InstanceName] = [long]$_.VideoOutputTechnology }; " +
        "$monitors = @(Get-CimInstance -Namespace root\\wmi WmiMonitorID -ErrorAction SilentlyContinue | Where-Object { $_.Active } | ForEach-Object { [pscustomobject]@{ Manufacturer = & $dec $_.ManufacturerName; Model = & $dec $_.UserFriendlyName; SerialNumber = & $dec $_.SerialNumberID; ProductCode = & $dec $_.ProductCodeID; Year = [int]$_.YearOfManufacture; Connection = $conn[$_.InstanceName] } }); " +
        "$inputs = @(@(Get-CimInstance Win32_Keyboard -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Kind = 'keyboard'; Name = $_.Name; PnpId = $_.PNPDeviceID } }) + @(Get-CimInstance Win32_PointingDevice -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Kind = 'mouse'; Name = $_.Name; PnpId = $_.PNPDeviceID } })); " +
        "$usb = @(Get-CimInstance Win32_PnPEntity -Filter 'DeviceID LIKE ''USB%''' -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -match '^USB\\\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\\\' -and @('USBSTOR','UASPStor','usbhub','USBHUB3') -notcontains $_.Service -and $_.PNPClass -ne 'WPD' -and -not ($_.PNPClass -eq 'USB' -and $_.Name -match 'hub') }); " +
        "$reported = @{}; foreach ($d in $usb) { try { $reported[$d.DeviceID] = @((Get-PnpDeviceProperty -InstanceId $d.DeviceID -KeyName DEVPKEY_Device_BusReportedDeviceDesc -ErrorAction Stop).Data)[0] } catch {} }; " +
        "$usbDevices = @($usb | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Reported = $reported[$_.DeviceID]; PnpId = $_.DeviceID; Class = $_.PNPClass } }); " +
        "ConvertTo-Json -Compress -Depth 4 -InputObject ([pscustomobject]@{ Baseboard = [pscustomobject]@{ Manufacturer = $board.Manufacturer; Product = $board.Product; SerialNumber = $board.SerialNumber }; BiosVersion = $bios.SMBIOSBIOSVersion; Memory = $memory; Disks = $disks; Gpus = $gpus; Monitors = $monitors; Inputs = $inputs; Usb = $usbDevices })";

    public static async Task<HardwareComponents?> CollectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            return Parse(await WindowsInventoryCollector.RunPowerShellAsync(Script, timeout.Token));
        }
        // Состав рабочего места — дополнительные сведения: его сбой не должен срывать heartbeat
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    internal static HardwareComponents? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            BaseboardInfo? board = null;
            if (root.TryGetProperty("Baseboard", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                board = new BaseboardInfo(Text(b, "Manufacturer"), Text(b, "Product"), Serial(Text(b, "SerialNumber")));
                if (board is { Manufacturer: null, Product: null, SerialNumber: null }) board = null;
            }

            var usb = Items(root, "Usb")
                .Select(u => (Id: PnpIds(Text(u, "PnpId")), Name: Text(u, "Reported") ?? Text(u, "Name"), Class: Text(u, "Class")))
                .Where(u => u.Id.Vid is not null)
                .Select(u => new PeripheralInfo("usb", u.Name, u.Id.Vid, u.Id.Pid, u.Id.Serial, u.Class))
                .ToArray();

            var inputs = Items(root, "Inputs")
                .Select(i => (Kind: Text(i, "Kind"), Name: Text(i, "Name"), PnpId: Text(i, "PnpId") ?? string.Empty))
                .Where(i => i.Kind is "keyboard" or "mouse" && IsPhysicalInput(i.PnpId))
                .Select(i =>
                {
                    var id = PnpIds(i.PnpId);
                    // Имя от самого USB-устройства («USB Keyboard»), у драйвера — «HID Keyboard Device»
                    var reported = usb.FirstOrDefault(u => u.Vid == id.Vid && u.Pid == id.Pid)?.Name;
                    return new PeripheralInfo(i.Kind!, reported ?? i.Name, id.Vid, id.Pid, id.Serial, null);
                })
                .ToArray();

            return new HardwareComponents(
                board,
                Text(root, "BiosVersion"),
                Items(root, "Memory").Select(m => new MemoryModuleInfo(
                    Text(m, "Slot"), Text(m, "Manufacturer"), Text(m, "PartNumber"), Serial(Text(m, "SerialNumber")),
                    Positive(Long(m, "CapacityBytes")), (int?)Positive(Long(m, "SpeedMhz")))).ToArray(),
                Items(root, "Disks").Select(d => new PhysicalDiskInfo(
                    Text(d, "Model"), Serial(Text(d, "SerialNumber")), Text(d, "BusType"), Positive(Long(d, "SizeBytes")))).ToArray(),
                Items(root, "Gpus").Select(g => new GpuInfo(Text(g, "Name"), Text(g, "DriverVersion"), Text(g, "PnpId"))).ToArray(),
                Items(root, "Monitors").Select(m => new MonitorInfo(
                    Text(m, "Manufacturer"), Text(m, "Model"), Serial(Text(m, "SerialNumber")), Text(m, "ProductCode"),
                    (int?)Positive(Long(m, "Year")), Long(m, "Connection"))).ToArray(),
                inputs,
                usb);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly Regex VidPid = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// VID/PID и серийный номер из Instance ID: «USB\VID_046D&amp;PID_C077\5&amp;140AC59&amp;0&amp;5» — без серийного
    /// (хвост с «&amp;» — номер порта, меняется при переподключении), «USB\VID_8087&amp;PID_0033\7D9924101807» — с серийным.
    /// </summary>
    internal static (string? Vid, string? Pid, string? Serial) PnpIds(string? pnpId)
    {
        if (string.IsNullOrWhiteSpace(pnpId)) return (null, null, null);
        var match = VidPid.Match(pnpId);
        if (!match.Success) return (null, null, null);
        var parts = pnpId.Split('\\');
        var serial = parts.Length == 3 && pnpId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            && !parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase) && !parts[2].Contains('&')
            ? Serial(parts[2])
            : null;
        return (match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value.ToUpperInvariant(), serial);
    }

    /// <summary>
    /// Реальные клавиатуры и мыши: USB и HID (в том числе I2C-тачпады). ACPI\PNP0303 / PNP0F13 —
    /// «PS/2-клавиатура», которую BIOS многих настольных ПК показывает и без подключённой клавиатуры;
    /// ROOT\ и TERMINPUT — виртуальные устройства RDP.
    /// </summary>
    internal static bool IsPhysicalInput(string pnpId) =>
        (pnpId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || pnpId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
        && !pnpId.Contains("TERMINPUT", StringComparison.OrdinalIgnoreCase);

    // Заглушки производителей вместо серийного номера
    private static readonly HashSet<string> SerialPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "0", "1", "00000000", "000000000000", "Default string", "To be filled by O.E.M.", "None", "N/A", "Not Specified", "System Serial Number",
    };

    internal static string? Serial(string? value)
    {
        var raw = value?.Trim();
        // NVMe-диски дописывают к серийному номеру точку
        var text = raw?.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(text) || SerialPlaceholders.Contains(raw!) || SerialPlaceholders.Contains(text) || text.All(c => c == '0')) return null;
        return text;
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return Array.Empty<JsonElement>();
        // Windows PowerShell 5.1 иногда сериализует вложенный массив как {"value":[…],"Count":n}
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner)) value = inner;
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).ToArray(),
            JsonValueKind.Object => new[] { value },
            _ => Array.Empty<JsonElement>(),
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number && !string.IsNullOrWhiteSpace(value.ToString())
            ? value.ToString().Trim()
            : null;

    private static long? Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static long? Positive(long? value) => value > 0 ? value : null;
}
