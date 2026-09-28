using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **`.nkx` 容器的特征提取**（音色地图「二期」）。
///
/// **`.nkx` 是什么**：NI 的单块归档容器 —— **一个文件装一整个乐器的采样**，
/// 实测本机 1,872 个容器、单个可达 **2 GB**（如 `Samples\Cinematic Guitars_001.nkx` = 2012 MB）。
///
/// **🔴 设计上必须避开的两件事**：
///
///   **① 绝不能「每个采样都重新解包」** —— 解包要读容器目录 + 建 XOR 掩码，
///   实测约 0.8 秒/次；一个容器里有几百上千个采样，那开销会乘几千倍。
///   ⇒ **正确做法：`ReadTree` + `BuildXorMask` 各做一次，然后复用 `FileStream` 连续取多个采样。**
///
///   **② 不可能「分析全部采样」** —— 1,872 容器 × 几百个采样 ≈ 十万级，
///   按 0.78 秒/个要 **20+ 小时**，不可接受。
///   ⇒ **正确做法：每个容器只取【有限个代表采样】（默认 8 个），
///   把它们的 MERT 嵌入【求均值】作为【该容器的一个特征】**。
///
/// **为什么「一个容器一个特征」是合理的粒度**：
///   容器本身通常就对应**一个乐器**的采样池（`Cinematic Guitars_001.nkx`），
///   所以「容器级聚合」**天然就是「乐器级」** —— 正好符合「找相似音色」的直觉。
///
/// **⚠️ 前提**：解包**需要注册表里的密钥**（`JDX` = AES 密钥 / `HU` = IV）。
/// 没有注册表条目（未入库的库）就解不开 —— 会**如实返回 null 并计入跳过**，不假装成功。
/// </summary>
public static class NkxFeatures
{
    /// <summary>每个容器最多取多少个代表采样（**控制总耗时的关键参数**）。</summary>
    public const int DefaultSamplesPerContainer = 8;

    /// <summary>一次提取的结果。</summary>
    public sealed class Result
    {
        /// <summary>聚合后的特征（已 L2 归一化）；失败为 null。</summary>
        public float[]? Vec { get; set; }
        /// <summary>容器内的采样总数。</summary>
        public int TotalSamples { get; set; }
        /// <summary>实际成功提特征的采样数。</summary>
        public int UsedSamples { get; set; }
        /// <summary>失败原因（成功时为空）。</summary>
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// **从一个 `.nkx` 容器提取聚合特征**。
    /// </summary>
    /// <param name="nkxPath">容器绝对路径。</param>
    /// <param name="regKey">该库的注册表键名（**用来取 JDX/HU 密钥**）。</param>
    /// <param name="maxSamples">最多取几个代表采样。</param>
    /// <param name="modelPath">MERT 模型路径。</param>
    /// <param name="secondsPerSample">每个采样最多分析几秒。</param>
    public static Result Extract(string nkxPath, string regKey, int maxSamples = DefaultSamplesPerContainer,
                                 string? modelPath = null, double secondsPerSample = 10.0, bool useGpu = false, int threads = 0)
    {
        var r = new Result();
        if (!File.Exists(nkxPath)) { r.Error = "容器不存在"; return r; }

        // ① 密钥（**一次**）
        byte[]? mask = null;
        try
        {
            var k = NkxCrypto.LoadKey(regKey ?? "");
            if (k != null) mask = NkxCrypto.BuildXorMask(k.Value.key, k.Value.iv);
        }
        catch { }
        if (mask == null)
        {
            r.Error = "拿不到解包密钥（注册表里没有该库的 JDX/HU）—— 未入库的库解不开";
            return r;
        }

        // ② 目录树（**一次**）
        NkxItem? tree;
        try { tree = NkxReader.ReadTree(nkxPath); }
        catch { tree = null; }
        if (tree == null) { r.Error = "读容器目录失败"; return r; }

        // ③ 收集容器内的音频文件（按名称排序，保证【可复现】；只取 .ncw/.wav）
        var files = tree.Flatten()
            .Where(f => f.Name.EndsWith(".ncw", StringComparison.OrdinalIgnoreCase)
                     || f.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        r.TotalSamples = files.Count;
        if (files.Count == 0) { r.Error = "容器里没有音频文件"; return r; }

        // ④ **均匀取样**（而不是只取前 N 个）—— 前 N 个可能全是同一力度/音高，
        //    均匀取样能覆盖更多变化，聚合出的特征更能代表「这个乐器」。
        var picked = UniformPick(files, Math.Max(1, maxSamples));

        // ⑤ 逐个解包 + 解码 + 提特征（**复用同一个 FileStream，避免重复打开 2 GB 文件**）
        var vecs = new List<float[]>();
        // **逐步骤失败原因**（只记前 3 条，避免刷屏）—— 便于诊断
        //   「容器内采样全部提取失败」到底卡在【解包 / 解码 / 推理】哪一步。
        var stepErrors = new List<string>();
        void NoteStep(string step, string why)
        {
            if (stepErrors.Count < 3) stepErrors.Add(step + "：" + why);
        }
        try
        {
            using var fs = File.OpenRead(nkxPath);
            foreach (var f in picked)
            {
                string? tmpNcw = null, tmpWav = null;
                try
                {
                    tmpNcw = Path.Combine(Path.GetTempPath(), "klm_nkx_" + Guid.NewGuid().ToString("N") + ".ncw");
                    long ex = 0;
                    using (var of = File.Create(tmpNcw))
                        ex = NkxReader.ExtractFile(fs, f, mask, of);
                    if (ex <= 0) { NoteStep("解包", f.Name + " ExtractFile 返回 " + ex); continue; }

                    tmpWav = Path.Combine(Path.GetTempPath(), "klm_nkx_" + Guid.NewGuid().ToString("N") + ".wav");
                    long dw = NcwDecoder.DecodeToWav(tmpNcw, tmpWav, secondsPerSample);
                    if (dw <= 0) { NoteStep("解码", f.Name + " DecodeToWav 返回 " + dw); continue; }

                    var v = MertFeatures.Extract(tmpWav, secondsPerSample, modelPath, useGpu, threads);
                    if (v != null) vecs.Add(v);
                    else NoteStep("推理", f.Name + " MERT 返回 null");
                }
                catch { /* 单个采样失败不中断容器 */ }
                finally
                {
                    // **必须删临时文件** —— 十万级采样会把磁盘撑爆
                    MertFeatures.DeleteWithRetry(tmpNcw);
                    MertFeatures.DeleteWithRetry(tmpWav);
                }
            }
        }
        catch (Exception ex) { r.Error = "读容器失败：" + ex.Message; return r; }

        if (vecs.Count == 0)
        {
            r.Error = "容器内采样全部提取失败"
                    + (stepErrors.Count > 0 ? "｜" + string.Join(" ; ", stepErrors) : "");
            return r;
        }

        // ⑥ 聚合：均值 + L2 归一化
        r.UsedSamples = vecs.Count;
        int dim = vecs[0].Length;
        var mean = new float[dim];
        int n = 0;
        foreach (var v in vecs)
        {
            if (v.Length != dim) continue;
            for (int i = 0; i < dim; i++) mean[i] += v[i];
            n++;
        }
        if (n == 0) { r.Error = "维度不一致"; return r; }
        for (int i = 0; i < dim; i++) mean[i] /= n;
        r.Vec = L2(mean);
        return r;
    }

    /// <summary>从列表里【均匀】挑 k 个（而不是只取前 k 个）。</summary>
    public static List<T> UniformPick<T>(List<T> src, int k)
    {
        if (src.Count <= k) return new List<T>(src);
        var outp = new List<T>(k);
        double step = (double)src.Count / k;
        for (int i = 0; i < k; i++)
        {
            int idx = (int)(i * step);
            if (idx >= src.Count) idx = src.Count - 1;
            outp.Add(src[idx]);
        }
        return outp;
    }

    private static float[] L2(float[] v)
    {
        double s = 0;
        foreach (var x in v) s += (double)x * x;
        s = Math.Sqrt(s);
        if (s <= 0) return v;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / s);
        return v;
    }
}
