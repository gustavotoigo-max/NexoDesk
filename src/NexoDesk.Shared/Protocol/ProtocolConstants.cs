namespace NexoDesk.Shared.Protocol;

/// <summary>Constantes do protocolo NexoDesk v1. A fonte de verdade é docs/protocol.md.</summary>
public static class ProtocolConstants
{
    /// <summary>Assinatura "NXDK" gravada em little-endian (bytes 'N','X','D','K').</summary>
    public const uint Magic = 0x4B44584E;

    public const ushort CurrentVersion = 1;

    /// <summary>Magic(4) + Version(2) + Type(2) + Length(4) + Sequence(4).</summary>
    public const int HeaderSize = 16;

    /// <summary>Limite das mensagens de controle (JSON).</summary>
    public const int MaxControlPayload = 64 * 1024;

    /// <summary>Limite de um VideoFrame (cabeçalho do frame + JPEG).</summary>
    public const int MaxVideoFramePayload = 8 * 1024 * 1024;

    /// <summary>Porta padrão do Agent na rede Tailscale.</summary>
    public const int DefaultPort = 47115;

    /// <summary>Limites de texto aceitos em campos livres.</summary>
    public const int MaxIdLength = 128;
    public const int MaxMessageTextLength = 512;
    public const int MaxCapabilities = 32;
}
