using System.Buffers.Binary;
using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Messages;

/// <summary>Um retângulo da tela que mudou, já em JPEG. Posição e tamanho em pixels da tela remota.</summary>
public sealed record ScreenTile(int X, int Y, int Width, int Height, ReadOnlyMemory<byte> Jpeg);

/// <summary>
/// Quadro por regiões: em vez da tela inteira, só os retângulos que mudaram desde o quadro anterior.
/// É o que mantém o tráfego baixo e a latência curta quando só uma parte da tela muda (listas, texto,
/// barras de progresso). O ADMIN mantém a imagem completa e cola cada retângulo no lugar.
///
/// Formato binário (little-endian):
///   FrameIndex u32 | ScreenWidth u16 | ScreenHeight u16 | TileCount u16
///   por tile: X u16 | Y u16 | Width u16 | Height u16 | JpegLen u32 | JPEG bytes
/// </summary>
public sealed record TiledFrame(uint FrameIndex, int ScreenWidth, int ScreenHeight, IReadOnlyList<ScreenTile> Tiles)
{
    public const int HeaderSize = 10;      // u32 + u16 + u16 + u16
    public const int TileHeaderSize = 12;  // 4×u16 + u32
    public const int MaxTiles = 4096;

    public byte[] Encode()
    {
        if (ScreenWidth is < 1 or > ushort.MaxValue || ScreenHeight is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ScreenWidth), "Dimensões inválidas.");
        if (Tiles.Count is 0 or > MaxTiles)
            throw new ProtocolException(ErrorCode.MalformedPayload, $"{Tiles.Count} retângulos");

        int total = HeaderSize;
        foreach (ScreenTile t in Tiles)
            total += TileHeaderSize + t.Jpeg.Length;
        if (total > ProtocolConstants.MaxVideoFramePayload)
            throw new ProtocolException(ErrorCode.PayloadTooLarge, $"quadro com {total} bytes");

        byte[] buffer = new byte[total];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], FrameIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], (ushort)ScreenWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], (ushort)ScreenHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..10], (ushort)Tiles.Count);

        int pos = HeaderSize;
        foreach (ScreenTile t in Tiles)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[pos..], (ushort)t.X);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(pos + 2)..], (ushort)t.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(pos + 4)..], (ushort)t.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(pos + 6)..], (ushort)t.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(pos + 8)..], (uint)t.Jpeg.Length);
            t.Jpeg.Span.CopyTo(span[(pos + TileHeaderSize)..]);
            pos += TileHeaderSize + t.Jpeg.Length;
        }
        return buffer;
    }

    public static TiledFrame Decode(Envelope envelope)
    {
        if (envelope.Type != MessageType.VideoTiles)
            throw new ProtocolException(ErrorCode.UnexpectedMessage, $"esperado VideoTiles, recebido {envelope.Type}");

        ReadOnlyMemory<byte> mem = envelope.Payload.AsMemory();
        ReadOnlySpan<byte> span = mem.Span;
        if (span.Length < HeaderSize)
            throw new ProtocolException(ErrorCode.MalformedPayload, "quadro sem cabeçalho");

        uint index = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]);
        int sw = BinaryPrimitives.ReadUInt16LittleEndian(span[4..6]);
        int sh = BinaryPrimitives.ReadUInt16LittleEndian(span[6..8]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[8..10]);
        if (sw == 0 || sh == 0)
            throw new ProtocolException(ErrorCode.MalformedPayload, "tela com dimensão zero");
        if (count is 0 or > MaxTiles)
            throw new ProtocolException(ErrorCode.MalformedPayload, "quantidade de retângulos inválida");

        var tiles = new List<ScreenTile>(count);
        int pos = HeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (pos + TileHeaderSize > span.Length)
                throw new ProtocolException(ErrorCode.MalformedPayload, "retângulo truncado");
            int x = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
            int y = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 2)..]);
            int w = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 4)..]);
            int h = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 6)..]);
            long len = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 8)..]);
            pos += TileHeaderSize;
            if (w == 0 || h == 0 || x + w > sw || y + h > sh)
                throw new ProtocolException(ErrorCode.MalformedPayload, "retângulo fora da tela");
            if (len < 2 || pos + len > span.Length)
                throw new ProtocolException(ErrorCode.MalformedPayload, "imagem do retângulo truncada");
            // Aceita JPEG (FF D8) ou PNG (89 50): PNG é usado nos blocos de texto/UI (sem perda).
            bool jpeg = span[pos] == 0xFF && span[pos + 1] == 0xD8;
            bool png = span[pos] == 0x89 && span[pos + 1] == 0x50;
            if (!jpeg && !png)
                throw new ProtocolException(ErrorCode.MalformedPayload, "retângulo não é imagem");
            tiles.Add(new ScreenTile(x, y, w, h, mem.Slice(pos, (int)len)));
            pos += (int)len;
        }
        return new TiledFrame(index, sw, sh, tiles);
    }
}
