using NexoDesk.Codec;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Session;

namespace NexoDesk.App.Services;

/// <summary>Tela capturada em BGRA (32bpp), para o caminho H.264.</summary>
public sealed record RawScreen(byte[] Bgra, int Stride, int Width, int Height);

/// <summary>
/// Fonte de vídeo H.264 do PC REMOTO: captura a tela em BGRA e passa pelo encoder do Media Foundation.
/// Pula quadros idênticos (detecção fina, pega cursor piscando) e força um keyframe a cada ~2 s para
/// um ADMIN que entra no meio conseguir sincronizar. Se o encoder não iniciar, nem é criada (fallback).
/// </summary>
public sealed class H264ScreenSource : IH264FrameSource, IDisposable
{
    private readonly GdiScreenCapture _capture;
    private readonly Func<int> _maxFps;
    private readonly H264Encoder _encoder;
    private readonly int _keyframeEvery;
    private int _sinceKey;
    private long _nextTicks;
    private bool _disposed;

    private H264ScreenSource(GdiScreenCapture capture, H264Encoder encoder, Func<int> maxFps)
    {
        _capture = capture;
        _encoder = encoder;
        _maxFps = maxFps;
        _keyframeEvery = 40; // ~2 s a 20 fps
    }

    /// <summary>Cria a fonte H.264 para a tela atual, ou null se o encoder não puder ser criado.</summary>
    public static H264ScreenSource? TryCreate(GdiScreenCapture capture, Func<int> maxFps, Func<int> quality)
    {
        RawScreen? first = capture.CaptureRaw();
        if (first is null)
            return null;

        int fps = 30; // H.264 roda a 30 fps para fluidez (independe do limite do modo imagem)
        // Taxa de bits-alvo conforme a qualidade escolhida (a tela parada gasta bem menos com baixa latência).
        int q = quality();
        int bitrate = q >= 96 ? 25_000_000 : q >= 90 ? 15_000_000 : 8_000_000;
        H264Encoder? enc = H264Encoder.TryCreate(first.Width, first.Height, fps, bitrate);
        if (enc is null)
            return null;
        return new H264ScreenSource(capture, enc, maxFps);
    }

    public async Task<H264Frame?> NextH264FrameAsync(uint frameIndex, CancellationToken ct)
    {
        int fps = 30;
        long interval = System.Diagnostics.Stopwatch.Frequency / fps;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_nextTicks == 0 || now - _nextTicks > interval * 4)
            _nextTicks = now;
        long wait = _nextTicks + interval - now;
        _nextTicks += interval;
        if (wait > 0)
            await Task.Delay(TimeSpan.FromSeconds((double)wait / System.Diagnostics.Stopwatch.Frequency), ct).ConfigureAwait(false);

        return await Task.Run(() => EncodeNext(frameIndex), ct).ConfigureAwait(false);
    }

    private H264Frame? EncodeNext(uint frameIndex)
    {
        if (_disposed)
            return null;
        RawScreen? raw = _capture.CaptureRaw();
        if (raw is null || raw.Width != _encoder.Width || raw.Height != _encoder.Height)
            return null; // mudou a resolução (ou tela de bloqueio): nada neste quadro.

        // O encoder do Media Foundation precisa de alimentação contínua para emitir quadros (tem
        // latência de 1-2 frames). Por isso alimentamos TODO quadro — em tela parada ele gera
        // P-frames minúsculos, então a banda continua baixa. Keyframe periódico para quem entra no meio.
        if (_sinceKey >= _keyframeEvery)
        {
            _encoder.RequestKeyFrame();
            _sinceKey = 0;
        }
        _sinceKey++;

        return _encoder.Encode(frameIndex, raw.Bgra, raw.Stride);
    }

    public void Reset()
    {
        _sinceKey = _keyframeEvery; // próximo quadro é keyframe
        _nextTicks = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _encoder.Dispose();
    }
}
