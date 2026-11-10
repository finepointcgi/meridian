using System.Net;
using System.Net.Sockets;

namespace Meridian.Web.Integrations;

/// <summary>
/// Server-side request forgery guard for the outbound calls project owners configure (webhooks, issue-tracker base URLs).
/// Every connection — including redirects — resolves the host itself and connects only to an address that passes
/// <see cref="IsBlocked"/>, so a DNS answer cannot change between the check and the connect.
/// <list type="bullet">
/// <item>Always refused: unspecified, broadcast, multicast, link-local (169.254.0.0/16 and fe80::/10, which hold cloud
/// metadata services) and the known metadata addresses inside private ranges.</item>
/// <item>Refused unless <c>Meridian:Integrations:AllowPrivateNetworks=true</c>: loopback, RFC 1918, carrier-grade NAT
/// (100.64.0.0/10), IPv6 unique-local and site-local addresses — e.g. to reach an on-premises Jira.</item>
/// </list>
/// The desktop app's local mode does not use the guard (single user on their own machine).
/// </summary>
public static class OutboundGuard
{
    public const string AllowPrivateNetworksKey = "Meridian:Integrations:AllowPrivateNetworks";

    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("100.100.100.200"),     // Alibaba Cloud metadata
        IPAddress.Parse("fd00:ec2::254"),       // AWS IMDS over IPv6
    ];

    /// <summary>True when Meridian must not open a connection to <paramref name="address"/>.</summary>
    public static bool IsBlocked(IPAddress address, bool allowPrivateNetworks)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (MetadataAddresses.Contains(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                  // 0.0.0.0/8 ("this network")
            if (b[0] == 169 && b[1] == 254) return true;                 // link-local, cloud metadata
            if (b[0] >= 224) return true;                                // multicast, reserved, broadcast
            var isPrivate = b[0] == 127 || b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)            // carrier-grade NAT
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19));          // benchmarking
            return isPrivate && !allowPrivateNetworks;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal) return true;
            var b = ip.GetAddressBytes();
            var isPrivate = IPAddress.IsLoopback(ip) || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal
                || (b[0] == 0x20 && b[1] == 0x02);                       // 6to4 can embed any IPv4 address
            return isPrivate && !allowPrivateNetworks;
        }
        return true;
    }

    /// <summary>A handler whose every connection goes through the guard.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, allowPrivateNetworks, ct),
    };

    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivateNetworks, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        var allowed = addresses.Where(a => !IsBlocked(a, allowPrivateNetworks)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException(addresses.Length == 0
                ? $"{endpoint.Host} could not be resolved."
                : $"Connections to {endpoint.Host} are not allowed: it is a loopback, link-local or private network address. " +
                  $"A server administrator can allow private networks with {AllowPrivateNetworksKey}=true.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
