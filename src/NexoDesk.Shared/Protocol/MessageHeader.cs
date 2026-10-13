using System.Buffers.Binary;

namespace NexoDesk.Shared.Protocol;

/// <summary>Cabeçalho fixo de 16 bytes, little-endian.</summary>
public readonly record struct MessageHeader(ushort Version, MessageType Type, uint Length, uint Sequence)
{
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ProtocolConstants.HeaderSize)
            throw new ArgumentException("Destino menor que o cabeçalho.", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], ProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], (ushort)Type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], Sequence);
    }

    /// <summary>
    /// Lê e valida o cabeçalho. A validação acontece antes de qualquer alocação para o payload:
    /// assinatura, versão, tipo conhecido e comprimento dentro do limite do tipo.
    /// </summary>
    public static MessageHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < ProtocolConstants.HeaderSize)
            throw new ProtocolException(ErrorCode.MalformedPayload, "cabeçalho incompleto");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);
        if (magic != ProtocolConstants.Magic)
            throw new ProtocolException(ErrorCode.BadMagic);

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]);
        if (version != ProtocolConstants.CurrentVersion)
            throw new ProtocolException(ErrorCode.UnsupportedVersion, $"recebida v{version}");

        var type = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]);
        if (!type.IsDefined())
            throw new ProtocolException(ErrorCode.UnknownMessageType, $"tipo {(ushort)type}");

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]);
        if (length > (uint)type.MaxPayload())
            throw new ProtocolException(ErrorCode.PayloadTooLarge, $"{type}: {length} bytes");

        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]);
        return new MessageHeader(version, type, length, sequence);
    }
}
