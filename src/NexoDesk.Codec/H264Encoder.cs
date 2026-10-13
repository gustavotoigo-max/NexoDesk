using System.Runtime.InteropServices;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Session;
using Vortice.MediaFoundation;
using Vortice.Multimedia;
using static Vortice.MediaFoundation.MediaFactory;

namespace NexoDesk.Codec;

/// <summary>
/// Codifica a tela em H.264 usando o encoder do Media Foundation (Windows). Recebe a captura em BGRA,
/// converte para NV12 e devolve quadros H.264 (Annex-B). Movimento fluido com baixa banda.
///
/// Opcional e à prova de falha: <see cref="TryCreate"/> devolve null se o Media Foundation não estiver
/// disponível ou a configuração falhar — o app então volta ao vídeo por imagem (tiles).
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private readonly IMFTransform _mft;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _nv12;
    private long _frameDuration100ns;
    private long _time100ns;
    private bool _started;
    private bool _disposed;

    private H264Encoder(IMFTransform mft, int width, int height, int fps)
    {
        _mft = mft;
        _width = width;
        _height = height;
        _nv12 = new byte[YuvConvert.Nv12Size(width, height)];
        _frameDuration100ns = 10_000_000L / Math.Max(fps, 1);
    }

    /// <summary>Cria o encoder, ou null se o Media Foundation não estiver utilizável neste computador.</summary>
    public static H264Encoder? TryCreate(int width, int height, int fps, int bitrateBitsPerSec)
    {
        // Dimensões pares (NV12/H.264 exigem).
        width &= ~1;
        height &= ~1;
        if (width < 16 || height < 16)
            return null;

        try
        {
            MFStartup(MFStartupFlags.Full).CheckError();

            var outputInfo = new TRegisterTypeInformation { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
            IMFActivate[] activates = MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                MFTEnumFlags.SyncMft | MFTEnumFlags.SortAndFilter,
                null,
                outputInfo);
            if (activates.Length == 0)
                return null;

            var mft = activates[0].ActivateObject<IMFTransform>();
            foreach (IMFActivate a in activates)
                a.Dispose();

            // Saída (H.264) primeiro, depois entrada (NV12).
            using (IMFMediaType outType = MFCreateMediaType())
            {
                outType.MajorType = MediaTypeGuids.Video;
                outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrateBitsPerSec);
                outType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                outType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
                outType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, (int)eAVEncH264VProfile.Main);
                mft.SetOutputType(0, outType, 0);
            }
            using (IMFMediaType inType = MFCreateMediaType())
            {
                inType.MajorType = MediaTypeGuids.Video;
                inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                inType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                inType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
                inType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                mft.SetInputType(0, inType, 0);
            }

            var enc = new H264Encoder(mft, width, height, fps);
            mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, IntPtr.Zero);
            mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, IntPtr.Zero);
            enc._started = true;
            return enc;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>Pede que o próximo quadro seja um keyframe (ex.: novo ADMIN conectando).</summary>
    public void RequestKeyFrame()
    {
        try { _mft.ProcessMessage(TMessageType.MessageCommandDrain, IntPtr.Zero); } catch (Exception) { }
    }

    /// <summary>
    /// Codifica um quadro BGRA. Devolve o(s) NAL(s) H.264 e se é keyframe, ou null se o encoder ainda
    /// não produziu saída para este quadro.
    /// </summary>
    public H264Frame? Encode(uint frameIndex, ReadOnlySpan<byte> bgra, int stride)
    {
        if (_disposed || !_started)
            return null;

        YuvConvert.BgraToNv12(bgra, stride, _width, _height, _nv12);

        IMFSample sample = CreateSample(_nv12, _time100ns, _frameDuration100ns);
        try
        {
            _mft.ProcessInput(0, sample, 0);
        }
        finally
        {
            sample.Dispose();
        }
        _time100ns += _frameDuration100ns;

        return DrainOutput(frameIndex);
    }

    private H264Frame? DrainOutput(uint frameIndex)
    {
        var buffers = new TOutputDataBuffer[1];
        buffers[0].StreamID = 0;

        IMFSample outSample = CreateEmptyOutputSample();
        buffers[0].Sample = outSample;
        try
        {
            Result res = _mft.ProcessOutput(0, 1, buffers, out _);
            if (res.Failure)
                return null; // MF_E_TRANSFORM_NEED_MORE_INPUT: sem saída ainda neste quadro.

            IMFSample produced = buffers[0].Sample;
            bool key = produced.GetUInt32(SampleAttributeKeys.CleanPoint) != 0;

            using IMFMediaBuffer buffer = produced.ConvertToContiguousBuffer();
            IntPtr ptr = buffer.Lock(out _, out int current);
            try
            {
                byte[] data = new byte[current];
                Marshal.Copy(ptr, data, 0, current);
                return new H264Frame(frameIndex, key, _width, _height, data);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            outSample.Dispose();
        }
    }

    private static IMFSample CreateSample(byte[] data, long time100ns, long duration100ns)
    {
        IMFSample sample = MFCreateSample();
        IMFMediaBuffer buffer = MFCreateMemoryBuffer(data.Length);
        IntPtr ptr = buffer.Lock(out _, out _);
        Marshal.Copy(data, 0, ptr, data.Length);
        buffer.Unlock();
        buffer.CurrentLength = data.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = time100ns;
        sample.SampleDuration = duration100ns;
        buffer.Dispose();
        return sample;
    }

    private IMFSample CreateEmptyOutputSample()
    {
        // Buffer generoso para o quadro comprimido.
        int size = Math.Max(_width * _height, 64 * 1024);
        IMFSample sample = MFCreateSample();
        IMFMediaBuffer buffer = MFCreateMemoryBuffer(size);
        sample.AddBuffer(buffer);
        buffer.Dispose();
        return sample;
    }

    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, IntPtr.Zero); } catch (Exception) { }
        _mft.Dispose();
    }
}
