using System.ComponentModel;
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
public sealed class GdiScreenCapture(Func<int> maxFps, Func<int> jpegQuality, bool compositeCursor = false) : IDisplaySource, IFrameSource, ITiledFrameSource, ICursorSource, IDisposable
{
    private const int TileSize = 256;

    private readonly ImageCodecInfo _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly object _gate = new();
    private Bitmap? _bitmap;
    private Size _size;
    private ulong _lastHash;
    private long _nextTicks;

    // Estado do diff por regiões: hash de cada bloco do quadro anterior.
    private ulong[]? _tileHashes;
    private int _tileCols;
    private int _tileRows;
    private long _tileNextTicks;

    public DisplayInfoMessage GetDisplayInfo()
    {
        Size size = PrimarySize();
        uint dpi = GetDpiForSystem();
        return new DisplayInfoMessage(size.Width, size.Height, dpi == 0 ? 1.0 : dpi / 96.0);
    }

    /// <summary>Posição do cursor na tela primária, normalizada 0..1. Visible=false quando oculto.</summary>
    public CursorSample GetCursor()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING)
            return new CursorSample(0, 0, Visible: false);

        Size size = PrimarySize();
        double x = Math.Clamp((double)ci.ptScreenPos.X / size.Width, 0, 1);
        double y = Math.Clamp((double)ci.ptScreenPos.Y / size.Height, 0, 1);
        return new CursorSample(x, y, Visible: true);
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
            _tileHashes = null;
            _tileNextTicks = 0;
        }
    }

    // ===================== Captura por regiões (tiles) =====================

    /// <summary>Captura e devolve só os blocos que mudaram desde o quadro anterior. Null se nada mudou.</summary>
    public async Task<TiledFrame?> NextTiledFrameAsync(uint frameIndex, CancellationToken ct)
    {
        int fps = Math.Clamp(maxFps(), 1, 30);
        long interval = Stopwatch.Frequency / fps;

        long now = Stopwatch.GetTimestamp();
        if (_tileNextTicks == 0 || now - _tileNextTicks > interval * 4)
            _tileNextTicks = now;
        long wait = _tileNextTicks + interval - now;
        _tileNextTicks += interval;
        if (wait > 0)
            await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), ct).ConfigureAwait(false);

        return await Task.Run(() => CaptureTiles(frameIndex), ct).ConfigureAwait(false);
    }

    private TiledFrame? CaptureTiles(uint frameIndex)
    {
        lock (_gate)
        {
            Size size = PrimarySize();
            bool resized = _bitmap is null || _size != size;
            if (resized)
            {
                _bitmap?.Dispose();
                _bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
                _size = size;
                _tileCols = (size.Width + TileSize - 1) / TileSize;
                _tileRows = (size.Height + TileSize - 1) / TileSize;
                _tileHashes = new ulong[_tileCols * _tileRows];
                Array.Fill(_tileHashes, 0UL);
            }

            try
            {
                using Graphics g = Graphics.FromImage(_bitmap!);
                g.CopyFromScreen(0, 0, 0, 0, size, CopyPixelOperation.SourceCopy);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException)
            {
                // Tela de bloqueio / desktop seguro: mantém o último quadro.
                return null;
            }

            var changed = new List<ScreenTile>();
            BitmapData data = _bitmap!.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < _tileRows; row++)
                {
                    int ty = row * TileSize;
                    int th = Math.Min(TileSize, size.Height - ty);
                    for (int col = 0; col < _tileCols; col++)
                    {
                        int tx = col * TileSize;
                        int tw = Math.Min(TileSize, size.Width - tx);
                        ulong hash = HashTile(data, tx, ty, tw, th);
                        int idx = row * _tileCols + col;
                        if (hash == _tileHashes![idx])
                            continue;
                        _tileHashes[idx] = hash;
                        changed.Add(new ScreenTile(tx, ty, tw, th, default)); // JPEG abaixo (fora do lock de pixels)
                    }
                }
            }
            finally
            {
                _bitmap!.UnlockBits(data);
            }

            if (changed.Count == 0)
                return null;

            int raw = jpegQuality();
            bool lossless = raw >= 96;                 // "Máxima" = sem perda (PNG) para texto/UI nítidos
            int quality = Math.Clamp(raw, 10, 95);
            var tiles = new List<ScreenTile>(changed.Count);
            foreach (ScreenTile t in changed)
            {
                using Bitmap crop = _bitmap!.Clone(new Rectangle(t.X, t.Y, t.Width, t.Height), PixelFormat.Format24bppRgb);
                byte[] data;
                if (lossless)
                {
                    byte[] png = EncodePng(crop);
                    // Bloco "chapado" (texto/UI) comprime bem em PNG e fica perfeito; bloco fotográfico
                    // incha em PNG, então nesse caso usa JPEG alto (continua nítido, bem menor).
                    data = png.Length <= LosslessTileCap ? png : Encode(crop, 92);
                }
                else
                {
                    data = Encode(crop, quality);
                }
                tiles.Add(t with { Jpeg = data });
            }

            // Se o quadro ficou grande demais (ex.: tela inteira mudou em alta qualidade), ainda assim
            // envia; o limite do protocolo é 8 MiB. Em casos extremos, o Encode do TiledFrame rejeita.
            return new TiledFrame(frameIndex, size.Width, size.Height, tiles);
        }
    }

    /// <summary>FNV-1a sobre uma amostra dos pixels do bloco; detecta mudança sem varrer tudo.</summary>
    private static ulong HashTile(BitmapData data, int x, int y, int w, int h)
    {
        ulong hash = 1469598103934665603UL;
        unsafe
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;
            for (int r = 0; r < h; r++)
            {
                byte* rowPtr = basePtr + (long)(y + r) * stride + (long)x * 4;
                // Amostra alguns bytes por linha (passo 17) para ganhar velocidade.
                for (int c = 0; c < w * 4; c += 17)
                {
                    hash ^= rowPtr[c];
                    hash *= 1099511628211UL;
                }
            }
        }
        return hash;
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

            long cursorKey = 0;
            try
            {
                using Graphics g = Graphics.FromImage(_bitmap);
                g.CopyFromScreen(0, 0, 0, 0, size, CopyPixelOperation.SourceCopy);
                // Por padrão o cursor NÃO entra na imagem: ele vai em mensagens leves (CursorUpdate)
                // e o ADMIN o desenha por cima. Assim mover o mouse não re-encoda a tela inteira.
                if (compositeCursor)
                    cursorKey = DrawCursor(g);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException)
            {
                // Tela de bloqueio / desktop seguro: não dá para capturar na sessão do usuário. Mantém o último frame.
                return null;
            }

            // Sem o cursor na imagem, só mudanças reais de tela geram frame novo (cursorKey=0).
            ulong hash = HashPixels(_bitmap) ^ (ulong)cursorKey;
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

    /// <summary>Desenha o cursor atual na imagem e devolve uma chave da posição (para o hash). 0 se oculto.</summary>
    private static long DrawCursor(Graphics g)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero)
            return 0;

        int x = ci.ptScreenPos.X;
        int y = ci.ptScreenPos.Y;
        if (GetIconInfo(ci.hCursor, out ICONINFO info))
        {
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            x -= info.xHotspot;
            y -= info.yHotspot;
        }

        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        return ((long)(ci.ptScreenPos.X & 0xFFFF) << 16) | (uint)(ci.ptScreenPos.Y & 0xFFFF);
    }

    private byte[] Encode(Bitmap bitmap, int quality)
    {
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        using var ms = new MemoryStream(64 * 1024);
        bitmap.Save(ms, _jpegCodec, p);
        return ms.ToArray();
    }

    /// <summary>Acima deste tamanho, o bloco PNG é considerado "fotográfico" e vai como JPEG alto.</summary>
    private const int LosslessTileCap = 80 * 1024;

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var ms = new MemoryStream(64 * 1024);
        bitmap.Save(ms, ImageFormat.Png);
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

    // --- Cursor ---
    private const int CURSOR_SHOWING = 1;
    private const int DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, int flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
