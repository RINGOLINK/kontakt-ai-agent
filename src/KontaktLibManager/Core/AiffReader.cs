using System;
using System.IO;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// **最小 AIFF 解析器** —— 只做「解出 PCM 采样」这一件事。
///
/// **为什么需要它**：实测发现 **NWaves 的 `WaveFile` 只读 WAV**，
/// **NAudio 核心包也没有 AIFF 读取器** ⇒ 导致 **160 个 `.aif` 100% 提取失败**。
/// 而这些文件经检查是【标准未压缩 AIFF】（`FORM....AIFFCOMM`），**格式本身很简单**。
///
/// **AIFF 结构**（大端）：
/// <code>
/// FORM &lt;size:4&gt; AIFF
///   COMM &lt;size:4&gt;  channels:2  numFrames:4  sampleSize:2  sampleRate:10(80 位扩展浮点)
///   SSND &lt;size:4&gt;  offset:4  blockSize:4  &lt;PCM 数据&gt;
/// </code>
///
/// **⚠️ 能力边界（如实说明）**：
///   · **只支持未压缩 AIFF**（`AIFF`）；**AIFC（压缩/扩展）不支持** ⇒ 返回 null；
///   · 只处理 **8 / 16 / 24 / 32 位** PCM（Kontakt 库里都是这几种）；
///   · 多声道会**在调用方**转单声道（本类只解出交错 PCM）。
/// </summary>
public static class AiffReader
{
    /// <summary>是否为 AIFF 文件（看文件头，不看扩展名）。</summary>
    public static bool LooksLikeAiff(string fullPath)
    {
        try
        {
            using var fs = File.OpenRead(fullPath);
            var h = new byte[12];
            if (fs.Read(h, 0, 12) != 12) return false;
            return h[0] == 'F' && h[1] == 'O' && h[2] == 'R' && h[3] == 'M'
                && h[8] == 'A' && h[9] == 'I' && h[10] == 'F' && h[11] == 'F';
        }
        catch { return false; }
    }

    /// <summary>
    /// **解码为交错 float**（范围 -1~1）。返回 `(样本, 声道数, 采样率)`；失败返回 null。
    /// </summary>
    public static (float[] Samples, int Channels, int SampleRate)? Decode(string fullPath, double maxSeconds = 0)
    {
        try
        {
            using var fs = File.OpenRead(fullPath);
            var br = new BinaryReader(fs);

            // ① FORM 头
            if (new string(br.ReadChars(4)) != "FORM") return null;
            br.ReadUInt32();                                  // FORM 大小（忽略）
            string formType = new string(br.ReadChars(4));
            if (formType != "AIFF") return null;              // AIFC 不支持（如实返回 null）

            int channels = 0, bits = 0, rate = 0;
            long ssndStart = -1;
            long ssndLen = 0;

            // ② 遍历 chunk
            while (fs.Position + 8 <= fs.Length)
            {
                string id = new string(br.ReadChars(4));
                uint size = ReadUInt32BE(br);
                long next = fs.Position + size + (size % 2);   // chunk 按偶数字节对齐

                if (id == "COMM")
                {
                    channels = ReadUInt16BE(br);
                    uint frames = ReadUInt32BE(br);
                    bits = ReadUInt16BE(br);
                    rate = (int)Math.Round(ReadExtended80(br));
                    _ = frames;
                }
                else if (id == "SSND")
                {
                    br.ReadUInt32();                          // offset
                    br.ReadUInt32();                          // blockSize
                    ssndStart = fs.Position;
                    ssndLen = size - 8;
                }

                if (ssndStart >= 0 && channels > 0) break;    // 两个 chunk 都拿到就够了
                fs.Position = Math.Min(next, fs.Length);
            }

            if (channels <= 0 || bits <= 0 || rate <= 0 || ssndStart < 0) return null;
            if (bits != 8 && bits != 16 && bits != 24 && bits != 32) return null;

            // ③ 读 PCM
            fs.Position = ssndStart;
            int bytesPerSample = bits / 8;
            int frameBytes = bytesPerSample * channels;
            long maxFrames = maxSeconds > 0 ? (long)(rate * maxSeconds) : long.MaxValue;
            long availFrames = Math.Min(ssndLen / frameBytes, maxFrames);
            if (availFrames <= 0) return null;
            int totalSamples = (int)Math.Min(availFrames * channels, int.MaxValue / 4);

            var raw = new byte[(long)availFrames * frameBytes];
            int read = 0;
            while (read < raw.Length)
            {
                int n = fs.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }
            int usable = read / frameBytes;
            if (usable <= 0) return null;
            var outp = new float[usable * channels];

            for (int i = 0; i < outp.Length; i++)
            {
                int b = i * bytesPerSample;
                outp[i] = bits switch
                {
                    8 => (raw[b] - 128) / 128f,                                       // 8 位无符号
                    16 => (short)((raw[b] << 8) | raw[b + 1]) / 32768f,               // 大端有符号
                    24 => (((raw[b] << 16) | (raw[b + 1] << 8) | raw[b + 2]) << 8) / 2147483648f,
                    32 => (int)((raw[b] << 24) | (raw[b + 1] << 16) | (raw[b + 2] << 8) | raw[b + 3]) / 2147483648f,
                    _ => 0f,
                };
            }
            return (outp, channels, rate);
        }
        catch { return null; }
    }

    // ── 大端读取辅助 ────────────────────────────────────────────
    private static ushort ReadUInt16BE(BinaryReader br)
    {
        var b = br.ReadBytes(2);
        return (ushort)((b[0] << 8) | b[1]);
    }

    private static uint ReadUInt32BE(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>AIFF 的 80 位扩展浮点采样率 → double。</summary>
    private static double ReadExtended80(BinaryReader br)
    {
        var b = br.ReadBytes(10);
        int exp = ((b[0] & 0x7F) << 8) | b[1];
        ulong mant = 0;
        for (int i = 2; i < 10; i++) mant = (mant << 8) | b[i];
        if (exp == 0 && mant == 0) return 0;
        double m = mant / Math.Pow(2, 63);
        return m * Math.Pow(2, exp - 16383);
    }
}
