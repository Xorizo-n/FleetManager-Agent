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
            hardware = new
            {
                manufacturer = snapshot.Hardware.Manufacturer,
                model = snapshot.Hardware.Model,
                serial_number = snapshot.Hardware.SerialNumber,
                operating_system = snapshot.Hardware.OperatingSystem,
                processor = snapshot.Hardware.Processor,
                total_memory_bytes = snapshot.Hardware.TotalMemoryBytes,
                fingerprint = snapshot.Hardware.Fingerprint
            },
            software = snapshot.Software.Select(item => new
            {
                name = item.Name,
                version = item.Version,
                publisher = item.Publisher,
                source = item.Source
            }).ToArray()
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
            throw new HttpRequestException($"Fleet Manager returned {(int)response.StatusCode}: {body}");
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
            os = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "windows_11" : "windows_10"
        };
        using var response = await _httpClient.PostAsJsonAsync($"{AgentConfiguration.DefaultApiPath}/register", payload, JsonDefaults.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Fleet Manager registration returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
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
