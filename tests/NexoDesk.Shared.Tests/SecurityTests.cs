using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Security;

namespace NexoDesk.Shared.Tests;

public class SecurityTests
{
    private static X509Certificate2 MakeCert(string oid, DateTimeOffset notBefore, DateTimeOffset notAfter, bool weakRsa = false)
    {
        CertificateRequest req;
        if (weakRsa)
        {
            req = new CertificateRequest("CN=fraco", RSA.Create(1024), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else
        {
            req = new CertificateRequest("CN=teste", ECDsa.Create(ECCurve.NamedCurves.nistP256), HashAlgorithmName.SHA256);
        }
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(oid)], critical: false));
        return req.CreateSelfSigned(notBefore, notAfter);
    }

    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fingerprint_Normalize_AcceptsCommonFormats()
    {
        string hex = new('a', 64);
        string colon = string.Join(':', Enumerable.Range(0, 32).Select(_ => "AA"));
        Assert.Equal(new string('A', 64), CertificateFingerprint.Normalize(hex));
        Assert.Equal(new string('A', 64), CertificateFingerprint.Normalize(colon));
        Assert.Null(CertificateFingerprint.Normalize("abc"));
        Assert.Null(CertificateFingerprint.Normalize(new string('g', 64)));
        Assert.Null(CertificateFingerprint.Normalize(new string('a', 65)));
        Assert.Null(CertificateFingerprint.Normalize(null));
    }

    [Fact]
    public void Validator_AcceptsPinnedCertificate_EvenWithChainErrors()
    {
        using X509Certificate2 cert = MakeCert(ClientAuth, Now.AddDays(-1), Now.AddDays(30));
        var v = new PinnedCertificateValidator([CertificateFingerprint.Sha256(cert)], CertificateUsage.Client, new ManualTimeProvider(Now));

        CertificateCheckResult r = v.Check(cert, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch);
        Assert.True(r.Accepted, r.Reason);
    }

    [Fact]
    public void Validator_RejectsUnknownCertificate()
    {
        using X509Certificate2 pinned = MakeCert(ClientAuth, Now.AddDays(-1), Now.AddDays(30));
        using X509Certificate2 other = MakeCert(ClientAuth, Now.AddDays(-1), Now.AddDays(30));
        var v = new PinnedCertificateValidator([CertificateFingerprint.Sha256(pinned)], CertificateUsage.Client, new ManualTimeProvider(Now));

        CertificateCheckResult r = v.Check(other, SslPolicyErrors.None);
        Assert.False(r.Accepted);
        Assert.Equal(CertificateFingerprint.Sha256(other), r.Fingerprint);
    }

    [Fact]
    public void Validator_RejectsMissingCertificate()
    {
        var v = new PinnedCertificateValidator([new string('A', 64)], CertificateUsage.Server);
        Assert.False(v.Check(null, SslPolicyErrors.RemoteCertificateNotAvailable).Accepted);
    }

    [Fact]
    public void Validator_RejectsExpiredAndNotYetValid()
    {
        using X509Certificate2 cert = MakeCert(ServerAuth, Now.AddDays(-30), Now.AddDays(-1));
        using X509Certificate2 future = MakeCert(ServerAuth, Now.AddDays(1), Now.AddDays(30));
        var time = new ManualTimeProvider(Now);
        var v = new PinnedCertificateValidator([CertificateFingerprint.Sha256(cert), CertificateFingerprint.Sha256(future)], CertificateUsage.Server, time);

        Assert.False(v.Check(cert, SslPolicyErrors.None).Accepted);
        Assert.False(v.Check(future, SslPolicyErrors.None).Accepted);
    }

    [Fact]
    public void Validator_RejectsWrongExtendedKeyUsage()
    {
        // Certificado de cliente apresentado como servidor.
        using X509Certificate2 cert = MakeCert(ClientAuth, Now.AddDays(-1), Now.AddDays(30));
        var v = new PinnedCertificateValidator([CertificateFingerprint.Sha256(cert)], CertificateUsage.Server, new ManualTimeProvider(Now));
        Assert.False(v.Check(cert, SslPolicyErrors.None).Accepted);
    }

    [Fact]
    public void Validator_RejectsWeakKey()
    {
        using X509Certificate2 cert = MakeCert(ServerAuth, Now.AddDays(-1), Now.AddDays(30), weakRsa: true);
        var v = new PinnedCertificateValidator([CertificateFingerprint.Sha256(cert)], CertificateUsage.Server, new ManualTimeProvider(Now));
        Assert.False(v.Check(cert, SslPolicyErrors.None).Accepted);
    }

    [Fact]
    public void Validator_InvalidPinInConfiguration_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PinnedCertificateValidator(["nao-e-hash"], CertificateUsage.Client));
    }

    [Fact]
    public void RateLimiter_BlocksAfterLimit_AndRecoversAfterWindow()
    {
        var time = new ManualTimeProvider(Now);
        var limiter = new ConnectionRateLimiter(3, TimeSpan.FromMinutes(1), time);
        var ip = IPAddress.Parse("100.100.1.2");

        Assert.True(limiter.TryAcquire(ip));
        Assert.True(limiter.TryAcquire(ip));
        Assert.True(limiter.TryAcquire(ip));
        Assert.False(limiter.TryAcquire(ip));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("100.100.1.3")));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(limiter.TryAcquire(ip));
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.0.1", false)]
    [InlineData("192.168.0.10", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("::ffff:100.100.1.1", true)]
    public void TailscaleAddress_Ranges(string address, bool expected)
    {
        Assert.Equal(expected, TailscaleAddress.IsTailscale(IPAddress.Parse(address)));
    }

    [Fact]
    public void TailscaleAddress_DetectsWildcard()
    {
        Assert.True(TailscaleAddress.IsWildcard(IPAddress.Any));
        Assert.True(TailscaleAddress.IsWildcard(IPAddress.IPv6Any));
        Assert.False(TailscaleAddress.IsWildcard(IPAddress.Parse("100.64.0.1")));
    }
}
