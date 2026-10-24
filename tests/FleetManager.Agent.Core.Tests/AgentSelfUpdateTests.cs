using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public class AgentSelfUpdateTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly SelfUpdateState Fresh = new(null, 0, null);

    [Theory]
    [InlineData("2026.10.24.23", "2026.10.25.24", -1)]
    [InlineData("2026.10.24.23", "2026.10.24.23", 0)]
    [InlineData("2026.10.9.5", "2026.10.10.1", -1)]
    [InlineData("2026.10.25.24", "2026.10.24.23", 1)]
    [InlineData("1.0", "1.0.0.0", 0)]
    [InlineData("dev", "2026.10.24.23", null)]
    [InlineData(null, "2026.10.24.23", null)]
    public void VersionsCompareNumerically(string? left, string? right, int? expected)
    {
        Assert.Equal(expected, AgentSelfUpdate.Compare(left, right));
    }

    [Fact]
    public void NewerOfferIsInstalled()
    {
        Assert.True(AgentSelfUpdate.ShouldInstall("2026.10.24.23", new("2026.10.25.24", Sha), Fresh, Now));
    }

    [Fact]
    public void SameOrOlderOfferIsIgnored()
    {
        Assert.False(AgentSelfUpdate.ShouldInstall("2026.10.24.23", new("2026.10.24.23", Sha), Fresh, Now));
        Assert.False(AgentSelfUpdate.ShouldInstall("2026.10.24.23", new("2026.10.22.22", Sha), Fresh, Now));
        Assert.False(AgentSelfUpdate.ShouldInstall(null, new("2026.10.25.24", Sha), Fresh, Now));
        Assert.False(AgentSelfUpdate.ShouldInstall("2026.10.24.23", null, Fresh, Now));
    }

    [Fact]
    public void FailedAttemptIsRetriedAfterAnHourAtMostThreeTimes()
    {
        var offer = new AgentUpdateOffer("2026.10.25.24", Sha);
        Assert.False(AgentSelfUpdate.ShouldInstall("2026.10.24.23", offer, new(offer.Version, 1, Now.AddMinutes(-30)), Now));
        Assert.True(AgentSelfUpdate.ShouldInstall("2026.10.24.23", offer, new(offer.Version, 2, Now.AddHours(-2)), Now));
        Assert.False(AgentSelfUpdate.ShouldInstall("2026.10.24.23", offer, new(offer.Version, 3, Now.AddDays(-1)), Now));
        // A newer release starts the count again
        Assert.True(AgentSelfUpdate.ShouldInstall("2026.10.24.23", new("2026.10.26.25", Sha), new(offer.Version, 3, Now), Now));
    }

    [Fact]
    public void OfferIsReadFromTheHeartbeatResponse()
    {
        var result = FleetManagerApiClient.ParseHeartbeatResponse(
            "{\"status\":\"accepted\",\"agent_update\":{\"version\":\"2026.10.25.24\",\"sha256\":\"" + Sha.ToUpperInvariant() + "\"}}");
        Assert.Equal(new AgentUpdateOffer("2026.10.25.24", Sha), result.Update);
        Assert.Null(result.Wallpapers);
    }

    [Theory]
    [InlineData("{\"status\":\"accepted\"}")]
    [InlineData("{\"agent_update\":{\"version\":\"2026.10.25.24\",\"sha256\":\"..\\\\evil\"}}")]
    [InlineData("{\"agent_update\":{\"sha256\":\"" + Sha + "\"}}")]
    [InlineData("{\"agent_update\":null}")]
    [InlineData("not json")]
    [InlineData("")]
    public void MalformedOfferIsIgnored(string body)
    {
        Assert.Null(FleetManagerApiClient.ParseHeartbeatResponse(body).Update);
    }
}
