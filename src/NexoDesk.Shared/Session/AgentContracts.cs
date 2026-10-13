using NexoDesk.Shared.Messages;

namespace NexoDesk.Shared.Session;

/// <summary>Administrador autorizado em um Agent: nome amigável e impressão SHA-256 do certificado de cliente.</summary>
public sealed record AuthorizedClient(string Name, string Fingerprint);

/// <summary>Pedido de sessão já autenticado por mTLS, entregue à política local do Agent.</summary>
public sealed record SessionRequest(
    string SessionId,
    AuthorizedClient Client,
    string DeclaredClientId,
    SessionMode Mode,
    string RemoteAddress);

public sealed record PolicyDecision(bool Allowed, DenyReason Reason, string Message)
{
    public static PolicyDecision Allow() => new(true, default, "Sessão autorizada.");
    public static PolicyDecision Deny(DenyReason reason, string message) => new(false, reason, message);
}

/// <summary>Política local do Agent: consentimento, acesso não assistido e disponibilidade da sessão interativa.</summary>
public interface IAgentPolicy
{
    Task<PolicyDecision> RequestSessionAsync(SessionRequest request, CancellationToken ct);
}

/// <summary>Informações do monitor capturado. Implementado no Agent (Windows).</summary>
public interface IDisplaySource
{
    DisplayInfoMessage GetDisplayInfo();
}

/// <summary>Aplica entrada remota. Implementado no Agent a partir do M5 (SendInput).</summary>
public interface IInputSink
{
    void Apply(PointerEventMessage pointer);
    void Apply(KeyEventMessage key);

    /// <summary>Solta teclas e botões mantidos. Chamado sempre que a sessão termina.</summary>
    void ReleaseAll();
}

/// <summary>Garante no máximo uma sessão por máquina.</summary>
public sealed class SessionGate
{
    private int _busy;

    public bool TryAcquire() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Release() => Volatile.Write(ref _busy, 0);

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
}
