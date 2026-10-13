using System.Text.Json;
using System.Text.Json.Serialization;
using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Messages;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    MaxDepth = 8)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(HelloAckMessage))]
[JsonSerializable(typeof(StartSessionMessage))]
[JsonSerializable(typeof(SessionAcceptedMessage))]
[JsonSerializable(typeof(SessionDeniedMessage))]
[JsonSerializable(typeof(DisplayInfoMessage))]
[JsonSerializable(typeof(PointerEventMessage))]
[JsonSerializable(typeof(KeyEventMessage))]
[JsonSerializable(typeof(PingMessage))]
[JsonSerializable(typeof(PongMessage))]
[JsonSerializable(typeof(EndSessionMessage))]
[JsonSerializable(typeof(ErrorMessage))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;

/// <summary>Serialização das mensagens de controle (JSON UTF-8) com validação obrigatória.</summary>
public static class ProtocolJson
{
    private static readonly Dictionary<Type, MessageType> TypeMap = new()
    {
        [typeof(HelloMessage)] = MessageType.Hello,
        [typeof(HelloAckMessage)] = MessageType.HelloAck,
        [typeof(StartSessionMessage)] = MessageType.StartSession,
        [typeof(SessionAcceptedMessage)] = MessageType.SessionAccepted,
        [typeof(SessionDeniedMessage)] = MessageType.SessionDenied,
        [typeof(DisplayInfoMessage)] = MessageType.DisplayInfo,
        [typeof(PointerEventMessage)] = MessageType.PointerEvent,
        [typeof(KeyEventMessage)] = MessageType.KeyEvent,
        [typeof(PingMessage)] = MessageType.Ping,
        [typeof(PongMessage)] = MessageType.Pong,
        [typeof(EndSessionMessage)] = MessageType.EndSession,
        [typeof(ErrorMessage)] = MessageType.Error,
    };

    public static MessageType TypeOf<T>() where T : IControlMessage => TypeMap[typeof(T)];

    public static byte[] Serialize<T>(T message) where T : class, IControlMessage
    {
        message.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(message, typeof(T), ProtocolJsonContext.Default);
    }

    /// <summary>Desserializa e valida. Qualquer falha vira ProtocolException(MalformedPayload).</summary>
    public static T Deserialize<T>(Envelope envelope) where T : class, IControlMessage
    {
        MessageType expected = TypeOf<T>();
        if (envelope.Type != expected)
            throw new ProtocolException(ErrorCode.UnexpectedMessage, $"esperado {expected}, recebido {envelope.Type}");

        T? message;
        try
        {
            message = (T?)JsonSerializer.Deserialize(envelope.Payload, typeof(T), ProtocolJsonContext.Default);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException(ErrorCode.MalformedPayload, ex.Message);
        }

        if (message is null)
            throw new ProtocolException(ErrorCode.MalformedPayload, "payload nulo");
        message.Validate();
        return message;
    }
}
