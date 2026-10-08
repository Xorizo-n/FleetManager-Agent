using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class RegistrationTimingTests
{
    [Theory]
    [InlineData("DESKTOP-ABC123", "SU5-E302-OP", true)]   // переименован, ждёт перезагрузки
    [InlineData("SU5-E302-OP", "SU5-E302-OP", false)]
    [InlineData("su5-e302-op", "SU5-E302-OP", false)]     // NetBIOS-имена регистронезависимы
    [InlineData(null, "SU5-E302-OP", false)]               // реестр не прочитан — не блокируем регистрацию
    [InlineData("SU5-E302-OP", "", false)]
    public void ComputerRename_isPendingOnlyWhenBothNamesKnownAndDiffer(string? active, string? pending, bool expected)
    {
        Assert.Equal(expected, ComputerRename.IsPending(active, pending));
    }

    [Fact]
    public void SyncSchedule_retriesEveryMinuteWhileWaitingForRegistration()
    {
        var options = new AgentOptions { ServerUrl = "http://10.40.240.154:8080", EnrollmentToken = "enrollment-token", SyncIntervalMinutes = 5 };
        Assert.Equal(TimeSpan.FromMinutes(1), SyncSchedule.NextDelay(options));
    }

    [Fact]
    public void SyncSchedule_usesTheConfiguredIntervalOnceRegistered()
    {
        var options = new AgentOptions { ServerUrl = "http://10.40.240.154:8080", AgentToken = "agent-token", SyncIntervalMinutes = 5 };
        Assert.Equal(TimeSpan.FromMinutes(5), SyncSchedule.NextDelay(options));
    }

    [Fact]
    public void SyncSchedule_doesNotSpinWithoutAnEnrollmentToken()
    {
        // Без токена регистрация невозможна: ежеминутные повторы только засоряли бы журнал.
        var options = new AgentOptions { ServerUrl = "http://10.40.240.154:8080", SyncIntervalMinutes = 5 };
        Assert.Equal(TimeSpan.FromMinutes(5), SyncSchedule.NextDelay(options));
    }
}
