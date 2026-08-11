using FleetManager.Agent.Core;
using Microsoft.Extensions.Hosting;

namespace FleetManager.Agent.Service;

public sealed class AgentWorker : BackgroundService
{
    private readonly AgentState _state;
    private readonly AgentLogger _logger;
    private readonly IInventoryCollector _inventory;
    private AgentOptions _options;

    public AgentWorker(AgentState state, AgentLogger logger, IInventoryCollector inventory)
    {
        _state = state;
        _logger = logger;
        _inventory = inventory;
        _options = AgentOptions.Load(_state.DataDirectory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options = AgentOptions.Load(_state.DataDirectory);
        _state.MarkRunning(_options.ServerUrl);
        _logger.Info("FleetManager Agent service started.");

        var pipeTask = RunPipeAsync(stoppingToken);
        await SyncOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(_options.SyncIntervalMinutes, 1, 1440)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SyncOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _state.MarkStopping();
            await SendOfflineAsync();
            try { await pipeTask; } catch (OperationCanceledException) { }
            _state.MarkOffline();
            _logger.Info("FleetManager Agent service stopped.");
        }
    }

    private async Task RunPipeAsync(CancellationToken stoppingToken)
    {
        var server = new AgentPipeServer(_state, _logger, SyncOnceAsync, stoppingToken);
        await server.RunAsync();
    }

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _options = AgentOptions.Load(_state.DataDirectory);
            var hardware = await _inventory.CollectHardwareAsync(cancellationToken);
            var software = await _inventory.CollectSoftwareAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(_options.ServerUrl))
            {
                using var client = new HttpClient { BaseAddress = new Uri(AgentConfiguration.NormalizeServerUrl(_options.ServerUrl) + "/") };
                var api = new FleetManagerApiClient(client, _logger);
                await EnsureRegistrationAsync(api, cancellationToken);
                EnsureSshKeyInstalled();
                var snapshot = new AgentSnapshot(_state.Status, hardware, software);
                await api.SendHeartbeatAsync(snapshot, _options.AgentToken, _options.SshLogin, cancellationToken);
                var previousFingerprint = _state.Hardware?.Fingerprint;
                if (!string.IsNullOrWhiteSpace(previousFingerprint) && previousFingerprint != hardware.Fingerprint && !string.IsNullOrWhiteSpace(_options.AgentToken))
                {
                    await api.SendAlertAsync(snapshot, _options.AgentToken, previousFingerprint, cancellationToken);
                    _logger.Warn("Hardware fingerprint changed; alert sent to Fleet Manager.");
                }
            }
            else
            {
                _logger.Warn("Server URL is not configured; inventory collected locally only.");
            }
            _state.MarkSync(DateTimeOffset.UtcNow, software.Count, hardware);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _state.MarkError(ex);
            _logger.Error("Inventory synchronization failed", ex);
        }
    }

    private async Task EnsureRegistrationAsync(FleetManagerApiClient api, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.AgentToken)) return;
        if (string.IsNullOrWhiteSpace(_options.EnrollmentToken))
            throw new InvalidOperationException("Enrollment token is not configured.");
        var registration = await api.RegisterAsync(
            _options.EnrollmentToken,
            MachineIdentity.GetOrCreate(_state.DataDirectory),
            Environment.MachineName,
            _options.SshLogin,
            cancellationToken);
        _options.AgentId = registration.AgentId;
        _options.AgentToken = registration.AgentToken;
        _options.SshPublicKey = registration.SshPublicKey;
        _options.SshLogin = registration.SshLogin;
        if (!string.IsNullOrWhiteSpace(registration.SshPublicKey))
        {
            WindowsSshKeyInstaller.Install(registration.SshPublicKey);
            _logger.Info("Fleet Manager SSH public key installed for local administrators.");
        }
        _options.EnrollmentToken = null;
        _options.Save(_state.DataDirectory);
        _logger.Info($"Agent registered as host {registration.HostId}.");
    }

    private void EnsureSshKeyInstalled()
    {
        if (string.IsNullOrWhiteSpace(_options.SshPublicKey)) return;
        WindowsSshKeyInstaller.Install(_options.SshPublicKey);
    }

    private async Task SendOfflineAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ServerUrl) || string.IsNullOrWhiteSpace(_options.AgentToken)) return;
            using var client = new HttpClient { BaseAddress = new Uri(AgentConfiguration.NormalizeServerUrl(_options.ServerUrl) + "/") };
            var api = new FleetManagerApiClient(client, _logger);
            await api.SendOfflineAsync(MachineIdentity.GetOrCreate(_state.DataDirectory), _options.AgentToken, CancellationToken.None);
        }
        catch (Exception ex) { _logger.Warn($"Unable to send offline status: {ex.Message}"); }
    }
}
