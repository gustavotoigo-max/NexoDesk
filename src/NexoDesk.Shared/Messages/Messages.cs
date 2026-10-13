using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Messages;

/// <summary>Toda mensagem de controle valida o próprio conteúdo depois de desserializada.</summary>
public interface IControlMessage
{
    /// <summary>Lança ProtocolException(MalformedPayload) se algum campo estiver fora do contrato.</summary>
    void Validate();
}

public enum SessionMode
{
    View = 1,
    Control = 2,
}

public enum DenyReason
{
    NotAuthorized = 1,
    Busy = 2,
    ConsentDeclined = 3,
    ConsentTimeout = 4,
    SessionUnavailable = 5,
    ModeNotSupported = 6,
}

public enum EndReason
{
    UserRequested = 1,
    LocalUserEnded = 2,
    PermissionRevoked = 3,
    Timeout = 4,
    ShuttingDown = 5,
}

public enum PointerButton
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
}

public enum PointerAction
{
    Move = 1,
    Down = 2,
    Up = 3,
    Wheel = 4,
}

public enum KeyAction
{
    Down = 1,
    Up = 2,
}

/// <summary>Recursos anunciados no handshake.</summary>
public static class Features
{
    public const string View = "view";
    public const string Control = "control";
}

public sealed record HelloMessage(int ProtocolVersion, string ClientId, string[] Capabilities, string AppVersion) : IControlMessage
{
    public void Validate()
    {
        Check.Version(ProtocolVersion);
        Check.Id(ClientId, nameof(ClientId));
        Check.Tokens(Capabilities, nameof(Capabilities));
        Check.Text(AppVersion, nameof(AppVersion), ProtocolConstants.MaxIdLength);
    }
}

public sealed record HelloAckMessage(int ProtocolVersion, string ServerId, string DisplayName, string[] Features, string AppVersion) : IControlMessage
{
    public void Validate()
    {
        Check.Version(ProtocolVersion);
        Check.Id(ServerId, nameof(ServerId));
        Check.Text(DisplayName, nameof(DisplayName), ProtocolConstants.MaxIdLength);
        Check.Tokens(Features, nameof(Features));
        Check.Text(AppVersion, nameof(AppVersion), ProtocolConstants.MaxIdLength);
    }
}

public sealed record StartSessionMessage(SessionMode Mode) : IControlMessage
{
    public void Validate() => Check.Enum(Mode, nameof(Mode));
}

public sealed record SessionAcceptedMessage(string SessionId, SessionMode Mode) : IControlMessage
{
    public void Validate()
    {
        Check.Id(SessionId, nameof(SessionId));
        Check.Enum(Mode, nameof(Mode));
    }
}

public sealed record SessionDeniedMessage(DenyReason Reason, string Message) : IControlMessage
{
    public void Validate()
    {
        Check.Enum(Reason, nameof(Reason));
        Check.Text(Message, nameof(Message), ProtocolConstants.MaxMessageTextLength);
    }
}

public sealed record DisplayInfoMessage(int Width, int Height, double DpiScale) : IControlMessage
{
    public void Validate()
    {
        if (Width is < 1 or > 16384 || Height is < 1 or > 16384)
            throw new ProtocolException(ErrorCode.MalformedPayload, "dimensões da tela fora do limite");
        if (!double.IsFinite(DpiScale) || DpiScale is < 0.5 or > 8)
            throw new ProtocolException(ErrorCode.MalformedPayload, "escala DPI fora do limite");
    }
}

/// <summary>Posição normalizada: X e Y entre 0 e 1 em relação à área capturada.</summary>
public sealed record PointerEventMessage(double X, double Y, PointerButton Button, PointerAction Action, int WheelDelta) : IControlMessage
{
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || X is < 0 or > 1 || Y is < 0 or > 1)
            throw new ProtocolException(ErrorCode.MalformedPayload, "coordenadas fora de [0,1]");
        Check.Enum(Action, nameof(Action));
        if (!Enum.IsDefined(Button))
            throw new ProtocolException(ErrorCode.MalformedPayload, "botão inválido");
        if (Action is PointerAction.Down or PointerAction.Up && Button == PointerButton.None)
            throw new ProtocolException(ErrorCode.MalformedPayload, "clique sem botão");
        if (WheelDelta is < -1200 or > 1200)
            throw new ProtocolException(ErrorCode.MalformedPayload, "rolagem fora do limite");
    }
}

/// <summary>
/// Posição do cursor do PC REMOTO (normalizada 0..1), enviada à parte dos frames de vídeo.
/// Assim mover o mouse não re-encoda a tela inteira: o ponteiro vai em mensagens minúsculas e o
/// ADMIN o desenha por cima da imagem. Visible=false quando o cursor está oculto na tela remota.
/// </summary>
public sealed record CursorUpdateMessage(double X, double Y, bool Visible) : IControlMessage
{
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || X is < 0 or > 1 || Y is < 0 or > 1)
            throw new ProtocolException(ErrorCode.MalformedPayload, "coordenadas do cursor fora de [0,1]");
    }
}

public sealed record KeyEventMessage(ushort ScanCode, ushort VirtualKey, bool IsExtended, KeyAction Action) : IControlMessage
{
    public void Validate()
    {
        Check.Enum(Action, nameof(Action));
        if (ScanCode == 0 && VirtualKey == 0)
            throw new ProtocolException(ErrorCode.MalformedPayload, "tecla sem código");
        if (VirtualKey > 0xFE)
            throw new ProtocolException(ErrorCode.MalformedPayload, "código virtual inválido");
    }
}

/// <summary>Timestamp em ticks do relógio monotônico de quem enviou; o Pong devolve o mesmo valor.</summary>
public sealed record PingMessage(long Timestamp) : IControlMessage
{
    public void Validate() { }
}

public sealed record PongMessage(long EchoTimestamp) : IControlMessage
{
    public void Validate() { }
}

public sealed record EndSessionMessage(EndReason Reason) : IControlMessage
{
    public void Validate() => Check.Enum(Reason, nameof(Reason));
}

public sealed record ErrorMessage(ErrorCode Code, string Message) : IControlMessage
{
    public void Validate()
    {
        if (!Enum.IsDefined(Code))
            throw new ProtocolException(ErrorCode.MalformedPayload, "código de erro inválido");
        Check.Text(Message, nameof(Message), ProtocolConstants.MaxMessageTextLength);
    }
}

/// <summary>
/// Pedido de pareamento (Admin → PC remoto). Proof = HMAC que prova conhecer o código exibido no PC remoto,
/// ligado às impressões dos dois certificados vistos no TLS. Ver PairingProof.
/// </summary>
public sealed record PairRequestMessage(string ClientName, string Proof) : IControlMessage
{
    public void Validate()
    {
        Check.Id(ClientName, nameof(ClientName));
        Check.Hex(Proof, nameof(Proof));
    }
}

/// <summary>Resposta do pareamento. Se aceito, Proof prova que o PC remoto também conhece o código.</summary>
public sealed record PairResultMessage(bool Accepted, string ServerId, string DisplayName, string Proof, string Message) : IControlMessage
{
    public void Validate()
    {
        Check.Id(ServerId, nameof(ServerId));
        Check.Text(DisplayName, nameof(DisplayName), ProtocolConstants.MaxIdLength);
        Check.Text(Message, nameof(Message), ProtocolConstants.MaxMessageTextLength);
        if (Accepted)
            Check.Hex(Proof, nameof(Proof));
        else
            Check.Text(Proof, nameof(Proof), 64);
    }
}

internal static class Check
{
    public static void Version(int version)
    {
        if (version != ProtocolConstants.CurrentVersion)
            throw new ProtocolException(ErrorCode.UnsupportedVersion, $"v{version}");
    }

    public static void Id(string? value, string field)
    {
        Text(value, field, ProtocolConstants.MaxIdLength);
        if (value!.Length == 0)
            throw new ProtocolException(ErrorCode.MalformedPayload, $"{field} vazio");
    }

    public static void Text(string? value, string field, int max)
    {
        if (value is null)
            throw new ProtocolException(ErrorCode.MalformedPayload, $"{field} ausente");
        if (value.Length > max)
            throw new ProtocolException(ErrorCode.MalformedPayload, $"{field} longo demais");
        foreach (char c in value)
        {
            if (char.IsControl(c))
                throw new ProtocolException(ErrorCode.MalformedPayload, $"{field} com caractere de controle");
        }
    }

    public static void Tokens(string[]? values, string field)
    {
        if (values is null)
            throw new ProtocolException(ErrorCode.MalformedPayload, $"{field} ausente");
        if (values.Length > ProtocolConstants.MaxCapabilities)
            throw new ProtocolException(ErrorCode.MalformedPayload, $"{field} com itens demais");
        foreach (string v in values)
            Text(v, field, 64);
    }

    public static void Hex(string? value, string field)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ProtocolException(ErrorCode.MalformedPayload, $"{field} inválido");
    }

    public static void Enum<T>(T value, string field) where T : struct, System.Enum
    {
        if (!System.Enum.IsDefined(value))
            throw new ProtocolException(ErrorCode.MalformedPayload, $"{field} inválido");
    }
}
