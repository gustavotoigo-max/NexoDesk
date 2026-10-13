using System.Net;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;

namespace NexoDesk.IntegrationTests;

/// <summary>AgentHost completo: escuta, mTLS, sessão única e auditoria.</summary>
public class AgentHostTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static AgentHost CreateHost(X509Certificate2 server, IEnumerable<AuthorizedClient> allowed, MemoryAuditLog audit, IAgentPolicy? policy = null, int attemptsPerMinute = 10) =>
        new(new AgentHostOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = server,
            AllowedClients = allowed.ToList(),
            MaxConnectionAttemptsPerMinute = attemptsPerMinute,
            Session = new AgentSessionOptions
            {
                ServerId = "pc-host",
                DisplayName = "PC Host",
                AppVersion = "test",
                HandshakeTimeout = TimeSpan.FromSeconds(5),
                IdleTimeout = TimeSpan.FromSeconds(5),
                PolicyTimeout = TimeSpan.FromSeconds(3),
            },
        }, policy ?? ScriptedPolicy.AllowAll, audit, new FixedDisplay());

    private static AdminConnectOptions Options(AgentHost host, X509Certificate2 server, X509Certificate2 client, string id = "admin") => new()
    {
        Host = "127.0.0.1",
        Port = host.LocalEndpoint!.Port,
        ClientCertificate = client,
        ServerFingerprints = [CertificateFingerprint.Sha256(server)],
        ClientId = id,
        AppVersion = "test",
        PingInterval = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public void Host_RefusesWildcardAddress()
    {
        using X509Certificate2 server = TestCertificates.Server();
        Assert.Throws<ArgumentException>(() => new AgentHost(new AgentHostOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Any, 0),
            ServerCertificate = server,
            AllowedClients = [new AuthorizedClient("x", new string('A', 64))],
            Session = new AgentSessionOptions { ServerId = "x", DisplayName = "x", AppVersion = "t" },
        }, ScriptedPolicy.AllowAll, new MemoryAuditLog()));
    }

    [Fact]
    public async Task SecondAdmin_IsRejectedAsBusy_AndFirstSessionContinues()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 admin1 = TestCertificates.Client("admin1");
        using X509Certificate2 admin2 = TestCertificates.Client("admin2");
        var audit = new MemoryAuditLog();
        await using AgentHost host = CreateHost(server,
            [new AuthorizedClient("Admin 1", CertificateFingerprint.Sha256(admin1)), new AuthorizedClient("Admin 2", CertificateFingerprint.Sha256(admin2))], audit);
        var activated = new TaskCompletionSource<AgentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionActivated += s => activated.TrySetResult(s);
        host.Start();

        AdminSession first = await AdminSession.ConnectAsync(Options(host, server, admin1, "a1"), CancellationToken.None);
        await activated.Task.WaitAsync(Wait);
        Assert.Equal("Admin 1", host.CurrentSession!.Client.Name);

        var ex = await Assert.ThrowsAsync<SessionDeniedException>(() => AdminSession.ConnectAsync(Options(host, server, admin2, "a2"), CancellationToken.None));
        Assert.Equal(DenyReason.Busy, ex.Reason);
        Assert.Equal(SessionState.Streaming, first.StateMachine.State);

        // Depois que a primeira termina, a vaga é liberada.
        var ended = new TaskCompletionSource<AgentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionEnded += (s, _) => ended.TrySetResult(s);
        await first.DisconnectAsync();
        await ended.Task.WaitAsync(Wait);

        AdminSession second = await AdminSession.ConnectAsync(Options(host, server, admin2, "a2"), CancellationToken.None);
        Assert.Equal(SessionState.Streaming, second.StateMachine.State);
        await second.DisconnectAsync();
    }

    [Fact]
    public async Task UnknownCertificate_IsAudited_WithFingerprint()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 allowed = TestCertificates.Client();
        using X509Certificate2 intruder = TestCertificates.Client("intruso");
        var audit = new MemoryAuditLog();
        await using AgentHost host = CreateHost(server, [new AuthorizedClient("Ok", CertificateFingerprint.Sha256(allowed))], audit);
        host.Start();

        await Assert.ThrowsAnyAsync<Exception>(() => AdminSession.ConnectAsync(Options(host, server, intruder), CancellationToken.None));

        string fp = CertificateFingerprint.Sha256(intruder);
        await WaitUntilAsync(() => audit.Events.Any(e => e.Event == AuditEventType.AuthenticationFailed));
        AuditEvent failed = audit.Events.First(e => e.Event == AuditEventType.AuthenticationFailed);
        Assert.Equal(fp, failed.ClientFingerprint);
        Assert.Equal("127.0.0.1", failed.RemoteAddress);
    }

    [Fact]
    public async Task TcpProbe_IsNotAuditedAsAuthenticationFailure()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        var audit = new MemoryAuditLog();
        await using AgentHost host = CreateHost(server, [new AuthorizedClient("Ok", CertificateFingerprint.Sha256(client))], audit);
        host.Start();

        using (var tcp = new System.Net.Sockets.TcpClient())
            await tcp.ConnectAsync(IPAddress.Loopback, host.LocalEndpoint!.Port);

        await WaitUntilAsync(() => audit.Events.Any(e => e.Event == AuditEventType.ConnectionRejected));
        Assert.DoesNotContain(audit.Events, e => e.Event == AuditEventType.AuthenticationFailed);
    }

    [Fact]
    public async Task RateLimit_RejectsExcessAttempts()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        var audit = new MemoryAuditLog();
        await using AgentHost host = CreateHost(server, [new AuthorizedClient("Ok", CertificateFingerprint.Sha256(client))], audit, attemptsPerMinute: 2);
        host.Start();

        for (int i = 0; i < 2; i++)
        {
            AdminSession s = await AdminSession.ConnectAsync(Options(host, server, client), CancellationToken.None);
            await s.DisconnectAsync();
            await WaitUntilAsync(() => host.CurrentSession is null);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => AdminSession.ConnectAsync(Options(host, server, client), CancellationToken.None));
        await WaitUntilAsync(() => audit.Events.Any(e => e.Event == AuditEventType.ConnectionRejected));
    }

    [Fact]
    public async Task LocalEnd_ThroughHost_EndsSession()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        var audit = new MemoryAuditLog();
        await using AgentHost host = CreateHost(server, [new AuthorizedClient("Ok", CertificateFingerprint.Sha256(client))], audit);
        var activated = new TaskCompletionSource<AgentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionActivated += s => activated.TrySetResult(s);
        host.Start();

        AdminSession s = await AdminSession.ConnectAsync(Options(host, server, client), CancellationToken.None);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Ended += r => ended.TrySetResult(r);
        await activated.Task.WaitAsync(Wait);

        host.EndCurrentSession(EndReason.LocalUserEnded);
        Assert.Contains("usuário do computador remoto", await ended.Task.WaitAsync(Wait));
        await WaitUntilAsync(() => host.CurrentSession is null);
    }

    [Fact]
    public async Task Dispose_EndsActiveSession_AndAuditsStop()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        var audit = new MemoryAuditLog();
        AgentHost host = CreateHost(server, [new AuthorizedClient("Ok", CertificateFingerprint.Sha256(client))], audit);
        host.Start();

        AdminSession s = await AdminSession.ConnectAsync(Options(host, server, client), CancellationToken.None);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Ended += r => ended.TrySetResult(r);
        await WaitUntilAsync(() => host.CurrentSession is not null);

        await host.DisposeAsync();
        Assert.Contains("fechado", await ended.Task.WaitAsync(Wait));
        Assert.Contains(audit.Events, e => e.Event == AuditEventType.ListenerStopped);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException("Condição não atingida.");
            await Task.Delay(25);
        }
    }
}
