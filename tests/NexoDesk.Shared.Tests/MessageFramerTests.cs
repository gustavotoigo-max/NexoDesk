using System.Buffers.Binary;
using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Tests;

public class MessageFramerTests
{
    private static async Task<byte[]> FrameAsync(MessageType type, uint seq, byte[] payload)
    {
        using var ms = new MemoryStream();
        await MessageFramer.WriteAsync(ms, type, seq, payload, CancellationToken.None);
        return ms.ToArray();
    }

    [Fact]
    public async Task RoundTrip_PreservesTypeSequenceAndPayload()
    {
        byte[] payload = "{\"timestamp\":42}"u8.ToArray();
        byte[] bytes = await FrameAsync(MessageType.Ping, 7, payload);

        Assert.Equal(ProtocolConstants.HeaderSize + payload.Length, bytes.Length);
        Envelope? env = await MessageFramer.ReadAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.NotNull(env);
        Assert.Equal(MessageType.Ping, env!.Type);
        Assert.Equal(7u, env.Sequence);
        Assert.Equal(payload, env.Payload);
    }

    [Fact]
    public async Task Header_IsLittleEndianWithMagic()
    {
        byte[] bytes = await FrameAsync(MessageType.Hello, 0x01020304, [1, 2, 3]);

        Assert.Equal((byte)'N', bytes[0]);
        Assert.Equal((byte)'X', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'K', bytes[3]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((ushort)MessageType.Hello, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
    }

    [Fact]
    public async Task Read_HandlesOneByteFragments()
    {
        byte[] a = await FrameAsync(MessageType.Ping, 1, [10, 20, 30]);
        byte[] b = await FrameAsync(MessageType.Pong, 2, new byte[5000]);
        var stream = new ChunkedReadStream([.. a, .. b], chunk: 1);

        Envelope? first = await MessageFramer.ReadAsync(stream, CancellationToken.None);
        Envelope? second = await MessageFramer.ReadAsync(stream, CancellationToken.None);
        Envelope? end = await MessageFramer.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(MessageType.Ping, first!.Type);
        Assert.Equal(new byte[] { 10, 20, 30 }, first.Payload);
        Assert.Equal(MessageType.Pong, second!.Type);
        Assert.Equal(5000, second.Payload.Length);
        Assert.Null(end);
    }

    [Fact]
    public async Task Read_EmptyStream_ReturnsNull()
    {
        Assert.Null(await MessageFramer.ReadAsync(new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task Read_TruncatedHeader_Throws()
    {
        byte[] bytes = await FrameAsync(MessageType.Ping, 1, [1]);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes[..10]), CancellationToken.None));
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public async Task Read_TruncatedPayload_Throws()
    {
        byte[] bytes = await FrameAsync(MessageType.Ping, 1, new byte[100]);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes[..^1]), CancellationToken.None));
        Assert.Equal(ErrorCode.MalformedPayload, ex.Code);
    }

    [Fact]
    public async Task Read_BadMagic_Throws()
    {
        byte[] bytes = await FrameAsync(MessageType.Ping, 1, [1]);
        bytes[0] = (byte)'G';
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
        Assert.Equal(ErrorCode.BadMagic, ex.Code);
    }

    [Fact]
    public async Task Read_HttpRequest_IsRejectedAsBadMagic()
    {
        byte[] bytes = "GET / HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray();
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
        Assert.Equal(ErrorCode.BadMagic, ex.Code);
    }

    [Fact]
    public async Task Read_UnsupportedVersion_Throws()
    {
        byte[] bytes = await FrameAsync(MessageType.Ping, 1, [1]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
        Assert.Equal(ErrorCode.UnsupportedVersion, ex.Code);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)17)]
    [InlineData((ushort)65535)]
    public async Task Read_UnknownType_Throws(ushort type)
    {
        byte[] bytes = await FrameAsync(MessageType.Ping, 1, [1]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), type);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
        Assert.Equal(ErrorCode.UnknownMessageType, ex.Code);
    }

    [Fact]
    public async Task Read_HugeLength_IsRejectedBeforeAllocating()
    {
        // Só o cabeçalho existe: se o leitor alocasse 4 GiB ou tentasse ler, o teste falharia de outra forma.
        byte[] header = new byte[ProtocolConstants.HeaderSize];
        new MessageHeader(1, MessageType.VideoFrame, uint.MaxValue, 1).WriteTo(header);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(header), CancellationToken.None));
        Assert.Equal(ErrorCode.PayloadTooLarge, ex.Code);
    }

    [Fact]
    public async Task Read_ControlMessageAbove64KiB_IsRejected()
    {
        byte[] header = new byte[ProtocolConstants.HeaderSize];
        new MessageHeader(1, MessageType.KeyEvent, ProtocolConstants.MaxControlPayload + 1, 1).WriteTo(header);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => MessageFramer.ReadAsync(new MemoryStream(header), CancellationToken.None));
        Assert.Equal(ErrorCode.PayloadTooLarge, ex.Code);
    }

    [Fact]
    public async Task Read_VideoFrameUpTo8MiB_IsAccepted()
    {
        byte[] payload = new byte[ProtocolConstants.MaxVideoFramePayload];
        byte[] bytes = await FrameAsync(MessageType.VideoFrame, 1, payload);
        Envelope? env = await MessageFramer.ReadAsync(new MemoryStream(bytes), CancellationToken.None);
        Assert.Equal(payload.Length, env!.Payload.Length);
    }

    [Fact]
    public async Task Write_OversizedPayload_Throws()
    {
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            MessageFramer.WriteAsync(new MemoryStream(), MessageType.Ping, 1, new byte[ProtocolConstants.MaxControlPayload + 1], CancellationToken.None));
        Assert.Equal(ErrorCode.PayloadTooLarge, ex.Code);
    }

    [Fact]
    public async Task Write_UndefinedType_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            MessageFramer.WriteAsync(new MemoryStream(), (MessageType)99, 1, Array.Empty<byte>(), CancellationToken.None));
    }
}
