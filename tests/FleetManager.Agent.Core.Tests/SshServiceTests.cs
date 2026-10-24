using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public class SshServiceTests
{
    [Fact]
    public void HealthySshdIsLeftAlone()
    {
        Assert.Equal(SshServicePlan.Nothing, SshService.Plan(start: 2, listening: true));
    }

    [Fact]
    public void ManualStartAfterAWindowsUpdateIsRepairedAndStarted()
    {
        // KB5129195: sshd stopped, start type reset to Manual
        Assert.Equal(new SshServicePlan(SetAutomatic: true, Start: true), SshService.Plan(start: 3, listening: false));
    }

    [Fact]
    public void ManualButRunningOnlyGetsAutomaticStart()
    {
        Assert.Equal(new SshServicePlan(SetAutomatic: true, Start: false), SshService.Plan(start: 3, listening: true));
    }

    [Fact]
    public void StoppedAutomaticSshdIsStarted()
    {
        Assert.Equal(new SshServicePlan(SetAutomatic: false, Start: true), SshService.Plan(start: 2, listening: false));
    }

    [Fact]
    public void DisabledSshdIsAnAdministratorsChoice()
    {
        Assert.Equal(SshServicePlan.Nothing, SshService.Plan(start: 4, listening: false));
    }

    [Fact]
    public void MissingSshdIsNotInstalledByTheAgent()
    {
        Assert.Equal(SshServicePlan.Nothing, SshService.Plan(start: null, listening: false));
    }

    [Theory]
    [InlineData(2, "auto")]
    [InlineData(3, "manual")]
    [InlineData(4, "disabled")]
    [InlineData(null, null)]
    public void StartTypeNames(int? start, string? expected)
    {
        Assert.Equal(expected, SshService.StartTypeName(start));
    }
}
