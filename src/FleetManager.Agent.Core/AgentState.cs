namespace FleetManager.Agent.Core;

public sealed class AgentState
{
    private readonly object _gate = new();
    private AgentStatus _status;
    private HardwareSnapshot? _hardware;
    private int _softwareCount;

    public AgentState(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? AgentConfiguration.DefaultDataDirectory;
        var options = AgentOptions.Load(DataDirectory);
        var machineId = MachineIdentity.GetOrCreate(DataDirectory);
        _status = new AgentStatus(machineId, Environment.MachineName, AgentLifecycleState.Starting,
            DateTimeOffset.UtcNow, null, null, null, options.ServerUrl, 0, null);
    }

    public string DataDirectory { get; }

    public AgentStatus Status { get { lock (_gate) return _status; } }

    public HardwareSnapshot? Hardware { get { lock (_gate) return _hardware; } }

    public void MarkRunning(string? serverUrl = null)
    {
        lock (_gate)
        {
            _status = _status with { State = AgentLifecycleState.Running, ServerUrl = serverUrl ?? _status.ServerUrl };
        }
    }

    public void SetServerUrl(string serverUrl)
    {
        lock (_gate)
        {
            _status = _status with { ServerUrl = serverUrl };
        }
    }

    public void MarkSync(DateTimeOffset at, int softwareCount, HardwareSnapshot? hardware)
    {
        lock (_gate)
        {
            _hardware = hardware ?? _hardware;
            _softwareCount = softwareCount;
            _status = _status with
            {
                State = AgentLifecycleState.Running,
                LastSyncAt = at,
                LastError = null,
                LastErrorAt = null,
                SoftwareCount = _softwareCount,
                HardwareFingerprint = _hardware?.Fingerprint
            };
        }
    }

    public void MarkError(Exception exception)
    {
        lock (_gate)
        {
            _status = _status with { State = AgentLifecycleState.Degraded, LastErrorAt = DateTimeOffset.UtcNow, LastError = exception.Message };
        }
    }

    public void MarkStopping() { lock (_gate) _status = _status with { State = AgentLifecycleState.Stopping }; }
    public void MarkOffline() { lock (_gate) _status = _status with { State = AgentLifecycleState.Offline }; }
}
