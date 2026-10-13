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

/// <summary>
/// Produz os frames da tela do PC REMOTO. Implementado no app (Windows).
/// NextFrameAsync deve respeitar o intervalo de FPS internamente (esperar antes de capturar),
/// e pode devolver null quando a tela não mudou desde o último frame.
/// </summary>
public interface IFrameSource
{
    Task<VideoFrame?> NextFrameAsync(uint frameIndex, CancellationToken ct);

    /// <summary>Libera recursos de captura ao fim da sessão.</summary>
    void Reset();
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

/// <summary>Administradores pareados com este PC REMOTO. Pode mudar com o programa aberto.</summary>
public interface IClientStore
{
    AuthorizedClient? Find(string fingerprint);

    /// <summary>Adiciona ou atualiza (mesma impressão → novo nome).</summary>
    void Add(AuthorizedClient client);
}

public sealed class InMemoryClientStore : IClientStore
{
    private readonly Dictionary<string, AuthorizedClient> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public InMemoryClientStore(IEnumerable<AuthorizedClient>? initial = null)
    {
        foreach (AuthorizedClient c in initial ?? [])
            Add(c);
    }

    public AuthorizedClient? Find(string fingerprint)
    {
        string? key = Security.CertificateFingerprint.Normalize(fingerprint);
        lock (_gate)
            return key is not null && _clients.TryGetValue(key, out AuthorizedClient? c) ? c : null;
    }

    public void Add(AuthorizedClient client)
    {
        string key = Security.CertificateFingerprint.Normalize(client.Fingerprint)
            ?? throw new ArgumentException("Impressão inválida.", nameof(client));
        lock (_gate)
            _clients[key] = client with { Fingerprint = key };
    }

    public int Count
    {
        get { lock (_gate) return _clients.Count; }
    }
}

/// <summary>Trata um pedido de pareamento já dentro do canal TLS.</summary>
public interface IPairingHandler
{
    /// <summary>Confere a prova; se aceitar, registra o admin e devolve a prova do PC REMOTO.</summary>
    Pairing.PairingCheck TryPair(string adminFingerprint, string remoteFingerprint, string adminName, string adminProof);
}
