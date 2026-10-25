using FleetManager.Agent.Core;
using Microsoft.Win32;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public class SshHealthTests
{
    [Fact]
    public void ConfiguredSourceAddressWins()
    {
        Assert.Equal(new[] { "10.40.240.154", "10.40.0.0/24" },
            SshHealthChecker.FirewallSources("10.40.240.154, 10.40.0.0/24", "http://fleet.example/"));
    }

    [Fact]
    public void InvalidSourceEntriesAreDropped()
    {
        Assert.Equal(new[] { "10.40.240.154" }, SshHealthChecker.FirewallSources("10.40.240.154,fleet.local,10.0.0.0/33", null));
    }

    [Fact]
    public void ServerIpIsTheDefaultSource()
    {
        Assert.Equal(new[] { "10.40.240.154" }, SshHealthChecker.FirewallSources(null, "http://10.40.240.154:8000"));
    }

    [Fact]
    public void NoServerUrlMeansNoSource()
    {
        Assert.Empty(SshHealthChecker.FirewallSources(null, null));
        Assert.Empty(SshHealthChecker.FirewallSources("", "not a url"));
    }

    [Theory]
    [InlineData("Trusted DC Connection Status Status = 0 0x0 NERR_Success\r\nTrust Verification Status = 0 0x0 NERR_Success\r\nThe command completed successfully", "ok")]
    [InlineData("Flags: 0\r\nTrusted DC Connection Status Status = 1311 0x51f ERROR_NO_LOGON_SERVERS\r\nTrust Verification Status = 1311 0x51f ERROR_NO_LOGON_SERVERS", "unreachable")]
    [InlineData("Trusted DC Connection Status Status = 1788 0x6fc ERROR_TRUSTED_DOMAIN_FAILURE\r\nThe command completed successfully", "broken")]
    [InlineData("Trusted DC Connection Status Status = 0 0x0 NERR_Success\r\nTrust Verification Status = 1789 0x6fd ERROR_TRUSTED_RELATIONSHIP_FAILURE", "broken")]
    [InlineData("I_NetLogonControl failed: Status = 5 0x5 ERROR_ACCESS_DENIED", null)]
    [InlineData(null, null)]
    public void DomainTrustFromNltest(string? output, string? expected)
    {
        Assert.Equal(expected, SshHealthChecker.DomainTrustState(output));
    }

    [Fact]
    public void PortIsReadBeforeTheFirstMatchBlock()
    {
        Assert.Equal(2222, SshHealthChecker.ConfiguredPort(new[] { "#Port 22", "Port 2222", "Match Address *,!10.40.240.154", "    Port 5022" }));
        Assert.Null(SshHealthChecker.ConfiguredPort(new[] { "#Port 22", "PasswordAuthentication yes" }));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true)]
    [InlineData(@"""C:\Program Files\PowerShell\7\pwsh.exe""", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(null, false)]
    public void DefaultShellMustBePowerShell(string? shell, bool expected)
    {
        Assert.Equal(expected, SshHealthChecker.IsPowerShell(shell));
    }

    [Fact]
    public void SourceRestrictionIsTheInstallersBlock()
    {
        Assert.True(SshHealthChecker.HasSourceRestriction(new[] { "Port 22", "", "# BEGIN FleetManager SSH source restriction", "Match Address *,!10.40.240.154", "    DenyUsers *", "# END FleetManager SSH source restriction" }));
        // An MSI installed by hand ships its stock sshd_config
        Assert.False(SshHealthChecker.HasSourceRestriction(new[] { "#Port 22", "Match Group administrators", "       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys" }));
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOx system@PC", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOx")]
    [InlineData("ecdsa-sha2-nistp256 AAAAE2VjZHNh", "ecdsa-sha2-nistp256 AAAAE2VjZHNh")]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void HostKeyLines(string line, string? expected)
    {
        Assert.Equal(expected, SshHealthChecker.PublicKeyOf(line));
    }

    [Fact]
    public void ScriptOutputIsParsedFromTheLastJsonLine()
    {
        var state = SshHealthChecker.ParseScriptOutput(
            "WARNING: noise\n{\"repaired\":\"missing\",\"rule\":\"ok\",\"ignored_on\":\"Domain\",\"gpo_allows\":false,\"blocking\":[],\"profiles\":{\"value\":[\"DomainAuthenticated\"],\"Count\":1},\"login_is_admin\":true,\"nltest\":\"Trusted DC Connection Status Status = 0 0x0 NERR_Success\"}\n")!;
        Assert.Equal("ok", state.Rule);
        Assert.Equal("missing", state.Repaired);
        Assert.Equal(new[] { "Domain" }, state.IgnoredOn);
        Assert.False(state.GpoAllows);
        Assert.Empty(state.Blocking);
        Assert.Equal(new[] { "DomainAuthenticated" }, state.Profiles);
        Assert.True(state.LoginIsAdmin);
        Assert.Equal("ok", SshHealthChecker.DomainTrustState(state.NltestOutput));
    }

    [Fact]
    public void NoJsonMeansNoState()
    {
        Assert.Null(SshHealthChecker.ParseScriptOutput("Get-NetFirewallRule : access denied"));
        Assert.Null(SshHealthChecker.ParseScriptOutput(null));
    }

    [Fact]
    public void ScriptHasNoDoubleQuotesAndQuotesValues()
    {
        var script = SshHealthChecker.Script(new[] { "10.40.240.154" }, repair: true, login: "at\\O'Brien", checkTrust: true);
        Assert.DoesNotContain('"', script);
        Assert.Contains("$sources = @('10.40.240.154')", script);
        Assert.Contains("$login = 'at\\O''Brien'", script);
        Assert.Contains("$repair = $true", script);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\system32;%SystemRoot%;C:\Tools", RegistryValueKind.ExpandString, null)]
    [InlineData(@"C:\MultiClet\bin", RegistryValueKind.ExpandString, MachinePath.Base + @";C:\MultiClet\bin")]
    [InlineData("", RegistryValueKind.ExpandString, MachinePath.Base)]
    [InlineData(null, RegistryValueKind.ExpandString, MachinePath.Base)]
    [InlineData(@"%SystemRoot%\system32;C:\Tools", RegistryValueKind.String, @"%SystemRoot%\system32;C:\Tools")]
    public void MachinePathRepair(string? raw, RegistryValueKind kind, string? expected)
    {
        Assert.Equal(expected, MachinePath.Repaired(raw, kind));
    }
}
