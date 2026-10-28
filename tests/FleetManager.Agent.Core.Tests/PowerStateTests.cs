using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public class PowerStateTests
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 28, 6, 26, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastEntry = new(2026, 10, 27, 16, 28, 56, TimeSpan.Zero);

    [Fact]
    public void KernelPowerWithoutBugcheckIsAPowerCut()
    {
        // SU5-F319-W06: Kernel-Power 41 and EventLog 6008 right after boot, the PC went dark the evening before
        var (kind, at) = PowerState.Classify(Boot, [new PowerEvent(41, Boot.AddSeconds(40)), new PowerEvent(6008, Boot.AddMinutes(1))], LastEntry);

        Assert.Equal("power_loss", kind);
        Assert.Equal(LastEntry, at);
    }

    [Fact]
    public void KernelPowerWithABugcheckIsACrash()
    {
        Assert.Equal("crash", PowerState.Classify(Boot, [new PowerEvent(41, Boot.AddSeconds(40), Bugcheck: 0x9F)], LastEntry).Kind);
    }

    [Fact]
    public void UnexpectedShutdownAloneIsAPowerCut()
    {
        Assert.Equal("power_loss", PowerState.Classify(Boot, [new PowerEvent(6008, Boot.AddMinutes(1))], LastEntry).Kind);
    }

    [Fact]
    public void ShortGapIsARestartLongOneIsAShutdown()
    {
        var restart = PowerState.Classify(Boot, [new PowerEvent(1074, Boot.AddMinutes(-3)), new PowerEvent(13, Boot.AddMinutes(-2))], null);
        var shutdown = PowerState.Classify(Boot, [new PowerEvent(1074, Boot.AddHours(-14)), new PowerEvent(13, Boot.AddHours(-14))], null);

        Assert.Equal(("restart", (DateTimeOffset?)Boot.AddMinutes(-2)), restart);
        Assert.Equal("clean", shutdown.Kind);
    }

    [Fact]
    public void OldUnexpectedShutdownsDoNotCount()
    {
        // 41 from an earlier boot a week ago; this one followed a normal shutdown
        var result = PowerState.Classify(Boot, [new PowerEvent(41, Boot.AddDays(-7)), new PowerEvent(13, Boot.AddHours(-12))], null);

        Assert.Equal("clean", result.Kind);
    }

    [Fact]
    public void NothingInTheLogIsUnknown()
    {
        Assert.Equal("unknown", PowerState.Classify(Boot, [], LastEntry).Kind);
    }

    [Fact]
    public void ScriptOutputIsParsed()
    {
        var output = "noise\n{\"boot\":\"2026-10-28T06:26:00.0000000Z\",\"last\":\"2026-10-27T16:28:56.0000000Z\",\"events\":[{\"id\":41,\"time\":\"2026-10-28T06:26:40.0000000Z\",\"bugcheck\":0}]}";

        var (boot, kind, at) = PowerState.Parse(output);

        Assert.Equal(Boot, boot);
        Assert.Equal("power_loss", kind);
        Assert.Equal(LastEntry, at);
        Assert.Equal((null, null, null), PowerState.Parse("Get-WinEvent : access denied"));
    }
}

public class SyncRetryTests
{
    private static AgentOptions Registered(int interval = 5) =>
        new() { ServerUrl = "http://10.40.240.154", AgentToken = "t", SyncIntervalMinutes = interval };

    [Fact]
    public void FailedHeartbeatsAreRetriedSoon()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), SyncSchedule.NextDelay(Registered(), 0));
        Assert.Equal(TimeSpan.FromSeconds(15), SyncSchedule.NextDelay(Registered(), 1));
        Assert.Equal(TimeSpan.FromSeconds(30), SyncSchedule.NextDelay(Registered(), 2));
        Assert.Equal(TimeSpan.FromMinutes(2), SyncSchedule.NextDelay(Registered(), 4));
        Assert.Equal(TimeSpan.FromMinutes(2), SyncSchedule.NextDelay(Registered(), 50));
    }

    [Fact]
    public void RetryIsNeverLaterThanTheInterval()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), SyncSchedule.NextDelay(Registered(interval: 1), 4));
    }
}
