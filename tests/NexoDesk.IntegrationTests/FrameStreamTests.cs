using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;

namespace NexoDesk.IntegrationTests;

/// <summary>Uma fonte de frames de teste: devolve frames até acabarem e então null.</summary>
internal sealed class FakeFrameSource(int count) : IFrameSource
{
    private int _sent;
    public int ResetCount { get; private set; }

    public async Task<VideoFrame?> NextFrameAsync(uint frameIndex, CancellationToken ct)
    {
        await Task.Delay(5, ct);
        if (_sent >= count)
            return null;
        _sent++;
        // JPEG mínimo válido (SOI + EOI) só para o transporte.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, (byte)frameIndex, 0xFF, 0xD9];
        return new VideoFrame(frameIndex, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 1920, 1080, jpeg);
    }

    public void Reset() => ResetCount++;
}

public class FrameStreamTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Admin_ReceivesStreamedFrames()
    {
        using X509Certificate2 server = TestCertificates.Server();
        using X509Certificate2 client = TestCertificates.Client();
        var frames = new FakeFrameSource(3);
        await using var agent = new LoopbackAgent(server, [new AuthorizedClient("Admin", CertificateFingerprint.Sha256(client))], frames: frames);

        var received = new List<VideoFrame>();
        var gotThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        AdminSession session = await AdminSession.ConnectAsync(new AdminConnectOptions
        {
            Host = "127.0.0.1",
            Port = agent.Port,
            ClientCertificate = client,
            ServerFingerprints = [agent.ServerFingerprint],
            ClientId = "admin",
            AppVersion = "test",
            PingInterval = TimeSpan.FromMilliseconds(200),
        }, CancellationToken.None);

        session.FrameReceived += f =>
        {
            lock (received)
            {
                received.Add(f);
                if (received.Count >= 3)
                    gotThree.TrySetResult();
            }
        };

        await gotThree.Task.WaitAsync(Wait);
        Assert.True(received.Count >= 3);
        Assert.Equal(1920, received[0].Width);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0xFF, 0xD9 }, received[0].Jpeg.ToArray());

        await session.DisconnectAsync();
        // Reset é chamado ao encerrar a sessão.
        await agent.Accepted.WaitAsync(Wait);
        Assert.True(frames.ResetCount >= 1);
    }
}
