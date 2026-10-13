using System.Net;
using System.Net.Sockets;

namespace NexoDesk.Shared.Security;

/// <summary>Faixas de endereço usadas pelo Tailscale: 100.64.0.0/10 (IPv4) e fd7a:115c:a1e0::/48 (IPv6).</summary>
public static class TailscaleAddress
{
    public static bool IsTailscale(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
        }

        return false;
    }

    /// <summary>Endereços que nunca podem ser usados para escutar (todas as interfaces).</summary>
    public static bool IsWildcard(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
}
