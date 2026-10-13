using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;

namespace NexoDesk.Shared.Security;

/// <summary>Localiza o certificado deste dispositivo no repositório do sistema pela impressão SHA-256.</summary>
public static class CertificateStoreLookup
{
    /// <summary>
    /// Procura em CurrentUser\My e depois em LocalMachine\My um certificado com essa impressão e com chave privada.
    /// Retorna null se não existir ou se a chave privada não estiver acessível para este usuário.
    /// </summary>
    public static X509Certificate2? FindWithPrivateKey(string sha256)
    {
        string? wanted = CertificateFingerprint.Normalize(sha256);
        if (wanted is null)
            return null;

        foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
            {
                continue;
            }

            X509Certificate2? match = null;
            foreach (X509Certificate2 cert in store.Certificates)
            {
                if (match is null && cert.HasPrivateKey && CertificateFingerprint.Sha256(cert) == wanted)
                    match = cert;
                else
                    cert.Dispose();
            }
            if (match is not null)
                return match;
        }
        return null;
    }
}

/// <summary>Endereços Tailscale presentes nas interfaces de rede deste computador.</summary>
public static class TailscaleInterfaces
{
    public static IReadOnlyList<IPAddress> FindLocalAddresses()
    {
        var result = new List<IPAddress>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
            {
                if (TailscaleAddress.IsTailscale(info.Address))
                    result.Add(info.Address);
            }
        }
        // IPv4 primeiro: é o que aparece no painel do Tailscale e no MagicDNS.
        return result.OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1).ToList();
    }

    /// <summary>Resolve "auto" ou um IP explícito. Lança ConfigException com orientação se não for possível.</summary>
    public static IPAddress ResolveListenAddress(string configured, bool allowNonTailscale)
    {
        if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            IPAddress ip = IPAddress.Parse(configured);
            if (TailscaleAddress.IsWildcard(ip))
                throw new Configuration.ConfigException("Escutar em 0.0.0.0/:: não é permitido.");
            if (!allowNonTailscale && !FindLocalAddresses().Contains(ip))
                throw new Configuration.ConfigException($"O IP {ip} não está ativo neste computador. O Tailscale está conectado?");
            return ip;
        }

        IPAddress? v4 = FindLocalAddresses().FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return v4 ?? throw new Configuration.ConfigException(
            "Nenhum IP Tailscale encontrado. Abra o Tailscale, entre na rede da empresa e tente de novo.");
    }
}
