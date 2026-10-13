namespace NexoDesk.Shared.Protocol;

/// <summary>Mensagem recebida: cabeçalho validado e payload com exatamente Length bytes.</summary>
public sealed record Envelope(MessageHeader Header, byte[] Payload)
{
    public MessageType Type => Header.Type;
    public uint Sequence => Header.Sequence;
}
