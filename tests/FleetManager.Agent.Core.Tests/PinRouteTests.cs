using System.Net;
using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public class PinRouteTests
{
    private static readonly IPAddress Distribution = IPAddress.Parse("10.152.240.10");

    [Fact]
    public void ParsesTheDistributionHostFromTheHeartbeatResponse()
    {
        var result = FleetManagerApiClient.ParseHeartbeatResponse("{\"status\":\"accepted\",\"pin_routes\":[{\"host\":\"10.152.240.10\",\"port\":8088}]}");

        Assert.Equal(new[] { new PinRoute("10.152.240.10", 8088) }, result.PinRoutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"status\":\"accepted\"}")]
    [InlineData("{\"pin_routes\":\"10.152.240.10\"}")]
    [InlineData("{\"pin_routes\":[{\"host\":\"\",\"port\":8088},{\"host\":\"10.152.240.10\"},{\"host\":\"10.152.240.10\",\"port\":0},{\"host\":\"10.152.240.10\",\"port\":70000},\"x\"]}")]
    public void AbsentOrMalformedMeansNoRoutes(string? body)
    {
        Assert.Empty(FleetManagerApiClient.ParsePinRoutes(body));
    }

    [Fact]
    public void NoMoreThanFourRoutesAreTaken()
    {
        var items = string.Join(",", Enumerable.Range(1, 9).Select(i => "{\"host\":\"10.152.240." + i + "\",\"port\":8088}"));

        Assert.Equal(4, FleetManagerApiClient.ParsePinRoutes("{\"pin_routes\":[" + items + "]}").Count);
    }

    [Fact]
    public void ClassroomPcPinsTheDistributionPointThroughItsWiredGateway()
    {
        // The same planner as for the server: the gateway that already led to the server without a VPN.
        var candidates = new[] { new RouteCandidate(12, "Ethernet", true, IPAddress.Parse("10.152.134.105"), 27, IPAddress.Parse("10.152.134.97")) };

        var plan = ServerRoute.Plan(Distribution, candidates, new[] { IPAddress.Parse("10.152.134.97") });

        Assert.NotNull(plan);
        Assert.Equal(Distribution, plan.Server);
        Assert.Equal(IPAddress.Parse("10.152.134.97"), plan.Gateway);
    }

    [Fact]
    public void UntrustedGatewayGetsNoRouteToTheDistributionPoint()
    {
        var candidates = new[] { new RouteCandidate(12, "Ethernet", true, IPAddress.Parse("10.152.134.105"), 27, IPAddress.Parse("10.152.134.97")) };

        Assert.Null(ServerRoute.Plan(Distribution, candidates, new[] { IPAddress.Parse("10.152.5.1") }));
    }
}
