using System.Net;
using System.Net.NetworkInformation;
using FleetManager.Agent.Core;
using Microsoft.Extensions.Hosting;

namespace FleetManager.Agent.Service;

public sealed class AgentWorker : BackgroundService
{
    // Each collection step has its own limit: a hung WMI query or PowerShell used to hold the heartbeat
    // forever, and the PC looked offline while the agent ran. A step past its limit gives the last known
    // data (or none) and the heartbeat goes on.
    private static readonly TimeSpan SshStepTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HardwareStepTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SoftwareStepTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SmallStepTimeout = TimeSpan.FromMinutes(1);
    // A sync running longer than this is hung (self-update, the longest step, gives up after 15 min of download and
    // 15 of installation): the service exits with an error and the SCM restarts it a minute later.
    internal static readonly TimeSpan WatchdogLimit = TimeSpan.FromMinutes(40);
    // 401 this many times in a row — the server no longer knows the token: register again with the enrollment token.
    internal const int UnauthorizedBeforeReRegistration = 3;
    private static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(10);

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
    // Wakes the loop before its delay ends: the network came back while the server was unreachable.
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Timer _networkSettle;
    private Timer? _watchdog;
    private AgentOptions _options;
    private bool _renameWaitLogged;
    private int _failures;
    private int _unauthorized;
    private long _syncStartedAt;
    private IReadOnlyList<SoftwareEntry>? _lastSoftware;

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
        _networkSettle = new Timer(_ => Wake());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options = AgentOptions.Load(_state.DataDirectory);
        _state.MarkRunning(_options.ServerUrl);
        _logger.Info("FleetManager Agent service started.");
        _serverRoute.Start(() => _options);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _watchdog = new Timer(_ => CheckWatchdog(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        var pipeTask = RunPipeAsync(stoppingToken);
        await SyncOnceAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = SyncSchedule.NextDelay(_options, _failures);
                _state.SetNextSync(DateTimeOffset.UtcNow + delay);
                await _wake.WaitAsync(delay, stoppingToken);
                await SyncOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            _watchdog?.Dispose();
            _state.MarkStopping();
            await SendOfflineAsync();
            try { await pipeTask; } catch (OperationCanceledException) { }
            _state.MarkOffline();
            _logger.Info("FleetManager Agent service stopped.");
        }
    }

    // The network changed while the server was unreachable (the cable came back, the VPN dropped): try again
    // once it settles instead of waiting for the retry delay.
    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _failures) > 0) _networkSettle.Change(NetworkSettle, Timeout.InfiniteTimeSpan);
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void CheckWatchdog()
    {
        var started = Interlocked.Read(ref _syncStartedAt);
        if (started == 0 || DateTime.UtcNow.Ticks - started < WatchdogLimit.Ticks) return;
        _logger.Error($"Synchronization has been running for over {WatchdogLimit.TotalMinutes:0} minutes and is considered hung; the service exits so that Windows restarts it.");
        // Recovery actions of the service (sc.exe failure, set by the installer) restart it a minute later
        Environment.Exit(1);
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
        Interlocked.Exchange(ref _syncStartedAt, DateTime.UtcNow.Ticks);
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

            // A sign of life before the slow collection. The server does not answer — no point collecting the
            // inventory for it: retry soon (SyncSchedule), and the PC reports as soon as the network is back.
            if (api is not null && !await PingAsync(api, cancellationToken)) return;

            // sshd — единственный канал управления ПК: обновление Windows может остановить его и
            // перевести на ручной запуск. Сбой проверки не срывает heartbeat.
            var ssh = await StepAsync("sshd", SshStepTimeout, async ct =>
            {
                var report = await Task.Run(() => SshService.Ensure(_options.KeepSshRunning, _logger), ct);
                // Брандмауэр, PATH, sshd_config, права входа — то, из-за чего сервер не попадает на ПК при живом sshd
                return report with { Health = await _sshHealth.CheckAsync(_options, _options.KeepSshRunning, ct) };
            }, null, cancellationToken);

            var hardware = await StepAsync("hardware", HardwareStepTimeout, _inventory.CollectHardwareAsync, _state.Hardware, cancellationToken)
                ?? throw new InvalidOperationException("Hardware inventory is not available yet.");
            var software = await StepAsync<IReadOnlyList<SoftwareEntry>?>("software", SoftwareStepTimeout,
                async ct => await _inventory.CollectSoftwareAsync(ct), _lastSoftware, cancellationToken);
            var softwareCollected = software is not null;
            if (software is not null) _lastSoftware = software;
            var activation = await StepAsync("activation", SmallStepTimeout, _inventory.CollectActivationAsync, null, cancellationToken);
            var power = await StepAsync("power", SmallStepTimeout, ct => PowerState.CollectAsync(_logger, ct), null, cancellationToken);
            if (api is not null)
            {
                EnsureSshKeyInstalled();
                var wallpaper = await StepAsync("wallpaper", SmallStepTimeout, _wallpapers.ReportAsync, null, cancellationToken);
                var snapshot = new AgentSnapshot(_state.Status, hardware, software ?? Array.Empty<SoftwareEntry>())
                {
                    Activation = activation, Wallpaper = wallpaper, Ssh = ssh, Power = power, SoftwareCollected = softwareCollected,
                };
                FleetManagerApiClient.HeartbeatResult result;
                try { result = await api.SendHeartbeatAsync(snapshot, _options.AgentToken, _options.SshLogin, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    NoteFailure(ex);
                    throw;
                }
                Interlocked.Exchange(ref _failures, 0);
                _unauthorized = 0;
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
            _state.MarkSync(DateTimeOffset.UtcNow, (software ?? Array.Empty<SoftwareEntry>()).Count, hardware,
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
            Interlocked.Exchange(ref _syncStartedAt, 0);
            _state.MarkSyncFinished();
            _syncGate.Release();
        }
    }

    /// <summary>
    /// One collection step with its own limit. Past the limit or on an error — <paramref name="fallback"/> (the last
    /// known data or null): the heartbeat still goes, with what there is.
    /// </summary>
    private async Task<T?> StepAsync<T>(string name, TimeSpan limit, Func<CancellationToken, Task<T>> work, T? fallback, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            // WaitAsync: a step that ignores the token (a hung COM call) must not hold the sync either
            return await work(timeout.Token).WaitAsync(limit, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var reason = ex is TimeoutException or OperationCanceledException ? $"did not finish in {limit.TotalSeconds:0} s" : ex.Message;
            _logger.Warn($"Collecting {name} failed ({reason}); the heartbeat goes with {(fallback is null ? "no" : "the last known")} data.");
            return fallback;
        }
    }

    /// <summary>True — the server answered (or has no ping yet); false — unreachable, the sync stops here.</summary>
    private async Task<bool> PingAsync(FleetManagerApiClient api, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AgentToken)) return true;
        try
        {
            await api.SendPingAsync(MachineIdentity.GetOrCreate(_state.DataDirectory), _options.AgentToken, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            NoteFailure(ex);
            _state.MarkError(ex);
            if (_failures == 1 || _failures % 20 == 0) _logger.Warn($"Fleet Manager server does not answer ({ex.Message}); retrying soon.");
            return false;
        }
    }

    private void NoteFailure(Exception ex)
    {
        Interlocked.Increment(ref _failures);
        if (ex is not HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }) return;
        _unauthorized++;
        if (_unauthorized < UnauthorizedBeforeReRegistration) return;
        if (string.IsNullOrWhiteSpace(_options.EnrollmentToken))
        {
            if (_unauthorized == UnauthorizedBeforeReRegistration)
                _logger.Error("Fleet Manager no longer accepts the agent token, and there is no enrollment token to register again: reinstall the agent with /EnrollmentToken.");
            return;
        }
        // The host record may still be there (the server adopts a stale record of the same name) or be gone — either
        // way registration gives a working token, the same as a reinstall would.
        _logger.Warn("Fleet Manager rejected the agent token several times in a row; registering again with the enrollment token.");
        _options.AgentToken = null;
        _options.Save(_state.DataDirectory);
        _unauthorized = 0;
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
        // The enrollment token stays in agent.json (readable only by SYSTEM and Administrators): with it the agent
        // registers again if the server stops accepting its token, instead of dropping out until a reinstall.
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

    public override void Dispose()
    {
        _networkSettle.Dispose();
        _watchdog?.Dispose();
        base.Dispose();
    }
}
