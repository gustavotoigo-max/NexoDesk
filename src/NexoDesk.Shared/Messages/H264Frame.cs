using System.Buffers.Binary;
using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Messages;

/// <summary>
/// Quadro de vídeo H.264 (Annex-B). Usado no modo de vídeo por codec, para movimento fluido.
///
/// Formato binário (little-endian):
///   FrameIndex u32 | Flags u8 (bit0 = keyframe) | Width u16 | Height u16 | bitstream H.264
/// </summary>
public sealed record H264Frame(uint FrameIndex, bool KeyFrame, int Width, int Height, ReadOnlyMemory<byte> Bitstream)
{
    public const int HeaderSize = 9; // u32 + u8 + u16 + u16
    public const int MaxBitstream = ProtocolConstants.MaxVideoFramePayload - HeaderSize;

    public byte[] Encode()
    {
        if (Width is < 1 or > ushort.MaxValue || Height is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Width), "Dimensões inválidas.");
        if (Bitstream.Length is 0 or > MaxBitstream)
            throw new ProtocolException(ErrorCode.PayloadTooLarge, $"H.264 com {Bitstream.Length} bytes");

        byte[] buffer = new byte[HeaderSize + Bitstream.Length];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], FrameIndex);
        span[4] = (byte)(KeyFrame ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[5..7], (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[7..9], (ushort)Height);
        Bitstream.Span.CopyTo(span[HeaderSize..]);
        return buffer;
    }

    public static H264Frame Decode(Envelope envelope)
    {
        if (envelope.Type != MessageType.VideoH264)
            throw new ProtocolException(ErrorCode.UnexpectedMessage, $"esperado VideoH264, recebido {envelope.Type}");

        ReadOnlyMemory<byte> mem = envelope.Payload.AsMemory();
        ReadOnlySpan<byte> span = mem.Span;
        if (span.Length < HeaderSize + 1)
            throw new ProtocolException(ErrorCode.MalformedPayload, "quadro H.264 vazio");

        uint index = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]);
        bool key = (span[4] & 1) != 0;
        int width = BinaryPrimitives.ReadUInt16LittleEndian(span[5..7]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(span[7..9]);
        if (width == 0 || height == 0)
            throw new ProtocolException(ErrorCode.MalformedPayload, "quadro H.264 com dimensão zero");

        return new H264Frame(index, key, width, height, mem[HeaderSize..]);
    }
}
