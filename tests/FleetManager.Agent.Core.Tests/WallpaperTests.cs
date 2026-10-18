using System.Text.Json;
using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class WallpaperTests
{
    // Локальная политика из образа SU5-K104-W09: экран блокировки, без пользовательской части
    private const string Su5MachinePol = "UFJlZwEAAABbAFMAbwBmAHQAdwBhAHIAZQBcAFAAbwBsAGkAYwBpAGUAcwBcAE0AaQBjAHIAbwBzAG8AZgB0AFwAVwBpAG4AZABvAHcAcwBcAFAAZQByAHMAbwBuAGEAbABpAHoAYQB0AGkAbwBuAAAAOwBMAG8AYwBrAFMAYwByAGUAZQBuAEkAbQBhAGcAZQAAADsAAQAAADsASgAAADsAQwA6AFwAVwBhAGwAbABwAGEAcABlAHIAXABiAGwAbwBjAGsAMQA2AC0AOQBfAG4AZQB3AC0AdwBpAG4AMQAxAC4AcABuAGcAAABdAFsAUwBvAGYAdAB3AGEAcgBlAFwAUABvAGwAaQBjAGkAZQBzAFwATQBpAGMAcgBvAHMAbwBmAHQAXABXAGkAbgBkAG8AdwBzAFwAUABlAHIAcwBvAG4AYQBsAGkAegBhAHQAaQBvAG4AAAA7AEwAbwBjAGsAUwBjAHIAZQBlAG4ATwB2AGUAcgBsAGEAeQBzAEQAaQBzAGEAYgBsAGUAZAAAADsABAAAADsABAAAADsAAQAAAF0AWwBTAG8AZgB0AHcAYQByAGUAXABQAG8AbABpAGMAaQBlAHMAXABNAGkAYwByAG8AcwBvAGYAdABcAFcAaQBuAGQAbwB3AHMAXABQAGUAcgBzAG8AbgBhAGwAaQB6AGEAdABpAG8AbgAAADsATgBvAEMAaABhAG4AZwBpAG4AZwBMAG8AYwBrAFMAYwByAGUAZQBuAAAAOwAEAAAAOwAEAAAAOwABAAAAXQA=";
    private const string Su5GptIni = "[General]\r\ngPCMachineExtensionNames=[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-3407-48AE-BA88-E8213C6761F1}]\r\nVersion=2\r\n";

    // MR32-411-02: обои для всех пользователей
    private const string Mr32UserPol = "UFJlZwEAAABbAFMAbwBmAHQAdwBhAHIAZQBcAE0AaQBjAHIAbwBzAG8AZgB0AFwAVwBpAG4AZABvAHcAcwBcAEMAdQByAHIAZQBuAHQAVgBlAHIAcwBpAG8AbgBcAFAAbwBsAGkAYwBpAGUAcwBcAFMAeQBzAHQAZQBtAAAAOwBXAGEAbABsAHAAYQBwAGUAcgAAADsAAQAAADsAKgAAADsAQwA6AFwAdwBhAGwAbABwAGEAcABlAHIAXwBuAGUAdwAuAHAAbgBnAAAAXQBbAFMAbwBmAHQAdwBhAHIAZQBcAE0AaQBjAHIAbwBzAG8AZgB0AFwAVwBpAG4AZABvAHcAcwBcAEMAdQByAHIAZQBuAHQAVgBlAHIAcwBpAG8AbgBcAFAAbwBsAGkAYwBpAGUAcwBcAFMAeQBzAHQAZQBtAAAAOwBXAGEAbABsAHAAYQBwAGUAcgBTAHQAeQBsAGUAAAA7AAEAAAA7AAQAAAA7ADQAAABdAA==";

    private const string Sha = "3f5a0c1e9d8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f";

    [Fact]
    public void PolicyFile_readsTheImagePolicy()
    {
        var entries = PolicyFile.Parse(Convert.FromBase64String(Su5MachinePol));

        Assert.Equal(new[] { "LockScreenImage", "LockScreenOverlaysDisabled", "NoChangingLockScreen" }, entries.Select(e => e.ValueName));
        Assert.Equal(@"C:\Wallpaper\block16-9_new-win11.png", entries[0].StringValue);
        Assert.Equal(PolicyEntry.RegDword, entries[1].Type);
        Assert.Equal(1u, BitConverter.ToUInt32(entries[1].Data));
    }

    [Theory]
    [InlineData(Su5MachinePol)]
    [InlineData(Mr32UserPol)]
    public void PolicyFile_writesBackByteForByte(string base64)
    {
        var original = Convert.FromBase64String(base64);
        Assert.Equal(original, PolicyFile.Write(PolicyFile.Parse(original)));
    }

    [Fact]
    public void PolicyFile_replacesOnlyTheLockScreenImage()
    {
        var entries = PolicyFile.Parse(Convert.FromBase64String(Su5MachinePol));
        var ours = PolicyEntry.String(WallpaperManager.LockScreenKey, WallpaperManager.LockScreenValue, @"C:\ProgramData\FleetManagerAgent\wallpapers\a.jpg");

        var updated = PolicyFile.Parse(PolicyFile.Write(PolicyFile.Set(entries, WallpaperManager.LockScreenKey, "lockscreenimage", ours)));

        Assert.Equal(3, updated.Count);
        Assert.Equal(@"C:\ProgramData\FleetManagerAgent\wallpapers\a.jpg", updated[0].StringValue);
        Assert.Equal(entries[1], updated[1], new EntryComparer());
        Assert.Equal(entries[2], updated[2], new EntryComparer());
    }

    [Fact]
    public void Backup_restoresTheImageValuesAndRemovesAddedOnes()
    {
        // SU5: обоев рабочего стола в образе нет — после возврата записей быть не должно
        var su5 = new List<PolicyEntry>();
        var su5Backup = new WallpaperBackup { Entries = WallpaperManager.BackupEntries(su5, WallpaperManager.DesktopKey, "Wallpaper", "WallpaperStyle") };
        var managed = PolicyFile.Set(su5, WallpaperManager.DesktopKey, "Wallpaper", PolicyEntry.String(WallpaperManager.DesktopKey, "Wallpaper", @"C:\x.jpg"));
        Assert.Empty(WallpaperManager.RestoreEntries(managed, su5Backup, WallpaperManager.DesktopKey, "Wallpaper", "WallpaperStyle"));

        // MR32: возвращаются C:\wallpaper_new.png и стиль «4»
        var mr32 = PolicyFile.Parse(Convert.FromBase64String(Mr32UserPol));
        var json = JsonSerializer.Serialize(new WallpaperBackup { Entries = WallpaperManager.BackupEntries(mr32, WallpaperManager.DesktopKey, "Wallpaper", "WallpaperStyle") }, JsonDefaults.Options);
        var backup = JsonSerializer.Deserialize<WallpaperBackup>(json, JsonDefaults.Options)!;
        managed = PolicyFile.Set(mr32, WallpaperManager.DesktopKey, "Wallpaper", PolicyEntry.String(WallpaperManager.DesktopKey, "Wallpaper", @"C:\x.jpg"));
        var restored = WallpaperManager.RestoreEntries(managed, backup, WallpaperManager.DesktopKey, "Wallpaper", "WallpaperStyle");
        Assert.Equal(PolicyFile.Write(mr32), PolicyFile.Write(restored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PReg\u0001\u0000\u0000\u0000[")]
    [InlineData("garbage!")]
    public void PolicyFile_refusesDamagedFiles(string content)
    {
        var data = System.Text.Encoding.Latin1.GetBytes(content);
        if (data.Length == 0) Assert.Empty(PolicyFile.Parse(data));
        else Assert.Throws<InvalidDataException>(() => PolicyFile.Parse(data));
    }

    [Fact]
    public void GptIni_bumpsTheUserVersionAndAddsTheUserExtension()
    {
        var updated = GptIni.Bump(Su5GptIni, machine: false, user: true);

        Assert.Contains("Version=65538\r\n", updated);
        Assert.Contains("gPCMachineExtensionNames=" + GptIni.MachineExtensions, updated);
        Assert.Contains("gPCUserExtensionNames=" + GptIni.UserExtensions, updated);
        Assert.StartsWith("[General]\r\n", updated);
    }

    [Fact]
    public void GptIni_bumpsTheMachineVersionOnly()
    {
        var updated = GptIni.Bump("[General]\r\nVersion=65537\r\ngPCUserExtensionNames=" + GptIni.UserExtensions + "\r\n", machine: true, user: false);

        Assert.Contains("Version=65538\r\n", updated);
        Assert.Contains("gPCMachineExtensionNames=" + GptIni.MachineExtensions, updated);
        Assert.Equal(1, updated.Split("gPCUserExtensionNames=").Length - 1);
    }

    [Fact]
    public void GptIni_isCreatedWhenMissing()
    {
        var created = GptIni.Bump(null, machine: true, user: false);

        Assert.StartsWith("[General]\r\n", created);
        Assert.Contains("\r\nVersion=1\r\n", created);
        Assert.Contains("\r\ngPCMachineExtensionNames=" + GptIni.MachineExtensions + "\r\n", created);
        Assert.Equal(3, created.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void DesiredWallpapers_parseTheHeartbeatResponse()
    {
        var body = $$$"""
            {"status":"accepted","wallpaper":{"desktop":{"id":"d1","sha256":"{{{Sha.ToUpperInvariant()}}}","file_name":"autumn.png","style":"3"},
             "lock_screen":null,"thumbnails":["{{{Sha}}}"]}}
            """;

        var desired = FleetManagerApiClient.ParseDesiredWallpapers(body)!;

        Assert.Equal(new WallpaperImage("d1", Sha, "autumn.png", "3"), desired.Desktop);
        Assert.Null(desired.LockScreen);
        Assert.Equal(new[] { Sha }, desired.Thumbnails);
    }

    [Theory]
    [InlineData("""{"status":"accepted"}""")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void DesiredWallpapers_areUnknownWithoutTheField(string body) =>
        Assert.Null(FleetManagerApiClient.ParseDesiredWallpapers(body));

    [Fact]
    public void DesiredWallpapers_ignoreAnInvalidHash()
    {
        var desired = FleetManagerApiClient.ParseDesiredWallpapers("""{"wallpaper":{"desktop":{"id":"d1","sha256":"..\\..\\evil"},"lock_screen":{"id":"l1","sha256":"zz"}}}""")!;
        Assert.Null(desired.Desktop);
        Assert.Null(desired.LockScreen);
    }

    [Theory]
    [InlineData("photo.JPEG", ".jpeg")]
    [InlineData("lock.png", ".png")]
    [InlineData("..\\..\\evil.exe", ".jpg")]
    [InlineData(null, ".jpg")]
    public void Extension_isLimitedToImages(string? name, string expected) =>
        Assert.Equal(expected, WallpaperManager.Extension(name));

    [Fact]
    public void Payload_usesTheServerFieldNames()
    {
        var report = new WallpaperReport(
            new CurrentWallpaper(@"C:\wallpaper_new.png", Sha, "policy"), null, "d1", null, null,
            new[] { new WallpaperThumbnail(Sha, "/9j/4AAQ") });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(FleetManagerApiClient.WallpaperPayload(report), JsonDefaults.Options));
        var root = json.RootElement;

        Assert.Equal("policy", root.GetProperty("desktop").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lock_screen").ValueKind);
        Assert.Equal("d1", root.GetProperty("applied_desktop_id").GetString());
        Assert.Equal("/9j/4AAQ", root.GetProperty("thumbnails")[0].GetProperty("jpeg_base64").GetString());
    }

    private sealed class EntryComparer : IEqualityComparer<PolicyEntry>
    {
        public bool Equals(PolicyEntry? x, PolicyEntry? y) =>
            x is not null && y is not null && x.Key == y.Key && x.ValueName == y.ValueName && x.Type == y.Type && x.Data.SequenceEqual(y.Data);

        public int GetHashCode(PolicyEntry obj) => obj.ValueName.GetHashCode();
    }
}
