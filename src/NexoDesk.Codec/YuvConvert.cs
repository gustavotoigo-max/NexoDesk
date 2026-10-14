using System.Threading.Tasks;

namespace NexoDesk.Codec;

/// <summary>
/// Conversão de cor BGRA (32bpp, como a captura entrega) para NV12 (o que o encoder H.264 espera) e
/// de volta. BT.601. Paralelizada por linhas (Parallel.For) para não virar gargalo de latência em
/// 1080p. Puro código gerenciado, isolando a aritmética de pixels da parte delicada do Media Foundation.
///
/// NV12: plano Y (largura×altura) seguido do plano UV entrelaçado (largura×altura/2), 4:2:0.
/// Largura e altura devem ser pares.
/// </summary>
public static class YuvConvert
{
    public static int Nv12Size(int width, int height) => width * height + width * (height / 2);

    public static void BgraToNv12(byte[] bgra, int stride, int width, int height, byte[] nv12)
    {
        int ySize = width * height;

        // Plano Y: cada linha é independente.
        Parallel.For(0, height, j =>
        {
            int row = j * stride;
            int yRow = j * width;
            for (int i = 0; i < width; i++)
            {
                int p = row + i * 4;
                int b = bgra[p];
                int g = bgra[p + 1];
                int r = bgra[p + 2];
                nv12[yRow + i] = (byte)Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            }
        });

        // Plano UV 4:2:0: cada bloco de 2 linhas é independente.
        int uvRows = height / 2;
        Parallel.For(0, uvRows, jr =>
        {
            int j = jr * 2;
            int uvRow = ySize + jr * width;
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
                nv12[idx] = (byte)Clamp(u);
                nv12[idx + 1] = (byte)Clamp(v);
            }
        });
    }

    public static void Nv12ToBgra(byte[] nv12, int width, int height, byte[] bgra, int stride)
    {
        int ySize = width * height;

        Parallel.For(0, height, j =>
        {
            int uvRow = ySize + (j / 2) * width;
            int outRow = j * stride;
            int yRow = j * width;
            for (int i = 0; i < width; i++)
            {
                int c = nv12[yRow + i] - 16;
                int uvIdx = uvRow + (i & ~1);
                int d = nv12[uvIdx] - 128;      // U
                int e = nv12[uvIdx + 1] - 128;  // V
                int r = (298 * c + 409 * e + 128) >> 8;
                int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                int b = (298 * c + 516 * d + 128) >> 8;
                int p = outRow + i * 4;
                bgra[p] = (byte)Clamp(b);
                bgra[p + 1] = (byte)Clamp(g);
                bgra[p + 2] = (byte)Clamp(r);
                bgra[p + 3] = 255;
            }
        });
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
