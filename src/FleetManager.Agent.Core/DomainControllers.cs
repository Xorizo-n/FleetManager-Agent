using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace FleetManager.Agent.Core;

/// <summary>
/// The domain controllers whose traffic a full-tunnel VPN must not capture. Signing in over SSH with a domain
/// account needs the controller: when the VPN took the route to it, the PC reported
/// <c>domain_trust: unreachable</c> and sshd reset the connection after the key exchange
/// (SU5-J108-W02), so the server lost the PC although the agent kept sending heartbeats.
/// The addresses go through the same route planner as the server (<see cref="ServerRoute.Plan"/>): a /32 route
/// only through a gateway that has already led to the server, and only while the controller answers on LDAP.
/// </summary>
public static class DomainControllers
{
    /// <summary>The port probed before keeping a route: every controller answers LDAP.</summary>
    public const int LdapPort = 389;
    private const int MaxAddresses = 6;
    private const int DiscoverTimeoutSeconds = 5;

    /// <summary>
    /// The DNS servers of the physical adapters (in a domain these are the controllers) and the controller Windows
    /// currently uses; IPv4 only, without loopback and link-local addresses.
    /// </summary>
    public static IReadOnlyList<IPAddress> Addresses(IEnumerable<RouteCandidate> candidates, IPAddress? discovered)
    {
        var result = new List<IPAddress>();
        foreach (var address in candidates.SelectMany(c => c.DnsServers ?? Array.Empty<IPAddress>()).Append(discovered))
        {
            if (address is null || address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || ServerRoute.IsLinkLocal(address)) continue;
            if (!result.Contains(address)) result.Add(address);
            if (result.Count == MaxAddresses) break;
        }
        return result;
    }

    /// <summary>The controller Windows has chosen for this PC's domain; null outside a domain or when none answers in time.</summary>
    public static async Task<IPAddress?> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return await Task.Run(QueryDomainController, cancellationToken).WaitAsync(TimeSpan.FromSeconds(DiscoverTimeoutSeconds), cancellationToken); }
        catch (TimeoutException) { return null; }
        // A failed lookup only means no extra route: it must not stop the server route from being kept
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    internal static IPAddress? ParseControllerAddress(string? raw)
    {
        // DomainControllerAddress looks like "\\10.74.240.10"
        var text = raw?.Trim().TrimStart('\\');
        return IPAddress.TryParse(text, out var address) ? address : null;
    }

    private static IPAddress? QueryDomainController()
    {
        var error = DsGetDcName(null, null, IntPtr.Zero, null, 0, out var buffer);
        if (error != 0 || buffer == IntPtr.Zero) return null; // 1355: the PC is not in a domain
        try { return ParseControllerAddress(Marshal.PtrToStructure<DomainControllerInfo>(buffer).DomainControllerAddress); }
        finally { NetApiBufferFree(buffer); }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int DsGetDcName(string? computerName, string? domainName, IntPtr domainGuid, string? siteName, uint flags, out IntPtr domainControllerInfo);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DomainControllerInfo
    {
        public string DomainControllerName;
        public string DomainControllerAddress;
        public uint DomainControllerAddressType;
        public Guid DomainGuid;
        public string DomainName;
        public string DnsForestName;
        public uint Flags;
        public string DcSiteName;
        public string ClientSiteName;
    }
}
