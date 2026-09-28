using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace KontaktLibManager.Core;

/// <summary>
/// **统一音频解码器** —— 把各种格式解成「单声道 float[]」。
///
/// **为什么需要它**：实测发现 `NWaves.Audio.WaveFile` **只支持 WAV / AIFF**，
/// 不支持 OGG / MP3 ⇒ 导致 **5,817 个 `.ogg` + 65 个 `.mp3` 全部提取失败**，
/// 而失败还被错误归因成「文件损坏」（见 `AudioFeatures.DecodableExts` 的校正注释）。
///
/// **分工**：
///   · `.wav` / `.aiff` / `.aif` → **NWaves `WaveFile`**（已有路径，实测稳定）
///   · `.mp3`                    → **NAudio `Mp3FileReader`**
///   · `.ogg`                    → **NAudio.Vorbis `VorbisWaveReader`**
///   · `.ncw` / `.nkx`           → **不在这里**（由 `NcwDecoder` / `NkxFeatures` 处理）
///
/// **⚠️ 能力边界**：本类只负责「解码成 PCM」；**重采样与归一化由调用方决定**
/// （`MertFeatures` 要 16 kHz，`AudioFeatures` 用原始采样率）。
/// </summary>
public static class AudioDecoder
{
    /// <summary>本类【声明支持】的扩展名（**与实现严格一致，实测过**）。</summary>
    public static readonly string[] SupportedExts = { "wav", "aiff", "aif", "mp3", "ogg" };

    /// <summary>是否由本类负责解码。</summary>
    public static bool CanDecode(string ext)
        => Array.IndexOf(SupportedExts, (ext ?? "").TrimStart('.').ToLowerInvariant()) >= 0;

    /// <summary>
    /// **解码为单声道**。返回 `(采样, 采样率)`；失败返回 null。
    /// </summary>
    /// <param name="fullPath">音频文件完整路径。</param>
    /// <param name="maxSeconds">最多取多少秒（0 或负 = 不限）。</param>
    public static (float[] Samples, int SampleRate)? DecodeMono(string fullPath, double maxSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return null;
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        try
        {
            return ext switch
            {
                "wav" => DecodeNwaves(fullPath, maxSeconds),
                // **AIFF 走自研解析器** —— 实测 NWaves 的 WaveFile 只读 WAV、
                //   NAudio 核心也没有 AIFF 读取器 ⇒ 160 个 .aif 曾 100% 失败。
                //   AIFF 结构简单（FORM + COMM + SSND），见 AiffReader 的注释。
                "aiff" or "aif" => DecodeAiff(fullPath, maxSeconds),
                "mp3" => DecodeWithNAudio(fullPath, maxSeconds, ogg: false),
                "ogg" => DecodeWithNAudio(fullPath, maxSeconds, ogg: true),
                _ => null,
            };
        }
        catch
        {
            return null;   // 单个文件失败不中断全局
        }
    }

    // ── AIFF：自研解析器（NWaves/NAudio 都不支持）──────────────
    private static (float[], int)? DecodeAiff(string fullPath, double maxSeconds)
    {
        var r = AiffReader.Decode(fullPath, maxSeconds);
        if (r == null) return null;
        var (samples, ch, rate) = r.Value;
        if (ch <= 1) return (samples, rate);
        // 多声道 → 单声道（取平均）
        int frames = samples.Length / ch;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++) sum += samples[f * ch + c];
            mono[f] = sum / ch;
        }
        return (mono, rate);
    }

    // ── WAV：沿用 NWaves（已有路径）──────────────────────────
    private static (float[], int)? DecodeNwaves(string fullPath, double maxSeconds)
    {
        using var stream = File.OpenRead(fullPath);
        var wave = new NWaves.Audio.WaveFile(stream);
        var sig = wave[0];
        var src = sig.Samples;
        int rate = sig.SamplingRate;
        int take = maxSeconds > 0
            ? (int)Math.Min(src.Length, rate * maxSeconds)
            : src.Length;
        if (take <= 0) return null;
        var outp = new float[take];
        Array.Copy(src, outp, take);
        return (outp, rate);
    }

    // ── MP3 / OGG：走 NAudio ────────────────────────────────────
    private static (float[], int)? DecodeWithNAudio(string fullPath, double maxSeconds, bool ogg)
    {
        using WaveStream reader = ogg
            ? new NAudio.Vorbis.VorbisWaveReader(fullPath)
            : new Mp3FileReader(fullPath);

        var fmt = reader.WaveFormat;
        int rate = fmt.SampleRate;
        int ch = Math.Max(1, fmt.Channels);

        // **用 ToSampleProvider() 直接取 float** —— 它会自动处理格式转换
        // （NAudio 的扩展方法，位于 NAudio.Wave 命名空间）。
        var sp = reader.ToSampleProvider();
        var fbuf = new float[8192];
        var acc = new List<float>(rate * 4);
        long maxFrames = maxSeconds > 0 ? (long)(rate * maxSeconds) : long.MaxValue;
        long gotFrames = 0;
        int n;
        while ((n = sp.Read(fbuf, 0, fbuf.Length)) > 0)
        {
            for (int i = 0; i < n; i++) acc.Add(fbuf[i]);
            gotFrames += n / ch;          // 交错存储 ⇒ 帧数 = 样本数 / 声道数
            if (gotFrames >= maxFrames) break;
        }

        if (acc.Count == 0) return null;

        // 多声道 → 单声道（取平均）
        if (ch > 1)
        {
            int frames = acc.Count / ch;
            var mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += acc[f * ch + c];
                mono[f] = sum / ch;
            }
            return (mono, rate);
        }
        return (acc.ToArray(), rate);
    }
}
