using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;
using NexoDesk.Shared.Transport;

namespace NexoDesk.IntegrationTests;

internal static class TestCertificates
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    public static X509Certificate2 Server(string name = "pc-teste") => Create(name, ServerAuth);
    public static X509Certificate2 Client(string name = "admin-teste") => Create(name, ClientAuth);

    private static X509Certificate2 Create(string name, string eku)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], critical: false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using X509Certificate2 ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Reimportar via PKCS#12: no Windows (SChannel) chaves efêmeras não funcionam em TLS.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }
}

internal sealed class ScriptedPolicy(Func<SessionRequest, Task<PolicyDecision>> decide) : IAgentPolicy
{
    public static ScriptedPolicy AllowAll => new(_ => Task.FromResult(PolicyDecision.Allow()));

    public Task<PolicyDecision> RequestSessionAsync(SessionRequest request, CancellationToken ct) => decide(request);
}

internal sealed class FixedDisplay : IDisplaySource
{
    public DisplayInfoMessage GetDisplayInfo() => new(1920, 1080, 1.0);
}

internal sealed class RecordingInput : IInputSink
{
    public List<object> Events { get; } = [];
    public int ReleaseCount { get; private set; }

    public void Apply(PointerEventMessage pointer) { lock (Events) Events.Add(pointer); }
    public void Apply(KeyEventMessage key) { lock (Events) Events.Add(key); }
    public void ReleaseAll() => ReleaseCount++;
}

/// <summary>
/// Agent mínimo em 127.0.0.1 com porta aleatória: aceita uma conexão, faz o TLS mútuo e roda AgentSession.
/// Reproduz o que o NexoDesk.Agent faz, sem a parte Windows.
/// </summary>
internal sealed class LoopbackAgent : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public LoopbackAgent(
        X509Certificate2 serverCert,
        IEnumerable<AuthorizedClient> allowed,
        IAgentPolicy? policy = null,
        string[]? features = null,
        RecordingInput? input = null)
    {
        ServerCertificate = serverCert;
        Allowed = allowed.ToList();
        Policy = policy ?? ScriptedPolicy.AllowAll;
        Features = features ?? [NexoDesk.Shared.Messages.Features.View, NexoDesk.Shared.Messages.Features.Control];
        Input = input;
        _listener.Start();
        Accepted = Task.Run(AcceptOneAsync);
    }

    public X509Certificate2 ServerCertificate { get; }
    public List<AuthorizedClient> Allowed { get; }
    public IAgentPolicy Policy { get; }
    public string[] Features { get; }
    public RecordingInput? Input { get; }
    public MemoryAuditLog Audit { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string ServerFingerprint => CertificateFingerprint.Sha256(ServerCertificate);

    /// <summary>Resultado da primeira conexão: a sessão concluída ou a falha de autenticação.</summary>
    public Task<(SessionOutcome? Outcome, Exception? Error)> Accepted { get; }

    public TaskCompletionSource<AgentSession> SessionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<(SessionOutcome?, Exception?)> AcceptOneAsync()
    {
        TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
        var validator = new PinnedCertificateValidator(Allowed.Select(a => a.Fingerprint), CertificateUsage.Client);
        SecureChannel channel;
        try
        {
            channel = await TlsConnector.AcceptAsync(client, ServerCertificate, validator, TimeSpan.FromSeconds(5), _cts.Token);
        }
        catch (ChannelAuthenticationException ex)
        {
            return (null, ex);
        }

        AuthorizedClient who = Allowed.First(a => a.Fingerprint == channel.RemoteFingerprint);
        var session = new AgentSession(channel, who, new AgentSessionOptions
        {
            ServerId = "pc-teste",
            DisplayName = "PC Teste",
            AppVersion = "test",
            Features = Features,
            HandshakeTimeout = TimeSpan.FromSeconds(5),
            IdleTimeout = TimeSpan.FromSeconds(5),
            PolicyTimeout = TimeSpan.FromSeconds(2),
        }, Policy, Audit, new FixedDisplay(), Input);
        session.Activated += s => SessionStarted.TrySetResult(s);
        SessionOutcome outcome = await session.RunAsync(_cts.Token);
        return (outcome, null);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await Accepted.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
    }
}
