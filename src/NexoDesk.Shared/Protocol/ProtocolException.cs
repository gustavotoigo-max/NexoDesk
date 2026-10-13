namespace NexoDesk.Shared.Protocol;

/// <summary>Violação do protocolo. Sempre leva ao encerramento seguro da conexão.</summary>
public sealed class ProtocolException : Exception
{
    public ErrorCode Code { get; }

    public ProtocolException(ErrorCode code, string? detail = null)
        : base(detail is null ? code.Describe() : $"{code.Describe()} ({detail})")
    {
        Code = code;
    }
}
