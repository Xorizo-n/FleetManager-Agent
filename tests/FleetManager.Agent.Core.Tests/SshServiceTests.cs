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

    [Fact]
    public void SshdThatDoesNotAnswerIsRestarted()
    {
        // SU5-F306-W01: port 22 open, "Connection timed out during banner exchange"
        Assert.Equal(new SshServicePlan(SetAutomatic: false, Start: false, Restart: true), SshService.Plan(start: 2, listening: true, responding: false));
    }

    [Fact]
    public void UncheckedBannerDoesNotRestart()
    {
        Assert.Equal(SshServicePlan.Nothing, SshService.Plan(start: 2, listening: true, responding: null));
    }

    [Fact]
    public void DisabledSshdIsNotRestartedEither()
    {
        Assert.Equal(SshServicePlan.Nothing, SshService.Plan(start: 4, listening: true, responding: false));
    }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("HTTP/1.1 400 Bad Request", false)]
    public void BannerRecognition(string? received, bool expected)
    {
        Assert.Equal(expected, SshService.IsSshBanner(received));
    }
}
