namespace NexoDesk.Shared.Protocol;

/// <summary>Tipos de mensagem do protocolo v1. Os valores são parte do contrato de rede; não renumerar.</summary>
public enum MessageType : ushort
{
    Hello = 1,
    HelloAck = 2,
    StartSession = 3,
    SessionAccepted = 4,
    SessionDenied = 5,
    DisplayInfo = 6,
    VideoFrame = 7,
    PointerEvent = 8,
    KeyEvent = 9,
    Ping = 10,
    Pong = 11,
    EndSession = 12,
    Error = 13,
    PairRequest = 14,
    PairResult = 15,
    CursorUpdate = 16,
    VideoTiles = 17,
}

public static class MessageTypeExtensions
{
    public static bool IsDefined(this MessageType type) => type is >= MessageType.Hello and <= MessageType.VideoTiles;

    /// <summary>Maior payload aceito para o tipo. Comprimentos acima disso encerram a sessão.</summary>
    public static int MaxPayload(this MessageType type) => type switch
    {
        MessageType.VideoFrame or MessageType.VideoTiles => ProtocolConstants.MaxVideoFramePayload,
        _ => ProtocolConstants.MaxControlPayload,
    };
}
