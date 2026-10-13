namespace NexoDesk.Codec;

/// <summary>
/// Conversão de cor BGRA (32bpp, como a captura entrega) para NV12 (o que o encoder H.264 espera)
/// e de volta. BT.601 (vídeo SD/HD comum). Puro código gerenciado, sem dependência de codec — assim
/// a parte delicada do Media Foundation fica isolada do que é só aritmética de pixels.
///
/// NV12: plano Y (largura×altura) seguido do plano UV entrelaçado (largura×altura/2), 4:2:0.
/// Largura e altura devem ser pares.
/// </summary>
public static class YuvConvert
{
    public static int Nv12Size(int width, int height) => width * height + width * (height / 2);

    public static void BgraToNv12(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12)
    {
        int ySize = width * height;
        Span<byte> y = nv12[..ySize];
        Span<byte> uv = nv12.Slice(ySize, width * (height / 2));

        for (int j = 0; j < height; j++)
        {
            int row = j * stride;
            int yRow = j * width;
            for (int i = 0; i < width; i++)
            {
                int p = row + i * 4;
                byte b = bgra[p];
                byte g = bgra[p + 1];
                byte r = bgra[p + 2];
                // Y
                y[yRow + i] = (byte)Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            }
        }

        // Cromas 4:2:0: média de blocos 2×2.
        for (int j = 0; j < height; j += 2)
        {
            int uvRow = (j / 2) * width;
            for (int i = 0; i < width; i += 2)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                for (int dj = 0; dj < 2; dj++)
                for (int di = 0; di < 2; di++)
                {
                    int p = (j + dj) * stride + (i + di) * 4;
                    sumB += bgra[p];
                    sumG += bgra[p + 1];
                    sumR += bgra[p + 2];
                }
                int r = sumR >> 2, g = sumG >> 2, b = sumB >> 2;
                int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                int idx = uvRow + i;
                uv[idx] = (byte)Clamp(u);
                uv[idx + 1] = (byte)Clamp(v);
            }
        }
    }

    public static void Nv12ToBgra(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgra, int stride)
    {
        int ySize = width * height;
        ReadOnlySpan<byte> y = nv12[..ySize];
        ReadOnlySpan<byte> uv = nv12.Slice(ySize, width * (height / 2));

        for (int j = 0; j < height; j++)
        {
            int uvRow = (j / 2) * width;
            int outRow = j * stride;
            for (int i = 0; i < width; i++)
            {
                int c = y[j * width + i] - 16;
                int uvIdx = uvRow + (i & ~1);
                int d = uv[uvIdx] - 128;      // U
                int e = uv[uvIdx + 1] - 128;  // V
                int r = (298 * c + 409 * e + 128) >> 8;
                int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                int b = (298 * c + 516 * d + 128) >> 8;
                int p = outRow + i * 4;
                bgra[p] = (byte)Clamp(b);
                bgra[p + 1] = (byte)Clamp(g);
                bgra[p + 2] = (byte)Clamp(r);
                bgra[p + 3] = 255;
            }
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
