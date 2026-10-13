using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Transport;

namespace NexoDesk.Shared.Session;

public sealed record AdminConnectOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = ProtocolConstants.DefaultPort;
    public required X509Certificate2 ClientCertificate { get; init; }

    /// <summary>Impressão(ões) SHA-256 aceitas para o certificado deste Agent.</summary>
    public required IReadOnlyCollection<string> ServerFingerprints { get; init; }

    public required string ClientId { get; init; }
    public required string AppVersion { get; init; }
    public SessionMode Mode { get; init; } = SessionMode.View;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Espera pela decisão do Agent (inclui consentimento local, que pode levar alguns segundos).</summary>
    public TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>O Agent recusou a sessão.</summary>
public sealed class SessionDeniedException(DenyReason reason, string message) : Exception(message)
{
    public DenyReason Reason { get; } = reason;
}

/// <summary>
/// Lado Admin de uma sessão. Use <see cref="ConnectAsync"/>: retorna a sessão já aceita (Streaming)
/// ou lança ChannelAuthenticationException, SessionDeniedException ou ProtocolException.
/// </summary>
public sealed class AdminSession : IAsyncDisposable
{
    private readonly SecureChannel _channel;
    private readonly AdminConnectOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private Task? _pingLoop;
    private int _ended;

    private AdminSession(SecureChannel channel, AdminConnectOptions options, HelloAckMessage ack, SessionAcceptedMessage accepted, SessionStateMachine sm)
    {
        _channel = channel;
        _options = options;
        Server = ack;
        Accepted = accepted;
        StateMachine = sm;
    }

    public HelloAckMessage Server { get; }
    public SessionAcceptedMessage Accepted { get; }
    public SessionStateMachine StateMachine { get; }
    public string ServerFingerprint => _channel.RemoteFingerprint;
    public string NegotiatedProtocol => _channel.NegotiatedProtocol;
    public DisplayInfoMessage? Display { get; private set; }
    public TimeSpan? LastLatency { get; private set; }

    public event Action<DisplayInfoMessage>? DisplayChanged;
    public event Action<VideoFrame>? FrameReceived;
    public event Action<TiledFrame>? TilesReceived;
    public event Action<CursorUpdateMessage>? CursorMoved;
    public event Action<TimeSpan>? LatencyMeasured;

    /// <summary>Disparado uma única vez quando a sessão termina, com o motivo em português.</summary>
    public event Action<string>? Ended;

    public static async Task<AdminSession> ConnectAsync(AdminConnectOptions options, CancellationToken ct)
    {
        var sm = new SessionStateMachine(SessionRole.Admin);
        sm.TransitionTo(SessionState.Connecting);

        var validator = new PinnedCertificateValidator(options.ServerFingerprints, CertificateUsage.Server);
        SecureChannel channel = await TlsConnector.ConnectAsync(
            options.Host, options.Port, options.ClientCertificate, validator, options.ConnectTimeout, ct).ConfigureAwait(false);

        try
        {
            sm.TransitionTo(SessionState.Authenticated);

            await channel.SendAsync(new HelloMessage(
                ProtocolConstants.CurrentVersion, options.ClientId, [Features.View, Features.Control], options.AppVersion), ct).ConfigureAwait(false);

            Envelope ackEnv = await ReceiveAllowedAsync(channel, sm, options.HandshakeTimeout, ErrorCode.HandshakeTimeout, ct).ConfigureAwait(false);
            ThrowIfError(ackEnv);
            HelloAckMessage ack = ProtocolJson.Deserialize<HelloAckMessage>(ackEnv);
            sm.TransitionTo(SessionState.Negotiating);

            await channel.SendAsync(new StartSessionMessage(options.Mode), ct).ConfigureAwait(false);

            while (true)
            {
                Envelope env = await ReceiveAllowedAsync(channel, sm, options.DecisionTimeout, ErrorCode.HandshakeTimeout, ct).ConfigureAwait(false);
                switch (env.Type)
                {
                    case MessageType.SessionAccepted:
                        SessionAcceptedMessage accepted = ProtocolJson.Deserialize<SessionAcceptedMessage>(env);
                        sm.TransitionTo(SessionState.Streaming);
                        var session = new AdminSession(channel, options, ack, accepted, sm);
                        session.Start();
                        return session;
                    case MessageType.SessionDenied:
                        SessionDeniedMessage denied = ProtocolJson.Deserialize<SessionDeniedMessage>(env);
                        throw new SessionDeniedException(denied.Reason, denied.Message);
                    case MessageType.Ping:
                        PingMessage ping = ProtocolJson.Deserialize<PingMessage>(env);
                        await channel.SendAsync(new PongMessage(ping.Timestamp), ct).ConfigureAwait(false);
                        break;
                    case MessageType.EndSession:
                        throw new SessionDeniedException(DenyReason.SessionUnavailable, "O PC REMOTO encerrou antes de iniciar o acesso.");
                    case MessageType.Error:
                        ThrowIfError(env);
                        break;
                }
            }
        }
        catch
        {
            sm.TryBeginClose();
            await channel.DisposeAsync().ConfigureAwait(false);
            sm.MarkClosed();
            throw;
        }
    }

    public bool CanControl => Accepted.Mode == SessionMode.Control && StateMachine.State == SessionState.Streaming;

    public Task SendPointerAsync(PointerEventMessage pointer, CancellationToken ct) =>
        CanControl ? _channel.SendAsync(pointer, ct) : Task.CompletedTask;

    public Task SendKeyAsync(KeyEventMessage key, CancellationToken ct) =>
        CanControl ? _channel.SendAsync(key, ct) : Task.CompletedTask;

    /// <summary>Encerramento ordenado pedido pelo administrador. Idempotente.</summary>
    public async Task DisconnectAsync()
    {
        if (StateMachine.State == SessionState.Streaming)
            await _channel.TrySendAsync(new EndSessionMessage(EndReason.UserRequested), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await CloseAsync("Acesso encerrado por você.").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private void Start()
    {
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _pingLoop = Task.Run(() => PingLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        string reason = "Conexão encerrada.";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Envelope env = await ReceiveAllowedAsync(_channel, StateMachine, _options.IdleTimeout, ErrorCode.IdleTimeout, ct).ConfigureAwait(false);
                switch (env.Type)
                {
                    case MessageType.DisplayInfo:
                        Display = ProtocolJson.Deserialize<DisplayInfoMessage>(env);
                        DisplayChanged?.Invoke(Display);
                        break;
                    case MessageType.VideoFrame:
                        FrameReceived?.Invoke(VideoFrame.Decode(env));
                        break;
                    case MessageType.VideoTiles:
                        TilesReceived?.Invoke(TiledFrame.Decode(env));
                        break;
                    case MessageType.CursorUpdate:
                        CursorMoved?.Invoke(ProtocolJson.Deserialize<CursorUpdateMessage>(env));
                        break;
                    case MessageType.Ping:
                        PingMessage ping = ProtocolJson.Deserialize<PingMessage>(env);
                        await _channel.SendAsync(new PongMessage(ping.Timestamp), ct).ConfigureAwait(false);
                        break;
                    case MessageType.Pong:
                        PongMessage pong = ProtocolJson.Deserialize<PongMessage>(env);
                        long elapsed = Stopwatch.GetTimestamp() - pong.EchoTimestamp;
                        if (elapsed >= 0)
                        {
                            LastLatency = Stopwatch.GetElapsedTime(pong.EchoTimestamp);
                            LatencyMeasured?.Invoke(LastLatency.Value);
                        }
                        break;
                    case MessageType.EndSession:
                        EndSessionMessage end = ProtocolJson.Deserialize<EndSessionMessage>(env);
                        reason = end.Reason switch
                        {
                            EndReason.LocalUserEnded => "Quem está no PC REMOTO encerrou o acesso.",
                            EndReason.PermissionRevoked => "Este ADMIN foi removido no PC REMOTO.",
                            EndReason.ShuttingDown => "O NexoDesk foi fechado no PC REMOTO.",
                            EndReason.Timeout => "Acesso encerrado por tempo esgotado.",
                            _ => "Acesso encerrado pelo PC REMOTO.",
                        };
                        return;
                    case MessageType.Error:
                        ErrorMessage err = ProtocolJson.Deserialize<ErrorMessage>(env);
                        reason = err.Code.Describe();
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = "Acesso encerrado por você.";
        }
        catch (ProtocolException ex)
        {
            reason = ex.Code == ErrorCode.ConnectionClosed ? "A conexão com o PC REMOTO caiu." : ex.Message;
            if (ex.Code is not ErrorCode.ConnectionClosed and not ErrorCode.IdleTimeout)
                await _channel.TrySendAsync(new ErrorMessage(ex.Code, ex.Code.Describe()), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            reason = "A conexão com o PC REMOTO caiu.";
        }
        finally
        {
            await CloseAsync(reason).ConfigureAwait(false);
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await _channel.SendAsync(new PingMessage(Stopwatch.GetTimestamp()), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A queda é tratada pelo laço de recepção.
        }
    }

    private async Task CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;
        StateMachine.TryBeginClose();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        await _channel.DisposeAsync().ConfigureAwait(false);
        StateMachine.MarkClosed();
        Ended?.Invoke(reason);
    }

    private static async Task<Envelope> ReceiveAllowedAsync(SecureChannel channel, SessionStateMachine sm, TimeSpan timeout, ErrorCode code, CancellationToken ct)
    {
        Envelope env = await channel.ReceiveAsync(timeout, code, ct).ConfigureAwait(false);
        sm.EnsureIncomingAllowed(env.Type);
        return env;
    }

    private static void ThrowIfError(Envelope env)
    {
        if (env.Type != MessageType.Error)
            return;
        ErrorMessage err = ProtocolJson.Deserialize<ErrorMessage>(env);
        throw new ProtocolException(err.Code, "informado pelo PC REMOTO");
    }
}
