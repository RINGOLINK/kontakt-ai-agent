using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace KontaktLibManager.Core;

/// <summary>
/// **MERT 音频嵌入**（调研报告「远期」第 7 项 · **档 2**）—— 512/768 维自监督语义嵌入。
///
/// **为什么需要它**：档 1 用的是 **32 维手工声学特征**（MFCC + 谱 + 时域），
/// 它只能刻画「亮度 / 噪声度 / 响度」这几个粗糙维度，**抓不住「音色语义」**。
/// 而 MERT（Music undERstanding model with large-scale self-supervised Training）
/// 是**自监督学出来的语义空间** —— 同类乐器天然聚在一起，**UMAP 在这种空间上效果显著更好**。
///
/// **模型来源**：`xycld/music-align-mert` 的 **`mert_uint8.onnx`**（117 MB，动态 UINT8 量化版）
/// —— 是官方 `m-a-p/MERT-v1-95M` 的 ONNX 导出。
///
/// **🔴 两个必须如实告知的限制**：
///   1. **许可是 `CC-BY-NC-4.0`（非商业）** —— **本工具若要商用/分发，需先解决许可问题**。
///   2. **该 ONNX 是【第三方】导出，HuggingFace 上【下载量为 0】** ——
///      **几乎无人验证过**，所以必须先用已知相似的音频验证输出是否合理（见 `--mert-test`）。
///
/// **预处理（与档 1 的关键差别）**：MERT 基于 wav2vec2 架构，要求
///   · **单声道**
///   · **16 kHz 采样率**（档 1 是直接读原始采样率，这里必须重采样）
/// 输入张量名 `input_values`，形状 `[1, N]`；输出 `last_hidden_state`，形状 `[1, frames, 768]`。
/// 我们对时间轴做**均值池化**得到定长嵌入（这就是「整段音频的音色指纹」）。
/// </summary>
public static class MertFeatures
{
    /// <summary>MERT 要求的采样率。</summary>
    public const int SampleRate = 16000;

    /// <summary>默认模型路径。</summary>
    public static string DefaultModelPath => Path.Combine(AppPaths.DataDir, "mert", "mert_uint8.onnx");

    private static InferenceSession? _session;
    private static string _loadedPath = "";

    /// <summary>模型是否已就位。</summary>
    public static bool Available(string? path = null)
        => File.Exists(path ?? DefaultModelPath);

    private static bool _loadedGpu;
    private static int _loadedThreads = -1;

    /// <summary>
    /// **惰性加载会话**（加载一次约 1–3 秒，之后复用）。
    ///
    /// **⚠️ 缓存键含 (path, useGpu, threads)** —— 换模式必须重建会话，
    /// 否则会出现「勾了 GPU 但还在用 CPU 会话」的假象。
    /// </summary>
    /// <param name="useGpu">true = 用 DirectML（GPU）；**失败会自动回退 CPU 并在日志说明**。</param>
    /// <param name="threads">CPU 模式的线程数（0 = 自动，留 2 核给系统）。GPU 模式忽略。</param>
    public static InferenceSession? GetSession(string path, bool useGpu = false, int threads = 0)
    {
        int th = threads <= 0 ? Math.Max(1, Environment.ProcessorCount - 2) : threads;
        if (_session != null && _loadedPath == path && _loadedGpu == useGpu && _loadedThreads == th)
            return _session;
        try
        {
            // **多片段并行时，算子内并行要降到 1–2** —— 否则【线程超订】会让整体更慢
            // （th 个片段 × th 个算子线程 = th² 个线程在抢 CPU）。
            // 经验值：并行片段数 >= 4 时，IntraOp 用 1；否则给 2。
            int intraOp = th >= 4 ? 1 : Math.Max(1, Math.Min(2, th));
            var opts = new SessionOptions
            {
                IntraOpNumThreads = intraOp,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            if (useGpu && !GpuKnownBroken)
            {
                try
                {
                    // **DirectML**：Windows 通用、不挑显卡厂商与驱动版本（比 CUDA 轻得多）
                    opts.AppendExecutionProvider_DML(0);
                }
                catch (Exception ex)
                {
                    // **回退 CPU** —— 不静默，让调用方能看到原因
                    LastGpuError = "DirectML 不可用，已回退 CPU：" + ex.Message;
                    LastGpuUsed = false;
                }
            }

            _session = new InferenceSession(path, opts);
            _loadedPath = path;
            _loadedGpu = useGpu;
            _loadedThreads = th;
            // ⚠️ **不在这里标记 LastGpuUsed** —— DirectML 可能「附加成功但推理失败」
            //    （实测 MERT 的 Reshape 节点在 DML 上直接报错）。
            //    真正的判定放在第一次推理成功之后（见 MarkGpuInferenceOk）。
            return _session;
        }
        catch { return null; }
    }

    /// <summary>上一次加载是否真的用上了 GPU（**供界面如实显示，不假装**）。</summary>
    public static bool LastGpuUsed { get; private set; }
    /// <summary>GPU 相关的错误/回退说明（空 = 没问题）。</summary>
    public static string LastGpuError { get; private set; } = "";

    /// <summary>
    /// **DirectML 是否已被实测证明不可用**（一旦推理失败就置 true，之后不再尝试 GPU）。
    ///
    /// **为什么要这个**：实测 **MERT 模型在 DirectML 上【附加 EP 成功但推理失败】**
    /// （`Reshape` 节点报错 —— 这是 DirectML 对动态形状的已知限制）。
    /// 所以「能不能用 GPU」**必须靠【真实推理】判定，不能靠 EP 附加是否成功**。
    /// </summary>
    public static bool GpuKnownBroken { get; private set; }

    /// <summary>推理成功后调用：确认 GPU 真的可用。</summary>
    public static void MarkGpuInferenceOk() { LastGpuUsed = true; LastGpuError = ""; }

    /// <summary>推理失败后调用（仅 GPU 模式）：标记 GPU 不可用并给出原因。</summary>
    public static void MarkGpuInferenceFailed(string why)
    {
        GpuKnownBroken = true;
        LastGpuUsed = false;
        LastGpuError = "GPU（DirectML）推理失败，已回退 CPU：" + why;
        ResetSession();
    }

    /// <summary>清掉会话缓存（换 GPU/CPU 模式时用）。</summary>
    public static void ResetSession() { _session = null; _loadedPath = null; _loadedGpu = false; _loadedThreads = -1; }

    /// <summary>描述模型的输入/输出签名（供自检与排错）。</summary>
    public static string DescribeModel(string? path = null)
    {
        var p = path ?? DefaultModelPath;
        var s = GetSession(p);
        if (s == null) return $"无法加载模型：{p}";
        var ins = s.InputMetadata.Select(kv => $"{kv.Key} [{string.Join(",", kv.Value.Dimensions)}] {kv.Value.ElementType}");
        var outs = s.OutputMetadata.Select(kv => $"{kv.Key} [{string.Join(",", kv.Value.Dimensions)}] {kv.Value.ElementType}");
        return "输入：" + string.Join(" | ", ins) + "\n输出：" + string.Join(" | ", outs);
    }

    /// <summary>
    /// **从音频文件提取 MERT 嵌入**；失败返回 null（不抛异常）。
    /// </summary>
    /// <param name="fullPath">音频文件路径（wav/aif/ogg）。</param>
    /// <param name="maxSeconds">最多分析开头多少秒（默认 10 秒 —— MERT 上下文有限，且够刻画音色）。</param>
    private static float[]? ExtractCore(string fullPath, double maxSeconds = 10.0, string? modelPath = null, bool useGpu = false, int threads = 0)
    {
        bool gpuMode = useGpu && !GpuKnownBroken;
        var mp = modelPath ?? DefaultModelPath;
        // **带 useGpu/threads** —— 会话缓存键含这两项，换模式会重建
        var sess = GetSession(mp, useGpu, threads);
        if (sess == null) return null;

        // ① 读音频 → 16 kHz 单声道
        float[] mono;
        try { mono = ReadMono16k(fullPath, maxSeconds); }
        catch (Exception ex) { if (gpuMode) MarkGpuInferenceFailed(ex.Message); return null; }
        if (mono.Length < SampleRate / 10) return null;      // 短于 0.1 秒没意义

        // ② 跑推理
        try
        {
            var inputName = sess.InputMetadata.Keys.First();
            var tensor = new DenseTensor<float>(mono, new[] { 1, mono.Length });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
            using var results = sess.Run(inputs);

            // ③ 取输出并沿时间轴均值池化
            var first = results.First();
            var t = first.AsTensor<float>();
            var dims = t.Dimensions.ToArray();
            if (dims.Length == 3)          // [1, frames, hidden]
            {
                int frames = dims[1], hid = dims[2];
                var emb = new float[hid];
                for (int f = 0; f < frames; f++)
                    for (int h = 0; h < hid; h++)
                        emb[h] += t[0, f, h];
                for (int h = 0; h < hid; h++) emb[h] /= Math.Max(1, frames);
                if (gpuMode) MarkGpuInferenceOk();
                return L2(emb);
            }
            if (dims.Length == 2)          // [1, hidden]（已池化）
            {
                int hid = dims[1];
                var emb = new float[hid];
                for (int h = 0; h < hid; h++) emb[h] = t[0, h];
                return L2(emb);
            }
            return null;
        }
        catch { return null; }
    }
    /// <summary>
    /// **从音频文件提取 MERT 嵌入**；失败返回 null（不抛异常）。
    ///
    /// **GPU 失败自动回退 CPU 并重试**（实测 MERT 的 Reshape 节点在 DirectML 上会失败）：
    /// 第一次尝试 GPU；失败后标记 GPU 不可用，**立刻用 CPU 重跑一次**（而不是直接返回失败）。
    /// </summary>
    public static float[]? Extract(string fullPath, double maxSeconds = 10.0, string? modelPath = null, bool useGpu = false, int threads = 0)
    {
        if (useGpu && !GpuKnownBroken)
        {
            var v = ExtractCore(fullPath, maxSeconds, modelPath, true, threads);
            if (v != null) return v;
            // 失败 —— MarkGpuInferenceFailed 已在 ExtractCore 里调用（标记 + 清会话）
            // **立刻用 CPU 重试**，别让用户白等
        }
        return ExtractCore(fullPath, maxSeconds, modelPath, false, threads);
    }


    /// <summary>
    /// **统一入口：不管什么格式都能提特征**（一期：原生 + .ncw）。
    ///
    /// .ncw 的处理：**先解码成临时 WAV，提完立刻删除** ——
    /// 临时文件必须删，否则两万多个片段会把磁盘撑爆。
    /// 解码用已有的 NcwDecoder（与「试听」功能同一套，已实测通过）。
    /// </summary>
    public static float[]? ExtractAny(string fullPath, string ext, double maxSeconds = 10.0, string? modelPath = null, bool useGpu = false, int threads = 0)
    {
        var e = (ext ?? "").TrimStart('.').ToLowerInvariant();
        if (AudioFeatures.CanDecode(e))
            return Extract(fullPath, maxSeconds, modelPath, useGpu, threads);

        if (AudioFeatures.NeedsDecode(e))
        {
            string? tmp = null;
            try
            {
                tmp = Path.Combine(Path.GetTempPath(), "klm_ncw_" + Guid.NewGuid().ToString("N") + ".wav");
                long written = NcwDecoder.DecodeToWav(fullPath, tmp, maxSeconds);
                if (written <= 0 || !File.Exists(tmp)) return null;
                return Extract(tmp, maxSeconds, modelPath, useGpu, threads);
            }
            catch { return null; }
            finally
            {
                // **必须删临时文件** —— 且要【重试】：
                // 实测偶发残留 1 个，原因是 File.Delete 时 NWaves 的读取句柄还没释放（Windows 文件锁时序）。
                DeleteWithRetry(tmp);
            }
        }
        return null;
    }

    /// <summary>
    /// **带重试地删除临时文件** —— 实测直接 File.Delete 偶发失败（句柄还没释放）。
    /// 重试 5 次、每次间隔 120ms，基本能清干净；仍失败就**留给系统临时目录**（不阻塞主流程）。
    /// </summary>
    internal static void DeleteWithRetry(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        for (int i = 0; i < 5; i++)
        {
            try { if (!File.Exists(path)) return; File.Delete(path); return; }
            catch { System.Threading.Thread.Sleep(120); }
        }
    }

    /// <summary>L2 归一化（余弦相似度用）。</summary>
    private static float[] L2(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += (double)x * x;
        n = Math.Sqrt(n);
        if (n <= 0) return v;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }

    /// <summary>
    /// 读音频文件并转成 **16 kHz 单声道 float[]**（范围约 [-1,1]）。
    /// 复用 NWaves 解码（与档 1 同一套），再做**线性插值重采样**。
    /// </summary>
    public static float[] ReadMono16k(string fullPath, double maxSeconds)
    {
        // **统一走 AudioDecoder** —— 支持 WAV/AIFF（NWaves）+ OGG/MP3（NAudio）。
        var dec = AudioDecoder.DecodeMono(fullPath, maxSeconds);
        if (dec == null) return Array.Empty<float>();
        var src = dec.Value.Samples;
        int srcRate = dec.Value.SampleRate;

        int take = (int)Math.Min(src.Length, Math.Max(srcRate, srcRate * maxSeconds));
        if (take <= 0) return Array.Empty<float>();

        // 重采样：线性插值到 16 kHz
        if (srcRate == SampleRate)
        {
            var same = new float[take];
            Array.Copy(src, same, take);
            return Normalize(same);
        }
        int outLen = (int)((long)take * SampleRate / srcRate);
        if (outLen <= 0) return Array.Empty<float>();
        var outp = new float[outLen];
        double ratio = (double)srcRate / SampleRate;
        for (int i = 0; i < outLen; i++)
        {
            double pos = i * ratio;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, take - 1);
            double frac = pos - i0;
            outp[i] = (float)(src[i0] * (1 - frac) + src[i1] * frac);
        }
        return Normalize(outp);
    }

    /// <summary>把峰值归一到 ~0.95（避免不同录音电平造成嵌入差异）。</summary>
    private static float[] Normalize(float[] x)
    {
        float peak = 0;
        foreach (var v in x) { var a = Math.Abs(v); if (a > peak) peak = a; }
        if (peak <= 1e-6f) return x;
        float g = 0.95f / peak;
        for (int i = 0; i < x.Length; i++) x[i] *= g;
        return x;
    }

    /// <summary>余弦相似度（两个已 L2 归一化的向量点积）。</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return dot;
    }
}
