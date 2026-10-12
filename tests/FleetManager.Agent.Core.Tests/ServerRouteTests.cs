using System.Net;
using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public class ServerRouteTests
{
    private static readonly IPAddress Server = IPAddress.Parse("10.40.240.154");
    private static readonly IPAddress[] Trusted = [IPAddress.Parse("10.152.5.1"), IPAddress.Parse("10.152.200.1")];

    private static RouteCandidate Nic(string address, int prefix, string gateway, bool wired = true, int index = 12, string name = "Ethernet") =>
        new(index, name, wired, IPAddress.Parse(address), prefix, IPAddress.Parse(gateway));

    [Fact]
    public void ClassroomPcPinsTheServerThroughItsWiredGateway()
    {
        var plan = ServerRoute.Plan(Server, [Nic("10.152.5.66", 25, "10.152.5.1")], Trusted);

        Assert.NotNull(plan);
        Assert.Equal(Server, plan.Server);
        Assert.Equal(IPAddress.Parse("10.152.5.1"), plan.Gateway);
        Assert.Equal(12, plan.InterfaceIndex);
    }

    [Fact]
    public void WiredAdapterWinsOverWifi()
    {
        var plan = ServerRoute.Plan(Server, [
            Nic("10.152.200.7", 24, "10.152.200.1", wired: false, index: 5, name: "Wi-Fi"),
            Nic("10.152.5.66", 25, "10.152.5.1", index: 12),
        ], Trusted);

        Assert.Equal("Ethernet", plan?.InterfaceName);
    }

    [Fact]
    public void LaptopInAnotherNetworkIsLeftToTheVpn()
    {
        // The server is reachable there only through the VPN — even when that network is also 10.x.
        Assert.Null(ServerRoute.Plan(Server, [Nic("192.168.1.34", 24, "192.168.1.1")], [IPAddress.Parse("192.168.1.1")]));
        Assert.Null(ServerRoute.Plan(Server, [Nic("10.20.30.139", 24, "10.20.30.1", wired: false)], Trusted));
        Assert.Null(ServerRoute.Plan(Server, [], Trusted));
    }

    [Fact]
    public void GatewayNeverSeenLeadingToTheServerIsNotUsed()
    {
        Assert.Null(ServerRoute.Plan(Server, [Nic("10.152.5.66", 25, "10.152.5.1")], []));
    }

    [Fact]
    public void ServerInTheSameSubnetNeedsNoRoute()
    {
        Assert.Null(ServerRoute.Plan(Server, [Nic("10.40.240.20", 24, "10.40.240.1")], [IPAddress.Parse("10.40.240.1")]));
    }

    [Fact]
    public void PublicOrIpv6ServerIsNotTouched()
    {
        Assert.Null(ServerRoute.Plan(IPAddress.Parse("93.88.176.5"), [Nic("10.152.5.66", 25, "10.152.5.1")], Trusted));
        Assert.Null(ServerRoute.Plan(IPAddress.IPv6Loopback, [Nic("10.152.5.66", 25, "10.152.5.1")], Trusted));
    }

    [Fact]
    public void GatewayOutsideTheAdapterSubnetIsIgnored()
    {
        Assert.Null(ServerRoute.Plan(Server, [Nic("10.152.5.66", 25, "10.152.6.1")], [IPAddress.Parse("10.152.6.1")]));
    }

    [Fact]
    public void HeartbeatOverThePhysicalAdapterRevealsATrustedGateway()
    {
        RouteCandidate[] nics = [Nic("10.152.5.66", 25, "10.152.5.1", index: 12)];

        Assert.Equal(IPAddress.Parse("10.152.5.1"), ServerRoute.PathCandidate(Server, nics, 12)?.Gateway);
        // The best interface is the VPN (not a physical adapter): nothing is learned.
        Assert.Null(ServerRoute.PathCandidate(Server, nics, 31));
        Assert.Null(ServerRoute.PathCandidate(Server, nics, -1));
    }

    [Theory]
    [InlineData("10.152.5.66", "10.152.5.0", 25, true)]
    [InlineData("10.152.5.200", "10.152.5.0", 25, false)]
    [InlineData("10.40.240.154", "10.0.0.0", 8, true)]
    [InlineData("172.20.1.1", "172.16.0.0", 12, true)]
    [InlineData("172.32.1.1", "172.16.0.0", 12, false)]
    public void SubnetMembership(string address, string network, int prefix, bool expected)
    {
        Assert.Equal(expected, ServerRoute.InNetwork(IPAddress.Parse(address), IPAddress.Parse(network), prefix));
    }
}
