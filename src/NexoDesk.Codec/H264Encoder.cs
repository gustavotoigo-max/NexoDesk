using System.Runtime.InteropServices;
using NexoDesk.Shared.Messages;
using SharpGen.Runtime;
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
            MFStartup();

            // Perfil H.264 Main (77); modo progressivo.
            const int ProfileMain = 77;
            const int Progressive = 2; // MFVideoInterlace_Progressive

            // Cria o encoder H.264 do Windows diretamente pelo CLSID (evita enumeração de MFTs).
            int hr = CoCreateInstance(ClsidH264Encoder, IntPtr.Zero, CLSCTX_INPROC_SERVER, IidImfTransform, out IntPtr ptr);
            if (hr < 0 || ptr == IntPtr.Zero)
                return null;
            var mft = new IMFTransform(ptr);

            // Saída (H.264) primeiro, depois entrada (NV12).
            using (IMFMediaType outType = MFCreateMediaType())
            {
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrateBitsPerSec);
                outType.Set(MediaTypeAttributeKeys.InterlaceMode, Progressive);
                outType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
                outType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileMain);
                mft.SetOutputType(0, outType, 0);
            }
            using (IMFMediaType inType = MFCreateMediaType())
            {
                inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                inType.Set(MediaTypeAttributeKeys.InterlaceMode, Progressive);
                inType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
                inType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
                inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                mft.SetInputType(0, inType, 0);
            }

            var enc = new H264Encoder(mft, width, height, fps);
            mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
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

    /// <summary>
    /// Reservado para forçar keyframe (via ICodecAPI) numa versão futura. Por ora é no-op: o encoder
    /// já emite um IDR no início e keyframes periódicos pelo GOP padrão. (NUNCA usar DRAIN aqui — isso
    /// encerra o fluxo e trava o encoder.)
    /// </summary>
    public void RequestKeyFrame()
    {
    }

    /// <summary>
    /// Codifica um quadro BGRA. Devolve o(s) NAL(s) H.264 e se é keyframe, ou null se o encoder ainda
    /// não produziu saída para este quadro.
    /// </summary>
    public H264Frame? Encode(uint frameIndex, ReadOnlySpan<byte> bgra, int stride)
    {
        if (_disposed || !_started)
            return null;

        try
        {
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
        catch (Exception)
        {
            // Erro transitório do Media Foundation: pula este quadro em vez de derrubar a transmissão.
            return null;
        }
    }

    private H264Frame? DrainOutput(uint frameIndex)
    {
        IMFSample outSample = CreateEmptyOutputSample();
        var buffer0 = new OutputDataBuffer { StreamID = 0, Sample = outSample };
        try
        {
            Result res = _mft.ProcessOutput(0, 1, ref buffer0, out _);
            if (res.Failure)
                return null; // MF_E_TRANSFORM_NEED_MORE_INPUT: sem saída ainda neste quadro.

            IMFSample produced = buffer0.Sample;
            bool key = produced.GetUInt32(SampleAttributeKeys.CleanPoint) != 0;

            using IMFMediaBuffer buffer = produced.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr ptr, out _, out int current);
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
        buffer.Lock(out IntPtr ptr, out _, out _);
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

    private const int CLSCTX_INPROC_SERVER = 1;
    private static readonly Guid ClsidH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid IidImfTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, int dwClsContext, in Guid riid, out IntPtr ppv);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch (Exception) { }
        _mft.Dispose();
    }
}
