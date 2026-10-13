using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NexoDesk.Shared.Security;

/// <summary>Impressão digital SHA-256 do certificado (DER), em hexadecimal maiúsculo sem separadores.</summary>
public static class CertificateFingerprint
{
    public static string Sha256(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>Aceita "AA:BB:..", "aa bb .." ou "AABB.."; retorna null se não for um SHA-256 válido.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        Span<char> buffer = stackalloc char[64];
        int count = 0;
        foreach (char c in value)
        {
            if (c is ':' or ' ' or '-')
                continue;
            if (!Uri.IsHexDigit(c) || count == 64)
                return null;
            buffer[count++] = char.ToUpperInvariant(c);
        }
        return count == 64 ? new string(buffer) : null;
    }

    /// <summary>Forma curta para exibir em telas e logs (primeiros 4 grupos).</summary>
    public static string Short(string fingerprint) =>
        fingerprint.Length < 16
            ? fingerprint
            : string.Join(':', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4)));
}
