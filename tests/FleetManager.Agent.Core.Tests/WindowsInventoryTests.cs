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

    [Fact]
    public async Task RegistrySoftware_survivesAMalformedUninstallValue()
    {
        // Регрессия: NetBeans пишет NoModify как REG_DWORD из 8 байт, Get-ItemProperty по Uninstall\*
        // падал с InvalidCastException, и с SU5-VM-SBX не приходило ни одной программы из реестра.
        if (!OperatingSystem.IsWindows()) return;

        var root = @"Software\FleetManagerAgentTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var good = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\Uninstall\Good"))
            {
                good.SetValue("DisplayName", "Good App");
                good.SetValue("DisplayVersion", "1.2.3");
                good.SetValue("Publisher", "Fleet");
            }
            using (var broken = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\Uninstall\Broken"))
            {
                broken.SetValue("DisplayName", "Broken App");
                broken.SetValue("DisplayVersion", "25");
                SetMalformedDword(broken, "NoModify");
            }
            var path = $"HKCU:\\{root}\\Uninstall";
            var keys = $"'{path}'";

            var old = await WindowsInventoryCollector.RunPowerShellAsync(
                $"try {{ $null = Get-ItemProperty '{path}\\*' -ErrorAction SilentlyContinue; 'ok' }} catch {{ 'throws' }}", CancellationToken.None);
            Assert.Equal("throws", old.Trim()); // тест действительно воспроизводит проблему

            var output = await WindowsInventoryCollector.RunPowerShellAsync(
                $"$keys = @({keys}); @({WindowsInventoryCollector.RegistrySoftwareScript}) | Sort-Object Name | ConvertTo-Json -Compress", CancellationToken.None);

            using var document = System.Text.Json.JsonDocument.Parse(output);
            var names = document.RootElement.EnumerateArray().Select(x => x.GetProperty("Name").GetString()).ToArray();
            Assert.Equal(new[] { "Broken App", "Good App" }, names);
            var good0 = document.RootElement.EnumerateArray().Single(x => x.GetProperty("Name").GetString() == "Good App");
            Assert.Equal("1.2.3", good0.GetProperty("Version").GetString());
            Assert.Equal("Fleet", good0.GetProperty("Publisher").GetString());
            Assert.Equal("registry", good0.GetProperty("Source").GetString());
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\FleetManagerAgentTests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void SoftwareScript_readsUninstallEntriesValueByValue()
    {
        Assert.DoesNotContain("Get-ItemProperty", WindowsInventoryCollector.SoftwareScript);
        Assert.Contains("$keys = @(" + WindowsInventoryCollector.UninstallKeys + ")", WindowsInventoryCollector.SoftwareScript);
        Assert.Contains(WindowsInventoryCollector.RegistrySoftwareScript, WindowsInventoryCollector.SoftwareScript);
        foreach (var hive in new[] { @"HKLM:\Software\Microsoft", @"HKLM:\Software\Wow6432Node", @"HKCU:\Software\Microsoft" })
            Assert.Contains(hive, WindowsInventoryCollector.UninstallKeys);
    }

    // RegistryKey.SetValue не даёт записать REG_DWORD не из 4 байт — так делает установщик NetBeans.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetMalformedDword(Microsoft.Win32.RegistryKey key, string name)
    {
        var data = new byte[] { 1, 0, 0, 0, 0x31, 0x02, 0, 0 };
        var status = RegSetValueEx(key.Handle, name, 0, 4 /* REG_DWORD */, data, data.Length);
        Assert.Equal(0, status);
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);

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
    [InlineData("1000000000", 1_000_000_000L)]
    [InlineData("100000000", 100_000_000L)]
    [InlineData("null", null)]
    [InlineData("0", null)]
    [InlineData("9223372036854775807", null)]
    [InlineData("\"1 Gbps\"", null)]
    public void NetworkAdapters_reportTheLinkSpeedOnlyWhenThereIsALink(string speed, long? expected)
    {
        var json = "{\"Name\":\"Ethernet\",\"MacAddress\":\"34-5A-60-BF-91-C7\",\"Ipv4\":[],\"Speed\":" + speed + "}";

        Assert.Equal(expected, WindowsInventoryCollector.ParseNetworkAdapters(json).Single().LinkSpeedBps);
    }

    [Fact]
    public void CollectDisks_reportsFixedDisksWithFreeSpace()
    {
        if (!OperatingSystem.IsWindows()) return;

        var disks = WindowsInventoryCollector.CollectDisks();

        var system = Assert.Single(disks, d => string.Equals(d.Name, Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        Assert.Matches("^[A-Z]:$", system.Name);
        Assert.InRange(system.FreeBytes, 0, system.SizeBytes);
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
