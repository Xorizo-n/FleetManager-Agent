using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

#pragma warning disable CA1416

namespace FleetManager.Agent.Core;

public sealed class AgentPipeClient
{
    // The server always keeps an instance listening, so a running service is connected to at
    // once; a long wait only delays reporting a stopped one (tray tooltip, control panel).
    private const int ConnectTimeoutMs = 1500;

    // Pipe uses line-framing (WriteLineAsync / ReadLineAsync).
    // WriteIndented MUST be false here — indented JSON spans multiple lines and
    // ReadLineAsync would return only the opening "{", breaking deserialization.
    // This is intentionally separate from JsonDefaults.Options so global options
    // cannot accidentally break the pipe protocol.
    private static readonly JsonSerializerOptions JsonWireOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;

    /// <param name="pipeName">Overridden only by tests, so they never talk to an installed agent.</param>
    public AgentPipeClient(string pipeName = AgentConfiguration.PipeName) => _pipeName = pipeName;

    public async Task<AgentPipeResponse> SendAsync(AgentPipeRequest request, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonWireOptions));
        var line = await reader.ReadLineAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(line)
            ? new AgentPipeResponse(false, "No response from agent service.")
            : JsonSerializer.Deserialize<AgentPipeResponse>(line, JsonWireOptions)
              ?? new AgentPipeResponse(false, "Invalid response from agent service.");
    }

    public Task<AgentPipeResponse> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new AgentPipeRequest("status"), cancellationToken);

    /// <summary>Current service status, or null when the service does not answer.</summary>
    public async Task<AgentStatus?> TryGetStatusAsync(TimeSpan timeout)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            var response = await GetStatusAsync(cancellation.Token);
            return response.Success ? response.DataAs<AgentStatus>() : null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public Task<AgentPipeResponse> GetLogsAsync(int lines = 200, CancellationToken cancellationToken = default) =>
        SendAsync(new AgentPipeRequest("logs", lines.ToString()), cancellationToken);

    public Task<AgentPipeResponse> SetServerUrlAsync(string url, CancellationToken cancellationToken = default) =>
        SendAsync(new AgentPipeRequest("set-server-url", url), cancellationToken);

    public Task<AgentPipeResponse> SyncAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new AgentPipeRequest("sync"), cancellationToken);
}

public sealed class AgentPipeServer
{
    // Same constraint as AgentPipeClient.JsonWireOptions — must not be indented.
    private static readonly JsonSerializerOptions JsonWireOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AgentState _state;
    private readonly AgentLogger _logger;
    private readonly Func<CancellationToken, Task> _sync;
    private readonly CancellationToken _stoppingToken;
    private readonly string _pipeName;

    public AgentPipeServer(AgentState state, AgentLogger logger, Func<CancellationToken, Task> sync, CancellationToken stoppingToken,
        string pipeName = AgentConfiguration.PipeName)
    {
        _state = state;
        _logger = logger;
        _sync = sync;
        _stoppingToken = stoppingToken;
        _pipeName = pipeName;
    }

    // "sync" holds its connection until the synchronization finishes (up to minutes). With a
    // single instance every other client — the tray polling "status" — timed out meanwhile and
    // reported the service as unavailable, so clients are served concurrently.
    private const int MaxInstances = 8;

    public async Task RunAsync()
    {
        while (!_stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 0, 0, CreatePipeSecurity());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // All instances are busy: wait for one to be released.
                try { await Task.Delay(250, _stoppingToken); } catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_stoppingToken);
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                await pipe.DisposeAsync();
                _logger.Error("Named pipe connection failed", ex);
                continue;
            }
            _ = ServeAsync(pipe);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try { await HandleAsync(pipe, _stoppingToken); }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { _logger.Error("Named pipe request failed", ex); }
        }
    }

    // The service runs as LocalSystem; the default pipe DACL only grants access to the
    // creator, so an unelevated tray process can't connect and always sees "недоступна".
    // Grant authenticated users read/write explicitly (command-level authorization for
    // privileged actions like set-server-url still happens in ExecuteAsync via IsAdministrator).
    // The account running the server needs CreateNewInstance for every instance after the first;
    // LocalSystem has it through FullControl, the explicit rule keeps that true for any account.
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        using (var current = WindowsIdentity.GetCurrent())
        {
            if (current.User is { } user)
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync(cancellationToken);
        AgentPipeResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<AgentPipeRequest>(line ?? string.Empty, JsonWireOptions)
                          ?? throw new InvalidOperationException("Invalid command.");
            var isAdministrator = IsAdministrator(pipe);
            response = await ExecuteAsync(request, isAdministrator, cancellationToken);
        }
        catch (Exception ex)
        {
            response = new AgentPipeResponse(false, ex.Message);
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonWireOptions));
    }

    private async Task<AgentPipeResponse> ExecuteAsync(AgentPipeRequest request, bool isAdministrator, CancellationToken cancellationToken)
    {
        return request.Command.ToLowerInvariant() switch
        {
            "status" => new AgentPipeResponse(true, Data: _state.Status),
            "logs" => new AgentPipeResponse(true, Data: _logger.Tail(int.TryParse(request.Value, out var n) ? n : 200)),
            "set-server-url" => isAdministrator ? SetServerUrl(request.Value) : new AgentPipeResponse(false, "Administrator privileges are required."),
            "sync" => await TriggerSyncAsync(cancellationToken),
            _ => new AgentPipeResponse(false, $"Unknown command '{request.Command}'.")
        };
    }

    private static bool IsAdministrator(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var administrator = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch { }
        return administrator;
    }

    private AgentPipeResponse SetServerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new AgentPipeResponse(false, "Server URL is required.");
        var options = AgentOptions.Load(_state.DataDirectory);
        options.ServerUrl = AgentConfiguration.NormalizeServerUrl(value);
        options.Save(_state.DataDirectory);
        _state.SetServerUrl(options.ServerUrl);
        _logger.Info($"Server URL changed to {options.ServerUrl}.");
        return new AgentPipeResponse(true, Data: options);
    }

    private async Task<AgentPipeResponse> TriggerSyncAsync(CancellationToken cancellationToken)
    {
        await _sync(cancellationToken);
        return new AgentPipeResponse(true, Data: _state.Status);
    }
}

#pragma warning restore CA1416
