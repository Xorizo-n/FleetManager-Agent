using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace FleetManager.Agent.Core;

/// <summary>
/// Update the server asked for in the heartbeat response: the installer it serves and its SHA-256.
/// <paramref name="Repair"/>: reinstall even the same version — the installer sets up what is missing
/// on the PC (OpenSSH Server when the first install could not add it).
/// </summary>
public sealed record AgentUpdateOffer(string Version, string Sha256, bool Repair = false);

/// <summary>Attempts to install one version, kept in self-update.json between restarts.</summary>
public sealed record SelfUpdateState(string? Version, int Attempts, DateTimeOffset? LastAttemptAt);

/// <summary>
/// Updates the agent without SSH. The server used to update agents only over SSH, so a PC with
/// broken SSH could never receive the version that repairs it. Now the server offers the update
/// in the heartbeat response; the agent downloads the installer with its own token (the same
/// GET /api/agent/installer the SSH update uses), checks its SHA-256 and runs it silently over
/// the current installation — upgrade mode keeps agent.json, so there is no re-registration.
/// The installer stops this service; the process it runs in survives that.
/// </summary>
public sealed class AgentSelfUpdate
{
    public const string StateFile = "self-update.json";
    public const string UpdatesFolder = "updates";
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly string _dataDirectory;
    private readonly AgentLogger _logger;

    public AgentSelfUpdate(string dataDirectory, AgentLogger logger)
    {
        _dataDirectory = dataDirectory;
        _logger = logger;
    }

    /// <summary>Numeric comparison of "2026.10.24.23"-style versions; null — not comparable.</summary>
    public static int? Compare(string? left, string? right)
    {
        static int[]? Parse(string? value)
        {
            value = AgentVersion.Normalize(value);
            if (value is null) return null;
            var parts = value.Split('.');
            var numbers = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
            return numbers;
        }
        var a = Parse(left);
        var b = Parse(right);
        if (a is null || b is null) return null;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    /// <summary>
    /// Install only a newer version (or the same one for a repair, never an older one),
    /// at most <see cref="MaxAttempts"/> times per version, an hour apart.
    /// </summary>
    public static bool ShouldInstall(string? current, AgentUpdateOffer? offer, SelfUpdateState state, DateTimeOffset now)
    {
        if (offer is null) return false;
        var order = Compare(current, offer.Version);
        if (order is not -1 && !(offer.Repair && order == 0)) return false;
        if (!string.Equals(state.Version, offer.Version, StringComparison.Ordinal)) return true;
        if (state.Attempts >= MaxAttempts) return false;
        return state.LastAttemptAt is null || now - state.LastAttemptAt >= RetryInterval;
    }

    internal static AgentUpdateOffer? ParseOffer(JsonElement root)
    {
        if (!root.TryGetProperty("agent_update", out var update) || update.ValueKind != JsonValueKind.Object) return null;
        var version = update.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? AgentVersion.Normalize(v.GetString()) : null;
        var sha = update.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (version is null || sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit)) return null;
        var repair = update.TryGetProperty("repair", out var r) && r.ValueKind == JsonValueKind.True;
        return new AgentUpdateOffer(version, sha.ToLowerInvariant(), repair);
    }

    public SelfUpdateState LoadState()
    {
        try
        {
            var file = Path.Combine(_dataDirectory, StateFile);
            if (File.Exists(file))
                return JsonSerializer.Deserialize<SelfUpdateState>(File.ReadAllText(file), JsonDefaults.Options) ?? new SelfUpdateState(null, 0, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new SelfUpdateState(null, 0, null);
    }

    private void SaveState(SelfUpdateState state) =>
        File.WriteAllText(Path.Combine(_dataDirectory, StateFile), JsonSerializer.Serialize(state, JsonDefaults.Options));

    /// <summary>Downloads, checks and launches the installer. true — it was started (the service will stop shortly).</summary>
    public async Task<bool> RunAsync(AgentUpdateOffer offer, Func<string, CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var previous = LoadState();
        var attempts = string.Equals(previous.Version, offer.Version, StringComparison.Ordinal) ? previous.Attempts + 1 : 1;
        // Counted before the download: an installer that hangs or kills the service must not be retried forever
        SaveState(new SelfUpdateState(offer.Version, attempts, DateTimeOffset.UtcNow));

        var folder = Path.Combine(_dataDirectory, UpdatesFolder);
        Directory.CreateDirectory(folder);
        foreach (var stale in Directory.EnumerateFiles(folder))
        {
            try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var installer = Path.Combine(folder, $"FleetManagerAgent-Setup-{offer.Version}.exe");
        _logger.Info($"Server offers agent {offer.Version}{(offer.Repair ? " as a repair" : "")} (attempt {attempts} of {MaxAttempts}); downloading the installer.");
        await download(installer, cancellationToken);

        string actual;
        await using (var file = File.OpenRead(installer))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
        if (actual != offer.Sha256)
        {
            File.Delete(installer);
            _logger.Warn($"Installer for {offer.Version} has SHA-256 {actual}, the server announced {offer.Sha256}; not installed.");
            return false;
        }

        var log = Path.Combine(folder, $"install-{offer.Version}.log");
        using var process = Process.Start(new ProcessStartInfo(installer, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=\"{log}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder,
        });
        if (process is null)
        {
            _logger.Warn($"Installer for {offer.Version} did not start.");
            return false;
        }
        _logger.Warn($"Installing agent {offer.Version} over {AgentVersion.Current ?? "unknown"}; the service restarts.");
        return true;
    }
}
