using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class WindowsInventoryTests
{
    [Fact]
    public async Task PowerShellOutput_keepsCyrillic()
    {
        // Регрессия: PowerShell 5.1 писал в перенаправленный stdout в OEM-кодировке, и на сервер
        // уходило «Windows 11 Pro ��� ��ࠧ���⥫��� ��०» и «1�:�।���⨥ 8».
        if (!OperatingSystem.IsWindows()) return;

        var output = await WindowsInventoryCollector.RunPowerShellAsync(
            "[pscustomobject]@{ Name = '1С:Предприятие 8 «тест» ё' } | ConvertTo-Json -Compress", CancellationToken.None);

        Assert.Equal("{\"Name\":\"1С:Предприятие 8 «тест» ё\"}", output.Trim());
    }

    [Theory]
    // На Windows 11 ProductName в реестре всё ещё «Windows 10 …», отличает сборка
    [InlineData("Windows 10 Pro", "26200", "Майкрософт Windows 11 Pro", "Microsoft Windows 11 Pro")]
    [InlineData("Windows 10 Enterprise", "22631", "Microsoft Windows 11 Enterprise", "Microsoft Windows 11 Enterprise")]
    [InlineData("Windows 10 Enterprise", "19045", "Майкрософт Windows 10 Корпоративная", "Microsoft Windows 10 Enterprise")]
    [InlineData("Windows 10 Enterprise LTSC 2021", "19044", null, "Microsoft Windows 10 Enterprise LTSC 2021")]
    [InlineData("Windows Server 2022 Standard", "20348", "Microsoft Windows Server 2022 Standard", "Microsoft Windows Server 2022 Standard")]
    [InlineData("Windows Server 2025 Datacenter", "26100", null, "Microsoft Windows Server 2025 Datacenter")]
    public void OperatingSystemName_doesNotDependOnInterfaceLanguage(string productName, string build, string? caption, string expected)
    {
        Assert.Equal(expected, WindowsInventoryCollector.OperatingSystemName(productName, build, caption));
    }

    [Fact]
    public void OperatingSystemName_fallsBackToCaption()
    {
        Assert.Equal("Майкрософт Windows 11 Pro", WindowsInventoryCollector.OperatingSystemName(null, "26200", "Майкрософт Windows 11 Pro"));
        Assert.Equal("Microsoft Windows 10 Pro", WindowsInventoryCollector.OperatingSystemName("Windows 10 Pro", "not-a-number", null));
        Assert.Null(WindowsInventoryCollector.OperatingSystemName(" ", null, null));
    }

    [Fact]
    public async Task CollectHardware_reportsTheEnglishOperatingSystemName()
    {
        // Регрессия: локализованный Caption менял отпечаток железа при смене языка интерфейса
        if (!OperatingSystem.IsWindows()) return;

        var first = await new WindowsInventoryCollector().CollectHardwareAsync(CancellationToken.None);
        var second = await new WindowsInventoryCollector().CollectHardwareAsync(CancellationToken.None);

        Assert.StartsWith("Microsoft Windows", first.OperatingSystem);
        Assert.DoesNotMatch("[А-Яа-яЁё]", first.OperatingSystem!);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }
    [Fact]
    public void NetworkAdapters_areParsedForWakeOnLan()
    {
        const string json = "[" +
            "{\"Name\":\"Ethernet\",\"Description\":\"Realtek Gaming 2.5GbE Family Controller\",\"MacAddress\":\"34-5A-60-BF-91-C7\",\"Status\":\"Up\",\"MediaType\":\"802.3\",\"Ipv4\":[\"10.152.5.66/25\"]}," +
            "{\"Name\":\"Wi-Fi\",\"Description\":\"Intel Wi-Fi 6\",\"MacAddress\":\"AA-BB-CC-DD-EE-FF\",\"Status\":\"Disconnected\",\"MediaType\":\"Native 802.11\",\"Ipv4\":{\"value\":[],\"Count\":0}}," +
            "{\"Name\":\"Bluetooth\",\"Description\":\"Bluetooth Device (PAN)\",\"MacAddress\":\"11-22-33-44-55-66\",\"Status\":\"Disconnected\",\"MediaType\":\"BlueTooth\",\"Ipv4\":[]}," +
            "{\"Name\":\"Broken\",\"MacAddress\":\"\",\"Ipv4\":\"10.0.0.1/8\"}]";

        var adapters = WindowsInventoryCollector.ParseNetworkAdapters(json);

        Assert.Equal(2, adapters.Count);
        Assert.Equal("34-5A-60-BF-91-C7", adapters[0].MacAddress);
        Assert.Equal(new[] { "10.152.5.66/25" }, adapters[0].Ipv4);
        Assert.Equal("802.3", adapters[0].MediaType);
        Assert.Empty(adapters[1].Ipv4);
    }

    [Theory]
    [InlineData("{\"Name\":\"Ethernet\",\"MacAddress\":\"34-5A-60-BF-91-C7\",\"Ipv4\":\"10.152.5.66/25\"}", 1)]
    [InlineData("[]", 0)]
    [InlineData("", 0)]
    [InlineData("not json", 0)]
    public void NetworkAdapters_tolerateSingleObjectsAndGarbage(string json, int expected)
    {
        Assert.Equal(expected, WindowsInventoryCollector.ParseNetworkAdapters(json).Count);
    }

    [Fact]
    public async Task CollectHardware_reportsPhysicalAdaptersWithMac()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hardware = await new WindowsInventoryCollector().CollectHardwareAsync(CancellationToken.None);

        Assert.All(hardware.NetworkAdapters, a => Assert.Matches("^([0-9A-F]{2}-){5}[0-9A-F]{2}$", a.MacAddress));
    }
}
