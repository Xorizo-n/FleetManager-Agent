using System.Text.Json.Serialization;

namespace FleetManager.Agent.Core;

public enum AgentLifecycleState
{
    Starting,
    Running,
    Degraded,
    Stopping,
    Offline
}

public sealed record AgentStatus(
    string MachineId,
    string ComputerName,
    AgentLifecycleState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastErrorAt,
    string? LastError,
    string ServerUrl,
    int SoftwareCount,
    string? HardwareFingerprint);

public sealed record HardwareSnapshot(
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? OperatingSystem,
    string? Processor,
    long? TotalMemoryBytes,
    string? Fingerprint);

public sealed record SoftwareEntry(
    string Name,
    string? Version,
    string? Publisher,
    string Source);

public sealed record AgentSnapshot(
    AgentStatus Status,
    HardwareSnapshot Hardware,
    IReadOnlyList<SoftwareEntry> Software);

public sealed record AgentPipeRequest(string Command, string? Value = null);

public sealed record AgentPipeResponse(bool Success, string? Error = null, object? Data = null);

public sealed record AgentRegistrationResult(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("agent_token")] string AgentToken,
    [property: JsonPropertyName("host_id")] string HostId,
    [property: JsonPropertyName("hostname")] string? Hostname,
    [property: JsonPropertyName("ip_address")] string? IpAddress,
    [property: JsonPropertyName("ssh_public_key")] string? SshPublicKey,
    [property: JsonPropertyName("ssh_login")] string? SshLogin);
