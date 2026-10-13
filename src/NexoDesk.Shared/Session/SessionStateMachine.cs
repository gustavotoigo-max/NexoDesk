using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Session;

public enum SessionState
{
    Disconnected,
    Connecting,
    Authenticated,
    Negotiating,
    Streaming,
    Closing,
}

public enum SessionRole
{
    Admin,
    Agent,
}

/// <summary>
/// Máquina de estados da sessão:
/// Disconnected → Connecting → Authenticated → Negotiating → Streaming → Closing → Disconnected.
/// Qualquer estado ativo pode ir para Closing. Mensagens incompatíveis com o estado são rejeitadas.
/// Thread-safe; encerramento idempotente.
/// </summary>
public sealed class SessionStateMachine
{
    private readonly object _gate = new();
    private SessionState _state = SessionState.Disconnected;

    public SessionStateMachine(SessionRole role) => Role = role;

    public SessionRole Role { get; }

    public SessionState State
    {
        get { lock (_gate) return _state; }
    }

    public event Action<SessionState, SessionState>? StateChanged;

    public static bool IsTransitionAllowed(SessionState from, SessionState to) => (from, to) switch
    {
        (SessionState.Disconnected, SessionState.Connecting) => true,
        (SessionState.Connecting, SessionState.Authenticated) => true,
        (SessionState.Authenticated, SessionState.Negotiating) => true,
        (SessionState.Negotiating, SessionState.Streaming) => true,
        (SessionState.Closing, SessionState.Disconnected) => true,
        (SessionState.Disconnected, SessionState.Closing) => false,
        (SessionState.Closing, SessionState.Closing) => false,
        (_, SessionState.Closing) => true,
        _ => false,
    };

    /// <summary>Move para o próximo estado. Lança InvalidOperationException se a transição não existe.</summary>
    public void TransitionTo(SessionState next)
    {
        SessionState previous;
        lock (_gate)
        {
            if (!IsTransitionAllowed(_state, next))
                throw new InvalidOperationException($"Transição inválida: {_state} → {next}.");
            previous = _state;
            _state = next;
        }
        StateChanged?.Invoke(previous, next);
    }

    /// <summary>Inicia o encerramento. Retorna false se já estava encerrando ou desconectado (idempotente).</summary>
    public bool TryBeginClose()
    {
        SessionState previous;
        lock (_gate)
        {
            if (_state is SessionState.Closing or SessionState.Disconnected)
                return false;
            previous = _state;
            _state = SessionState.Closing;
        }
        StateChanged?.Invoke(previous, SessionState.Closing);
        return true;
    }

    /// <summary>Conclui o encerramento. Idempotente.</summary>
    public void MarkClosed()
    {
        SessionState previous;
        lock (_gate)
        {
            if (_state == SessionState.Disconnected)
                return;
            previous = _state;
            _state = SessionState.Disconnected;
        }
        StateChanged?.Invoke(previous, SessionState.Disconnected);
    }

    /// <summary>Lança ProtocolException(UnexpectedMessage) se a mensagem recebida não cabe no estado atual.</summary>
    public void EnsureIncomingAllowed(MessageType type)
    {
        SessionState state = State;
        if (!IsIncomingAllowed(Role, state, type))
            throw new ProtocolException(ErrorCode.UnexpectedMessage, $"{type} em {state}");
    }

    public static bool IsIncomingAllowed(SessionRole role, SessionState state, MessageType type) => role switch
    {
        SessionRole.Agent => state switch
        {
            SessionState.Authenticated => type is MessageType.Hello,
            SessionState.Negotiating => type is MessageType.StartSession or MessageType.PairRequest or MessageType.Ping or MessageType.EndSession,
            SessionState.Streaming => type is MessageType.PointerEvent or MessageType.KeyEvent
                or MessageType.Ping or MessageType.Pong or MessageType.EndSession,
            _ => false,
        },
        SessionRole.Admin => state switch
        {
            SessionState.Authenticated => type is MessageType.HelloAck or MessageType.Error,
            SessionState.Negotiating => type is MessageType.SessionAccepted or MessageType.SessionDenied or MessageType.PairResult
                or MessageType.Ping or MessageType.Pong or MessageType.EndSession or MessageType.Error,
            SessionState.Streaming => type is MessageType.DisplayInfo or MessageType.VideoFrame or MessageType.CursorUpdate
                or MessageType.Ping or MessageType.Pong or MessageType.EndSession or MessageType.Error,
            _ => false,
        },
        _ => false,
    };
}
