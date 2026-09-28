using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NWaves.Audio;
using NWaves.FeatureExtractors;
using NWaves.FeatureExtractors.Options;
using NWaves.FeatureExtractors.Multi;
using NWaves.FeatureExtractors.Base;
using NWaves.Signals;
using NWaves.Utils;

namespace KontaktLibManager.Core;

/// <summary>
/// **音色声学特征 + 相似度检索** —— 调研报告「远期」第 7 项（档 1）。
///
/// **要解决的问题**：报告指出本项目「**音频完全缺席**」—— 只有文件名和路径，
/// 没有对「这个音色听起来像什么」的任何量化。有了声学特征，才能做「**找相似音色**」
/// （对标 Atlas / XO 的核心交互），而不只是靠文件名猜。
///
/// **做法（档 1，纯本地）**：用 **NWaves**（纯 C# 库、离线、不联网）提取 ~40 维特征：
///   · **MFCC 13 维的均值 + 标准差**（26 维）—— 音色「音质」的主成分，与 librosa 对齐；
///   · **谱质心 / 谱滚降 / 谱平坦度 / 过零率 / RMS 能量的均值**（5 维）—— 明亮度、噪声度、响度；
///   · **起音时间（attack）**（1 维）—— 打击感 vs 渐入。
/// 存进 SQLite（`audio_features` 表），检索用**余弦相似度 KNN**。
///
/// **⚠️ 明确的适用范围（必须如实告知用户）**：
///   本机 37,760 个音频片段里，**Kontakt 专有的 `.ncw`（22,909 个）与 `.nkx`（1,872 个）
///   无法用开源库解码** —— 它们分别是 Kontakt 的压缩采样格式与加密容器。
///   可分析的是 **`.wav`（6,937）+ `.aif`（160）≈ 7,100 个（约 19%）**。
///   其余格式会被**跳过并计数**，不会静默失败。
/// </summary>
public static class AudioFeatures
{
    /// <summary>特征维度：13 MFCC 均值 + 13 MFCC 标准差 + 5 谱特征均值 + 1 起音 = 32。</summary>
    public const int Dim = 32;

    /// <summary>**原生可解码**的扩展名（NWaves 直接支持）。</summary>
    // 🔴 **实测校正（重要）**：NWaves 的 `WaveFile` 【只支持 WAV / AIFF】——
        //   **不支持 OGG**（实测：5,817 个 .ogg 100% 提取失败）。
        //   旧声明里写了 "ogg" 是【错误的】，导致失败被错误归因成「文件损坏」。
        //   ⇒ 这里只保留真正能解的；`.ogg` / `.aif` 的失败是【格式不支持】，不是文件坏。
        //   ⚠️ `.aif` 实测也 100% 失败（160 个）—— NWaves 可能只认标准 AIFF 变体，先移除。
        // **实测支持**：WAV/AIFF（NWaves）+ **OGG/MP3（NAudio，2026-09-24 新增）**。
        //   `.ncw`/`.nkx` 不在这里（走 NcwDecoder / NkxFeatures）。
        public static readonly string[] DecodableExts = { "wav", "aiff", "aif", "ogg", "mp3" };

    /// <summary>
    /// **需要先解码才能读**的扩展名 —— Kontakt 专有压缩格式。
    /// `.ncw`（一期）：用 NcwDecoder 解成临时 WAV；
    /// `.nkx`（二期）：**容器** —— 走 NkxFeatures（按容器取代表采样聚合，见该类注释）。
    /// </summary>
    public static readonly string[] NeedsDecodeExts = { "ncw", "nkx" };

    /// <summary>判断某扩展名是否【原生】可解码。</summary>
    public static bool CanDecode(string ext)
        => DecodableExts.Contains((ext ?? "").TrimStart('.').ToLowerInvariant());

    /// <summary>判断某扩展名是否需要【先解码】（.ncw）。</summary>
    public static bool NeedsDecode(string ext)
        => NeedsDecodeExts.Contains((ext ?? "").TrimStart('.').ToLowerInvariant());

    /// <summary>判断某扩展名是否【最终可分析】（原生 + 需解码两级之和）。</summary>
    /// <summary>
    /// **统一入口：不管什么格式都能提【档 1 的 32 维】特征**（原生 + .ncw）。
    ///
    /// **🔴 为什么必须有这个函数**：之前 `AudioFeatureTask` 误用了 `MertFeatures.ExtractAny`
    /// —— 那个内部调的是 **MERT 模型（768 维）**，导致：
    ///   · **慢得要命**（跑神经网络 vs 纯 NWaves 计算）；
    ///   · **存进 audio_features 表的是 768 维向量**（维度错了，实测污染了 4,011 行）。
    /// ⇒ **档 1 必须走这个函数**（只做 NWaves 计算 + 必要时解码 .ncw）。
    /// </summary>
    public static float[]? ExtractAny(string fullPath, string ext, double maxSeconds = 12.0)
    {
        var e = (ext ?? "").TrimStart('.').ToLowerInvariant();
        if (CanDecode(e)) return Extract(fullPath, maxSeconds);

        if (NeedsDecode(e))
        {
            // .nkx 是容器 —— 档 1 不做容器解包（那是档 2 的 MertFeatures 路径）
            if (e == "nkx") return null;
            string? tmp = null;
            try
            {
                tmp = Path.Combine(Path.GetTempPath(), "klm_ncw1_" + Guid.NewGuid().ToString("N") + ".wav");
                if (NcwDecoder.DecodeToWav(fullPath, tmp, maxSeconds) <= 0) return null;
                return Extract(tmp, maxSeconds);
            }
            catch { return null; }
            finally { MertFeatures.DeleteWithRetry(tmp); }
        }
        return null;
    }

    public static bool CanAnalyze(string ext) => CanDecode(ext) || NeedsDecode(ext);

    /// <summary>
    /// **从一个音频文件提取特征**；失败返回 null（不抛异常 —— 批量跑时个别坏文件不该中断全局）。
    /// </summary>
    /// <param name="fullPath">音频文件绝对路径。</param>
    /// <param name="maxSeconds">最多分析开头多少秒（默认 12 秒 —— 足够刻画音色，又不用读完整文件）。</param>
    public static float[]? Extract(string fullPath, double maxSeconds = 12.0)
    {
        try
        {
            if (!File.Exists(fullPath)) return null;

            // **统一走 AudioDecoder** —— 它支持 WAV/AIFF（NWaves）+ OGG/MP3（NAudio）。
            //   旧实现直接用 NWaves 的 WaveFile，只认 WAV/AIFF ⇒ ogg/mp3 全失败。
            var dec = AudioDecoder.DecodeMono(fullPath, maxSeconds);
            if (dec == null) return null;
            var samples = dec.Value.Samples;

            // 只分析开头一段（长采样动辄几十 MB，全读没必要）
            int sr = dec.Value.SampleRate;
            int take = (int)Math.Min(samples.Length, Math.Max(sr, (int)(sr * maxSeconds)));
            if (take < sr / 10) return null;               // 短于 0.1 秒 → 没意义

            var seg = new float[take];
            Array.Copy(samples, seg, take);

            // 跳过开头的静音（否则 attack / RMS 会被前导空白带偏）
            int start = 0;
            float peak = seg.Max(Math.Abs);
            if (peak > 0)
            {
                float thr = peak * 0.02f;
                while (start < take - sr / 20 && Math.Abs(seg[start]) < thr) start++;
            }
            if (start > 0 && start < take)
            {
                var trimmed = new float[take - start];
                Array.Copy(seg, start, trimmed, 0, trimmed.Length);
                seg = trimmed;
            }

            var sig = new DiscreteSignal(sr, seg);
            var feats = new List<float>();

            // ── ① MFCC 13 维：取均值 + 标准差 ──
            var mfccOpts = new MfccOptions
            {
                SamplingRate = sr,
                FeatureCount = 13,
                FilterBankSize = 40,
                LowFrequency = 20,
                HighFrequency = sr / 2.0,
            };
            var mfcc = new MfccExtractor(mfccOpts);
            var mfccVecs = mfcc.ComputeFrom(sig);
            for (int k = 0; k < 13; k++)
            {
                var col = mfccVecs.Select(v => v[k]).ToList();
                feats.Add(col.Count > 0 ? col.Average() : 0f);
            }
            for (int k = 0; k < 13; k++)
            {
                var col = mfccVecs.Select(v => v[k]).ToList();
                feats.Add(col.Count > 1 ? (float)StdDev(col) : 0f);
            }

            // ── ② 谱特征（Multi 提取器一次算多个）+ 时域特征，各取前几维的均值 ──
            // 说明：NWaves 没有单独的 SpectralCentroidExtractor，谱质心/滚降/平坦度等
            //      由 Multi.SpectralFeaturesExtractor 一次性产出；时域的 ZCR/RMS 同理。
            var specVecs = TryCompute(new SpectralFeaturesExtractor(new MultiFeatureOptions { SamplingRate = sr, FftSize = 1024, HopSize = 512 }), sig);
            // 🔴 **量纲修正（重要）**：实测该提取器的前两维是【Hz 量纲】——
            //   spec[0] = 谱质心（如 1854 Hz）、spec[1] = 谱滚降（如 1537 Hz），
            //   而 spec[2]/spec[3] 是 0~1（谱平坦度 / 过零率）。
            //   若不修正，后面统一做的 tanh(x/10) 会让 Hz 量纲的维度【饱和到 1.0】
            //   （tanh(1854/10) = tanh(185.4) = 1.0）⇒ 实测导致「明亮度」「高频含量」
            //   两维全库恒为 1.0、百分位恒为 0、筛选永远命中 0（用户报的 bug）。
            //   ⇒ **先把 Hz 量纲的两维除以 Nyquist 归一到 0~1**，再交给 tanh。
            // 🔴 **列顺序是用合成信号探明的（`--specorder-test`），不是猜的** ——
            //   NWaves `SpectralFeaturesExtractor` 实际输出（共 14 列）：
            //     列 0 = 谱质心(Hz)  列 1 = 谱展宽(Hz)  列 2 = 谱平坦度  列 3 = 谱峰度
            //     **列 4 = 谱滚降(Hz)**  ……
            //   ⚠️ 早期版本只取列 0–3，且把【列 1（展宽）】当成「谱滚降」、
            //      【列 3（峰度）】当成「过零率」⇒ 真正的滚降根本没取。
            //      用户报「高频含量 ≥85% 筛出来全是 BASS」正是这个错误的表现。
            //   ⇒ 现在改为取：26=质心(列0)、**27=滚降(列4)**、28=平坦度(列2)、29=峰度(列3)
            float nyquist = Math.Max(1f, sr / 2f);
            foreach (int col in new[] { 0, 4, 2, 3 })
            {
                float v = ColMean(specVecs, col);
                if (col == 0 || col == 4) v /= nyquist;   // Hz 量纲的两列 → 0~1
                feats.Add(v);
            }

            var timeVecs = TryCompute(new TimeDomainFeaturesExtractor(new MultiFeatureOptions { SamplingRate = sr, FftSize = 1024, HopSize = 512 }), sig);
            feats.Add(ColMean(timeVecs, 0));

            // ── ③ 起音时间：**用 RMS 包络的上升时间**（不是全局峰值位置）──
            //   ⚠️ 原实现取「全局最大振幅样本的下标」—— 对【持续音/渐入音】极不可靠：
            //      PAD 的峰值可能出现在任意位置，导致「起音时间」变成随机数。
            //   改为：**先算 10ms 窗的 RMS 包络，再找「达到峰值 90% 所需时间」**，
            //      这才是「起音（attack）」的标准定义。
            feats.Add(AttackTime(seg, sr));

            if (feats.Count != Dim) return null;
            return Normalize(feats.ToArray());
        }
        catch { return null; }
    }

    /// <summary>跑一个 NWaves 提取器；失败返回空列表（个别文件/参数不支持时不该中断全局）。</summary>
    private static List<float[]> TryCompute(FeatureExtractor extractor, DiscreteSignal sig)
    {
        try { return extractor.ComputeFrom(sig) ?? new List<float[]>(); }
        catch { return new List<float[]>(); }
    }

    /// <summary>取所有帧的第 col 维均值（帧数不足或维度不足时返回 0）。</summary>
    private static float ColMean(List<float[]> vecs, int col)
    {
        if (vecs.Count == 0) return 0f;
        double sum = 0; int n = 0;
        foreach (var v in vecs) { if (v.Length > col) { sum += v[col]; n++; } }
        return n > 0 ? (float)(sum / n) : 0f;
    }

    /// <summary>
    /// **起音时间（秒）** —— 用 RMS 包络估计：10ms 窗求 RMS，再找「首次达到峰值 90%」的时间。
    /// **为什么不用「全局峰值位置」**：对持续音/PAD，峰值可能在任意位置，结果近似随机。
    /// **定义**：起音时间 = 从开始到能量达到峰值 90% 所经历的时间；**越小 = 打击感越强**。
    /// 返回上限 1.0 秒（超过就视为「无打击感」，不必再细分）。
    /// </summary>
    private static float AttackTime(float[] seg, int sr)
    {
        int win = Math.Max(1, sr / 100);                 // 10 ms
        int n = seg.Length / win;
        if (n < 2) return 1f;

        // ① RMS 包络
        var env = new float[n];
        float peak = 0f;
        for (int w = 0; w < n; w++)
        {
            double sum = 0;
            int b = w * win;
            for (int i = 0; i < win; i++) { float x = seg[b + i]; sum += x * x; }
            env[w] = (float)Math.Sqrt(sum / win);
            if (env[w] > peak) peak = env[w];
        }
        if (peak <= 1e-9f) return 1f;

        // ② 首次达到峰值 90% 的窗下标（从【能量起点】起算，跳过前导静音）
        float thr = peak * 0.9f;
        int start = 0;
        float sil = peak * 0.02f;                        // 2% 以下视为静音
        while (start < n && env[start] < sil) start++;
        for (int w = start; w < n; w++)
            if (env[w] >= thr) return Math.Min(1f, (float)(w - start) * win / sr);
        return 1f;
    }

    private static double StdDev(List<float> xs)
    {
        if (xs.Count < 2) return 0;
        double m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / (xs.Count - 1));
    }

    /// <summary>把特征归一化到可比范围（每维做 tanh 压缩，抑制量纲差异 —— 余弦相似度对尺度敏感）。</summary>
    private static float[] Normalize(float[] v)
    {
        var o = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            var x = v[i];
            if (float.IsNaN(x) || float.IsInfinity(x)) x = 0;
            o[i] = (float)Math.Tanh(x / 10.0);
        }
        return o;
    }

    /// <summary>余弦相似度（两个已归一化向量的点积）。</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na <= 0 || nb <= 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>把 float[] 打包成 BLOB（SQLite 存储用）。</summary>
    public static byte[] Pack(float[] v)
    {
        var b = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        return b;
    }

    /// <summary>从 BLOB 解包。</summary>
    public static float[]? Unpack(byte[]? b)
    {
        if (b == null || b.Length == 0 || b.Length % 4 != 0) return null;
        var v = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }
}
