using System.Buffers.Binary;
using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Messages;

/// <summary>
/// Payload binário de VideoFrame: FrameIndex(u32) + Timestamp(i64, ms UTC) + Width(u16) + Height(u16) + JPEG.
/// Cada frame é uma imagem completa; quem recebe pode descartar frames atrasados.
/// </summary>
public sealed record VideoFrame(uint FrameIndex, long TimestampUnixMs, int Width, int Height, ReadOnlyMemory<byte> Jpeg)
{
    public const int FrameHeaderSize = 16;
    public const int MaxJpegBytes = ProtocolConstants.MaxVideoFramePayload - FrameHeaderSize;

    public byte[] Encode()
    {
        if (Width is < 1 or > ushort.MaxValue || Height is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Width), "Dimensões inválidas.");
        if (Jpeg.Length is 0 or > MaxJpegBytes)
            throw new ProtocolException(ErrorCode.PayloadTooLarge, $"JPEG com {Jpeg.Length} bytes");

        byte[] buffer = new byte[FrameHeaderSize + Jpeg.Length];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], FrameIndex);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..12], TimestampUnixMs);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..14], (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..16], (ushort)Height);
        Jpeg.Span.CopyTo(span[FrameHeaderSize..]);
        return buffer;
    }

    public static VideoFrame Decode(Envelope envelope)
    {
        if (envelope.Type != MessageType.VideoFrame)
            throw new ProtocolException(ErrorCode.UnexpectedMessage, $"esperado VideoFrame, recebido {envelope.Type}");

        ReadOnlySpan<byte> span = envelope.Payload;
        if (span.Length < FrameHeaderSize + 2)
            throw new ProtocolException(ErrorCode.MalformedPayload, "frame sem imagem");

        uint index = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]);
        long ts = BinaryPrimitives.ReadInt64LittleEndian(span[4..12]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(span[12..14]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(span[14..16]);
        if (width == 0 || height == 0)
            throw new ProtocolException(ErrorCode.MalformedPayload, "frame com dimensão zero");

        // Início de JPEG (SOI) — rejeita lixo antes de entregar ao decodificador de imagem.
        if (span[FrameHeaderSize] != 0xFF || span[FrameHeaderSize + 1] != 0xD8)
            throw new ProtocolException(ErrorCode.MalformedPayload, "frame não é JPEG");

        return new VideoFrame(index, ts, width, height, envelope.Payload.AsMemory(FrameHeaderSize));
    }
}
