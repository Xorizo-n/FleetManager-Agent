using System.Text.Json;
using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class HardwareComponentsTests
{
    // Снято с SU5-K104-W09: MSI PRO B760M, один модуль памяти без партномера, NVMe + HDD,
    // монитор ASUS по DisplayPort, USB-клавиатура и мышь, гарнитура, подсветка платы (MSI 1462:7D99).
    private const string Su5Output = """
        {"Baseboard":{"Manufacturer":"Micro-Star International Co., Ltd.","Product":"PRO B760M-A DDR4 II (MS-7D99)","SerialNumber":"Default string"},
         "BiosVersion":"1.F1",
         "Memory":[{"Slot":"Controller1-DIMMB2","Manufacturer":"0x0080","PartNumber":"                    ","SerialNumber":"01480300","CapacityBytes":17179869184,"SpeedMhz":3200}],
         "Disks":[{"Model":"AirDisk 512GB SSD","SerialNumber":"0000_0006_2501_2623_3A5A_2703_4E00_0041.","BusType":"NVMe","SizeBytes":512110190592},
                  {"Model":"TOSHIBA DT02ABA200V","SerialNumber":"35F2S4R1SNDL","BusType":"IDE","SizeBytes":2000398934016}],
         "Gpus":{"Name":"Intel(R) UHD Graphics 770","DriverVersion":"32.0.101.7088","PnpId":"PCI\\VEN_8086&DEV_4680&SUBSYS_7D991462&REV_0C\\3&11583659&0&10"},
         "Monitors":[{"Manufacturer":"AUS","Model":"VA27UQSB","SerialNumber":"S2LMTF164672","ProductCode":"27C7","Year":2023,"Connection":10}],
         "Inputs":[{"Kind":"keyboard","Name":"Enhanced (101- or 102-key)","PnpId":"USB\\VID_1A2C&PID_2124&MI_00\\6&1B9C5C4F&0&0000"},
                   {"Kind":"keyboard","Name":"Standard PS/2 Keyboard","PnpId":"ACPI\\PNP0303\\4&1B2C3D&0"},
                   {"Kind":"mouse","Name":"USB Input Device","PnpId":"USB\\VID_1C4F&PID_0034\\5&2AF8BBA2&0&6"},
                   {"Kind":"mouse","Name":"Terminal Server Mouse Driver","PnpId":"ROOT\\TERMINPUT_BUS\\0000"}],
         "Usb":[{"Name":"USB Input Device","Reported":"Usb Mouse","PnpId":"USB\\VID_1C4F&PID_0034\\5&2AF8BBA2&0&6","Class":"HIDClass"},
                {"Name":"USB Input Device","Reported":null,"PnpId":"USB\\VID_1462&PID_7D99\\7D9924101807","Class":"HIDClass"},
                {"Name":"USB Composite Device","Reported":"USB Keyboard","PnpId":"USB\\VID_1A2C&PID_2124\\5&2AF8BBA2&0&5","Class":"USB"},
                {"Name":"USB Composite Device","Reported":"Logitech USB Headset","PnpId":"USB\\VID_046D&PID_0A45\\000000000000","Class":"USB"}]}
        """;

    [Fact]
    public void Parse_readsTheWorkplace()
    {
        var c = HardwareComponentsCollector.Parse(Su5Output)!;

        Assert.Equal("PRO B760M-A DDR4 II (MS-7D99)", c.Baseboard!.Product);
        Assert.Null(c.Baseboard.SerialNumber);
        Assert.Equal("1.F1", c.BiosVersion);

        var module = Assert.Single(c.MemoryModules);
        Assert.Equal("Controller1-DIMMB2", module.Slot);
        Assert.Null(module.PartNumber);
        Assert.Equal(17179869184, module.CapacityBytes);

        Assert.Equal(new[] { "0000_0006_2501_2623_3A5A_2703_4E00_0041", "35F2S4R1SNDL" }, c.PhysicalDisks.Select(d => d.SerialNumber));
        Assert.Equal("Intel(R) UHD Graphics 770", Assert.Single(c.Gpus).Name);

        var monitor = Assert.Single(c.Monitors);
        Assert.Equal(("AUS", "VA27UQSB", "S2LMTF164672", 10L), (monitor.Manufacturer, monitor.Model, monitor.SerialNumber, monitor.Connection));
    }

    [Fact]
    public void Parse_keepsRealKeyboardsAndMiceWithTheirOwnNames()
    {
        var inputs = HardwareComponentsCollector.Parse(Su5Output)!.InputDevices;

        // PS/2-заглушка BIOS и мышь RDP отброшены; имена — от самих устройств
        Assert.Equal(2, inputs.Count);
        Assert.Equal(new PeripheralInfo("keyboard", "USB Keyboard", "1A2C", "2124", null, null), inputs[0]);
        Assert.Equal(new PeripheralInfo("mouse", "Usb Mouse", "1C4F", "0034", null, null), inputs[1]);
    }

    [Fact]
    public void Parse_readsUsbDevicesWithSerials()
    {
        var usb = HardwareComponentsCollector.Parse(Su5Output)!.UsbDevices;

        Assert.Equal(4, usb.Count);
        Assert.Equal("7D9924101807", usb.Single(u => u.Vid == "1462").Serial);
        // Нули вместо серийного номера — не серийный номер
        var headset = usb.Single(u => u.Pid == "0A45");
        Assert.Equal(("Logitech USB Headset", null), (headset.Name, headset.Serial));
        Assert.Equal("USB Input Device", usb.Single(u => u.Vid == "1462").Name);
    }

    [Theory]
    [InlineData(@"USB\VID_046D&PID_C077\5&140AC59&0&5", "046D", "C077", null)]
    [InlineData(@"USB\VID_8087&PID_0033\7D9924101807", "8087", "0033", "7D9924101807")]
    [InlineData(@"USB\VID_1A2C&PID_2124&MI_00\6&1B9C5C4F&0&0000", "1A2C", "2124", null)]
    [InlineData(@"HID\VID_046D&PID_C52B&MI_01&Col01\7&2C1F&0&0000", "046D", "C52B", null)]
    [InlineData(@"usb\vid_05e3&pid_0610\abc123", "05E3", "0610", "abc123")]
    [InlineData(@"ACPI\PNP0303\4&1B2C3D&0", null, null, null)]
    public void PnpIds_takeTheSerialOnlyWhenTheDeviceReportsOne(string pnpId, string? vid, string? pid, string? serial) =>
        Assert.Equal((vid, pid, serial), HardwareComponentsCollector.PnpIds(pnpId));

    [Theory]
    [InlineData("Default string", null)]
    [InlineData("To be filled by O.E.M.", null)]
    [InlineData("0000000000", null)]
    [InlineData(" 424AB2C2 ", "424AB2C2")]
    [InlineData("E823_8FA6_BF53_0001_001B_448B_4E97_D390.", "E823_8FA6_BF53_0001_001B_448B_4E97_D390")]
    public void Serial_dropsVendorPlaceholders(string value, string? expected) =>
        Assert.Equal(expected, HardwareComponentsCollector.Serial(value));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Parse_returnsNullWithoutUsableOutput(string output) =>
        Assert.Null(HardwareComponentsCollector.Parse(output));

    [Fact]
    public void Payload_usesTheServerFieldNames()
    {
        var payload = FleetManagerApiClient.ComponentsPayload(HardwareComponentsCollector.Parse(Su5Output));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, JsonDefaults.Options));
        var root = json.RootElement;

        Assert.Equal("VA27UQSB", root.GetProperty("monitors")[0].GetProperty("model").GetString());
        Assert.Equal(17179869184, root.GetProperty("memory_modules")[0].GetProperty("capacity_bytes").GetInt64());
        Assert.Equal("mouse", root.GetProperty("input_devices")[1].GetProperty("kind").GetString());
        Assert.Equal("HIDClass", root.GetProperty("usb_devices")[0].GetProperty("device_class").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("baseboard").GetProperty("serial_number").ValueKind);
        Assert.Null(FleetManagerApiClient.ComponentsPayload(null));
    }

    [Fact]
    public async Task Script_runsOnThisMachine()
    {
        if (!OperatingSystem.IsWindows()) return;

        var components = await HardwareComponentsCollector.CollectAsync(CancellationToken.None);

        Assert.NotNull(components);
        Assert.NotEmpty(components.MemoryModules);
        Assert.All(components.InputDevices, d => Assert.Contains(d.Kind, new[] { "keyboard", "mouse" }));
    }
}
