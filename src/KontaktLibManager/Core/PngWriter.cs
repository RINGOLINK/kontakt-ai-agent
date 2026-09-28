using System.IO.Compression;

namespace KontaktLibManager.Core;

/// <summary>
/// 极简 PNG 编码器（无第三方依赖）。
/// 用途：把 PDFium 渲染出的 BGRA 像素缓冲编码成 PNG，供界面显示说明书例图。
/// 依赖 .NET 内置的 ZLibStream（PNG 的 IDAT 正是 zlib 流）。
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>BGRA 像素 → PNG（RGBA，不压缩优化以求快）。</summary>
    public static byte[] FromBgra(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("尺寸无效");
        int need = width * height * 4;
        if (bgra.Length < need) throw new ArgumentException($"像素数据不足：{bgra.Length} < {need}");

        // 每行前置一个 filter 字节（0 = None）
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            raw[rowStart] = 0;
            int src = y * stride;
            for (int x = 0; x < width; x++)
            {
                int s = src + x * 4;
                int d = rowStart + 1 + x * 4;
                // **用 alpha 合成到白底上** —— 这是关键。
                //
                // PDF 页面本身**没有背景色**（它假设"纸是白的"），PDFium 渲染出来的
                // 背景像素是**全透明**的（BGRA 的 A=0、RGB=0）。旧实现直接把 A 写成 255，
                // 于是**透明背景变成纯黑**，整页看起来就是「黑底白字」或「一片灰块」
                //（用户实测：Studio Drummer Manual French.pdf 全灰块、
                //  Orchestral Essentials 2 Reference Manual.pdf 一片空白 —— 都是这个原因）。
                //
                // 正确做法：`out = src*a/255 + 白*a'`，即按 alpha 与白色做 alpha 混合。
                byte a = bgra[s + 3];
                byte sb = bgra[s], sg = bgra[s + 1], sr = bgra[s + 2];
                if (a == 255)
                {
                    raw[d] = sr; raw[d + 1] = sg; raw[d + 2] = sb;
                }
                else if (a == 0)
                {
                    raw[d] = 255; raw[d + 1] = 255; raw[d + 2] = 255;
                }
                else
                {
                    int inv = 255 - a;
                    raw[d] = (byte)((sr * a + 255 * inv) / 255);
                    raw[d + 1] = (byte)((sg * a + 255 * inv) / 255);
                    raw[d + 2] = (byte)((sb * a + 255 * inv) / 255);
                }
                raw[d + 3] = 255;           // 输出始终不透明
            }
        }

        byte[] compressed;
        using (var zms = new MemoryStream())
        {
            using (var z = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            compressed = zms.ToArray();
        }

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, width);
        WriteBE(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // color type: RGBA
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter
        ihdr[12] = 0;   // interlace

        using var ms = new MemoryStream();
        ms.Write(Signature, 0, Signature.Length);
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", compressed);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteBE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len, 0, 4);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);

        uint crc = Crc32.Compute(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBE(crcBytes, 0, unchecked((int)crc));
        s.Write(crcBytes, 0, 4);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Compute(byte[] a, byte[] b)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
