using System.Text.Json;
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

/// <summary>
/// Why the last synchronization did not reach the server. Tray and Control turn it into
/// a human-readable state, so a network outage is not reported like a rejected token.
/// </summary>
public enum AgentIssue
{
    None,
    /// <summary>Server URL is not configured; inventory is collected locally only.</summary>
    ServerNotConfigured,
    /// <summary>Neither an agent token nor an enrollment token: the agent cannot register.</summary>
    NotEnrolled,
    /// <summary>Registration waits for the reboot that applies a pending computer rename.</summary>
    AwaitingReboot,
    /// <summary>Network, DNS or timeout: the server could not be reached.</summary>
    ServerUnreachable,
    /// <summary>The server answered with an HTTP error.</summary>
    ServerRejected,
    Failed
}

// Fields after HardwareFingerprint are appended with defaults so the record stays
// source-compatible; the pipe serializes it by name, so their order is irrelevant there.
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
    string? HardwareFingerprint,
    AgentIssue Issue = AgentIssue.None,
    bool IsSyncing = false,
    DateTimeOffset? NextSyncAt = null,
    string? AgentVersion = null);

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

public sealed record AgentPipeResponse(bool Success, string? Error = null, object? Data = null)
{
    /// <summary>
    /// Typed view of <see cref="Data"/>: on the client side it arrives as a JsonElement.
    /// Never pass Data to the UI as-is — its ToString() is the raw JSON.
    /// </summary>
    public T? DataAs<T>() => Data switch
    {
        T value => value,
        JsonElement { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element => element.Deserialize<T>(JsonDefaults.Options),
        _ => default
    };
}

public sealed record AgentRegistrationResult(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("agent_token")] string AgentToken,
    [property: JsonPropertyName("host_id")] string HostId,
    [property: JsonPropertyName("hostname")] string? Hostname,
    [property: JsonPropertyName("ip_address")] string? IpAddress,
    [property: JsonPropertyName("ssh_public_key")] string? SshPublicKey,
    [property: JsonPropertyName("ssh_login")] string? SshLogin);
