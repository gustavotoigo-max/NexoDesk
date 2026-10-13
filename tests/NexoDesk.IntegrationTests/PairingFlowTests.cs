using System.Net;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Pairing;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;
using NexoDesk.Shared.Transport;

namespace NexoDesk.IntegrationTests;

/// <summary>Fluxo completo: ADMIN desconhecido pareia com o código, depois conecta normalmente.</summary>
public class PairingFlowTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private sealed record Remote(AgentHost Host, InMemoryClientStore Clients, PairingWindow Window, MemoryAuditLog Audit, X509Certificate2 Cert);

    private static Remote StartRemote()
    {
        X509Certificate2 cert = TestCertificates.Server("pc-remoto");
        var clients = new InMemoryClientStore();
        var window = new PairingWindow();
        var audit = new MemoryAuditLog();
        var host = new AgentHost(new AgentHostOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = cert,
            Clients = clients,
            Pairing = window,
            Session = new AgentSessionOptions { ServerId = "pc-remoto", DisplayName = "PC Remoto", AppVersion = "test" },
        }, ScriptedPolicy.AllowAll, audit, new FixedDisplay());
        host.Start();
        return new Remote(host, clients, window, audit, cert);
    }

    private static AdminPairOptions PairOptions(Remote r, X509Certificate2 admin, string code) => new()
    {
        Host = "127.0.0.1",
        Port = r.Host.LocalEndpoint!.Port,
        ClientCertificate = admin,
        ClientId = "admin-teste",
        ClientName = "Gustavo",
        Code = code,
        AppVersion = "test",
    };

    [Fact]
    public async Task Pairing_ThenConnect_Works()
    {
        Remote r = StartRemote();
        await using AgentHost host = r.Host;
        using X509Certificate2 admin = TestCertificates.Client("admin");

        // Antes do pareamento: recusado já no TLS.
        await Assert.ThrowsAnyAsync<Exception>(() => AdminSession.ConnectAsync(ConnectOptions(r, admin, CertificateFingerprint.Sha256(r.Cert)), CancellationToken.None));

        string code = r.Window.Open(TimeSpan.FromMinutes(5));
        PairedRemote paired = await AdminPairing.PairAsync(PairOptions(r, admin, code), CancellationToken.None);

        Assert.Equal("pc-remoto", paired.ServerId);
        Assert.Equal("PC Remoto", paired.DisplayName);
        Assert.Equal(CertificateFingerprint.Sha256(r.Cert), paired.Fingerprint);
        Assert.Equal("Gustavo", r.Clients.Find(CertificateFingerprint.Sha256(admin))!.Name);
        Assert.False(r.Window.IsOpen);
        Assert.Contains(r.Audit.Events, e => e.Event == AuditEventType.Paired);

        // Depois: conecta com a identidade fixada no pareamento.
        AdminSession session = await AdminSession.ConnectAsync(ConnectOptions(r, admin, paired.Fingerprint), CancellationToken.None);
        Assert.Equal(SessionState.Streaming, session.StateMachine.State);
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task WrongCode_IsRejected_AndNothingIsStored()
    {
        Remote r = StartRemote();
        await using AgentHost host = r.Host;
        using X509Certificate2 admin = TestCertificates.Client("admin");
        r.Window.Open(TimeSpan.FromMinutes(5));

        var ex = await Assert.ThrowsAsync<PairingFailedException>(() =>
            AdminPairing.PairAsync(PairOptions(r, admin, "AAAA-BBBB-CCCC"), CancellationToken.None));
        Assert.Contains("incorreto", ex.Message);
        Assert.Equal(0, r.Clients.Count);
        Assert.True(r.Window.IsOpen);
    }

    [Fact]
    public async Task NoWindowOpen_UnknownAdminCannotEvenHandshake()
    {
        Remote r = StartRemote();
        await using AgentHost host = r.Host;
        using X509Certificate2 admin = TestCertificates.Client("admin");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            AdminPairing.PairAsync(PairOptions(r, admin, "AAAA-BBBB-CCCC"), CancellationToken.None));
        Assert.Equal(0, r.Clients.Count);
    }

    [Fact]
    public async Task UnpairedAdmin_DuringOpenWindow_CannotStartSession()
    {
        Remote r = StartRemote();
        await using AgentHost host = r.Host;
        using X509Certificate2 admin = TestCertificates.Client("admin");
        r.Window.Open(TimeSpan.FromMinutes(5));

        // Passa no TLS (janela aberta), mas não pode iniciar sessão sem parear.
        var ex = await Assert.ThrowsAsync<SessionDeniedException>(() =>
            AdminSession.ConnectAsync(ConnectOptions(r, admin, CertificateFingerprint.Sha256(r.Cert)), CancellationToken.None));
        Assert.Equal(DenyReason.NotAuthorized, ex.Reason);
        Assert.Equal(0, r.Clients.Count);
    }

    [Fact]
    public async Task InvalidCodeFormat_FailsBeforeConnecting()
    {
        using X509Certificate2 admin = TestCertificates.Client("admin");
        var ex = await Assert.ThrowsAsync<PairingFailedException>(() => AdminPairing.PairAsync(new AdminPairOptions
        {
            Host = "127.0.0.1", Port = 1, ClientCertificate = admin, ClientId = "a", ClientName = "a", Code = "123", AppVersion = "t",
        }, CancellationToken.None));
        Assert.Contains("Código inválido", ex.Message);
    }

    private static AdminConnectOptions ConnectOptions(Remote r, X509Certificate2 admin, string serverFp) => new()
    {
        Host = "127.0.0.1",
        Port = r.Host.LocalEndpoint!.Port,
        ClientCertificate = admin,
        ServerFingerprints = [serverFp],
        ClientId = "admin-teste",
        AppVersion = "test",
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };
}

/// <summary>Identidade criada como o app cria: no repositório do usuário, usável em TLS dos dois lados.</summary>
public class DeviceIdentityTests
{
    [Fact]
    public async Task CreatedIdentity_IsFoundInStore_AndWorksAsServerAndClient()
    {
        X509Certificate2 identity = DeviceIdentity.Create($"Teste {Guid.NewGuid():N}");
        string fp = CertificateFingerprint.Sha256(identity);
        try
        {
            using X509Certificate2? found = CertificateStoreLookup.FindWithPrivateKey(fp);
            Assert.NotNull(found);
            Assert.True(found!.HasPrivateKey);

            // Mesma identidade serve como servidor (PC REMOTO) e como cliente (ADMIN).
            var serverCheck = new PinnedCertificateValidator([fp], CertificateUsage.Server).Check(found, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);
            var clientCheck = new PinnedCertificateValidator([fp], CertificateUsage.Client).Check(found, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);
            Assert.True(serverCheck.Accepted, serverCheck.Reason);
            Assert.True(clientCheck.Accepted, clientCheck.Reason);

            // Handshake TLS real com o certificado lido do repositório.
            using X509Certificate2 admin = TestCertificates.Client("admin");
            var clients = new InMemoryClientStore([new AuthorizedClient("Admin", CertificateFingerprint.Sha256(admin))]);
            await using var host = new AgentHost(new AgentHostOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ServerCertificate = found,
                Clients = clients,
                Session = new AgentSessionOptions { ServerId = "id", DisplayName = "Id", AppVersion = "t" },
            }, ScriptedPolicy.AllowAll, new MemoryAuditLog());
            host.Start();
            AdminSession s = await AdminSession.ConnectAsync(new AdminConnectOptions
            {
                Host = "127.0.0.1", Port = host.LocalEndpoint!.Port, ClientCertificate = admin,
                ServerFingerprints = [fp], ClientId = "a", AppVersion = "t",
            }, CancellationToken.None);
            await s.DisconnectAsync();
        }
        finally
        {
            DeviceIdentity.Remove(fp);
            identity.Dispose();
        }
        Assert.Null(CertificateStoreLookup.FindWithPrivateKey(fp));
    }
}
