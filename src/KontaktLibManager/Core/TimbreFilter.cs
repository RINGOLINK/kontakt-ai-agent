using System;
using System.Collections.Generic;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **音色可解释筛选引擎**（用户问题 ①「混合筛选」的底层）。
///
/// **为什么需要它 —— 混合筛选的动机**：
///   · **MERT（档 2）768 维**：语义强、但**完全不可解释**（没人知道第 137 维代表什么）
///     ⇒ 只能回答「哪个和哪个像」；
///   · **手工声学特征（档 1）32 维**：语义弱、但**其中 6 维是可解释的**
///     ⇒ 能回答「**我要明亮、有打击感、噪声多的**」这类**可描述的需求**。
///
/// **⇒ 混合用法**：**用 MERT 找「听起来像」，用本引擎做「按可解释特征筛」**。
///
/// **📐 32 维布局（`AudioFeatures.Dim = 32`）**：
///   | 下标 | 含义 | 可解释 |
///   |---|---|---|
///   | 0–12  | MFCC 13 维**均值**   | ❌ |
///   | 13–25 | MFCC 13 维**标准差** | ❌ |
///   | **26** | **谱质心**（Spectral Centroid）→ **明亮度** | ✅ |
///   | **27** | **谱滚降**（Rolloff）→ **高频含量** | ✅ |
///   | **28** | **谱平坦度**（Flatness）→ **噪声感**（打击/气声） | ✅ |
///   | **29** | **过零率**（ZCR）→ **粗糙度** | ✅ |
///   | **30** | **RMS 能量** → **响度** | ✅ |
///   | **31** | **起音时间 attack** → **打击感 vs 渐入** | ✅ |
///
/// **⚠️ 量纲问题（重要）**：这 6 维的**原始量纲差异极大**（谱质心可能是几千 Hz，
/// 而 RMS 是 0~1）。**直接给用户拖滑杆会很难用**，所以本引擎提供
/// **【按全体样本的百分位归一化到 0~1】** —— 滑杆上的 0.7 表示「比 70% 的样本更亮」，
/// **这是人能理解的说法**，也避免了「明亮度阈值该设多少」这种无法回答的问题。
/// </summary>
public static class TimbreFilter
{
    /// <summary>可解释维度的下标（与 <see cref="AudioFeatures.Dim"/> 对应）。</summary>
    public static class Dim
    {
        public const int SpectralCentroid = 26;   // 明亮度
        public const int SpectralRolloff = 27;    // 高频含量（**真·谱滚降**，已修正）
        public const int SpectralFlatness = 28;   // 噪声感
        public const int SpectralCrest = 29;      // 尖锐度（原误标为「过零率」）
        public const int Rms = 30;                // 响度（时域列）
        public const int Attack = 31;             // 打击感（越小越"打击"）
    }

    /// <summary>一个可解释维度的元信息（供界面生成滑杆 / 供 Agent 理解）。</summary>
    public sealed class DimInfo
    {
        public string Key { get; set; } = "";        // 机器名（Agent 用）
        public string Label { get; set; } = "";      // 中文名（界面用）
        public string Meaning { get; set; } = "";    // 含义说明
        public int Index { get; set; }               // 在 32 维里的下标
        /// <summary>true = 值越大越「强」（如明亮度）；false = 越大越「弱/慢」（如 attack 越大越不打击）。</summary>
        /// <summary>原始值越大是否代表该特征越强。**false 的维度会在 Normalize 里把百分位反转**，
    /// 使 <see cref="Row.Pct"/> 统一表示【语义强度】（越大越强）。</summary>
    public bool HigherIsStronger { get; set; } = true;
    }

    /// <summary>6 个可解释维度的定义（界面与 Agent 共用）。</summary>
    public static readonly DimInfo[] Dims =
    {
        new() { Key = "brightness", Label = "明亮度",   Meaning = "**谱质心**（Hz 归一）—— 越高越明亮/越尖", Index = Dim.SpectralCentroid },
        new() { Key = "highFreq",   Label = "高频延展", Meaning = "**谱滚降**：能量累积到 85% 时所在的频率 —— 衡量【声音的能量向上延伸到多高】。高 = 大量能量分布在很高频段（镲片/气声/明亮弦乐）；低 = 能量主要压在低频（底鼓/贝斯基音）。⚠️ 这【不是】「高频占多少比例」—— 低音也可以有高频泛音，但那部分能量占比小，所以滚降值仍然低。", Index = Dim.SpectralRolloff },
        new() { Key = "noisiness",  Label = "噪声感",   Meaning = "**谱平坦度** —— 越高越像噪声/气声/打击", Index = Dim.SpectralFlatness },
        new() { Key = "roughness",  Label = "尖锐度",   Meaning = "**谱峰度** —— 越高越尖锐/越有峰值突出", Index = Dim.SpectralCrest },
        new() { Key = "loudness",   Label = "响度",     Meaning = "RMS 能量 —— 越高越响", Index = Dim.Rms },
        new() { Key = "punch",      Label = "打击感",   Meaning = "起音时间倒数 —— 越高越「打击」、越低越「渐入」", Index = Dim.Attack, HigherIsStronger = false },
    };
    /// <summary>筛选条件：每个维度给一个 0~1 的**百分位区间**（null = 不限）。</summary>
    public sealed class Filter
    {
        public float? BrightnessMin, BrightnessMax;
        public float? HighFreqMin, HighFreqMax;
        public float? NoisinessMin, NoisinessMax;
        public float? RoughnessMin, RoughnessMax;
        public float? LoudnessMin, LoudnessMax;
        public float? PunchMin, PunchMax;

        public bool IsEmpty =>
            BrightnessMin == null && BrightnessMax == null && HighFreqMin == null && HighFreqMax == null &&
            NoisinessMin == null && NoisinessMax == null && RoughnessMin == null && RoughnessMax == null &&
            LoudnessMin == null && LoudnessMax == null && PunchMin == null && PunchMax == null;

        /// <summary>按 key 取区间（Agent 用字符串 key 传参）。</summary>
        public (float? Min, float? Max) Get(string key) => key switch
        {
            "brightness" => (BrightnessMin, BrightnessMax),
            "highFreq" => (HighFreqMin, HighFreqMax),
            "noisiness" => (NoisinessMin, NoisinessMax),
            "roughness" => (RoughnessMin, RoughnessMax),
            "loudness" => (LoudnessMin, LoudnessMax),
            "punch" => (PunchMin, PunchMax),
            _ => (null, null),
        };

        public void Set(string key, float? min, float? max)
        {
            switch (key)
            {
                case "brightness": BrightnessMin = min; BrightnessMax = max; break;
                case "highFreq": HighFreqMin = min; HighFreqMax = max; break;
                case "noisiness": NoisinessMin = min; NoisinessMax = max; break;
                case "roughness": RoughnessMin = min; RoughnessMax = max; break;
                case "loudness": LoudnessMin = min; LoudnessMax = max; break;
                case "punch": PunchMin = min; PunchMax = max; break;
            }
        }
    }

    /// <summary>一条候选（clipId + 已归一化到 0~1 的 6 维）。</summary>
    public sealed class Row
    {
        public long ClipId { get; set; }
        public long LibraryId { get; set; }
        /// <summary>6 维百分位值（0~1），顺序与 <see cref="Dims"/> 一致。</summary>
        public float[] Pct { get; set; } = Array.Empty<float>();
        public float[] Raw { get; set; } = Array.Empty<float>();
    }

    /// <summary>
    /// **把原始 32 维转成「按全体样本百分位归一化」的 6 维**（0~1）。
    ///
    /// **为什么必须归一化**：6 个维度量纲差异极大（谱质心可能几千、RMS 是 0~1），
    /// 直接让用户拖滑杆是没法用的。百分位化之后，**0.7 = 「比 70% 的样本更亮」** —— 人能理解。
    /// </summary>
    public static List<Row> Normalize(List<(long ClipId, long LibraryId, float[] Vec)> raw)
    {
        var rows = new List<Row>();
        if (raw.Count == 0) return rows;

        // 逐维收集原始值 → 排序 → 二分求百分位
        var sorted = new List<float>[Dims.Length];
        for (int d = 0; d < Dims.Length; d++) sorted[d] = new List<float>(raw.Count);
        foreach (var (_, _, v) in raw)
            for (int d = 0; d < Dims.Length; d++)
                sorted[d].Add(v.Length > Dims[d].Index ? v[Dims[d].Index] : 0f);
        for (int d = 0; d < Dims.Length; d++) sorted[d].Sort();

        foreach (var (clipId, libId, v) in raw)
        {
            var pct = new float[Dims.Length];
            var rawv = new float[Dims.Length];
            for (int d = 0; d < Dims.Length; d++)
            {
                float x = v.Length > Dims[d].Index ? v[Dims[d].Index] : 0f;
                rawv[d] = x;
                pct[d] = Percentile(sorted[d], x);

                // 🔴 **语义反转（重要修复）** ——
                //   有些维度的【原始值越小越强】（如「打击感」的 raw 是【起音时间】：
                //   峰值来得早 = 打击感强 = raw 小）。若不做这步，下游全都会反：
                //     · 筛选「打击感 ≥ 80%」实际筛出的是【起音晚】的 PAD ❌
                //     · 地图着色把「起音晚」画成红色（高）❌
                //   实测症状（用户报）：一个强劲打击音色被画成打击感低，
                //   而一个几乎无打击感的 PAD FX 被画成打击感高。
                //   ⇒ **把 Pct 统一成「语义强度」**：值越大 = 该特征越强，
                //     这样筛选、着色、Agent 三处自动一致。
                if (!Dims[d].HigherIsStronger) pct[d] = 1f - pct[d];
            }
            rows.Add(new Row { ClipId = clipId, LibraryId = libId, Pct = pct, Raw = rawv });
        }
        return rows;
    }

    /// <summary>值 x 在已排序数组里的百分位（0~1）。</summary>
    private static float Percentile(List<float> sortedArr, float x)
    {
        if (sortedArr.Count == 0) return 0f;
        int lo = 0, hi = sortedArr.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sortedArr[mid] < x) lo = mid + 1; else hi = mid;
        }
        return sortedArr.Count <= 1 ? 0.5f : (float)lo / (sortedArr.Count - 1);
    }

    /// <summary>对归一化后的行应用筛选条件（**闭区间**，null 表示不限）。</summary>
    public static List<Row> Apply(List<Row> rows, Filter f)
    {
        if (f == null || f.IsEmpty) return rows;
        var outp = new List<Row>();
        foreach (var r in rows)
        {
            bool ok = true;
            for (int d = 0; d < Dims.Length && ok; d++)
            {
                var (min, max) = f.Get(Dims[d].Key);
                if (min == null && max == null) continue;
                float v = r.Pct[d];
                if (min != null && v < min.Value) ok = false;
                else if (max != null && v > max.Value) ok = false;
            }
            if (ok) outp.Add(r);
        }
        return outp;
    }

    /// <summary>把筛选条件渲染成**人能读的一句话**（用于日志与 Agent 回答）。</summary>
    public static string Describe(Filter f)
    {
        if (f == null || f.IsEmpty) return "（无筛选条件）";
        var parts = new List<string>();
        foreach (var d in Dims)
        {
            var (min, max) = f.Get(d.Key);
            if (min == null && max == null) continue;
            if (min != null && max != null) parts.Add($"{d.Label} {min:P0}~{max:P0}");
            else if (min != null) parts.Add($"{d.Label} ≥ {min:P0}");
            else parts.Add($"{d.Label} ≤ {max:P0}");
        }
        return string.Join(" 且 ", parts);
    }
}
