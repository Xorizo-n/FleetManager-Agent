using FleetManager.Agent.Core;
using Microsoft.Extensions.Hosting;

namespace FleetManager.Agent.Service;

public sealed class AgentWorker : BackgroundService
{
    private readonly AgentState _state;
    private readonly AgentLogger _logger;
    private readonly IInventoryCollector _inventory;
    private readonly ServerRouteKeeper _serverRoute;
    private readonly WallpaperManager _wallpapers;
    private readonly SshHealthChecker _sshHealth;
    private readonly AgentSelfUpdate _selfUpdate;
    // The periodic loop and "sync" from Tray/Control may ask at the same time; the run
    // mutates _options and the state, so syncs are serialized.
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private AgentOptions _options;
    private bool _renameWaitLogged;

    public AgentWorker(AgentState state, AgentLogger logger, IInventoryCollector inventory, ServerRouteKeeper serverRoute)
    {
        _state = state;
        _logger = logger;
        _inventory = inventory;
        _serverRoute = serverRoute;
        _options = AgentOptions.Load(_state.DataDirectory);
        _wallpapers = new WallpaperManager(_state.DataDirectory, logger);
        _state.SetWallpaperRevision(_wallpapers.DesktopRevision);
        _sshHealth = new SshHealthChecker(logger);
        _selfUpdate = new AgentSelfUpdate(_state.DataDirectory, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options = AgentOptions.Load(_state.DataDirectory);
        _state.MarkRunning(_options.ServerUrl);
        _logger.Info("FleetManager Agent service started.");
        _serverRoute.Start(() => _options);

        var pipeTask = RunPipeAsync(stoppingToken);
        await SyncOnceAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = SyncSchedule.NextDelay(_options);
                _state.SetNextSync(DateTimeOffset.UtcNow + delay);
                await Task.Delay(delay, stoppingToken);
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
        try { await _syncGate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }
        _state.MarkSyncStarted();
        try
        {
            _options = AgentOptions.Load(_state.DataDirectory);
            using var client = string.IsNullOrWhiteSpace(_options.ServerUrl)
                ? null
                : new HttpClient { BaseAddress = new Uri(AgentConfiguration.NormalizeServerUrl(_options.ServerUrl) + "/") };
            var api = client is null ? null : new FleetManagerApiClient(client, _logger);
            // Before talking to the server: with a full-tunnel VPN up it must go over the LAN.
            if (api is not null) await _serverRoute.EnsureAsync(cancellationToken);
            // Registration first: until it succeeds there is nowhere to send the inventory,
            // and collecting it on every one-minute retry would only load the PC.
            if (api is not null && !await EnsureRegistrationAsync(api, cancellationToken)) return;

            // sshd — единственный канал управления ПК: обновление Windows может остановить его и
            // перевести на ручной запуск. Сбой проверки не срывает heartbeat.
            SshServiceReport? ssh = null;
            try { ssh = SshService.Ensure(_options.KeepSshRunning, _logger); }
            catch (Exception ex) { _logger.Warn($"Unable to check sshd: {ex.Message}"); }
            // Брандмауэр, PATH, sshd_config, права входа — то, из-за чего сервер не попадает на ПК при живом sshd
            if (ssh is not null)
            {
                try { ssh = ssh with { Health = await _sshHealth.CheckAsync(_options, _options.KeepSshRunning, cancellationToken) }; }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"Unable to check SSH health: {ex.Message}"); }
            }

            var hardware = await _inventory.CollectHardwareAsync(cancellationToken);
            var software = await _inventory.CollectSoftwareAsync(cancellationToken);
            var activation = await _inventory.CollectActivationAsync(cancellationToken);
            if (api is not null)
            {
                EnsureSshKeyInstalled();
                var wallpaper = await _wallpapers.ReportAsync(cancellationToken);
                var snapshot = new AgentSnapshot(_state.Status, hardware, software) { Activation = activation, Wallpaper = wallpaper, Ssh = ssh };
                var result = await api.SendHeartbeatAsync(snapshot, _options.AgentToken, _options.SshLogin, cancellationToken);
                // Точка раздачи, до которой VPN не должен перехватывать трафик: маршрут нужен уже самообновлению ниже
                if (_serverRoute.SetPins(result.PinRoutes ?? Array.Empty<PinRoute>())) await _serverRoute.EnsureAsync(cancellationToken);
                // Назначенные сервером обои применяются сразу; что получилось — уйдёт в следующем heartbeat
                if (result.Wallpapers is { } desired && !string.IsNullOrWhiteSpace(_options.AgentToken))
                {
                    var token = _options.AgentToken;
                    await _wallpapers.ApplyAsync(desired, (id, path, ct) => api.DownloadWallpaperAsync(id, token, path, ct), cancellationToken);
                    _state.SetWallpaperRevision(_wallpapers.DesktopRevision);
                }
                // Обновление без SSH: сервер предлагает его, когда до ПК не достучаться. Сбой не срывает синхронизацию.
                if (!string.IsNullOrWhiteSpace(_options.AgentToken)
                    && AgentSelfUpdate.ShouldInstall(AgentVersion.Current, result.Update, _selfUpdate.LoadState(), DateTimeOffset.UtcNow))
                {
                    var token = _options.AgentToken;
                    try { await _selfUpdate.RunAsync(result.Update!, (path, ct) => api.DownloadInstallerAsync(token, path, ct), cancellationToken, api.DownloadFromUrlAsync); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"Agent self-update failed: {ex.Message}"); }
                }
                await _serverRoute.ConfirmPathAsync(cancellationToken);
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
            _state.MarkSync(DateTimeOffset.UtcNow, software.Count, hardware,
                api is null ? AgentIssue.ServerNotConfigured : AgentIssue.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _state.MarkError(ex);
            _logger.Error("Inventory synchronization failed", ex);
        }
        finally
        {
            _state.MarkSyncFinished();
            _syncGate.Release();
        }
    }

    /// <summary>Registers the agent if needed; false while registration has to wait.</summary>
    private async Task<bool> EnsureRegistrationAsync(FleetManagerApiClient api, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.AgentToken)) return true;
        if (string.IsNullOrWhiteSpace(_options.EnrollmentToken))
            throw new AgentIssueException(AgentIssue.NotEnrolled, "Enrollment token is not configured.");
        // The host is registered under Environment.MachineName. Right after a rename (e.g. the
        // AutoDomain domain join) that is still the old name until the reboot, so wait for it.
        if (ComputerRename.IsPending())
        {
            if (!_renameWaitLogged)
            {
                _logger.Info("Computer rename is pending a reboot; registration waits until the new name is active.");
                _renameWaitLogged = true;
            }
            _state.MarkWaiting(AgentIssue.AwaitingReboot);
            return false;
        }
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
        return true;
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
