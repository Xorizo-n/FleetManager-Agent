using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

namespace FleetManager.Agent.Core;

/// <summary>
/// Активация Windows и Office: статус лицензий, последнее обращение к KMS из журнала
/// и проверка, открыт ли с этого ПК порт KMS-сервера. С подсетей SU5 и SU7 порт 1688
/// до KMS университета был закрыт, и ПК годами оставались неактивированными — сервер
/// показывает это в карточке хоста.
/// </summary>
/// <param name="ClientMachineId">CMID: у клонов образа без sysprep /generalize он одинаковый,
/// и KMS считает их одним компьютером.</param>
public sealed record ActivationSnapshot(
    DateTimeOffset CollectedAt,
    string? ClientMachineId,
    IReadOnlyList<LicenseProductInfo> Products,
    KmsProbeResult? KmsProbe = null);

/// <param name="Kind">«windows» или «office».</param>
/// <param name="Id">SKU ID — по нему события KMS из журнала привязываются к продукту.</param>
/// <param name="LicenseStatus">SoftwareLicensingProduct.LicenseStatus: 0 — нет лицензии, 1 — активирован,
/// 2–4 и 6 — пробный период, 5 — режим уведомлений (не активирован).</param>
/// <param name="StatusReason">LicenseStatusReason — HRESULT причины, например 0xC004F009.</param>
/// <param name="GraceMinutes">Сколько минут осталось: до конца пробного периода или, у активированного
/// через KMS, до истечения активации (продлевается раз в 7 дней).</param>
/// <param name="KmsMachine">KMS, заданный вручную (slmgr /skms); пусто — ищется через DNS.</param>
public sealed record LicenseProductInfo(
    string Kind,
    string? Id,
    string? Name,
    string? Description,
    string? Channel,
    string? PartialProductKey,
    int LicenseStatus,
    long? StatusReason,
    long? GraceMinutes,
    string? KmsMachine,
    int? KmsPort,
    string? DiscoveredKmsMachine,
    int? DiscoveredKmsPort,
    KmsRequestInfo? LastRequest = null)
{
    public bool IsKmsClient =>
        (Description?.Contains("VOLUME_KMSCLIENT", StringComparison.OrdinalIgnoreCase) ?? false)
        || string.Equals(Channel, "Volume:GVLK", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Kms">«sk5-cod-kms3.at.urfu.ru:1688».</param>
/// <param name="Result">HRESULT в виде «0xC0020017»; 0x00000000 — KMS активировал; null — ответа нет в журнале.</param>
public sealed record KmsRequestInfo(DateTimeOffset At, string? Kms, string? Result);

/// <param name="Error">Код SocketError («TimedOut», «ConnectionRefused», «HostNotFound») при неудаче.</param>
public sealed record KmsProbeResult(string Host, int Port, bool Reachable, string? Error);

/// <summary>
/// Собирает <see cref="ActivationSnapshot"/> не чаще раза в час: запрос к службе
/// лицензирования будит sppsvc, и делать его на каждый heartbeat (раз в 5 минут) незачем —
/// статус активации меняется редко. Между сборами отдаётся последний снимок.
/// </summary>
public sealed class LicenseActivationCollector
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const int DefaultKmsPort = 1688;
    private const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";
    private const string OfficeApplicationId = "0ff1ce15-a989-479d-af46-f275c6370663";

    private ActivationSnapshot? _last;

    // Продукты Windows и Office (2013+) с установленным ключом. События 12288 (запрос к KMS,
    // Info: «HRESULT, …, узел:порт, CMID, время, …, SKU ID, …») и 12289 (ответ KMS, Info
    // начинается с HRESULT) — по ним видно, когда ПК последний раз обращался к KMS и с каким
    // итогом. Офис при неудаче повторяет запрос каждые 2 часа, Windows — раз в сутки, поэтому
    // 200 событий хватает, чтобы найти последнюю попытку обоих.
    internal const string Script =
        "$apps = '" + WindowsApplicationId + "','" + OfficeApplicationId + "'; " +
        "$products = @(Get-CimInstance SoftwareLicensingProduct -Filter 'PartialProductKey IS NOT NULL' -ErrorAction SilentlyContinue | Where-Object { $apps -contains $_.ApplicationID } | ForEach-Object { " +
        "[pscustomobject]@{ ApplicationId = $_.ApplicationID; Id = $_.ID; Name = $_.Name; Description = $_.Description; Channel = $_.ProductKeyChannel; PartialProductKey = $_.PartialProductKey; " +
        "LicenseStatus = [int]$_.LicenseStatus; StatusReason = [long]$_.LicenseStatusReason; GraceMinutes = [long]$_.GracePeriodRemaining; " +
        "KmsMachine = $_.KeyManagementServiceMachine; KmsPort = [int]$_.KeyManagementServicePort; " +
        "DiscoveredKmsMachine = $_.DiscoveredKeyManagementServiceMachineName; DiscoveredKmsPort = [int]$_.DiscoveredKeyManagementServiceMachinePort } }); " +
        "$service = Get-CimInstance SoftwareLicensingService -ErrorAction SilentlyContinue; " +
        "$events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Microsoft-Windows-Security-SPP'; Id = 12288,12289 } -MaxEvents 200 -ErrorAction SilentlyContinue | Where-Object { $_.Properties.Count -gt 0 } | ForEach-Object { " +
        "[pscustomobject]@{ Id = $_.Id; At = $_.TimeCreated.ToUniversalTime().ToString('o'); Info = [string]$_.Properties[0].Value } }); " +
        "ConvertTo-Json -Compress -Depth 4 -InputObject ([pscustomobject]@{ ClientMachineId = $service.ClientMachineID; KmsMachine = $service.KeyManagementServiceMachine; KmsPort = [int]$service.KeyManagementServicePort; Products = $products; Events = $events })";

    public async Task<ActivationSnapshot?> CollectAsync(CancellationToken cancellationToken)
    {
        if (_last is not null && DateTimeOffset.UtcNow - _last.CollectedAt < RefreshInterval) return _last;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScriptTimeout);
        try
        {
            var snapshot = Parse(await WindowsInventoryCollector.RunPowerShellAsync(Script, timeout.Token), DateTimeOffset.UtcNow);
            if (snapshot is null) return _last;
            if (KmsTarget(snapshot) is { } target)
                snapshot = snapshot with { KmsProbe = await ProbeAsync(target.Host, target.Port, timeout.Token) };
            return _last = snapshot;
        }
        // Активация — дополнительные сведения: её сбой не должен срывать heartbeat
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            return _last;
        }
    }

    /// <summary>Разбирает вывод <see cref="Script"/>; null — вывода нет или он не JSON.</summary>
    internal static ActivationSnapshot? Parse(string? output, DateTimeOffset collectedAt)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var requests = LastRequestsBySku(Items(root, "Events").Select(e =>
                (Id: (int)(GetLong(e, "Id") ?? 0), At: GetDate(e, "At"), Info: GetString(e, "Info") ?? string.Empty)));
            var serviceKms = GetString(root, "KmsMachine");
            var serviceKmsPort = PositivePort(GetLong(root, "KmsPort"));
            var products = Items(root, "Products")
                .Select(p => (Kind: Kind(GetString(p, "ApplicationId")), Product: p))
                .Where(x => x.Kind is not null)
                .Select(x =>
                {
                    var p = x.Product;
                    var id = GetString(p, "Id");
                    var kms = GetString(p, "KmsMachine");
                    return new LicenseProductInfo(
                        x.Kind!, id, GetString(p, "Name"), GetString(p, "Description"), GetString(p, "Channel"),
                        GetString(p, "PartialProductKey"), (int)(GetLong(p, "LicenseStatus") ?? 0),
                        GetLong(p, "StatusReason"), GetLong(p, "GraceMinutes"),
                        // Заданный для всей службы KMS действует на продукты без своего
                        kms ?? serviceKms, kms is null && serviceKms is not null ? serviceKmsPort : PositivePort(GetLong(p, "KmsPort")),
                        GetString(p, "DiscoveredKmsMachine"), PositivePort(GetLong(p, "DiscoveredKmsPort")),
                        id is not null && requests.TryGetValue(id, out var request) ? request : null);
                })
                .ToArray();
            return new ActivationSnapshot(collectedAt, GetString(root, "ClientMachineId"), products);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Последний запрос к KMS по каждому SKU с итогом. Запрос (12288) с ненулевым HRESULT
    /// не дошёл до KMS — итог берётся из него; дошедший запрос получает итог из ответа (12289),
    /// записанного после него. В ответе нет SKU, поэтому ответ достаётся последнему
    /// ещё не отвеченному запросу перед ним.
    /// </summary>
    internal static IReadOnlyDictionary<string, KmsRequestInfo> LastRequestsBySku(IEnumerable<(int Id, DateTimeOffset? At, string Info)> events)
    {
        var pending = new List<(string Sku, DateTimeOffset At, string? Kms)>();
        var result = new Dictionary<string, KmsRequestInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events.Where(e => e.At is not null).OrderBy(e => e.At))
        {
            var fields = e.Info.Split(',', StringSplitOptions.TrimEntries);
            var hresult = Hresult(fields.ElementAtOrDefault(0));
            if (e.Id == 12288)
            {
                var sku = fields.ElementAtOrDefault(8);
                if (string.IsNullOrWhiteSpace(sku)) continue;
                var kms = NullIfBlank(fields.ElementAtOrDefault(2));
                pending.RemoveAll(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase));
                if (hresult is not null && hresult != SuccessHresult)
                    result[sku] = new KmsRequestInfo(e.At!.Value, kms, hresult);
                else
                {
                    result[sku] = new KmsRequestInfo(e.At!.Value, kms, null);
                    pending.Add((sku, e.At!.Value, kms));
                }
            }
            else if (e.Id == 12289 && pending.Count > 0)
            {
                var answered = pending[^1];
                pending.RemoveAt(pending.Count - 1);
                result[answered.Sku] = new KmsRequestInfo(answered.At, answered.Kms, hresult);
            }
        }
        return result;
    }

    /// <summary>
    /// Куда проверять доступность KMS: заданный вручную, иначе тот, к которому ПК обращался
    /// последним, иначе найденный через DNS. Без KMS-продуктов проверять нечего.
    /// </summary>
    internal static (string Host, int Port)? KmsTarget(ActivationSnapshot snapshot)
    {
        var kms = snapshot.Products.Where(p => p.IsKmsClient).ToArray();
        foreach (var p in kms)
            if (!string.IsNullOrWhiteSpace(p.KmsMachine)) return (p.KmsMachine.Trim(), p.KmsPort ?? DefaultKmsPort);
        foreach (var p in kms.Where(p => p.LastRequest is not null).OrderByDescending(p => p.LastRequest!.At))
            if (SplitHostPort(p.LastRequest!.Kms) is { } target) return target;
        foreach (var p in kms)
            if (!string.IsNullOrWhiteSpace(p.DiscoveredKmsMachine)) return (p.DiscoveredKmsMachine.Trim(), p.DiscoveredKmsPort ?? DefaultKmsPort);
        return null;
    }

    internal static async Task<KmsProbeResult> ProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return new KmsProbeResult(host, port, true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KmsProbeResult(host, port, false, nameof(SocketError.TimedOut));
        }
        catch (SocketException ex)
        {
            return new KmsProbeResult(host, port, false, ex.SocketErrorCode.ToString());
        }
    }

    internal static (string Host, int Port)? SplitHostPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out var port) && port is > 0 and < 65536)
            return (text[..colon], port);
        return (text, DefaultKmsPort);
    }

    private const string SuccessHresult = "0x00000000";

    /// <summary>«0x0», «0xc0020017» → «0x00000000», «0xC0020017»; не HRESULT — null.</summary>
    internal static string? Hresult(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith("hr=", StringComparison.OrdinalIgnoreCase)) text = text[3..];
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ? $"0x{code:X8}" : null;
    }

    private static string? Kind(string? applicationId) => applicationId?.Trim().ToLowerInvariant() switch
    {
        WindowsApplicationId => "windows",
        OfficeApplicationId => "office",
        _ => null,
    };

    private static int? PositivePort(long? value) => value is > 0 and < 65536 ? (int)value : null;

    // Windows PowerShell 5.1 иногда сериализует вложенный массив как {"value":[…],"Count":n}
    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return Array.Empty<JsonElement>();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner)) value = inner;
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).ToArray(),
            JsonValueKind.Object => new[] { value },
            _ => Array.Empty<JsonElement>(),
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? NullIfBlank(value.ToString())
            : null;

    private static long? GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        DateTimeOffset.TryParse(GetString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
