using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexoDesk.Shared.Audit;

public enum AuditEventType
{
    ListenerStarted,
    ListenerStopped,
    ConnectionRejected,
    AuthenticationFailed,
    AuthenticationSucceeded,
    SessionRequested,
    SessionDenied,
    SessionStarted,
    SessionEnded,
    ProtocolError,
    ConfigurationError,
    Paired,
    PairingFailed,
}

/// <summary>
/// Evento de auditoria. Por regra do projeto NÃO contém conteúdo de tela, teclas digitadas,
/// área de transferência, senhas ou chaves privadas.
/// </summary>
public sealed record AuditEvent(
    DateTimeOffset TimestampUtc,
    AuditEventType Event,
    string Machine,
    string? ClientName = null,
    string? ClientFingerprint = null,
    string? RemoteAddress = null,
    string? SessionId = null,
    string? Result = null,
    string? Reason = null,
    string? AppVersion = null);

public interface IAuditLog
{
    void Write(AuditEvent e);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuditEvent))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;

/// <summary>Grava um arquivo JSON Lines por dia (UTC): audit-AAAA-MM-DD.jsonl.</summary>
public sealed class JsonLinesAuditLog : IAuditLog
{
    private readonly string _directory;
    private readonly object _gate = new();

    public JsonLinesAuditLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public event Action<AuditEvent>? Written;

    public void Write(AuditEvent e)
    {
        string line = JsonSerializer.Serialize(e, AuditJsonContext.Default.AuditEvent);
        string path = Path.Combine(_directory, $"audit-{e.TimestampUtc.UtcDateTime:yyyy-MM-dd}.jsonl");
        lock (_gate)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        Written?.Invoke(e);
    }
}

/// <summary>Log em memória, para testes e para mostrar eventos recentes na tela.</summary>
public sealed class MemoryAuditLog : IAuditLog
{
    private readonly List<AuditEvent> _events = [];
    private readonly object _gate = new();

    public IReadOnlyList<AuditEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public void Write(AuditEvent e)
    {
        lock (_gate) _events.Add(e);
    }
}

/// <summary>Repassa cada evento para vários destinos.</summary>
public sealed class CompositeAuditLog(params IAuditLog[] targets) : IAuditLog
{
    public void Write(AuditEvent e)
    {
        foreach (IAuditLog target in targets)
            target.Write(e);
    }
}
