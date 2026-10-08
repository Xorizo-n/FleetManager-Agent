using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class AgentConfigurationTests
{
    [Fact]
    public void NormalizeServerUrl_trimsTrailingSlash()
    {
        Assert.Equal("https://fleet.example/api", AgentConfiguration.NormalizeServerUrl("  https://fleet.example/api/// "));
    }

    [Fact]
    public void DefaultDataDirectory_isUnderProgramData()
    {
        var path = AgentConfiguration.DefaultDataDirectory;
        Assert.EndsWith(Path.Combine("FleetManagerAgent"), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MachineIdentityFile_isStableForDataDirectory()
    {
        Assert.Equal(
            Path.Combine("FleetManagerAgent", "machine-id"),
            Path.Combine("FleetManagerAgent", Path.GetFileName(AgentConfiguration.MachineIdentityFile("FleetManagerAgent"))));
    }

    [Fact]
    public void DefaultApiPath_isProxiedThroughFrontend()
    {
        Assert.Equal("/api/agent", AgentConfiguration.DefaultApiPath);
    }

    [Fact]
    public void MachineIdentity_isPersisted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fleet-manager-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = MachineIdentity.GetOrCreate(directory);
            var second = MachineIdentity.GetOrCreate(directory);
            Assert.Equal(first, second);
            Assert.True(Guid.TryParse(first, out _));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AgentOptions_keepsInstallerWrittenSshSourceAddressAcrossServiceRewrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fleet-manager-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            // Установщик пишет PascalCase, служба читает и перезаписывает файл camelCase.
            File.WriteAllText(
                AgentConfiguration.ConfigurationFile(directory),
                "{\"ServerUrl\":\"http://10.40.240.154:8080\",\"SshSourceAddress\":\"10.40.240.154,10.40.0.0/24\",\"SyncIntervalMinutes\":5}");

            AgentOptions.Load(directory).Save(directory);

            Assert.Equal("10.40.240.154,10.40.0.0/24", AgentOptions.Load(directory).SshSourceAddress);
            Assert.Contains("\"sshSourceAddress\"", File.ReadAllText(AgentConfiguration.ConfigurationFile(directory)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NormalizeServerUrl_rejects_non_http_urls()
    {
        Assert.Throws<ArgumentException>(() => AgentConfiguration.NormalizeServerUrl("ftp://fleet.example"));
    }
}
