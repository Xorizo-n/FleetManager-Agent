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
}
