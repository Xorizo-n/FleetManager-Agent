using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class DataFolderSecurityTests : IDisposable
{
    // Tests run unelevated: the current user stands in for SYSTEM and Administrators.
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly DataFolderSecurity.Principals Test = new(Me, new[] { Me }, Users);

    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "fm-acl-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public DataFolderSecurityTests()
    {
        _root = Path.Combine(_sandbox, "FleetManagerAgent");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void FolderInheritedFromProgramDataIsClosedToEveryoneButTheAgent()
    {
        AllowInheritable(_root, Everyone, FileSystemRights.ReadAndExecute);
        File.WriteAllText(Path.Combine(_root, "agent.json"), "{\"AgentToken\":\"secret\"}");
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(Path.Combine(_root, "logs", "agent.log"), "log");
        var planted = Path.Combine(_root, "planted.json");
        File.WriteAllText(planted, "{}");
        var plantedInfo = new FileInfo(planted);
        var plantedSecurity = plantedInfo.GetAccessControl();
        plantedSecurity.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Modify, AccessControlType.Allow));
        plantedInfo.SetAccessControl(plantedSecurity);

        var fixes = DataFolderSecurity.Repair(_root, Test);

        Assert.NotEmpty(fixes);
        var root = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(root.AreAccessRulesProtected);
        Assert.Equal(new IdentityReference[] { Me }, Rules(root).Select(r => r.IdentityReference).Distinct());
        foreach (var file in new[] { "agent.json", Path.Combine("logs", "agent.log"), "planted.json" })
        {
            var rules = Rules(new FileInfo(Path.Combine(_root, file)).GetAccessControl());
            Assert.All(rules, r => Assert.True(r.IsInherited, $"{file}: {r.IdentityReference}"));
            Assert.All(rules, r => Assert.Equal(Me, r.IdentityReference));
        }
        AssertNothingToFix();
    }

    [Fact]
    public void UsersStillReadTheWallpapers()
    {
        var folder = Path.Combine(_root, DataFolderSecurity.WallpapersFolder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "abc.jpg"), "jpg");

        DataFolderSecurity.Repair(_root, Test);

        var folderRules = Rules(new DirectoryInfo(folder).GetAccessControl());
        Assert.Contains(folderRules, r => !r.IsInherited && r.IdentityReference.Equals(Users) &&
            r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        var image = Rules(new FileInfo(Path.Combine(folder, "abc.jpg")).GetAccessControl());
        var read = Assert.Single(image, r => r.IdentityReference.Equals(Users));
        Assert.True(read.IsInherited);
        Assert.Equal(FileSystemRights.ReadAndExecute, read.FileSystemRights & ~FileSystemRights.Synchronize);
        AssertNothingToFix();
    }

    [Fact]
    public void MissingWallpapersFolderIsCreatedReadable()
    {
        Assert.Empty(Directory.GetDirectories(_root));

        DataFolderSecurity.Repair(_root, Test);

        var rules = Rules(new DirectoryInfo(Path.Combine(_root, DataFolderSecurity.WallpapersFolder)).GetAccessControl());
        Assert.Contains(rules, r => r.IdentityReference.Equals(Users) && !r.IsInherited);
        // An elevated process creates folders owned by Administrators, not by itself: the new
        // folder must get the same owner as the rest, or the next start would "fix" it again.
        AssertNothingToFix();
    }

    [Fact]
    public void PlantedJunctionIsRemovedWithoutTouchingItsTarget()
    {
        var elsewhere = Path.Combine(_sandbox, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "important.txt"), "keep");
        var link = Path.Combine(_root, DataFolderSecurity.WallpapersFolder);
        Junction(link, elsewhere);

        var fixes = DataFolderSecurity.Repair(_root, Test);

        Assert.Contains(fixes, f => f.Contains(link, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(elsewhere, "important.txt")));
        // Вместо ссылки — настоящая папка обоев.
        Assert.False(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Empty(Directory.GetFiles(link));
    }

    [Fact]
    public void DataFolderThatIsAJunctionIsReplaced()
    {
        var elsewhere = Path.Combine(_sandbox, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "agent.json"), "{}");
        Directory.Delete(_root);
        Junction(_root, elsewhere);

        DataFolderSecurity.Repair(_root, Test);

        Assert.False(new DirectoryInfo(_root).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.False(File.Exists(Path.Combine(_root, "agent.json")));
        Assert.True(File.Exists(Path.Combine(elsewhere, "agent.json")));
    }

    [Fact]
    public void FileMovedInKeepsNoRightsOfItsOldFolder()
    {
        DataFolderSecurity.Repair(_root, Test);
        var outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(outside);
        AllowInheritable(outside, Everyone, FileSystemRights.ReadAndExecute);
        var source = Path.Combine(outside, "server-route.json");
        File.WriteAllText(source, "{}");
        var moved = Path.Combine(_root, "server-route.json");
        // A move keeps the old folder's ACEs, still flagged as inherited.
        File.Move(source, moved);
        Assert.Contains(Rules(new FileInfo(moved).GetAccessControl()), r => r.IdentityReference.Equals(Everyone));

        Assert.NotEmpty(DataFolderSecurity.Repair(_root, Test));

        Assert.DoesNotContain(Rules(new FileInfo(moved).GetAccessControl()), r => r.IdentityReference.Equals(Everyone));
    }

    // What the installer's icacls leaves must pass the service's check, or the service would rewrite it on every start.
    [Theory]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", true)]
    [InlineData("O:SYD:PAI(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)", true)]
    [InlineData("O:BAD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;0x1200a9;;;BU)(A;CIID;0x116;;;BU)", false)]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)", false)]
    [InlineData("O:BUD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", false)]
    public void InstallerRootPassesTheServiceCheck(string sddl, bool inPlace)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        Assert.Equal(inPlace, DataFolderSecurity.IsRootInPlace(security, DataFolderSecurity.Machine));
    }

    [Theory]
    [InlineData("O:BAD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)", false, true)]
    [InlineData("O:BAD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1200a9;;;BU)", false, false)]
    [InlineData("O:S-1-5-21-1-2-3-1001D:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)", false, false)]
    [InlineData("O:BAD:AI(A;OICI;0x1200a9;;;BU)(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)", true, true)]
    [InlineData("O:BAD:AI(A;OICI;0x1301bf;;;BU)(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)", true, false)]
    public void InstallerChildrenPassTheServiceCheck(string sddl, bool wallpapersFolder, bool inPlace)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        var expected = wallpapersFolder ? new[] { DataFolderSecurity.ReadRule(DataFolderSecurity.Machine) } : Array.Empty<FileSystemAccessRule>();
        Assert.Equal(inPlace, DataFolderSecurity.IsChildInPlace(security, expected, wallpapersFolder, DataFolderSecurity.Machine));
    }

    // A second pass must find nothing, or the service would rewrite the permissions on every start.
    private void AssertNothingToFix()
    {
        var fixes = DataFolderSecurity.Repair(_root, Test);
        Assert.True(fixes.Count == 0, string.Join(Environment.NewLine, fixes));
    }

    private static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    private static void AllowInheritable(string directory, IdentityReference who, FileSystemRights rights)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(who, rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    // A junction needs no privilege, unlike a symlink — exactly what an unprivileged user would plant.
    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
