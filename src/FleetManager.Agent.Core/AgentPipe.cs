using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

#pragma warning disable CA1416

namespace FleetManager.Agent.Core;

public sealed class AgentPipeClient
{
    // Pipe uses line-framing (WriteLineAsync / ReadLineAsync).
    // WriteIndented MUST be false here — indented JSON spans multiple lines and
    // ReadLineAsync would return only the opening "{", breaking deserialization.
    // This is intentionally separate from JsonDefaults.Options so global options
    // cannot accidentally break the pipe protocol.
    private static readonly JsonSerializerOptions JsonWireOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AgentPipeResponse> SendAsync(AgentPipeRequest request, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", AgentConfiguration.PipeName, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, cancellationToken);
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

    public AgentPipeServer(AgentState state, AgentLogger logger, Func<CancellationToken, Task> sync, CancellationToken stoppingToken)
    {
        _state = state;
        _logger = logger;
        _sync = sync;
        _stoppingToken = stoppingToken;
    }

    public async Task RunAsync()
    {
        while (!_stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(AgentConfiguration.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_stoppingToken);
                await HandleAsync(pipe, _stoppingToken);
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.Error("Named pipe request failed", ex); }
        }
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
