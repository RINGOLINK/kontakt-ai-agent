using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **乐器级特征聚合**（音色地图「三期」）。
///
/// **为什么需要它**：一个音色库的「音色」由**几十上百个采样**构成
/// （不同力度、不同音高）。**单独分析一个采样 ≠ 分析一个音色。**
/// 用户问「找听起来像 Cymbals 的**音色**」时，想要的是**乐器**，而不是某个采样文件。
///
/// **🎯 核心设计：不重新跑模型，而是【聚合已有特征】**
///   采样级特征（MERT 768 维）已经提好了 ⇒ **乐器级特征 = 该乐器目录下所有采样特征的均值向量**。
///   **零额外推理成本**，只做向量平均 + L2 归一化。
///
/// **「乐器」怎么界定（不解析 NKI 二进制）**：
///   用**采样所在的【父目录】**近似 —— 采样通常按乐器分目录
///   （如 `DILRUBA\Dilruba KEY*.wav` 都在同一目录下）。
///   这**避开了「解析 NKI 格式取出采样引用列表」这个大工程**，用目录结构就能得到很好的近似。
/// </summary>
public static class InstrumentAggregate
{
    /// <summary>一个「乐器」（= 一个采样目录）的聚合结果。</summary>
    public sealed class Instrument
    {
        public long LibraryId { get; set; }
        /// <summary>乐器目录的相对路径（聚合键）。</summary>
        public string RelDir { get; set; } = "";
        /// <summary>显示名（取目录最后一段）。</summary>
        public string Name { get; set; } = "";
        /// <summary>聚合后的特征向量（组内均值的 L2 归一化）。</summary>
        public float[] Vec { get; set; } = Array.Empty<float>();
        /// <summary>参与聚合的采样数（**供界面如实显示「这个乐器由几个采样代表」**）。</summary>
        public int SampleCount { get; set; }
    }

    /// <summary>聚合进度回调：`(已处理组数, 总组数)`。</summary>
    public delegate void ProgressHandler(int done, int total);

    /// <summary>
    /// **把采样级特征聚合成乐器级**。
    /// </summary>
    /// <param name="db">索引库。</param>
    /// <param name="onProgress">进度（按组）。</param>
    /// <param name="isCancelled">返回 true 时中止。</param>
    public static List<Instrument>? Aggregate(Database db, ProgressHandler? onProgress = null, Func<bool>? isCancelled = null)
    {
        // ① 取全部采样特征 + 它们的路径（用来算「父目录」）
        var feats = db.LoadAllMertFeatures();
        if (feats.Count == 0) return null;

        var pathOf = new Dictionary<long, (long LibId, string RelPath)>();
        foreach (var lib in db.GetLibraries())
        {
            try
            {
                foreach (var c in db.GetAnalyzableClips(lib.Id))
                    pathOf[c.Id] = (lib.Id, c.RelPath);
            }
            catch { }
        }

        // ② 按 (库, 父目录) 分组
        var groups = new Dictionary<string, (long LibId, string RelDir, List<float[]> Vecs)>();
        foreach (var (clipId, libId, vec) in feats)
        {
            if (!pathOf.TryGetValue(clipId, out var p)) continue;
            var relDir = ParentDir(p.RelPath);
            var key = p.LibId + "|" + relDir;
            if (!groups.TryGetValue(key, out var g))
            {
                g = (p.LibId, relDir, new List<float[]>());
                groups[key] = g;
            }
            g.Vecs.Add(vec);
        }
        if (groups.Count == 0) return null;

        // ③ 逐组求均值 + L2 归一化
        var result = new List<Instrument>();
        int done = 0;
        foreach (var kv in groups)
        {
            if (isCancelled != null && isCancelled()) return null;

            var (libId, relDir, vecs) = kv.Value;
            int dim = vecs[0].Length;
            var usable = vecs.Where(v => v.Length == dim).ToList();
            if (usable.Count == 0) continue;

            var mean = new float[dim];
            foreach (var v in usable)
                for (int i = 0; i < dim; i++) mean[i] += v[i];
            for (int i = 0; i < dim; i++) mean[i] /= usable.Count;

            result.Add(new Instrument
            {
                LibraryId = libId,
                RelDir = relDir,
                Name = LeafName(relDir),
                Vec = L2(mean),
                SampleCount = usable.Count,
            });

            done++;
            if (done % 20 == 0) onProgress?.Invoke(done, groups.Count);
        }
        onProgress?.Invoke(done, groups.Count);
        return result;
    }

    /// <summary>取相对路径的父目录（去掉文件名）。无目录时返回 `(根)`。</summary>
    public static string ParentDir(string relPath)
    {
        var s = (relPath ?? "").Replace('/', '\\').Trim('\\');
        int i = s.LastIndexOf('\\');
        return i > 0 ? s[..i] : "(根)";
    }

    /// <summary>取目录的最后一段作为显示名。</summary>
    public static string LeafName(string relDir)
    {
        var s = (relDir ?? "").Trim('\\');
        int i = s.LastIndexOf('\\');
        return i >= 0 && i + 1 < s.Length ? s[(i + 1)..] : s;
    }

    private static float[] L2(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += (double)x * x;
        n = Math.Sqrt(n);
        if (n <= 0) return v;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }
}
