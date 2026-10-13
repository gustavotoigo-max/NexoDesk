using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;
using NexoDesk.Shared.Transport;

namespace NexoDesk.IntegrationTests;

/// <summary>Critérios do M2: admin autorizado conecta; cliente desconhecido é rejeitado; handshake e Ping/Pong funcionam.</summary>
public class SecureSessionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static AdminConnectOptions Options(LoopbackAgent agent, X509Certificate2 clientCert, SessionMode mode = SessionMode.View, string? pin = null) => new()
    {
        Host = "127.0.0.1",
        Port = agent.Port,
        ClientCertificate = clientCert,
        ServerFingerprints = [pin ?? agent.ServerFingerprint],
        ClientId = "admin-teste",
        AppVersion = "test",
        Mode = mode,
        PingInterval = TimeSpan.FromMilliseconds(100),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        HandshakeTimeout = TimeSpan.FromSeconds(5),
        DecisionTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task AuthorizedAdmin_Connects_ReceivesDisplay_MeasuresLatency_AndDisconnects()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Gustavo", CertificateFingerprint.Sha256(client))]);

        var display = new TaskCompletionSource<DisplayInfoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latency = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        AdminSession session = await AdminSession.ConnectAsync(Options(agent, client), CancellationToken.None);
        session.DisplayChanged += d => display.TrySetResult(d);
        session.LatencyMeasured += l => latency.TrySetResult(l);
        session.Ended += r => ended.TrySetResult(r);

        Assert.Equal(SessionState.Streaming, session.StateMachine.State);
        Assert.Equal("pc-teste", session.Server.ServerId);
        Assert.Equal(agent.ServerFingerprint, session.ServerFingerprint);

        AgentSession agentSide = await agent.SessionStarted.Task.WaitAsync(Wait);
        Assert.Equal("Gustavo", agentSide.ClientName);

        if (session.Display is null)
            Assert.Equal(1920, (await display.Task.WaitAsync(Wait)).Width);
        Assert.True((await latency.Task.WaitAsync(Wait)) >= TimeSpan.Zero);

        await session.DisconnectAsync();
        Assert.Equal("Acesso encerrado por você.", await ended.Task.WaitAsync(Wait));

        (SessionOutcome? outcome, Exception? error) = await agent.Accepted.WaitAsync(Wait);
        Assert.Null(error);
        Assert.True(outcome!.WasAccepted);
        Assert.Contains("ADMIN", outcome.Reason);

        Assert.Contains(agent.Audit.Events, e => e.Event == AuditEventType.SessionStarted && e.ClientName == "Gustavo");
        Assert.Contains(agent.Audit.Events, e => e.Event == AuditEventType.SessionEnded);
    }

    [Fact]
    public async Task UnknownClientCertificate_IsRejectedByAgent()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 allowed = TestCertificates.Client("autorizado");
        using X509Certificate2 intruder = TestCertificates.Client("intruso");
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Autorizado", CertificateFingerprint.Sha256(allowed))]);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            AdminSession s = await AdminSession.ConnectAsync(Options(agent, intruder), CancellationToken.None);
            await s.DisconnectAsync();
        });

        (SessionOutcome? outcome, Exception? error) = await agent.Accepted.WaitAsync(Wait);
        Assert.Null(outcome);
        var auth = Assert.IsType<ChannelAuthenticationException>(error);
        Assert.Equal(CertificateFingerprint.Sha256(intruder), auth.RemoteFingerprint);
    }

    [Fact]
    public async Task WrongServerPin_IsRejectedByAdmin()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))]);

        var ex = await Assert.ThrowsAsync<ChannelAuthenticationException>(() =>
            AdminSession.ConnectAsync(Options(agent, client, pin: new string('A', 64)), CancellationToken.None));
        Assert.Contains("recusado", ex.Message);
        Assert.Equal(agent.ServerFingerprint, ex.RemoteFingerprint);
    }

    [Fact]
    public async Task PolicyDenial_ReachesAdmin()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        var policy = new ScriptedPolicy(_ => Task.FromResult(PolicyDecision.Deny(DenyReason.ConsentDeclined, "O usuário recusou o acesso.")));
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))], policy);

        var ex = await Assert.ThrowsAsync<SessionDeniedException>(() => AdminSession.ConnectAsync(Options(agent, client), CancellationToken.None));
        Assert.Equal(DenyReason.ConsentDeclined, ex.Reason);

        (SessionOutcome? outcome, _) = await agent.Accepted.WaitAsync(Wait);
        Assert.False(outcome!.WasAccepted);
        Assert.Contains(agent.Audit.Events, e => e.Event == AuditEventType.SessionDenied);
    }

    [Fact]
    public async Task PolicyThatNeverAnswers_TimesOutAsConsentTimeout()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        // Política que nunca responde; o fixture dá 2 s de prazo à política.
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))], new CancellablePolicy());

        var ex = await Assert.ThrowsAsync<SessionDeniedException>(() => AdminSession.ConnectAsync(Options(agent, client), CancellationToken.None));
        Assert.Equal(DenyReason.ConsentTimeout, ex.Reason);
    }

    [Fact]
    public async Task ControlMode_IsDenied_WhenAgentOffersViewOnly()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))], features: [Features.View]);

        var ex = await Assert.ThrowsAsync<SessionDeniedException>(() =>
            AdminSession.ConnectAsync(Options(agent, client, SessionMode.Control), CancellationToken.None));
        Assert.Equal(DenyReason.ModeNotSupported, ex.Reason);
    }

    [Fact]
    public async Task ControlMode_DeliversInput_AndReleasesKeysOnEnd()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        var input = new RecordingInput();
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))], input: input);

        AdminSession session = await AdminSession.ConnectAsync(Options(agent, client, SessionMode.Control), CancellationToken.None);
        Assert.True(session.CanControl);
        await session.SendPointerAsync(new PointerEventMessage(0.5, 0.5, PointerButton.Left, PointerAction.Down, 0), CancellationToken.None);
        await session.SendKeyAsync(new KeyEventMessage(0x1E, 0x41, false, KeyAction.Down), CancellationToken.None);
        await session.DisconnectAsync();

        await agent.Accepted.WaitAsync(Wait);
        Assert.Equal(2, input.Events.Count);
        Assert.True(input.ReleaseCount >= 1);
    }

    [Fact]
    public async Task LocalEnd_OnAgent_NotifiesAdmin()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))]);

        AdminSession session = await AdminSession.ConnectAsync(Options(agent, client), CancellationToken.None);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Ended += r => ended.TrySetResult(r);

        AgentSession agentSide = await agent.SessionStarted.Task.WaitAsync(Wait);
        agentSide.End(EndReason.LocalUserEnded);

        string reason = await ended.Task.WaitAsync(Wait);
        Assert.Contains("Quem está no PC REMOTO", reason);

        (SessionOutcome? outcome, _) = await agent.Accepted.WaitAsync(Wait);
        Assert.Contains("quem está neste computador", outcome!.Reason);
    }

    [Fact]
    public async Task GarbageAfterTls_IsRejectedWithProtocolError()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))]);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", agent.Port);
        await using var ssl = new SslStream(tcp.GetStream());
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "pc-teste",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true, // apenas neste teste: o alvo é o Agent
            LocalCertificateSelectionCallback = (_, _, _, _, _) => client,
        });
        await ssl.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
        await ssl.FlushAsync();

        (SessionOutcome? outcome, _) = await agent.Accepted.WaitAsync(Wait);
        Assert.False(outcome!.WasAccepted);
        Assert.Contains(agent.Audit.Events, e => e.Event == AuditEventType.ProtocolError && e.Result == nameof(ErrorCode.BadMagic));
    }

    [Fact]
    public async Task OutOfOrderMessage_IsRejected()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))]);

        var validator = new PinnedCertificateValidator([agent.ServerFingerprint], CertificateUsage.Server);
        await using SecureChannel channel = await TlsConnector.ConnectAsync("127.0.0.1", agent.Port, client, validator, Wait, CancellationToken.None);
        // KeyEvent antes do Hello
        await channel.SendAsync(new KeyEventMessage(0x1E, 0x41, false, KeyAction.Down), CancellationToken.None);

        (SessionOutcome? outcome, _) = await agent.Accepted.WaitAsync(Wait);
        Assert.False(outcome!.WasAccepted);
        Assert.Contains(agent.Audit.Events, e => e.Event == AuditEventType.ProtocolError && e.Result == nameof(ErrorCode.UnexpectedMessage));
    }

    private sealed class CancellablePolicy : IAgentPolicy
    {
        public async Task<PolicyDecision> RequestSessionAsync(SessionRequest request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return PolicyDecision.Allow();
        }
    }
}
