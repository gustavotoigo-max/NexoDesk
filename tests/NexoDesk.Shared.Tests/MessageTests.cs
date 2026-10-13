using System.Text;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Tests;

public class MessageTests
{
    private static Envelope Wrap(MessageType type, byte[] payload) =>
        new(new MessageHeader(ProtocolConstants.CurrentVersion, type, (uint)payload.Length, 1), payload);

    private static Envelope Wrap(MessageType type, string json) => Wrap(type, Encoding.UTF8.GetBytes(json));

    private static T RoundTrip<T>(T message) where T : class, IControlMessage =>
        ProtocolJson.Deserialize<T>(Wrap(ProtocolJson.TypeOf<T>(), ProtocolJson.Serialize(message)));

    [Fact]
    public void Hello_RoundTrips()
    {
        var msg = new HelloMessage(1, "admin-01", ["view", "control"], "0.2.0");
        HelloMessage back = RoundTrip(msg);
        Assert.Equal(msg.ClientId, back.ClientId);
        Assert.Equal(msg.Capabilities, back.Capabilities);
    }

    [Fact]
    public void AllControlMessages_RoundTrip()
    {
        RoundTrip(new HelloAckMessage(1, "pc-oficina", "PC Oficina", ["view"], "0.2.0"));
        RoundTrip(new StartSessionMessage(SessionMode.View));
        RoundTrip(new SessionAcceptedMessage("abc123", SessionMode.Control));
        RoundTrip(new SessionDeniedMessage(DenyReason.Busy, "Ocupado."));
        RoundTrip(new DisplayInfoMessage(1920, 1080, 1.25));
        RoundTrip(new PointerEventMessage(0.5, 0.25, PointerButton.Left, PointerAction.Down, 0));
        RoundTrip(new KeyEventMessage(0x1E, 0x41, false, KeyAction.Down));
        RoundTrip(new PingMessage(123));
        RoundTrip(new PongMessage(123));
        RoundTrip(new EndSessionMessage(EndReason.UserRequested));
        RoundTrip(new ErrorMessage(ErrorCode.SessionBusy, "Ocupado."));
    }

    [Fact]
    public void Enums_AreSerializedAsText()
    {
        string json = Encoding.UTF8.GetString(ProtocolJson.Serialize(new StartSessionMessage(SessionMode.Control)));
        Assert.Contains("\"Control\"", json);
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(1.01, 0.5)]
    [InlineData(0.5, -1)]
    [InlineData(double.NaN, 0.5)]
    public void Pointer_OutOfRange_IsRejected(double x, double y)
    {
        var ex = Assert.Throws<ProtocolException>(() => new PointerEventMessage(x, y, PointerButton.None, PointerAction.Move, 0).Validate());
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public void Pointer_ClickWithoutButton_IsRejected()
    {
        Assert.Throws<ProtocolException>(() => new PointerEventMessage(0.1, 0.1, PointerButton.None, PointerAction.Down, 0).Validate());
    }

    [Fact]
    public void Pointer_JsonWithOutOfRangeCoordinates_IsRejectedOnReceive()
    {
        Envelope env = Wrap(MessageType.PointerEvent, "{\"x\":5,\"y\":0.1,\"button\":\"None\",\"action\":\"Move\",\"wheelDelta\":0}");
        var ex = Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<PointerEventMessage>(env));
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public void Hello_WithControlCharacters_IsRejected()
    {
        Assert.Throws<ProtocolException>(() => new HelloMessage(1, "admin\u0007", [], "1").Validate());
    }

    [Fact]
    public void Hello_WithTooLongId_IsRejected()
    {
        Assert.Throws<ProtocolException>(() => new HelloMessage(1, new string('a', 200), [], "1").Validate());
    }

    [Fact]
    public void Hello_WrongProtocolVersion_IsRejected()
    {
        var ex = Assert.Throws<ProtocolException>(() => new HelloMessage(2, "admin", [], "1").Validate());
        Assert.Equal(ErrorCode.UnsupportedVersion, ex.Code);
    }

    [Fact]
    public void Deserialize_InvalidJson_IsMalformed()
    {
        var ex = Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<PingMessage>(Wrap(MessageType.Ping, "{not json")));
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public void Deserialize_MissingRequiredField_IsMalformed()
    {
        var ex = Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<StartSessionMessage>(Wrap(MessageType.StartSession, "{}")));
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public void Deserialize_NullString_IsMalformed()
    {
        Envelope env = Wrap(MessageType.SessionDenied, "{\"reason\":\"Busy\",\"message\":null}");
        var ex = Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<SessionDeniedMessage>(env));
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public void Deserialize_UnknownEnumValue_IsMalformed()
    {
        var ex = Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<StartSessionMessage>(Wrap(MessageType.StartSession, "{\"mode\":\"Admin\"}")));
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public void Deserialize_NumericEnumOutOfRange_IsMalformed()
    {
        var ex = Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<StartSessionMessage>(Wrap(MessageType.StartSession, "{\"mode\":99}")));
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public void Deserialize_TypeMismatch_IsUnexpected()
    {
        var ex = Assert.Throws<ProtocolException>(() => ProtocolJson.Deserialize<PingMessage>(Wrap(MessageType.Pong, "{\"echoTimestamp\":1}")));
        Assert.Equal(ErrorCode.UnexpectedMessage, ex.Code);
    }

    [Fact]
    public void Serialize_InvalidMessage_Throws()
    {
        Assert.Throws<ProtocolException>(() => ProtocolJson.Serialize(new DisplayInfoMessage(0, 1080, 1)));
    }

    [Fact]
    public void VideoFrame_RoundTrips()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];
        var frame = new VideoFrame(42, 1_700_000_000_000, 1920, 1080, jpeg);
        VideoFrame back = VideoFrame.Decode(Wrap(MessageType.VideoFrame, frame.Encode()));

        Assert.Equal(42u, back.FrameIndex);
        Assert.Equal(1920, back.Width);
        Assert.Equal(1080, back.Height);
        Assert.Equal(jpeg, back.Jpeg.ToArray());
    }

    [Fact]
    public void VideoFrame_NonJpeg_IsRejected()
    {
        byte[] payload = new VideoFrame(1, 0, 10, 10, new byte[] { 0xFF, 0xD8, 0 }).Encode();
        payload[VideoFrame.FrameHeaderSize] = 0x89; // PNG
        Assert.Throws<ProtocolException>(() => VideoFrame.Decode(Wrap(MessageType.VideoFrame, payload)));
    }

    [Fact]
    public void VideoFrame_TooShort_IsRejected()
    {
        Assert.Throws<ProtocolException>(() => VideoFrame.Decode(Wrap(MessageType.VideoFrame, new byte[VideoFrame.FrameHeaderSize + 1])));
    }
}
