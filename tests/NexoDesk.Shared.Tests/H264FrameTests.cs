using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;
using Xunit;

namespace NexoDesk.Shared.Tests;

public class H264FrameTests
{
    private static Envelope Wrap(byte[] payload) =>
        new(new MessageHeader(ProtocolConstants.CurrentVersion, MessageType.VideoH264, (uint)payload.Length, 1), payload);

    [Fact]
    public void Roundtrip_preserves_fields_and_bitstream()
    {
        byte[] nals = { 0, 0, 0, 1, 0x67, 0x42, 1, 2, 3, 0, 0, 0, 1, 0x65, 9, 8, 7 };
        var frame = new H264Frame(42, KeyFrame: true, 1920, 1080, nals);

        H264Frame back = H264Frame.Decode(Wrap(frame.Encode()));

        Assert.Equal(42u, back.FrameIndex);
        Assert.True(back.KeyFrame);
        Assert.Equal(1920, back.Width);
        Assert.Equal(1080, back.Height);
        Assert.True(back.Bitstream.Span.SequenceEqual(nals));
    }

    [Fact]
    public void Non_keyframe_roundtrips()
    {
        var frame = new H264Frame(7, KeyFrame: false, 1280, 720, new byte[] { 0, 0, 1, 0x41, 5 });
        H264Frame back = H264Frame.Decode(Wrap(frame.Encode()));
        Assert.False(back.KeyFrame);
        Assert.Equal(7u, back.FrameIndex);
    }

    [Fact]
    public void Encode_rejects_empty_bitstream()
    {
        var frame = new H264Frame(1, true, 640, 480, System.ReadOnlyMemory<byte>.Empty);
        Assert.Throws<ProtocolException>(() => frame.Encode());
    }
}
