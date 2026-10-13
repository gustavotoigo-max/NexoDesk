using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Session;

namespace NexoDesk.App.Services;

/// <summary>
/// Captura a tela do PC REMOTO com GDI (CopyFromScreen) e envia como JPEG. Funciona na sessão do usuário
/// desbloqueada; a tela de bloqueio/login é a etapa do serviço. Pula frames idênticos ao anterior.
/// </summary>
public sealed class GdiScreenCapture(Func<int> maxFps, Func<int> jpegQuality) : IDisplaySource, IFrameSource, IDisposable
{
    private readonly ImageCodecInfo _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly object _gate = new();
    private Bitmap? _bitmap;
    private Size _size;
    private ulong _lastHash;
    private long _nextTicks;

    public DisplayInfoMessage GetDisplayInfo()
    {
        Size size = PrimarySize();
        uint dpi = GetDpiForSystem();
        return new DisplayInfoMessage(size.Width, size.Height, dpi == 0 ? 1.0 : dpi / 96.0);
    }

    public async Task<VideoFrame?> NextFrameAsync(uint frameIndex, CancellationToken ct)
    {
        int fps = Math.Clamp(maxFps(), 1, 30);
        long interval = Stopwatch.Frequency / fps;

        // Mantém o ritmo-alvo sem acumular atraso.
        long now = Stopwatch.GetTimestamp();
        if (_nextTicks == 0 || now - _nextTicks > interval * 4)
            _nextTicks = now;
        long wait = _nextTicks + interval - now;
        _nextTicks += interval;
        if (wait > 0)
            await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), ct).ConfigureAwait(false);

        return await Task.Run(() => CaptureFrame(frameIndex), ct).ConfigureAwait(false);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _lastHash = 0;
            _nextTicks = 0;
        }
    }

    public void Dispose() => Reset();

    private VideoFrame? CaptureFrame(uint frameIndex)
    {
        lock (_gate)
        {
            Size size = PrimarySize();
            if (_bitmap is null || _size != size)
            {
                _bitmap?.Dispose();
                _bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
                _size = size;
                _lastHash = 0;
            }

            try
            {
                using (Graphics g = Graphics.FromImage(_bitmap))
                    g.CopyFromScreen(0, 0, 0, 0, size, CopyPixelOperation.SourceCopy);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException)
            {
                // Tela de bloqueio / desktop seguro: não dá para capturar na sessão do usuário. Mantém o último frame.
                return null;
            }

            ulong hash = HashPixels(_bitmap);
            if (hash == _lastHash)
                return null;
            _lastHash = hash;

            byte[] jpeg = Encode(_bitmap, Math.Clamp(jpegQuality(), 10, 95));
            if (jpeg.Length > VideoFrame.MaxJpegBytes)
                return null;
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new VideoFrame(frameIndex, timestamp, size.Width, size.Height, jpeg);
        }
    }

    private byte[] Encode(Bitmap bitmap, int quality)
    {
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        using var ms = new MemoryStream(64 * 1024);
        bitmap.Save(ms, _jpegCodec, p);
        return ms.ToArray();
    }

    /// <summary>FNV-1a sobre os bytes da imagem; detecta telas idênticas para não reenviar.</summary>
    private static ulong HashPixels(Bitmap bitmap)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * data.Height;
            ulong hash = 1469598103934665603UL;
            unsafe
            {
                byte* p = (byte*)data.Scan0;
                // Amostra 1 byte a cada 7 para ganhar velocidade sem perder sensibilidade útil.
                for (int i = 0; i < bytes; i += 7)
                {
                    hash ^= p[i];
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Size PrimarySize() => new(Math.Max(GetSystemMetrics(0), 1), Math.Max(GetSystemMetrics(1), 1));

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
