using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace FleetManager.Agent.Core;

/// <summary>Изображение из каталога сервера, которое должно стоять на ПК.</summary>
/// <param name="Style">WallpaperStyle для рабочего стола: «4» — заполнение, «3» — по размеру, «2» — растянуть, «0» — по центру.</param>
public sealed record WallpaperImage(string Id, string Sha256, string? FileName, string Style = "4");

/// <summary>
/// Что сервер хочет видеть на ПК (ответ на heartbeat). Desktop/LockScreen = null — обоями сервер
/// не управляет: агент возвращает то, что стояло до него (политика из образа).
/// </summary>
public sealed record DesiredWallpapers(WallpaperImage? Desktop, WallpaperImage? LockScreen, IReadOnlyList<string> Thumbnails);

/// <param name="Source">policy — локальная групповая политика (для всех пользователей), csp — PersonalizationCSP,
/// user — собственные обои пользователя.</param>
public sealed record CurrentWallpaper(string Path, string? Sha256, string Source);

public sealed record WallpaperThumbnail(string Sha256, string JpegBase64);

/// <summary>Что стоит на ПК сейчас — уходит в heartbeat.</summary>
public sealed record WallpaperReport(
    CurrentWallpaper? Desktop,
    CurrentWallpaper? LockScreen,
    string? AppliedDesktopId,
    string? AppliedLockScreenId,
    string? Error,
    IReadOnlyList<WallpaperThumbnail> Thumbnails);

/// <summary>
/// Обои рабочего стола и экрана блокировки, которые назначает сервер.
///
/// На ПК из образа их задаёт локальная групповая политика (Registry.pol): экран блокировки —
/// «Принудительно использовать изображение экрана блокировки» (LockScreenImage), обои —
/// пользовательская политика Wallpaper для всех пользователей. Windows восстанавливает их из
/// Registry.pol при каждом обновлении политики, поэтому агент меняет записи там же, увеличивает
/// версию в gpt.ini и сразу пишет те же значения в реестр (HKLM и загруженные профили), чтобы не
/// ждать обновления. На SU5 экран блокировки задан ещё и через PersonalizationCSP — его агент тоже
/// переписывает. Исходные значения образа сохраняются при первом изменении и возвращаются, когда
/// сервер перестаёт управлять обоями. Открытый рабочий стол обновляет трей (процесс в сеансе
/// пользователя): служба сообщает ему ревизию обоев через Named Pipe.
/// </summary>
public sealed class WallpaperManager
{
    internal const string LockScreenKey = @"Software\Policies\Microsoft\Windows\Personalization";
    internal const string LockScreenValue = "LockScreenImage";
    internal const string DesktopKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    internal const string DesktopValue = "Wallpaper";
    internal const string DesktopStyleValue = "WallpaperStyle";
    private const string CspKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";

    private static string PolicyRoot => Path.Combine(Environment.SystemDirectory, "GroupPolicy");
    private static string MachinePolicyFile => Path.Combine(PolicyRoot, "Machine", "Registry.pol");
    private static string UserPolicyFile => Path.Combine(PolicyRoot, "User", "Registry.pol");
    private static string GptIniFile => Path.Combine(PolicyRoot, "gpt.ini");

    private readonly string _directory;
    private readonly string _stateFile;
    private readonly AgentLogger _logger;
    private readonly Dictionary<string, (long Length, DateTime Modified, string Sha256)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _thumbnailRequests = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastError;

    public WallpaperManager(string dataDirectory, AgentLogger logger)
    {
        _directory = Path.Combine(dataDirectory, "wallpapers");
        _stateFile = Path.Combine(dataDirectory, "wallpaper-state.json");
        _logger = logger;
    }

    /// <summary>Ревизия обоев рабочего стола для трея: меняется, когда агент поставил или вернул обои.</summary>
    public string? DesktopRevision => LoadState().DesktopRevision;

    public async Task<WallpaperReport> ReportAsync(CancellationToken cancellationToken)
    {
        var state = LoadState();
        CurrentWallpaper? desktop = null, lockScreen = null;
        try
        {
            desktop = CurrentDesktop();
            lockScreen = CurrentLockScreen();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        {
            _lastError = $"Не удалось прочитать текущие обои: {ex.Message}";
        }

        var thumbnails = new List<WallpaperThumbnail>();
        var requested = _thumbnailRequests;
        _thumbnailRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var current in new[] { desktop, lockScreen })
        {
            if (current?.Sha256 is not { } sha || !requested.Contains(sha) || thumbnails.Any(t => t.Sha256 == sha)) continue;
            var jpeg = await ThumbnailAsync(current.Path, cancellationToken);
            if (jpeg is not null) thumbnails.Add(new WallpaperThumbnail(sha, jpeg));
        }
        return new WallpaperReport(desktop, lockScreen, state.Desktop?.Id, state.LockScreen?.Id, _lastError, thumbnails);
    }

    /// <summary>Приводит ПК к назначенным обоям. download(id, путь) скачивает изображение каталога.</summary>
    public async Task ApplyAsync(DesiredWallpapers desired, Func<string, string, CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        _thumbnailRequests = new HashSet<string>(desired.Thumbnails, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var state = LoadState();
        foreach (var kind in new[] { WallpaperKind.LockScreen, WallpaperKind.Desktop })
        {
            try
            {
                var target = kind == WallpaperKind.Desktop ? desired.Desktop : desired.LockScreen;
                var applied = kind == WallpaperKind.Desktop ? state.Desktop : state.LockScreen;
                if (target is null)
                {
                    if (applied is not null) Restore(kind, state);
                    continue;
                }
                var path = await EnsureFileAsync(target, download, cancellationToken);
                if (applied?.Sha256 == target.Sha256 && applied.Style == target.Style && IsInPlace(kind, path)) continue;
                Apply(kind, target, path, state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException
                                         or System.Security.SecurityException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{(kind == WallpaperKind.Desktop ? "Рабочий стол" : "Экран блокировки")}: {ex.Message}");
                _logger.Warn($"Wallpaper ({kind}) was not applied: {ex.Message}");
            }
        }
        _lastError = errors.Count > 0 ? string.Join("; ", errors) : null;
        CleanupFiles(state);
    }

    internal enum WallpaperKind { Desktop, LockScreen }

    private void Apply(WallpaperKind kind, WallpaperImage target, string path, WallpaperState state)
    {
        if (kind == WallpaperKind.LockScreen)
        {
            var entries = ReadPolicy(MachinePolicyFile);
            state.LockScreenBackup ??= new WallpaperBackup
            {
                Entries = BackupEntries(entries, LockScreenKey, LockScreenValue),
                Csp = ReadCsp("LockScreenImagePath", "LockScreenImageUrl", "LockScreenImageStatus"),
            };
            WritePolicy(MachinePolicyFile, PolicyFile.Set(entries, LockScreenKey, LockScreenValue, PolicyEntry.String(LockScreenKey, LockScreenValue, path)), machine: true);
            using (var key = Registry.LocalMachine.CreateSubKey(LockScreenKey)) key.SetValue(LockScreenValue, path, RegistryValueKind.String);
            WriteCsp(new Dictionary<string, object?> { ["LockScreenImagePath"] = path, ["LockScreenImageUrl"] = path, ["LockScreenImageStatus"] = 1 });
            state.LockScreen = new AppliedWallpaper(target.Id, target.Sha256, path, target.Style);
        }
        else
        {
            var entries = ReadPolicy(UserPolicyFile);
            state.DesktopBackup ??= new WallpaperBackup { Entries = BackupEntries(entries, DesktopKey, DesktopValue, DesktopStyleValue) };
            entries = PolicyFile.Set(entries, DesktopKey, DesktopValue, PolicyEntry.String(DesktopKey, DesktopValue, path));
            entries = PolicyFile.Set(entries, DesktopKey, DesktopStyleValue, PolicyEntry.String(DesktopKey, DesktopStyleValue, target.Style));
            WritePolicy(UserPolicyFile, entries, machine: false);
            ForEachUserHive(key =>
            {
                key.SetValue(DesktopValue, path, RegistryValueKind.String);
                key.SetValue(DesktopStyleValue, target.Style, RegistryValueKind.String);
            });
            state.Desktop = new AppliedWallpaper(target.Id, target.Sha256, path, target.Style);
            state.DesktopRevision = $"{target.Sha256}:{target.Style}";
        }
        SaveState(state);
        _logger.Info($"Wallpaper ({kind}) set to {target.FileName ?? target.Id}.");
    }

    private void Restore(WallpaperKind kind, WallpaperState state)
    {
        if (kind == WallpaperKind.LockScreen)
        {
            var backup = state.LockScreenBackup ?? new WallpaperBackup();
            var entries = RestoreEntries(ReadPolicy(MachinePolicyFile), backup, LockScreenKey, LockScreenValue);
            WritePolicy(MachinePolicyFile, entries, machine: true);
            using (var key = Registry.LocalMachine.CreateSubKey(LockScreenKey))
                SetOrDelete(key, LockScreenValue, entries.FirstOrDefault(e => e.Matches(LockScreenKey, LockScreenValue))?.StringValue);
            WriteCsp(backup.Csp);
            state.LockScreen = null;
            state.LockScreenBackup = null;
        }
        else
        {
            var backup = state.DesktopBackup ?? new WallpaperBackup();
            var entries = RestoreEntries(ReadPolicy(UserPolicyFile), backup, DesktopKey, DesktopValue, DesktopStyleValue);
            WritePolicy(UserPolicyFile, entries, machine: false);
            var wallpaper = entries.FirstOrDefault(e => e.Matches(DesktopKey, DesktopValue))?.StringValue;
            var style = entries.FirstOrDefault(e => e.Matches(DesktopKey, DesktopStyleValue))?.StringValue;
            ForEachUserHive(key =>
            {
                SetOrDelete(key, DesktopValue, wallpaper);
                SetOrDelete(key, DesktopStyleValue, style);
            });
            state.Desktop = null;
            state.DesktopBackup = null;
            state.DesktopRevision = $"restored:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        }
        SaveState(state);
        _logger.Info($"Wallpaper ({kind}) is no longer managed; the previous setting is restored.");
    }

    // ---------- чтение текущего состояния ----------

    private CurrentWallpaper? CurrentLockScreen()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(LockScreenKey))
            if (key?.GetValue(LockScreenValue) is string path && !string.IsNullOrWhiteSpace(path)) return Describe(path, "policy");
        using (var csp = Registry.LocalMachine.OpenSubKey(CspKey))
            if (csp?.GetValue("LockScreenImagePath") is string path && !string.IsNullOrWhiteSpace(path)) return Describe(path, "csp");
        return null;
    }

    private CurrentWallpaper? CurrentDesktop()
    {
        var policy = ReadPolicy(UserPolicyFile).FirstOrDefault(e => e.Matches(DesktopKey, DesktopValue))?.StringValue;
        if (!string.IsNullOrWhiteSpace(policy)) return Describe(policy, "policy");
        // Без политики — собственные обои пользователя, который сейчас за ПК (или последнего загруженного профиля)
        string? own = null;
        foreach (var sid in UserHives())
        {
            using var key = Registry.Users.OpenSubKey(sid + @"\Control Panel\Desktop");
            if (key?.GetValue("Wallpaper") is string path && !string.IsNullOrWhiteSpace(path)) own = path;
        }
        return own is null ? null : Describe(own, "user");
    }

    private CurrentWallpaper Describe(string path, string source)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return new CurrentWallpaper(expanded, Sha256Of(expanded), source);
    }

    private string? Sha256Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (_hashes.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc) return cached.Sha256;
            using var stream = info.OpenRead();
            var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            _hashes[path] = (info.Length, info.LastWriteTimeUtc, sha);
            return sha;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private bool IsInPlace(WallpaperKind kind, string path)
    {
        if (kind == WallpaperKind.LockScreen)
            return ReadPolicy(MachinePolicyFile).Any(e => e.Matches(LockScreenKey, LockScreenValue) && string.Equals(e.StringValue, path, StringComparison.OrdinalIgnoreCase));
        return ReadPolicy(UserPolicyFile).Any(e => e.Matches(DesktopKey, DesktopValue) && string.Equals(e.StringValue, path, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- файлы изображений ----------

    private async Task<string> EnsureFileAsync(WallpaperImage image, Func<string, string, CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        // Обои открывает Проводник в сеансе пользователя, а остальная папка агента ему закрыта.
        DataFolderSecurity.EnsureWallpapersFolder(_directory);
        var path = Path.Combine(_directory, image.Sha256.ToLowerInvariant() + Extension(image.FileName));
        if (File.Exists(path) && Sha256Of(path) == image.Sha256.ToLowerInvariant()) return path;
        var temp = path + ".download";
        await download(image.Id, temp, cancellationToken);
        string actual;
        await using (var stream = File.OpenRead(temp))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (actual != image.Sha256.ToLowerInvariant())
        {
            File.Delete(temp);
            throw new InvalidDataException($"контрольная сумма скачанного изображения не совпала ({actual[..12]}… вместо {image.Sha256[..Math.Min(12, image.Sha256.Length)]}…)");
        }
        File.Move(temp, path, overwrite: true);
        return path;
    }

    internal static string Extension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" ? ext : ".jpg";
    }

    /// <summary>Удаляет скачанные изображения, которые больше не стоят.</summary>
    private void CleanupFiles(WallpaperState state)
    {
        if (!Directory.Exists(_directory)) return;
        var keep = new[] { state.Desktop?.Path, state.LockScreen?.Path }.Where(p => p is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            if (keep.Contains(file)) continue;
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Миниатюра 320 px по ширине через System.Drawing в Windows PowerShell: в .NET 8 он не входит
    private static async Task<string?> ThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var literal = path.Replace("'", "''");
        var script =
            "Add-Type -AssemblyName System.Drawing; " +
            $"$src = [System.Drawing.Image]::FromFile('{literal}'); " +
            "try { $w = [Math]::Min(320, $src.Width); $h = [int][Math]::Max(1, $src.Height * $w / $src.Width); " +
            "$bmp = New-Object System.Drawing.Bitmap $w, $h; $g = [System.Drawing.Graphics]::FromImage($bmp); " +
            "$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic; $g.DrawImage($src, 0, 0, $w, $h); $g.Dispose(); " +
            "$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }; " +
            "$params = New-Object System.Drawing.Imaging.EncoderParameters 1; $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 80L; " +
            "$ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, $codec, $params); $bmp.Dispose(); [Convert]::ToBase64String($ms.ToArray()) } finally { $src.Dispose() }";
        try
        {
            var output = (await WindowsInventoryCollector.RunPowerShellAsync(script, cancellationToken)).Trim();
            return output.Length is > 0 and < 400_000 && !output.Contains(' ') ? output : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    // ---------- политика и реестр ----------

    private static List<PolicyEntry> ReadPolicy(string file) => File.Exists(file) ? PolicyFile.Parse(File.ReadAllBytes(file)) : new List<PolicyEntry>();

    private static void WritePolicy(string file, IEnumerable<PolicyEntry> entries, bool machine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".fleet";
        File.WriteAllBytes(temp, PolicyFile.Write(entries));
        File.Move(temp, file, overwrite: true);
        var ini = File.Exists(GptIniFile) ? File.ReadAllText(GptIniFile) : null;
        File.WriteAllText(GptIniFile, GptIni.Bump(ini, machine, !machine), System.Text.Encoding.ASCII);
    }

    internal static List<BackupEntry> BackupEntries(IEnumerable<PolicyEntry> entries, string key, params string[] values) =>
        values.Select(name =>
        {
            var e = entries.FirstOrDefault(x => x.Matches(key, name));
            return new BackupEntry(name, e?.Type, e is null ? null : Convert.ToBase64String(e.Data));
        }).ToList();

    internal static List<PolicyEntry> RestoreEntries(List<PolicyEntry> entries, WallpaperBackup backup, string key, params string[] values)
    {
        foreach (var name in values)
        {
            var saved = backup.Entries.FirstOrDefault(b => string.Equals(b.ValueName, name, StringComparison.OrdinalIgnoreCase));
            var value = saved?.Data is { } data && saved.Type is { } type ? new PolicyEntry(key, name, type, Convert.FromBase64String(data)) : null;
            entries = PolicyFile.Set(entries, key, name, value);
        }
        return entries;
    }

    private static Dictionary<string, object?> ReadCsp(params string[] names)
    {
        using var key = Registry.LocalMachine.OpenSubKey(CspKey);
        return names.ToDictionary(n => n, n => key?.GetValue(n) is { } v ? (object?)(v is int i ? i : v.ToString()) : null);
    }

    private static void WriteCsp(Dictionary<string, object?> values)
    {
        if (values.Count == 0) return;
        using var key = Registry.LocalMachine.CreateSubKey(CspKey);
        foreach (var (name, value) in values)
        {
            switch (value)
            {
                case null: key.DeleteValue(name, false); break;
                case int number: key.SetValue(name, number, RegistryValueKind.DWord); break;
                case JsonElement { ValueKind: JsonValueKind.Number } json: key.SetValue(name, json.GetInt32(), RegistryValueKind.DWord); break;
                default: key.SetValue(name, value.ToString()!, RegistryValueKind.String); break;
            }
        }
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    // Загруженные профили пользователей (S-1-5-21-…): политика пишется им сразу, остальным — при входе
    private static IEnumerable<string> UserHives() =>
        Registry.Users.GetSubKeyNames().Where(n => n.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !n.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase));

    private static void ForEachUserHive(Action<RegistryKey> action)
    {
        foreach (var sid in UserHives())
        {
            using var key = Registry.Users.CreateSubKey(sid + "\\" + DesktopKey);
            action(key);
        }
    }

    // ---------- состояние ----------

    private WallpaperState LoadState()
    {
        try
        {
            return File.Exists(_stateFile) ? JsonSerializer.Deserialize<WallpaperState>(File.ReadAllText(_stateFile), JsonDefaults.Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private void SaveState(WallpaperState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(state, JsonDefaults.Options));
    }
}

public sealed record AppliedWallpaper(string Id, string Sha256, string Path, string Style);

/// <param name="Type">Тип записи Registry.pol; null — записи не было.</param>
/// <param name="Data">Данные записи в base64.</param>
public sealed record BackupEntry(string ValueName, uint? Type, string? Data);

public sealed class WallpaperBackup
{
    public List<BackupEntry> Entries { get; set; } = new();
    /// <summary>Значения PersonalizationCSP до агента; null — значения не было.</summary>
    public Dictionary<string, object?> Csp { get; set; } = new();
}

public sealed class WallpaperState
{
    public AppliedWallpaper? Desktop { get; set; }
    public AppliedWallpaper? LockScreen { get; set; }
    public WallpaperBackup? DesktopBackup { get; set; }
    public WallpaperBackup? LockScreenBackup { get; set; }
    [JsonPropertyName("desktopRevision")]
    public string? DesktopRevision { get; set; }
}
