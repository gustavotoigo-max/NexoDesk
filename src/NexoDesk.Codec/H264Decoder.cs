using System.Runtime.InteropServices;
using NexoDesk.Shared.Messages;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using Vortice.Multimedia;
using static Vortice.MediaFoundation.MediaFactory;

namespace NexoDesk.Codec;

/// <summary>
/// Decodifica o vídeo H.264 recebido (Annex-B) usando o decoder do Media Foundation e devolve a imagem
/// em BGRA (pronta para o WriteableBitmap do ADMIN). Opcional e à prova de falha via <see cref="TryCreate"/>.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    // Códigos de retorno do Media Foundation que tratamos à parte.
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    private readonly IMFTransform _mft;
    private int _width;
    private int _height;
    private byte[] _nv12 = [];
    private byte[] _bgra = [];
    private long _time100ns;
    private bool _disposed;

    private H264Decoder(IMFTransform mft, int width, int height)
    {
        _mft = mft;
        _width = width;
        _height = height;
        _nv12 = new byte[YuvConvert.Nv12Size(width, height)];
        _bgra = new byte[width * 4 * height];
    }

    public int Width => _width;
    public int Height => _height;

    public static H264Decoder? TryCreate(int width, int height)
    {
        width &= ~1;
        height &= ~1;
        if (width < 16 || height < 16)
            return null;

        try
        {
            MFStartup();

            int hr = CoCreateInstance(ClsidH264Decoder, IntPtr.Zero, CLSCTX_INPROC_SERVER, IidImfTransform, out IntPtr ptr);
            if (hr < 0 || ptr == IntPtr.Zero)
                return null;
            var mft = new IMFTransform(ptr);

            const int Progressive = 2;
            using (IMFMediaType inType = MFCreateMediaType())
            {
                inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                inType.Set(MediaTypeAttributeKeys.InterlaceMode, Progressive);
                inType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
                mft.SetInputType(0, inType, 0);
            }

            var dec = new H264Decoder(mft, width, height);
            mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            return dec;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodifica um quadro. Em caso de saída pronta, devolve true e preenche <paramref name="bgra"/>
    /// (BGRA, stride = width*4) com a resolução atual. False quando o decoder ainda não produziu imagem.
    /// </summary>
    public bool Decode(H264Frame frame, out byte[] bgra, out int width, out int height)
    {
        bgra = [];
        width = _width;
        height = _height;
        if (_disposed)
            return false;

        try { FeedInput(frame.Bitstream.Span); }
        catch (Exception) { return false; }

        // Esvazia a saída disponível. O decoder sinaliza a resolução real via STREAM_CHANGE.
        for (int guard = 0; guard < 8; guard++)
        {
            IMFSample outSample = MFCreateSample();
            IMFMediaBuffer outBuffer = MFCreateMemoryBuffer(Math.Max(_nv12.Length, 1));
            outSample.AddBuffer(outBuffer);
            outBuffer.Dispose();

            var buffer0 = new OutputDataBuffer { StreamID = 0, Sample = outSample };
            Result res;
            try { res = _mft.ProcessOutput(0, 1, ref buffer0, out _); }
            catch (Exception) { outSample.Dispose(); return false; }

            if (res.Success)
            {
                try
                {
                    ReadNv12(buffer0.Sample);
                    YuvConvert.Nv12ToBgra(_nv12, _width, _height, _bgra, _width * 4);
                    bgra = _bgra;
                    width = _width;
                    height = _height;
                    return true;
                }
                catch (Exception) { return false; }
                finally { outSample.Dispose(); }
            }

            outSample.Dispose();
            if (res.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                RenegotiateOutput();
                continue; // tenta de novo com a resolução/buffers atualizados.
            }
            // NEED_MORE_INPUT ou outro: sem imagem neste quadro.
            return false;
        }
        return false;
    }

    private void FeedInput(ReadOnlySpan<byte> h264)
    {
        IMFSample sample = MFCreateSample();
        IMFMediaBuffer buffer = MFCreateMemoryBuffer(h264.Length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        unsafe { h264.CopyTo(new Span<byte>((void*)ptr, h264.Length)); }
        buffer.Unlock();
        buffer.CurrentLength = h264.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = _time100ns;
        _time100ns += 10_000_000L / 30;
        buffer.Dispose();
        try { _mft.ProcessInput(0, sample, 0); }
        catch (Exception) { /* fila cheia: a saída será esvaziada no laço */ }
        finally { sample.Dispose(); }
    }

    private void RenegotiateOutput()
    {
        // Procura um tipo de saída NV12 e o aplica, lendo a resolução real do fluxo.
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = _mft.GetOutputAvailableType(0, i); }
            catch (Exception) { return; }
            try
            {
                Guid subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
                if (subtype == VideoFormatGuids.NV12)
                {
                    ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    _width = (int)(size >> 32);
                    _height = (int)(size & 0xFFFFFFFF);
                    _mft.SetOutputType(0, type, 0);
                    _nv12 = new byte[YuvConvert.Nv12Size(_width, _height)];
                    _bgra = new byte[_width * 4 * _height];
                    return;
                }
            }
            finally
            {
                type.Dispose();
            }
        }
    }

    private void ReadNv12(IMFSample sample)
    {
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr ptr, out _, out int current);
        try
        {
            int n = Math.Min(current, _nv12.Length);
            Marshal.Copy(ptr, _nv12, 0, n);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch (Exception) { }
        _mft.Dispose();
    }

    private const int CLSCTX_INPROC_SERVER = 1;
    private static readonly Guid ClsidH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid IidImfTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, int dwClsContext, in Guid riid, out IntPtr ppv);
}
