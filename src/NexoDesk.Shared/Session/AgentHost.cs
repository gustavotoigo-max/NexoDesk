using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Transport;

namespace NexoDesk.Shared.Session;

public sealed record AgentHostOptions
{
    public required IPEndPoint Endpoint { get; init; }
    public required X509Certificate2 ServerCertificate { get; init; }
    public required IReadOnlyList<AuthorizedClient> AllowedClients { get; init; }
    public required AgentSessionOptions Session { get; init; }
    public TimeSpan TlsTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxConnectionAttemptsPerMinute { get; init; } = 10;

    /// <summary>Conexões simultâneas ainda em negociação (TLS/handshake). Excedentes são fechadas.</summary>
    public int MaxPendingConnections { get; init; } = 4;
}

/// <summary>
/// Servidor do Agent: escuta no endereço privado, autentica por mTLS, aceita no máximo uma sessão
/// e registra tudo em auditoria. Independente de Windows; a interface e a política ficam no app.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly AgentHostOptions _options;
    private readonly IAgentPolicy _policy;
    private readonly IAuditLog _audit;
    private readonly IDisplaySource? _display;
    private readonly IInputSink? _input;
    private readonly PinnedCertificateValidator _clientValidator;
    private readonly Dictionary<string, AuthorizedClient> _clients;
    private readonly ConnectionRateLimiter _rateLimiter;
    private readonly SessionGate _gate = new();
    private readonly SemaphoreSlim _pending;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private AgentSession? _current;

    public AgentHost(AgentHostOptions options, IAgentPolicy policy, IAuditLog audit, IDisplaySource? display = null, IInputSink? input = null)
    {
        if (TailscaleAddress.IsWildcard(options.Endpoint.Address))
            throw new ArgumentException("O Agent não pode escutar em todas as interfaces (0.0.0.0/::).", nameof(options));
        if (options.AllowedClients.Count == 0)
            throw new ArgumentException("Nenhum administrador autorizado.", nameof(options));

        _options = options;
        _policy = policy;
        _audit = audit;
        _display = display;
        _input = input;
        _clientValidator = new PinnedCertificateValidator(options.AllowedClients.Select(c => c.Fingerprint), CertificateUsage.Client);
        _clients = options.AllowedClients.ToDictionary(c => CertificateFingerprint.Normalize(c.Fingerprint)!, StringComparer.Ordinal);
        _rateLimiter = new ConnectionRateLimiter(options.MaxConnectionAttemptsPerMinute);
        _pending = new SemaphoreSlim(options.MaxPendingConnections, options.MaxPendingConnections);
    }

    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;
    public AgentSession? CurrentSession => Volatile.Read(ref _current);
    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    public event Action<AgentSession>? SessionActivated;
    public event Action<AgentSession, SessionOutcome>? SessionEnded;

    public void Start()
    {
        if (_listener is not null)
            throw new InvalidOperationException("O Agent já foi iniciado.");

        _listener = new TcpListener(_options.Endpoint);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(backlog: 8);
        Audit(AuditEventType.ListenerStarted, result: LocalEndpoint?.ToString());
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>Encerra a sessão ativa (botão local "Encerrar acesso").</summary>
    public void EndCurrentSession(EndReason reason) => CurrentSession?.End(reason);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException && ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote is null || !_rateLimiter.TryAcquire(remote.Address))
            {
                Audit(AuditEventType.ConnectionRejected, remote: remote?.Address.ToString(), reason: "Limite de tentativas por minuto atingido.");
                client.Dispose();
                continue;
            }
            if (!_pending.Wait(0))
            {
                Audit(AuditEventType.ConnectionRejected, remote: remote.Address.ToString(), reason: "Conexões em negociação demais.");
                client.Dispose();
                continue;
            }

            Task task = Task.Run(() => HandleConnectionAsync(client, remote, ct), CancellationToken.None);
            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, IPEndPoint remote, CancellationToken ct)
    {
        bool pendingReleased = false;
        void ReleasePending()
        {
            if (!pendingReleased)
            {
                pendingReleased = true;
                _pending.Release();
            }
        }

        try
        {
            SecureChannel channel;
            try
            {
                channel = await TlsConnector.AcceptAsync(client, _options.ServerCertificate, _clientValidator, _options.TlsTimeout, ct).ConfigureAwait(false);
            }
            catch (ChannelAuthenticationException ex) when (ex.RemoteFingerprint is null && ex.InnerException is IOException)
            {
                // Fechou antes de apresentar certificado: sondagem de disponibilidade do Admin ou queda de rede.
                Audit(AuditEventType.ConnectionRejected, remote: remote.Address.ToString(),
                    reason: "Conexão encerrada antes da autenticação (sondagem ou queda de rede).");
                return;
            }
            catch (ChannelAuthenticationException ex)
            {
                Audit(AuditEventType.AuthenticationFailed, remote: remote.Address.ToString(), fingerprint: ex.RemoteFingerprint, reason: ex.Message);
                return;
            }

            AuthorizedClient who = _clients[channel.RemoteFingerprint];
            Audit(AuditEventType.AuthenticationSucceeded, remote: remote.Address.ToString(), fingerprint: who.Fingerprint, clientName: who.Name, result: channel.NegotiatedProtocol);

            var gatePolicy = new GatedPolicy(_gate, _policy);
            var session = new AgentSession(channel, who, _options.Session, gatePolicy, _audit, _display, _input);
            session.Activated += s =>
            {
                Volatile.Write(ref _current, s);
                ReleasePending();
                SessionActivated?.Invoke(s);
            };

            SessionOutcome outcome = await session.RunAsync(ct).ConfigureAwait(false);
            if (gatePolicy.Acquired)
            {
                Interlocked.CompareExchange(ref _current, null, session);
                _gate.Release();
            }
            if (outcome.WasAccepted)
                SessionEnded?.Invoke(session, outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Agent parando.
        }
        catch (Exception ex)
        {
            Audit(AuditEventType.ProtocolError, remote: remote.Address.ToString(), reason: $"Erro inesperado: {ex.GetType().Name}");
        }
        finally
        {
            ReleasePending();
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested)
            return;
        CurrentSession?.End(EndReason.ShuttingDown);
        _cts.Cancel();
        _listener?.Stop();

        Task[] pending;
        lock (_connections)
            pending = [.. _connections, .. _acceptLoop is null ? [] : new[] { _acceptLoop }];
        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        if (_listener is not null)
            Audit(AuditEventType.ListenerStopped);
        _cts.Dispose();
    }

    private void Audit(AuditEventType type, string? remote = null, string? fingerprint = null, string? clientName = null, string? result = null, string? reason = null) =>
        _audit.Write(new AuditEvent(DateTimeOffset.UtcNow, type, _options.Session.ServerId,
            ClientName: clientName, ClientFingerprint: fingerprint, RemoteAddress: remote, Result: result, Reason: reason, AppVersion: _options.Session.AppVersion));

    /// <summary>Reserva a vaga única de sessão antes de consultar a política; libera se a política negar.</summary>
    private sealed class GatedPolicy(SessionGate gate, IAgentPolicy inner) : IAgentPolicy
    {
        public bool Acquired { get; private set; }

        public async Task<PolicyDecision> RequestSessionAsync(SessionRequest request, CancellationToken ct)
        {
            if (!gate.TryAcquire())
                return PolicyDecision.Deny(DenyReason.Busy, "Já existe uma sessão ativa neste computador.");
            Acquired = true;

            PolicyDecision decision;
            try
            {
                decision = await inner.RequestSessionAsync(request, ct).ConfigureAwait(false);
            }
            catch
            {
                Release();
                throw;
            }
            if (!decision.Allowed)
                Release();
            return decision;
        }

        private void Release()
        {
            Acquired = false;
            gate.Release();
        }
    }
}
