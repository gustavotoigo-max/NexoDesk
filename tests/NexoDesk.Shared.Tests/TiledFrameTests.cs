using System.Buffers.Binary;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;
using Xunit;

namespace NexoDesk.Shared.Tests;

public class TiledFrameTests
{
    private static byte[] FakeJpeg(int n)
    {
        byte[] b = new byte[n];
        b[0] = 0xFF; b[1] = 0xD8; // SOI
        for (int i = 2; i < n; i++) b[i] = (byte)(i & 0xFF);
        return b;
    }

    private static Envelope Wrap(byte[] payload) =>
        new(new MessageHeader(ProtocolConstants.CurrentVersion, MessageType.VideoTiles, (uint)payload.Length, 1), payload);

    [Fact]
    public void Roundtrip_preserves_tiles()
    {
        var tiles = new List<ScreenTile>
        {
            new(0, 0, 256, 256, FakeJpeg(40)),
            new(256, 0, 128, 256, FakeJpeg(30)),
            new(0, 256, 256, 100, FakeJpeg(50)),
        };
        var frame = new TiledFrame(7, 1920, 1080, tiles);

        byte[] payload = frame.Encode();
        TiledFrame back = TiledFrame.Decode(Wrap(payload));

        Assert.Equal(7u, back.FrameIndex);
        Assert.Equal(1920, back.ScreenWidth);
        Assert.Equal(1080, back.ScreenHeight);
        Assert.Equal(3, back.Tiles.Count);
        Assert.Equal(256, back.Tiles[0].Width);
        Assert.Equal(256, back.Tiles[1].X);
        Assert.Equal(100, back.Tiles[2].Height);
        Assert.True(back.Tiles[0].Jpeg.Span.SequenceEqual(tiles[0].Jpeg.Span));
        Assert.True(back.Tiles[2].Jpeg.Span.SequenceEqual(tiles[2].Jpeg.Span));
    }

    [Fact]
    public void Decode_rejects_tile_outside_screen()
    {
        var frame = new TiledFrame(1, 300, 300, [new ScreenTile(200, 0, 256, 50, FakeJpeg(10))]);
        byte[] payload = frame.Encode();
        Assert.Throws<ProtocolException>(() => TiledFrame.Decode(Wrap(payload)));
    }

    [Fact]
    public void Decode_rejects_non_jpeg_tile()
    {
        var frame = new TiledFrame(1, 300, 300, [new ScreenTile(0, 0, 64, 64, new byte[] { 1, 2, 3, 4 })]);
        byte[] payload = frame.Encode();
        Assert.Throws<ProtocolException>(() => TiledFrame.Decode(Wrap(payload)));
    }

    [Fact]
    public void Encode_rejects_empty()
    {
        var frame = new TiledFrame(1, 300, 300, []);
        Assert.Throws<ProtocolException>(() => frame.Encode());
    }
}
