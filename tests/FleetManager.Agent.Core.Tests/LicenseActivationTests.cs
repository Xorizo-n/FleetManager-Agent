using System.Text.Json;
using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class LicenseActivationTests
{
    private const string OfficeKmsSku = "85dd8b5f-eaa4-4af3-a628-cce9e77c9a03";
    private const string WindowsSku = "3f1afc82-f8ac-4f6c-8005-1d233e606eee";

    // Снято с SU5-K104-W09: Windows в режиме уведомлений, Office 2019 KMS в пробном периоде,
    // Project — заглушка Retail(Grace); KMS с подсети SU5 недоступен (0xC0020017).
    private const string Su5Output = """
        {"ClientMachineId":"36185d39-f35d-4efb-8610-c2aac5b98cb4","KmsMachine":null,"KmsPort":1688,
         "Products":[
          {"ApplicationId":"55c92734-d682-4d71-983e-d6ec3f16059f","Id":"3f1afc82-f8ac-4f6c-8005-1d233e606eee","Name":"Windows(R), ProfessionalEducation edition","Description":"Windows(R) Operating System, VOLUME_KMSCLIENT channel","Channel":"Volume:GVLK","PartialProductKey":"J447Y","LicenseStatus":5,"StatusReason":3221549071,"GraceMinutes":0,"KmsMachine":null,"KmsPort":1688,"DiscoveredKmsMachine":"sk5-cod-kms3.at.urfu.ru","DiscoveredKmsPort":1688},
          {"ApplicationId":"0ff1ce15-a989-479d-af46-f275c6370663","Id":"aaaaaaaa-0000-0000-0000-000000000000","Name":"Office 19, Office19ProjectPro2019R_Grace edition","Description":"Office 19, RETAIL(Grace) channel","Channel":"Retail","PartialProductKey":"B4JJJ","LicenseStatus":5,"StatusReason":3221549065,"GraceMinutes":0,"KmsMachine":null,"KmsPort":0,"DiscoveredKmsMachine":null,"DiscoveredKmsPort":0},
          {"ApplicationId":"0ff1ce15-a989-479d-af46-f275c6370663","Id":"85dd8b5f-eaa4-4af3-a628-cce9e77c9a03","Name":"Office 19, Office19ProPlus2019VL_KMS_Client_AE edition","Description":"Office 19, VOLUME_KMSCLIENT channel","Channel":"Volume:GVLK","PartialProductKey":"6MWKP","LicenseStatus":2,"StatusReason":1074065420,"GraceMinutes":38879,"KmsMachine":null,"KmsPort":1688,"DiscoveredKmsMachine":null,"DiscoveredKmsPort":0},
          {"ApplicationId":"11111111-2222-3333-4444-555555555555","Id":"x","Name":"Something else","LicenseStatus":1}
         ],
         "Events":[
          {"Id":12288,"At":"2026-10-17T10:13:00.0000000Z","Info":"0xC0020017, 0x00000000, sk5-cod-kms3.at.urfu.ru:1688, 36185d39-f35d-4efb-8610-c2aac5b98cb4, 2026/10/17 10:13, 1, 2, 38899, 85dd8b5f-eaa4-4af3-a628-cce9e77c9a03, 5"},
          {"Id":12288,"At":"2026-10-17T08:13:00.0000000Z","Info":"0xC0020017, 0x00000000, sk5-cod-kms3.at.urfu.ru:1688, 36185d39-f35d-4efb-8610-c2aac5b98cb4, 2026/10/17 08:13, 1, 2, 39019, 85dd8b5f-eaa4-4af3-a628-cce9e77c9a03, 5"},
          {"Id":12288,"At":"2026-10-17T06:56:00.0000000Z","Info":"0xC0020017, 0x00000000, sk5-cod-kms3.at.urfu.ru:1688, 36185d39-f35d-4efb-8610-c2aac5b98cb4, 2026/10/17 06:56, 1, 5, 0, 3f1afc82-f8ac-4f6c-8005-1d233e606eee, 25"}
         ]}
        """;

    [Fact]
    public void Parse_readsWindowsAndOfficeProductsWithTheirLastKmsAttempt()
    {
        var at = DateTimeOffset.Parse("2026-10-17T10:20:00Z");
        var snapshot = WindowsActivation(Su5Output, at);

        Assert.Equal(at, snapshot.CollectedAt);
        Assert.Equal("36185d39-f35d-4efb-8610-c2aac5b98cb4", snapshot.ClientMachineId);
        Assert.Equal(new[] { "windows", "office", "office" }, snapshot.Products.Select(p => p.Kind));

        var windows = snapshot.Products[0];
        Assert.Equal(5, windows.LicenseStatus);
        Assert.Equal(0xC004F00FL, windows.StatusReason);
        Assert.True(windows.IsKmsClient);
        Assert.Equal("sk5-cod-kms3.at.urfu.ru", windows.DiscoveredKmsMachine);
        Assert.Equal(new KmsRequestInfo(DateTimeOffset.Parse("2026-10-17T06:56:00Z"), "sk5-cod-kms3.at.urfu.ru:1688", "0xC0020017"), windows.LastRequest);

        var project = snapshot.Products[1];
        Assert.False(project.IsKmsClient);
        Assert.Null(project.LastRequest);
        Assert.Null(project.KmsPort);

        var office = snapshot.Products[2];
        Assert.Equal(2, office.LicenseStatus);
        Assert.Equal(38879, office.GraceMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T10:13:00Z"), office.LastRequest!.At);
        Assert.Equal("0xC0020017", office.LastRequest.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Parse_returnsNullWithoutUsableOutput(string output) =>
        Assert.Null(LicenseActivationCollector.Parse(output, DateTimeOffset.UtcNow));

    [Fact]
    public void Parse_acceptsASingleProductAndThePowerShellArrayWrapper()
    {
        // PowerShell 5.1: один элемент — объект вместо массива, вложенный массив — {"value":[…],"Count":n}
        const string output = """
            {"ClientMachineId":"8afd3330-03f6-4c97-bd68-51a77d554483","KmsMachine":"kms.urfu.ru","KmsPort":1688,
             "Products":{"value":[{"ApplicationId":"55C92734-D682-4D71-983E-D6EC3F16059F","Id":"73111121-5638-40f6-bc11-f1d7b0d64300","Name":"Windows(R), Enterprise edition","Description":"Windows(R) Operating System, VOLUME_KMSCLIENT channel","LicenseStatus":1,"StatusReason":0,"GraceMinutes":251882,"KmsMachine":null,"KmsPort":0}],"Count":1},
             "Events":{"Id":12289,"At":"2026-10-12T08:35:00Z","Info":"0x00000000, 0x00000000, 1, 0, 50, 120, 10080, 2026/10/12 08:35"}}
            """;

        var windows = Assert.Single(WindowsActivation(output, DateTimeOffset.UtcNow).Products);

        Assert.Equal("windows", windows.Kind);
        Assert.Equal(1, windows.LicenseStatus);
        // Продукт без своего KMS наследует заданный для всей службы (slmgr /skms)
        Assert.Equal("kms.urfu.ru", windows.KmsMachine);
        Assert.Equal(1688, windows.KmsPort);
    }

    [Fact]
    public void LastRequests_takeTheResultFromTheKmsResponse()
    {
        // MR32: запросы Windows и Office ушли одновременно, оба получили ответ 0x0
        var requests = LicenseActivationCollector.LastRequestsBySku(new (int, DateTimeOffset?, string)[]
        {
            (12288, At("08:35:00"), $"0x00000000, 0x00000000, kms.urfu.ru:1688, cmid, 2026/10/12 08:35, 0, 1, 249120, {WindowsSku}, 25"),
            (12289, At("08:35:01"), "0x00000000, 0x00000000, 1, 0, 50, 120, 10080, 2026/10/12 08:35"),
            (12288, At("08:35:02"), $"0x0, 0x00000000, kms.urfu.ru:1688, cmid, 2026/10/12 08:35, 0, 1, 249120, {OfficeKmsSku}, 5"),
            (12289, At("08:35:03"), "0xC004F038, 0x00000000, 1, 0, 3, 120, 10080, 2026/10/12 08:35"),
        });

        Assert.Equal("0x00000000", requests[WindowsSku].Result);
        Assert.Equal("kms.urfu.ru:1688", requests[WindowsSku].Kms);
        Assert.Equal("0xC004F038", requests[OfficeKmsSku].Result);
        Assert.Equal(At("08:35:02"), requests[OfficeKmsSku].At);
    }

    [Fact]
    public void LastRequests_leaveTheResultUnknownWithoutAResponse()
    {
        var requests = LicenseActivationCollector.LastRequestsBySku(new (int, DateTimeOffset?, string)[]
        {
            (12288, At("09:00:00"), $"0xC0020017, 0x0, kms:1688, cmid, t, 1, 2, 100, {OfficeKmsSku}, 5"),
            (12288, At("11:00:00"), $"0x00000000, 0x0, kms:1688, cmid, t, 1, 2, 100, {OfficeKmsSku}, 5"),
        });

        var request = requests[OfficeKmsSku];
        Assert.Equal(At("11:00:00"), request.At);
        Assert.Null(request.Result);
    }

    [Theory]
    [InlineData("0x0", "0x00000000")]
    [InlineData("0xc0020017", "0xC0020017")]
    [InlineData("hr=0x8007139F", "0x8007139F")]
    [InlineData("12", null)]
    [InlineData("0xZZ", null)]
    [InlineData(null, null)]
    public void Hresult_isNormalized(string? value, string? expected) =>
        Assert.Equal(expected, LicenseActivationCollector.Hresult(value));

    [Fact]
    public void KmsTarget_prefersTheConfiguredServer()
    {
        var snapshot = WindowsActivation(Su5Output, DateTimeOffset.UtcNow);
        Assert.Equal(("sk5-cod-kms3.at.urfu.ru", 1688), LicenseActivationCollector.KmsTarget(snapshot));

        var configured = snapshot with
        {
            Products = snapshot.Products.Select(p => p.Kind == "windows" ? p with { KmsMachine = "kms.urfu.ru", KmsPort = 1700 } : p).ToArray()
        };
        Assert.Equal(("kms.urfu.ru", 1700), LicenseActivationCollector.KmsTarget(configured));

        var retailOnly = snapshot with { Products = snapshot.Products.Where(p => !p.IsKmsClient).ToArray() };
        Assert.Null(LicenseActivationCollector.KmsTarget(retailOnly));
    }

    [Theory]
    [InlineData("sk5-cod-kms3.at.urfu.ru:1688", "sk5-cod-kms3.at.urfu.ru", 1688)]
    [InlineData("kms.urfu.ru", "kms.urfu.ru", 1688)]
    [InlineData("10.0.0.5:1700", "10.0.0.5", 1700)]
    public void SplitHostPort_defaultsToTheKmsPort(string value, string host, int port) =>
        Assert.Equal((host, port), LicenseActivationCollector.SplitHostPort(value));

    [Fact]
    public async Task Probe_reportsAClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True((await LicenseActivationCollector.ProbeAsync("127.0.0.1", port, CancellationToken.None)).Reachable);
        }
        finally
        {
            listener.Stop();
        }

        var closed = await LicenseActivationCollector.ProbeAsync("127.0.0.1", port, CancellationToken.None);
        Assert.False(closed.Reachable);
        Assert.NotNull(closed.Error);
    }

    [Fact]
    public void Payload_usesTheServerFieldNames()
    {
        var snapshot = WindowsActivation(Su5Output, DateTimeOffset.Parse("2026-10-17T10:20:00Z")) with
        {
            KmsProbe = new KmsProbeResult("sk5-cod-kms3.at.urfu.ru", 1688, false, "TimedOut")
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(FleetManagerApiClient.ActivationPayload(snapshot), JsonDefaults.Options));
        var root = json.RootElement;

        Assert.Equal("36185d39-f35d-4efb-8610-c2aac5b98cb4", root.GetProperty("client_machine_id").GetString());
        Assert.False(root.GetProperty("kms_probe").GetProperty("reachable").GetBoolean());
        var windows = root.GetProperty("products")[0];
        Assert.Equal(5, windows.GetProperty("license_status").GetInt32());
        Assert.Equal(0xC004F00FL, windows.GetProperty("status_reason").GetInt64());
        Assert.Equal("0xC0020017", windows.GetProperty("last_request").GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("products")[1].GetProperty("last_request").ValueKind);
        Assert.Null(FleetManagerApiClient.ActivationPayload(null));
    }

    [Fact]
    public async Task Script_runsOnThisMachine()
    {
        if (!OperatingSystem.IsWindows()) return;

        var output = await WindowsInventoryCollector.RunPowerShellAsync(LicenseActivationCollector.Script, CancellationToken.None);
        var snapshot = LicenseActivationCollector.Parse(output, DateTimeOffset.UtcNow);

        Assert.NotNull(snapshot);
        Assert.All(snapshot.Products, p => Assert.Contains(p.Kind, new[] { "windows", "office" }));
    }

    private static ActivationSnapshot WindowsActivation(string output, DateTimeOffset at) =>
        LicenseActivationCollector.Parse(output, at) ?? throw new InvalidOperationException("Parse returned null");

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse("2026-10-12T" + time + "Z");
}
