using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NexoDesk.Shared.Security;

public enum CertificateUsage
{
    /// <summary>O certificado remoto deve servir para autenticar servidor (usado pelo Admin ao validar o Agent).</summary>
    Server,

    /// <summary>O certificado remoto deve servir para autenticar cliente (usado pelo Agent ao validar o Admin).</summary>
    Client,
}

public sealed record CertificateCheckResult(bool Accepted, string? Fingerprint, string? Subject, string Reason);

/// <summary>
/// Validação por fixação (pinning): a identidade do outro lado é a impressão SHA-256 do certificado,
/// que precisa estar na lista local. Como os certificados são autoassinados por dispositivo, erros de cadeia
/// e de nome são esperados e substituídos por estas verificações — nunca se aceita um certificado qualquer.
/// Verifica: certificado presente, impressão na lista, validade (datas), uso estendido (EKU) e chave mínima.
/// </summary>
public sealed class PinnedCertificateValidator
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    private readonly Func<string, bool> _isPinned;
    private readonly Func<bool> _acceptUnpinned;
    private readonly CertificateUsage _usage;
    private readonly TimeProvider _time;

    /// <summary>Lista fixa de impressões autorizadas.</summary>
    public PinnedCertificateValidator(IEnumerable<string> fingerprints, CertificateUsage usage, TimeProvider? time = null)
    {
        var pins = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in fingerprints)
        {
            string? normalized = CertificateFingerprint.Normalize(raw)
                ?? throw new ArgumentException($"Impressão digital inválida: '{raw}'. Use o SHA-256 com 64 dígitos hexadecimais.");
            pins.Add(normalized);
        }
        _isPinned = pins.Contains;
        _acceptUnpinned = static () => false;
        _usage = usage;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Lista dinâmica (pareamentos podem mudar com o programa aberto). <paramref name="acceptUnpinned"/> só deve
    /// retornar true durante um pareamento: o certificado desconhecido ainda passa pelas checagens de validade,
    /// uso e chave, e a identidade é então provada pelo código de pareamento.
    /// </summary>
    public PinnedCertificateValidator(Func<string, bool> isPinned, Func<bool> acceptUnpinned, CertificateUsage usage, TimeProvider? time = null)
    {
        _isPinned = isPinned;
        _acceptUnpinned = acceptUnpinned;
        _usage = usage;
        _time = time ?? TimeProvider.System;
    }

    public CertificateCheckResult Check(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return new(false, null, null, "O outro lado não apresentou certificado.");

        using var cert = new X509Certificate2(certificate);
        string fingerprint = CertificateFingerprint.Sha256(cert);
        string subject = cert.Subject;

        bool pinned = _isPinned(fingerprint);
        if (!pinned && !_acceptUnpinned())
            return new(false, fingerprint, subject, "Certificado não está na lista autorizada.");

        DateTime now = _time.GetUtcNow().UtcDateTime;
        if (now < cert.NotBefore.ToUniversalTime())
            return new(false, fingerprint, subject, "Certificado ainda não é válido.");
        if (now > cert.NotAfter.ToUniversalTime())
            return new(false, fingerprint, subject, "Certificado expirado.");

        if (!HasExpectedUsage(cert))
            return new(false, fingerprint, subject, $"Certificado sem uso estendido para {(_usage == CertificateUsage.Server ? "servidor" : "cliente")}.");

        if (!HasStrongKey(cert))
            return new(false, fingerprint, subject, "Chave do certificado fraca (mínimo RSA 2048 ou ECDSA P-256).");

        return new(true, fingerprint, subject, pinned ? "Certificado autorizado." : "Certificado novo aceito para pareamento.");
    }

    /// <summary>Callback para SslStream. O resultado de cada verificação é entregue a <paramref name="onResult"/>.</summary>
    public RemoteCertificateValidationCallback CreateCallback(Action<CertificateCheckResult>? onResult = null) =>
        (_, certificate, _, errors) =>
        {
            CertificateCheckResult result = Check(certificate, errors);
            onResult?.Invoke(result);
            return result.Accepted;
        };

    private bool HasExpectedUsage(X509Certificate2 cert)
    {
        string oid = _usage == CertificateUsage.Server ? ServerAuthOid : ClientAuthOid;
        foreach (X509Extension ext in cert.Extensions)
        {
            if (ext is X509EnhancedKeyUsageExtension eku)
            {
                foreach (Oid usage in eku.EnhancedKeyUsages)
                {
                    if (usage.Value == oid)
                        return true;
                }
                return false;
            }
        }
        return false;
    }

    private static bool HasStrongKey(X509Certificate2 cert)
    {
        using RSA? rsa = cert.GetRSAPublicKey();
        if (rsa is not null)
            return rsa.KeySize >= 2048;
        using ECDsa? ec = cert.GetECDsaPublicKey();
        if (ec is not null)
            return ec.KeySize >= 256;
        return false;
    }
}
