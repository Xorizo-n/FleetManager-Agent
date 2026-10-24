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

    internal static object? WallpaperPayload(WallpaperReport? report) => report is null ? null : new
    {
        desktop = Current(report.Desktop),
        lock_screen = Current(report.LockScreen),
        applied_desktop_id = report.AppliedDesktopId,
        applied_lock_screen_id = report.AppliedLockScreenId,
        error = report.Error,
        thumbnails = report.Thumbnails.Select(t => new { sha256 = t.Sha256, jpeg_base64 = t.JpegBase64 }).ToArray()
    };

    private static object? Current(CurrentWallpaper? current) =>
        current is null ? null : new { path = current.Path, sha256 = current.Sha256, source = current.Source };

    /// <summary>
    /// Назначенные обои из ответа на heartbeat. null — сервер о них не сообщил (старая версия сервера):
    /// тогда агент ничего не меняет. Поле desktop/lock_screen = null — обоями сервер не управляет.
    /// </summary>
    internal static DesiredWallpapers? ParseDesiredWallpapers(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("wallpaper", out var wallpaper) || wallpaper.ValueKind != JsonValueKind.Object) return null;
            var thumbnails = wallpaper.TryGetProperty("thumbnails", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(8).ToArray()
                : Array.Empty<string>();
            return new DesiredWallpapers(WallpaperImageOf(wallpaper, "desktop"), WallpaperImageOf(wallpaper, "lock_screen"), thumbnails);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WallpaperImage? WallpaperImageOf(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object) return null;
        var id = e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        var sha = e.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit)) return null;
        var style = e.TryGetProperty("style", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() is { Length: > 0 and <= 2 } value && value.All(char.IsDigit) ? value : "4";
        var fileName = e.TryGetProperty("file_name", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        return new WallpaperImage(id, sha.ToLowerInvariant(), fileName, style);
    }

    /// <summary>Скачивает изображение каталога своим agent-токеном.</summary>
    public async Task DownloadWallpaperAsync(string id, string token, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{AgentConfiguration.DefaultApiPath}/wallpapers/{Uri.EscapeDataString(id)}");
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Fleet Manager returned {(int)response.StatusCode} for wallpaper {id}", null, response.StatusCode);
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, cancellationToken);
    }

    /// <returns>Назначенные обои из ответа сервера; null — сервер о них не сообщил.</returns>
    /// <summary>What the server wants from the agent in its heartbeat response; null fields — nothing asked.</summary>
    public sealed record HeartbeatResult(DesiredWallpapers? Wallpapers, AgentUpdateOffer? Update);

    internal static HeartbeatResult ParseHeartbeatResponse(string? body)
    {
        AgentUpdateOffer? update = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object) update = AgentSelfUpdate.ParseOffer(document.RootElement);
            }
            catch (JsonException) { }
        }
        return new HeartbeatResult(ParseDesiredWallpapers(body), update);
    }

    /// <summary>The installer the server serves to its agents, downloaded with the agent's own token.</summary>
    public async Task DownloadInstallerAsync(string token, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{AgentConfiguration.DefaultApiPath}/installer");
        request.Headers.Authorization = new("Bearer", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Fleet Manager returned {(int)response.StatusCode} for the agent installer", null, response.StatusCode);
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, timeout.Token);
    }

    private static object? SshPayload(SshServiceReport? ssh)
    {
        if (ssh is null) return null;
        var health = ssh.Health;
        return new
        {
            installed = ssh.Installed,
            start_type = ssh.StartType,
            listening = ssh.Listening,
            responding = ssh.Responding,
            repaired = ssh.Repaired.Concat(health?.Repaired ?? Array.Empty<string>()).ToArray(),
            firewall_rule = health?.FirewallRule,
            firewall_ignored_on = health?.FirewallIgnoredOn,
            firewall_gpo_allows = health?.FirewallGpoAllows,
            firewall_blocking = health?.FirewallBlocking,
            firewall_error = health?.FirewallError,
            network_profiles = health?.NetworkProfiles,
            config_valid = health?.ConfigValid,
            config_port = health?.ConfigPort,
            path_ok = health?.PathOk,
            login_is_admin = health?.LoginIsAdmin,
            domain_trust = health?.DomainTrust,
            host_keys = health?.HostKeys,
        };
    }

    public async Task<HeartbeatResult> SendHeartbeatAsync(AgentSnapshot snapshot, string? token, string? sshLogin, CancellationToken cancellationToken)
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
            activation = ActivationPayload(snapshot.Activation),
            wallpaper = WallpaperPayload(snapshot.Wallpaper),
            ssh = SshPayload(snapshot.Ssh)
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
        return ParseHeartbeatResponse(await response.Content.ReadAsStringAsync(cancellationToken));
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
