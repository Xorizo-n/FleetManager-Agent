using System.Net.Http.Json;
using System.Text.Json;

namespace FleetManager.Agent.Core;

public sealed class FleetManagerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AgentLogger _logger;

    public FleetManagerApiClient(HttpClient httpClient, AgentLogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    internal static object? ComponentsPayload(HardwareComponents? components) => components is null ? null : new
    {
        baseboard = components.Baseboard is { } board
            ? new { manufacturer = board.Manufacturer, product = board.Product, serial_number = board.SerialNumber }
            : null,
        bios_version = components.BiosVersion,
        memory_modules = components.MemoryModules.Select(m => new
        {
            slot = m.Slot,
            manufacturer = m.Manufacturer,
            part_number = m.PartNumber,
            serial_number = m.SerialNumber,
            capacity_bytes = m.CapacityBytes,
            speed_mhz = m.SpeedMhz
        }).ToArray(),
        physical_disks = components.PhysicalDisks.Select(d => new
        {
            model = d.Model,
            serial_number = d.SerialNumber,
            bus_type = d.BusType,
            size_bytes = d.SizeBytes
        }).ToArray(),
        gpus = components.Gpus.Select(g => new { name = g.Name, driver_version = g.DriverVersion, pnp_id = g.PnpId }).ToArray(),
        monitors = components.Monitors.Select(m => new
        {
            manufacturer = m.Manufacturer,
            model = m.Model,
            serial_number = m.SerialNumber,
            product_code = m.ProductCode,
            year = m.Year,
            connection = m.Connection
        }).ToArray(),
        input_devices = components.InputDevices.Select(Peripheral).ToArray(),
        usb_devices = components.UsbDevices.Select(Peripheral).ToArray()
    };

    private static object Peripheral(PeripheralInfo device) => new
    {
        kind = device.Kind,
        name = device.Name,
        vid = device.Vid,
        pid = device.Pid,
        serial = device.Serial,
        device_class = device.DeviceClass
    };

    internal static object? ActivationPayload(ActivationSnapshot? activation) => activation is null ? null : new
    {
        collected_at = activation.CollectedAt,
        client_machine_id = activation.ClientMachineId,
        kms_probe = activation.KmsProbe is { } probe
            ? new { host = probe.Host, port = probe.Port, reachable = probe.Reachable, error = probe.Error }
            : null,
        products = activation.Products.Select(product => new
        {
            kind = product.Kind,
            id = product.Id,
            name = product.Name,
            description = product.Description,
            channel = product.Channel,
            partial_product_key = product.PartialProductKey,
            license_status = product.LicenseStatus,
            status_reason = product.StatusReason,
            grace_minutes = product.GraceMinutes,
            kms_machine = product.KmsMachine,
            kms_port = product.KmsPort,
            discovered_kms_machine = product.DiscoveredKmsMachine,
            discovered_kms_port = product.DiscoveredKmsPort,
            last_request = product.LastRequest is { } request
                ? new { at = request.At, kms = request.Kms, result = request.Result }
                : null
        }).ToArray()
    };

    public async Task SendHeartbeatAsync(AgentSnapshot snapshot, string? token, string? sshLogin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_httpClient.BaseAddress?.ToString()))
            throw new InvalidOperationException("Fleet Manager server URL is not configured.");

        var payload = new
        {
            machine_id = snapshot.Status.MachineId,
            hostname = snapshot.Status.ComputerName,
            ssh_login = sshLogin,
            ip_address = (string?)null,
            os = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "windows_11" : "windows_10",
            status = "online",
            agent_version = AgentVersion.Current,
            hardware = new
            {
                manufacturer = snapshot.Hardware.Manufacturer,
                model = snapshot.Hardware.Model,
                serial_number = snapshot.Hardware.SerialNumber,
                operating_system = snapshot.Hardware.OperatingSystem,
                processor = snapshot.Hardware.Processor,
                total_memory_bytes = snapshot.Hardware.TotalMemoryBytes,
                fingerprint = snapshot.Hardware.Fingerprint,
                network_adapters = snapshot.Hardware.NetworkAdapters.Select(adapter => new
                {
                    name = adapter.Name,
                    description = adapter.Description,
                    mac_address = adapter.MacAddress,
                    ipv4 = adapter.Ipv4,
                    status = adapter.Status,
                    media_type = adapter.MediaType,
                    link_speed_bps = adapter.LinkSpeedBps
                }).ToArray(),
                disks = snapshot.Hardware.Disks.Select(disk => new
                {
                    name = disk.Name,
                    label = disk.Label,
                    file_system = disk.FileSystem,
                    size_bytes = disk.SizeBytes,
                    free_bytes = disk.FreeBytes
                }).ToArray(),
                components = ComponentsPayload(snapshot.Hardware.Components)
            },
            software = snapshot.Software.Select(item => new
            {
                name = item.Name,
                version = item.Version,
                publisher = item.Publisher,
                source = item.Source
            }).ToArray(),
            activation = ActivationPayload(snapshot.Activation)
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AgentConfiguration.DefaultApiPath}/heartbeat")
        {
            Content = JsonContent.Create(payload, options: JsonDefaults.Options)
        };
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Fleet Manager returned {(int)response.StatusCode}: {body}", null, response.StatusCode);
        }
        _logger.Info("Heartbeat and inventory uploaded successfully.");
    }

    public async Task<AgentRegistrationResult> RegisterAsync(string enrollmentToken, string machineId, string hostname, string? sshLogin, CancellationToken cancellationToken)
    {
        var payload = new
        {
            enrollment_token = enrollmentToken,
            machine_id = machineId,
            hostname,
            ssh_login = sshLogin,
            ip_address = (string?)null,
            os = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "windows_11" : "windows_10",
            agent_version = AgentVersion.Current
        };
        using var response = await _httpClient.PostAsJsonAsync($"{AgentConfiguration.DefaultApiPath}/register", payload, JsonDefaults.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Fleet Manager registration returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AgentRegistrationResult>(JsonDefaults.Options, cancellationToken)
               ?? throw new InvalidOperationException("Fleet Manager returned an empty registration response.");
    }

    public async Task SendAlertAsync(AgentSnapshot snapshot, string token, string previousFingerprint, CancellationToken cancellationToken)
    {
        var payload = new
        {
            machine_id = snapshot.Status.MachineId,
            alert_type = "hardware_changed",
            message = "Hardware fingerprint changed.",
            previous_fingerprint = previousFingerprint,
            current_fingerprint = snapshot.Hardware.Fingerprint
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AgentConfiguration.DefaultApiPath}/alerts") { Content = JsonContent.Create(payload, options: JsonDefaults.Options) };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendOfflineAsync(string machineId, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AgentConfiguration.DefaultApiPath}/offline")
        {
            Content = JsonContent.Create(new { machine_id = machineId }, options: JsonDefaults.Options)
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
