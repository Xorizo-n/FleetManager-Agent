using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace FleetManager.Agent.Core;

/// <summary>A local IPv4 address with a default gateway, as the route planner sees it.</summary>
public sealed record RouteCandidate(int InterfaceIndex, string Name, bool IsWired, IPAddress Address, int PrefixLength, IPAddress Gateway);

/// <summary>An extra host the server asks to keep reachable past a VPN (the distribution point), with the port to probe.</summary>
public sealed record PinRoute(string Host, int Port);

/// <summary>Host route to the Fleet Manager server through a physical adapter's gateway.</summary>
public sealed record ServerRoutePlan(IPAddress Server, int InterfaceIndex, string InterfaceName, IPAddress Gateway);

/// <summary>
/// Keeps the Fleet Manager server reachable over the local network while a full-tunnel VPN is up.
/// The built-in Windows VPN on SU5 PCs carries the internet and also captures traffic to the
/// server: heartbeats then arrive from the VPN pool, and the server's SSH replies leave through
/// the tunnel, so the PC drops out of Fleet Manager. A /32 route to the server through the
/// physical adapter's gateway wins over the VPN routes (longest prefix) and touches nothing
/// else — the users' internet still goes through the VPN.
/// Only gateways through which the server has already been reached without a VPN are trusted
/// (<see cref="PathCandidate"/> after a successful heartbeat), so a laptop in another network —
/// even one also numbered 10.x — never gets a route past its VPN.
/// </summary>
public static class ServerRoute
{
    private static readonly string[] VirtualAdapterMarkers =
        ["virtual", "hyper-v", "vmware", "virtualbox", "tap-", "wireguard", "wintun", "openvpn", "vpn", "loopback"];

    /// <summary>
    /// The route to pin, or null when none is needed or safe: the server is not a private IPv4
    /// address, it is on-link, or no adapter has a trusted gateway in the server's private block
    /// (a laptop at home reaches the server only through the VPN — leave that alone).
    /// </summary>
    public static ServerRoutePlan? Plan(IPAddress server, IEnumerable<RouteCandidate> candidates, IReadOnlyCollection<IPAddress> trustedGateways)
    {
        var inNetwork = InServerNetwork(server, candidates);
        if (inNetwork.Count == 0 || inNetwork.Any(c => InNetwork(server, c.Address, c.PrefixLength))) return null;
        var best = inNetwork
            .Where(c => trustedGateways.Contains(c.Gateway))
            .OrderByDescending(c => c.IsWired).ThenBy(c => c.InterfaceIndex)
            .FirstOrDefault();
        return best is null ? null : new ServerRoutePlan(server, best.InterfaceIndex, best.Name, best.Gateway);
    }

    /// <summary>
    /// The physical adapter the system currently uses to reach the server (its best interface),
    /// or null when that is a VPN or anything else. Called right after a successful heartbeat:
    /// its gateway is then known to lead to the server.
    /// </summary>
    public static RouteCandidate? PathCandidate(IPAddress server, IEnumerable<RouteCandidate> candidates, int bestInterfaceIndex) =>
        InServerNetwork(server, candidates)
            .Where(c => c.InterfaceIndex == bestInterfaceIndex)
            .OrderByDescending(c => c.IsWired)
            .FirstOrDefault();

    /// <summary>Index of the interface Windows would use to reach <paramref name="server"/> right now; -1 if unknown.</summary>
    public static int BestInterface(IPAddress server)
    {
        if (!OperatingSystem.IsWindows() || server.AddressFamily != AddressFamily.InterNetwork) return -1;
        // dwDestAddr is in network byte order — exactly the address bytes read as a native uint.
        return GetBestInterface(BitConverter.ToUInt32(server.GetAddressBytes(), 0), out var index) == 0 ? (int)index : -1;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    private static List<RouteCandidate> InServerNetwork(IPAddress server, IEnumerable<RouteCandidate> candidates)
    {
        if (server.AddressFamily != AddressFamily.InterNetwork || PrivateBlock(server) is not { } block) return [];
        return candidates
            .Where(c => c.Address.AddressFamily == AddressFamily.InterNetwork && InNetwork(c.Address, block.Network, block.Prefix))
            .Where(c => InNetwork(c.Gateway, c.Address, c.PrefixLength))
            .ToList();
    }

    /// <summary>Up physical adapters (wired or Wi-Fi) with an IPv4 default gateway; VPN and virtual adapters are skipped.</summary>
    public static IReadOnlyList<RouteCandidate> LocalCandidates()
    {
        var result = new List<RouteCandidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            bool wired;
            switch (nic.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx:
                    wired = true;
                    break;
                case NetworkInterfaceType.Wireless80211:
                    wired = false;
                    break;
                default:
                    continue; // Ppp (built-in VPN), Tunnel, Loopback...
            }
            var description = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            if (VirtualAdapterMarkers.Any(description.Contains)) continue;
            try
            {
                var properties = nic.GetIPProperties();
                var gateway = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gateway is null) continue;
                var index = properties.GetIPv4Properties().Index;
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IsLinkLocal(address)) continue;
                    result.Add(new RouteCandidate(index, nic.Name, wired, address, unicast.PrefixLength, gateway));
                }
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }
        }
        return result;
    }

    internal static bool InNetwork(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || network.AddressFamily != AddressFamily.InterNetwork) return false;
        if (prefixLength <= 0) return true;
        var mask = prefixLength >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefixLength);
        return (ToUInt32(address) & mask) == (ToUInt32(network) & mask);
    }

    private static (IPAddress Network, int Prefix)? PrivateBlock(IPAddress address)
    {
        foreach (var (network, prefix) in new[] { ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16) })
        {
            var block = IPAddress.Parse(network);
            if (InNetwork(address, block, prefix)) return (block, prefix);
        }
        return null;
    }

    private static bool IsLinkLocal(IPAddress address) => InNetwork(address, IPAddress.Parse("169.254.0.0"), 16);

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }
}
