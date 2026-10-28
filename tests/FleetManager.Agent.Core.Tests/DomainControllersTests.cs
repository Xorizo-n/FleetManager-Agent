using System.Net;
using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public class DomainControllersTests
{
    private static RouteCandidate Nic(params string[] dns) =>
        new(12, "Ethernet", true, IPAddress.Parse("10.152.134.105"), 27, IPAddress.Parse("10.152.134.97"), dns.Select(IPAddress.Parse).ToList());

    [Fact]
    public void DnsServersOfThePhysicalAdapterAreTheControllers()
    {
        var addresses = DomainControllers.Addresses([Nic("10.74.240.10", "10.98.240.10")], null);

        Assert.Equal(new[] { IPAddress.Parse("10.74.240.10"), IPAddress.Parse("10.98.240.10") }, addresses);
    }

    [Fact]
    public void TheControllerWindowsChoseIsAddedOnce()
    {
        // SU5-J108-W04: the PDC (10.98.241.10) is not among the DNS servers; the DNS one is not repeated
        var addresses = DomainControllers.Addresses([Nic("10.74.240.10")], IPAddress.Parse("10.98.241.10"));
        var again = DomainControllers.Addresses([Nic("10.74.240.10")], IPAddress.Parse("10.74.240.10"));

        Assert.Equal(new[] { IPAddress.Parse("10.74.240.10"), IPAddress.Parse("10.98.241.10") }, addresses);
        Assert.Single(again);
    }

    [Fact]
    public void LoopbackLinkLocalAndIpv6AreSkipped()
    {
        var addresses = DomainControllers.Addresses([Nic("127.0.0.1", "169.254.1.1", "0.0.0.0", "::1", "10.74.240.10")], null);

        Assert.Equal(new[] { IPAddress.Parse("10.74.240.10") }, addresses);
    }

    [Fact]
    public void CandidateWithoutDnsDataGivesNothing()
    {
        var plain = new RouteCandidate(12, "Ethernet", true, IPAddress.Parse("10.152.134.105"), 27, IPAddress.Parse("10.152.134.97"));

        Assert.Empty(DomainControllers.Addresses([plain], null));
    }

    [Fact]
    public void NoMoreThanSixAddresses()
    {
        var many = Enumerable.Range(1, 12).Select(i => "10.74.240." + i).ToArray();

        Assert.Equal(6, DomainControllers.Addresses([Nic(many)], null).Count);
    }

    [Theory]
    [InlineData("\\\\10.98.241.10", "10.98.241.10")]
    [InlineData("10.74.240.10", "10.74.240.10")]
    [InlineData("\\\\T04-501-PDC-PRI", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParsesTheControllerAddressOfDsGetDcName(string? raw, string? expected)
    {
        Assert.Equal(expected is null ? null : IPAddress.Parse(expected), DomainControllers.ParseControllerAddress(raw));
    }

    [Fact]
    public async Task LookupNeverThrows()
    {
        // Outside a domain (a build agent, a home PC) there is no controller; the native call must still be safe
        var address = await DomainControllers.DiscoverAsync(CancellationToken.None);

        Assert.True(address is null || address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }

    [Fact]
    public void ControllerBehindAnotherGatewayIsPinnedLikeTheServer()
    {
        // The same planner as for the server and the distribution point: only through a trusted gateway
        var candidates = new[] { Nic("10.74.240.10") };

        var plan = ServerRoute.Plan(IPAddress.Parse("10.74.240.10"), candidates, new[] { IPAddress.Parse("10.152.134.97") });

        Assert.NotNull(plan);
        Assert.Equal(IPAddress.Parse("10.152.134.97"), plan.Gateway);
        Assert.Null(ServerRoute.Plan(IPAddress.Parse("10.74.240.10"), candidates, new[] { IPAddress.Parse("10.152.5.1") }));
    }
}
