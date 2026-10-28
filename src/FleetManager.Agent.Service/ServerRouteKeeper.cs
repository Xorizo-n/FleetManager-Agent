using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using FleetManager.Agent.Core;

namespace FleetManager.Agent.Service;

/// <summary>
/// Maintains the /32 route to the Fleet Manager server (see <see cref="ServerRoute"/>): on every
/// sync and a few seconds after any network change (a VPN connecting or dropping). The route goes
/// only through a gateway that has already led to the server without a VPN (remembered in
/// server-route.json), it is not persistent — after a reboot the service puts it back — and it is
/// kept only if the server answers through it; otherwise it is removed at once.
/// The same is done for the hosts the server names in <c>pin_routes</c> (the distribution point): a VPN
/// captures them too, and nginx there refuses VPN addresses, so installers would fall back to the server.
/// </summary>
public sealed class ServerRouteKeeper : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    // After a failed probe the same route is not retried on every network event, only on the next syncs.
    private static readonly TimeSpan FailedPlanCooldown = TimeSpan.FromMinutes(10);
    private const int MaxTrustedGateways = 16;
    private static readonly string RouteExe = Path.Combine(Environment.SystemDirectory, "route.exe");

    private readonly AgentLogger _logger;
    private readonly string _trustFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _debounce;
    private Func<AgentOptions>? _options;
    private TrustedPaths? _trusted;
    private ServerRoutePlan? _applied;
    private bool _cleanedUp;
    private (ServerRoutePlan Plan, DateTimeOffset Until)? _failed;
    private IReadOnlyList<PinRoute> _pins = [];
    private bool _pinsLoaded;
    private bool _pinsDirty;
    private readonly Dictionary<string, ServerRoutePlan> _pinApplied = new();
    private readonly Dictionary<string, (ServerRoutePlan Plan, DateTimeOffset Until)> _pinFailed = new();
    private readonly HashSet<string> _pinCleaned = [];

    public ServerRouteKeeper(AgentLogger logger, AgentState state)
    {
        _logger = logger;
        _trustFile = Path.Combine(state.DataDirectory, "server-route.json");
        _debounce = new Timer(_ => _ = RunAsync(confirmPath: false, fromNetworkChange: true, CancellationToken.None));
    }

    public void Start(Func<AgentOptions> options)
    {
        _options = options;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// The hosts from the heartbeat response to keep reachable past a VPN; remembered in server-route.json, so the
    /// routes come back after a restart before the first heartbeat. True when the list changed.
    /// </summary>
    public bool SetPins(IReadOnlyList<PinRoute> pins)
    {
        _pinsLoaded = true;
        if (_pins.SequenceEqual(pins)) return false;
        _pins = pins;
        _pinsDirty = true;
        return true;
    }

    /// <summary>Before talking to the server: pin (or drop) the route for the current network.</summary>
    public Task EnsureAsync(CancellationToken cancellationToken) => RunAsync(confirmPath: false, fromNetworkChange: false, cancellationToken);

    /// <summary>After a successful heartbeat: if it went through a physical adapter, trust its gateway.</summary>
    public Task ConfirmPathAsync(CancellationToken cancellationToken) => RunAsync(confirmPath: true, fromNetworkChange: false, cancellationToken);

    private async Task RunAsync(bool confirmPath, bool fromNetworkChange, CancellationToken cancellationToken)
    {
        if (_options is null) return;
        try { await _gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }
        try
        {
            var options = _options();
            if (!Uri.TryCreate(AgentConfiguration.NormalizeServerUrl(options.ServerUrl), UriKind.Absolute, out var uri)) return;
            var server = await ResolveAsync(uri.Host, cancellationToken);
            if (server is null) return;
            var trusted = LoadTrusted(server);
            var candidates = ServerRoute.LocalCandidates();
            if (confirmPath
                && ServerRoute.PathCandidate(server, candidates, ServerRoute.BestInterface(server)) is { } path
                && !trusted.Gateways.Contains(path.Gateway.ToString()))
            {
                trusted.Gateways.Add(path.Gateway.ToString());
                if (trusted.Gateways.Count > MaxTrustedGateways) trusted.Gateways.RemoveAt(0);
                SaveTrusted(trusted);
            }
            var gateways = trusted.Gateways.Select(IPAddress.Parse).ToList();
            if (!_pinsLoaded)
            {
                _pins = trusted.Pins.Select(p => new PinRoute(p.Host, p.Port)).ToList();
                _pinsLoaded = true;
            }
            else if (_pinsDirty)
            {
                trusted.Pins = _pins.Select(p => new TrustedPin { Host = p.Host, Port = p.Port }).ToList();
                SaveTrusted(trusted);
            }
            _pinsDirty = false;
            await SyncPinsAsync(options.PinServerRoute, server, candidates, gateways, fromNetworkChange, cancellationToken);
            var plan = options.PinServerRoute ? ServerRoute.Plan(server, candidates, gateways) : null;

            if (plan is not null && plan == _applied)
            {
                Run("ADD", plan); // still there? "already exists" is fine
                return;
            }
            if (_applied is not null)
            {
                Run("DELETE", _applied);
                _logger.Info($"Route to the Fleet Manager server {_applied.Server} via {_applied.Gateway} removed.");
                _applied = null;
            }
            else if (!_cleanedUp)
            {
                // A route left by the previous run of the service (it survives a restart, e.g. an agent update).
                RunRoute($"DELETE {server} MASK 255.255.255.255");
            }
            _cleanedUp = true;
            if (plan is null) return;
            if (_failed is { } failed && failed.Plan == plan && (fromNetworkChange || DateTimeOffset.UtcNow < failed.Until)) return;

            Run("ADD", plan);
            if (await ProbeAsync(server, uri.Port, cancellationToken))
            {
                _applied = plan;
                _failed = null;
                _logger.Info($"Route to the Fleet Manager server {plan.Server} pinned via {plan.Gateway} ({plan.InterfaceName}), so a VPN does not cut the PC off from Fleet Manager.");
            }
            else
            {
                Run("DELETE", plan);
                _failed = (plan, DateTimeOffset.UtcNow + FailedPlanCooldown);
                _logger.Warn($"Fleet Manager server {plan.Server} did not answer via {plan.Gateway} ({plan.InterfaceName}); the route was removed.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn($"Unable to maintain the route to the Fleet Manager server: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Pins a /32 route to each host from <c>pin_routes</c> through a trusted gateway, with the same safety as the
    /// server route: only a gateway that has led to the server without a VPN, and only while the host's port answers.
    /// </summary>
    private async Task SyncPinsAsync(bool enabled, IPAddress server, IReadOnlyList<RouteCandidate> candidates,
        IReadOnlyList<IPAddress> gateways, bool fromNetworkChange, CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<string, (IPAddress Address, int Port)>();
        if (enabled)
        {
            foreach (var pin in _pins)
            {
                var address = await ResolveAsync(pin.Host, cancellationToken);
                if (address is null || address.Equals(server)) continue;
                wanted[address.ToString()] = (address, pin.Port);
            }
        }
        foreach (var key in _pinApplied.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            Run("DELETE", _pinApplied[key]);
            _logger.Info($"Route to {key} removed.");
            _pinApplied.Remove(key);
            _pinFailed.Remove(key);
        }
        foreach (var (key, (address, port)) in wanted)
        {
            var plan = ServerRoute.Plan(address, candidates, gateways);
            if (plan is not null && _pinApplied.TryGetValue(key, out var applied) && applied == plan)
            {
                Run("ADD", plan);
                continue;
            }
            if (_pinApplied.Remove(key, out var old))
            {
                Run("DELETE", old);
                _logger.Info($"Route to {old.Server} via {old.Gateway} removed.");
            }
            else if (_pinCleaned.Add(key))
            {
                // A route left by the previous run of the service.
                RunRoute($"DELETE {address} MASK 255.255.255.255");
            }
            if (plan is null) continue;
            if (_pinFailed.TryGetValue(key, out var failed) && failed.Plan == plan
                && (fromNetworkChange || DateTimeOffset.UtcNow < failed.Until)) continue;

            Run("ADD", plan);
            if (await ProbeAsync(address, port, cancellationToken))
            {
                _pinApplied[key] = plan;
                _pinFailed.Remove(key);
                _logger.Info($"Route to {plan.Server} pinned via {plan.Gateway} ({plan.InterfaceName}), so a VPN does not cut the PC off from the distribution point.");
            }
            else
            {
                Run("DELETE", plan);
                _pinFailed[key] = (plan, DateTimeOffset.UtcNow + FailedPlanCooldown);
                _logger.Warn($"{plan.Server}:{port} did not answer via {plan.Gateway} ({plan.InterfaceName}); the route was removed.");
            }
        }
    }

    private TrustedPaths LoadTrusted(IPAddress server)
    {
        if (_trusted is null)
        {
            try { _trusted = JsonSerializer.Deserialize<TrustedPaths>(File.ReadAllText(_trustFile), JsonDefaults.Options); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        // Another server address: the gateways learned for the old one say nothing about it.
        if (_trusted is null || _trusted.Server != server.ToString()
            || _trusted.Gateways.Any(g => !IPAddress.TryParse(g, out _)))
        {
            _trusted = new TrustedPaths { Server = server.ToString() };
        }
        return _trusted;
    }

    private void SaveTrusted(TrustedPaths trusted)
    {
        try { File.WriteAllText(_trustFile, JsonSerializer.Serialize(trusted, JsonDefaults.Options)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Unable to save {_trustFile}: {ex.Message}");
        }
    }

    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
            return addresses.FirstOrDefault();
        }
        catch (SocketException) { return null; }
    }

    private static async Task<bool> ProbeAsync(IPAddress server, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await client.ConnectAsync(server, port, timeout.Token);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static void Run(string verb, ServerRoutePlan plan) =>
        RunRoute($"{verb} {plan.Server} MASK 255.255.255.255 {plan.Gateway} METRIC 1 IF {plan.InterfaceIndex}");

    private static void RunRoute(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(RouteExe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null) return;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _debounce.Dispose();
        _gate.Dispose();
    }

    private sealed class TrustedPaths
    {
        public string Server { get; set; } = string.Empty;
        public List<string> Gateways { get; set; } = [];
        public List<TrustedPin> Pins { get; set; } = [];
    }

    private sealed class TrustedPin
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
    }
}
