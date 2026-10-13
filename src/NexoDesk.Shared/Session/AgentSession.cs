using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Transport;

namespace NexoDesk.Shared.Session;

public sealed record AgentSessionOptions
{
    public required string ServerId { get; init; }
    public required string DisplayName { get; init; }
    public required string AppVersion { get; init; }

    /// <summary>Recursos que este Agent oferece. Sem "control", pedidos de controle são negados.</summary>
    public string[] Features { get; init; } = [Messages.Features.View];

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tempo máximo para a política decidir (inclui o pedido de consentimento local).</summary>
    public TimeSpan PolicyTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

public sealed record SessionOutcome(bool WasAccepted, string? SessionId, string Reason);

/// <summary>
/// Lado Agent de uma conexão já autenticada por mTLS. Executa handshake, aplica a política,
/// mantém a sessão e registra auditoria. Uma instância por conexão.
/// </summary>
public sealed class AgentSession
{
    private readonly SecureChannel _channel;
    private readonly AuthorizedClient? _client;
    private readonly IPairingHandler? _pairing;
    private readonly AgentSessionOptions _options;
    private readonly IAgentPolicy _policy;
    private readonly IAuditLog _audit;
    private readonly IDisplaySource? _display;
    private readonly IInputSink? _input;
    private readonly IFrameSource? _frames;
    private readonly CancellationTokenSource _endCts = new();
    private EndReason? _localEndReason;

    public AgentSession(
        SecureChannel channel,
        AuthorizedClient? client,
        AgentSessionOptions options,
        IAgentPolicy policy,
        IAuditLog audit,
        IDisplaySource? display = null,
        IInputSink? input = null,
        IPairingHandler? pairing = null,
        IFrameSource? frames = null)
    {
        _channel = channel;
        _client = client;
        _options = options;
        _policy = policy;
        _audit = audit;
        _display = display;
        _input = input;
        _frames = frames;
        _pairing = pairing;
        StateMachine = new SessionStateMachine(SessionRole.Agent);
        SessionId = Guid.NewGuid().ToString("N")[..12];
    }

    public SessionStateMachine StateMachine { get; }
    public string SessionId { get; }
    /// <summary>Admin pareado. Null quando o certificado ainda não é conhecido (só pode parear).</summary>
    public AuthorizedClient? Client => _client;

    public string ClientName => _client?.Name ?? "Admin não pareado";
    public SessionMode? Mode { get; private set; }

    /// <summary>Disparado quando a sessão entra em Streaming (sessão ativa — mostrar indicador).</summary>
    public event Action<AgentSession>? Activated;

    /// <summary>Encerramento local (botão "Encerrar acesso", revogação, desligamento). Idempotente.</summary>
    public void End(EndReason reason)
    {
        _localEndReason ??= reason;
        try { _endCts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task<SessionOutcome> RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _endCts.Token);
        CancellationToken token = linked.Token;
        bool accepted = false;
        string reason = "Sessão encerrada.";

        StateMachine.TransitionTo(SessionState.Connecting);
        StateMachine.TransitionTo(SessionState.Authenticated);

        try
        {
            // 1. Hello → HelloAck
            Envelope hello = await ReceiveAsync(_options.HandshakeTimeout, ErrorCode.HandshakeTimeout, token).ConfigureAwait(false);
            HelloMessage helloMsg = ProtocolJson.Deserialize<HelloMessage>(hello);
            await _channel.SendAsync(new HelloAckMessage(
                ProtocolConstants.CurrentVersion, _options.ServerId, _options.DisplayName, _options.Features, _options.AppVersion), token).ConfigureAwait(false);
            StateMachine.TransitionTo(SessionState.Negotiating);

            // 2. StartSession (Ping permitido enquanto negocia)
            StartSessionMessage start;
            while (true)
            {
                Envelope env = await ReceiveAsync(_options.HandshakeTimeout, ErrorCode.HandshakeTimeout, token).ConfigureAwait(false);
                if (env.Type == MessageType.Ping)
                {
                    await ReplyPongAsync(env, token).ConfigureAwait(false);
                    continue;
                }
                if (env.Type == MessageType.EndSession)
                {
                    reason = "O ADMIN cancelou antes de iniciar.";
                    return Finish(false, reason);
                }
                if (env.Type == MessageType.PairRequest)
                {
                    reason = await HandlePairingAsync(env, token).ConfigureAwait(false);
                    return Finish(false, reason, audited: true);
                }
                start = ProtocolJson.Deserialize<StartSessionMessage>(env);
                break;
            }

            if (_client is null)
            {
                const string notPaired = "Este ADMIN não está pareado com este PC REMOTO. Gere um código de pareamento no PC REMOTO.";
                await _channel.SendAsync(new SessionDeniedMessage(DenyReason.NotAuthorized, notPaired), token).ConfigureAwait(false);
                Audit(AuditEventType.SessionDenied, result: nameof(DenyReason.NotAuthorized), reason: notPaired);
                return Finish(false, notPaired, audited: true);
            }

            Mode = start.Mode;
            var request = new SessionRequest(SessionId, _client, helloMsg.ClientId, start.Mode, _channel.RemoteAddress);
            Audit(AuditEventType.SessionRequested, result: start.Mode.ToString());

            PolicyDecision decision = await DecideAsync(request, token).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                await _channel.SendAsync(new SessionDeniedMessage(decision.Reason, decision.Message), token).ConfigureAwait(false);
                Audit(AuditEventType.SessionDenied, result: decision.Reason.ToString(), reason: decision.Message);
                reason = decision.Message;
                return Finish(false, reason, audited: true);
            }

            await _channel.SendAsync(new SessionAcceptedMessage(SessionId, start.Mode), token).ConfigureAwait(false);
            StateMachine.TransitionTo(SessionState.Streaming);
            accepted = true;
            Audit(AuditEventType.SessionStarted, result: start.Mode.ToString());
            Activated?.Invoke(this);

            if (_display is not null)
                await _channel.SendAsync(_display.GetDisplayInfo(), token).ConfigureAwait(false);

            // 3. Sessão ativa
            reason = await StreamLoopAsync(token).ConfigureAwait(false);
            return Finish(true, reason);
        }
        catch (OperationCanceledException) when (_endCts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            EndReason end = _localEndReason ?? EndReason.ShuttingDown;
            await _channel.TrySendAsync(new EndSessionMessage(end), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            reason = end == EndReason.LocalUserEnded ? "Encerrado por quem está neste computador." : $"Encerrado neste computador ({end}).";
            return Finish(accepted, reason);
        }
        catch (ProtocolException ex)
        {
            if (ex.Code != ErrorCode.ConnectionClosed)
                await _channel.TrySendAsync(new ErrorMessage(ex.Code, ex.Code.Describe()), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            Audit(AuditEventType.ProtocolError, result: ex.Code.ToString(), reason: ex.Message);
            return Finish(accepted, ex.Code.Describe(), audited: !accepted);
        }
        catch (IOException ex)
        {
            return Finish(accepted, $"Conexão perdida: {ex.Message}");
        }
        finally
        {
            _input?.ReleaseAll();
            StateMachine.TryBeginClose();
            await _channel.DisposeAsync().ConfigureAwait(false);
            StateMachine.MarkClosed();
            _endCts.Dispose();
        }

        SessionOutcome Finish(bool wasAccepted, string why, bool audited = false)
        {
            if (wasAccepted)
                Audit(AuditEventType.SessionEnded, result: "Concluida", reason: why);
            else if (!audited)
                Audit(AuditEventType.SessionEnded, result: "NaoIniciada", reason: why);
            return new SessionOutcome(wasAccepted, wasAccepted ? SessionId : null, why);
        }
    }

    private async Task<string> HandlePairingAsync(Envelope env, CancellationToken token)
    {
        PairRequestMessage request = ProtocolJson.Deserialize<PairRequestMessage>(env);
        Pairing.PairingCheck check = _pairing is null
            ? new Pairing.PairingCheck(false, null, "Este PC REMOTO não aceita pareamento agora.")
            : await Task.Run(() => _pairing.TryPair(_channel.RemoteFingerprint, _channel.LocalFingerprint, request.ClientName, request.Proof), token).ConfigureAwait(false);

        await _channel.SendAsync(new PairResultMessage(
            check.Accepted, _options.ServerId, _options.DisplayName, check.RemoteProof ?? "", check.Message), token).ConfigureAwait(false);

        _audit.Write(new AuditEvent(DateTimeOffset.UtcNow, check.Accepted ? AuditEventType.Paired : AuditEventType.PairingFailed, _options.ServerId,
            ClientName: request.ClientName, ClientFingerprint: _channel.RemoteFingerprint, RemoteAddress: _channel.RemoteAddress,
            Result: check.Accepted ? "Pareado" : "Recusado", Reason: check.Message, AppVersion: _options.AppVersion));

        // Dá tempo de o ADMIN ler a resposta antes de fechar.
        try
        {
            await _channel.ReceiveAsync(TimeSpan.FromSeconds(2), ErrorCode.HandshakeTimeout, token).ConfigureAwait(false);
        }
        catch (ProtocolException)
        {
        }
        return check.Accepted ? $"ADMIN \"{request.ClientName}\" pareado." : check.Message;
    }

    private async Task<string> StreamLoopAsync(CancellationToken token)
    {
        bool control = Mode == SessionMode.Control;
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Envia os frames da tela em paralelo com a recepção de ping/entrada.
        Task framePump = _frames is not null ? FramePumpAsync(loopCts.Token) : Task.CompletedTask;
        try
        {
            while (true)
            {
                Envelope env = await ReceiveAsync(_options.IdleTimeout, ErrorCode.IdleTimeout, token).ConfigureAwait(false);
                switch (env.Type)
                {
                    case MessageType.Ping:
                        await ReplyPongAsync(env, token).ConfigureAwait(false);
                        break;
                    case MessageType.Pong:
                        ProtocolJson.Deserialize<PongMessage>(env);
                        break;
                    case MessageType.EndSession:
                        EndSessionMessage end = ProtocolJson.Deserialize<EndSessionMessage>(env);
                        return $"Encerrado pelo ADMIN ({end.Reason}).";
                    case MessageType.PointerEvent:
                        PointerEventMessage pointer = ProtocolJson.Deserialize<PointerEventMessage>(env);
                        if (control)
                            _input?.Apply(pointer);
                        break;
                    case MessageType.KeyEvent:
                        KeyEventMessage key = ProtocolJson.Deserialize<KeyEventMessage>(env);
                        if (control)
                            _input?.Apply(key);
                        break;
                }
            }
        }
        finally
        {
            loopCts.Cancel();
            try { await framePump.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            _frames?.Reset();
        }
    }

    private async Task FramePumpAsync(CancellationToken token)
    {
        uint index = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                VideoFrame? frame = await _frames!.NextFrameAsync(index, token).ConfigureAwait(false);
                if (frame is null)
                    continue;
                await _channel.SendRawAsync(MessageType.VideoFrame, frame.Encode(), token).ConfigureAwait(false);
                index++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or InvalidOperationException)
        {
            // A conexão caiu: o laço de recepção encerra a sessão.
        }
    }

    private async Task<PolicyDecision> DecideAsync(SessionRequest request, CancellationToken token)
    {
        if (request.Mode == SessionMode.Control && !_options.Features.Contains(Features.Control))
            return PolicyDecision.Deny(DenyReason.ModeNotSupported, "Controlar ainda não está disponível. Conecte no modo Ver a tela.");
        if (request.Mode == SessionMode.View && !_options.Features.Contains(Features.View))
            return PolicyDecision.Deny(DenyReason.ModeNotSupported, "Ver a tela não está disponível neste PC REMOTO.");

        using var policyCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        policyCts.CancelAfter(_options.PolicyTimeout);
        try
        {
            return await _policy.RequestSessionAsync(request, policyCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return PolicyDecision.Deny(DenyReason.ConsentTimeout, "Ninguém respondeu ao pedido de acesso no PC REMOTO.");
        }
    }

    private async Task<Envelope> ReceiveAsync(TimeSpan timeout, ErrorCode timeoutCode, CancellationToken token)
    {
        Envelope env = await _channel.ReceiveAsync(timeout, timeoutCode, token).ConfigureAwait(false);
        StateMachine.EnsureIncomingAllowed(env.Type);
        return env;
    }

    private Task ReplyPongAsync(Envelope ping, CancellationToken token)
    {
        PingMessage msg = ProtocolJson.Deserialize<PingMessage>(ping);
        return _channel.SendAsync(new PongMessage(msg.Timestamp), token);
    }

    private void Audit(AuditEventType type, string? result = null, string? reason = null) =>
        _audit.Write(new AuditEvent(
            DateTimeOffset.UtcNow, type, _options.ServerId,
            ClientName: _client?.Name,
            ClientFingerprint: _client?.Fingerprint ?? _channel.RemoteFingerprint,
            RemoteAddress: _channel.RemoteAddress,
            SessionId: SessionId,
            Result: result,
            Reason: reason,
            AppVersion: _options.AppVersion));
}
