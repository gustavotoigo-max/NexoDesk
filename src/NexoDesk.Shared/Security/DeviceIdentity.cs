using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NexoDesk.Shared.Security;

/// <summary>
/// Identidade deste computador: um certificado ECDSA P-256 autoassinado, criado pelo próprio NexoDesk na
/// primeira abertura e guardado no repositório Pessoal do usuário (chave não exportável, protegida pelo Windows).
/// Serve tanto para o modo ADMIN (cliente) quanto para o modo PC REMOTO (servidor).
/// </summary>
public static class DeviceIdentity
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    public const string SubjectPrefix = "CN=NexoDesk ";

    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(730);

    /// <summary>Cria, guarda no repositório e devolve o certificado com a chave privada.</summary>
    public static X509Certificate2 Create(string deviceName, TimeSpan? validity = null, StoreLocation location = StoreLocation.CurrentUser)
    {
        string safeName = new(deviceName.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').Take(48).ToArray());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"{SubjectPrefix}{(safeName.Length == 0 ? "dispositivo" : safeName)}, O=Nexotool", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid), new Oid(ClientAuthOid)], critical: false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 ephemeral = request.CreateSelfSigned(now.AddMinutes(-5), now + (validity ?? DefaultValidity));

        // Reimporta para persistir a chave no perfil do usuário, sem marcá-la como exportável.
        byte[] pfx = ephemeral.Export(X509ContentType.Pkcs12);
        X509Certificate2 persisted;
        try
        {
            X509KeyStorageFlags flags = X509KeyStorageFlags.PersistKeySet
                | (location == StoreLocation.CurrentUser ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.MachineKeySet);
            persisted = X509CertificateLoader.LoadPkcs12(pfx, password: null, flags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return persisted;
    }

    /// <summary>Remove o certificado do repositório (e a chave, no Windows). Usado ao recriar a identidade.</summary>
    public static void Remove(string sha256, StoreLocation location = StoreLocation.CurrentUser)
    {
        string? wanted = CertificateFingerprint.Normalize(sha256);
        if (wanted is null)
            return;
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);
        foreach (X509Certificate2 cert in store.Certificates)
        {
            using (cert)
            {
                if (CertificateFingerprint.Sha256(cert) != wanted)
                    continue;
                store.Remove(cert);
                if (OperatingSystem.IsWindows() && cert.GetECDsaPrivateKey() is ECDsaCng cng)
                {
                    using (cng)
                    {
                        try { cng.Key.Delete(); } catch (CryptographicException) { }
                    }
                }
            }
        }
    }

    /// <summary>Dias até vencer (negativo se já venceu).</summary>
    public static int DaysUntilExpiry(X509Certificate2 cert) => (int)Math.Floor((cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);
}
