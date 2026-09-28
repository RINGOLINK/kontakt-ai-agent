using System;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **试听候选的质量筛选规则**（2026-09-27 新增，用户提出）。
///
/// 背景（用户实测反馈）：
///   1. `find_diverse_candidates` 挑出的第 1、2 组是 **`.nksn` / `.nki` 乐器预览**（0.05~0.1 MB），
///      不是真正的镲片采样 ⇒ 用户听到的是「预设片段」，不是他要的音色。
///   2. 名字含关键词 ≠ 就是那个乐器：`Cymbals and Gongs`（镲+锣）、
///      `Cymbals and Bassdrum`（镲+底鼓）都是**混合乐器**，但 `name` 判定都通过。
///   3. 多麦位交响库里 `Hall_` / `Decca_` 是**远距离麦位**，不仅电平低，
///      音色也不是用户想听的「本体」。用户明确要求：
///      **「如果能命中主麦位，可以选；如果只能命中远距离麦位就忽略掉。」**
/// </summary>
public static class AudioQuality
{
    /// <summary>远距离/房间类麦位关键词（命中即视为「远距离」）。</summary>
    private static readonly string[] FarMics =
    {
        "hall", "decca", "far", "room", "amb", "surround", "tail", "verb",
        "dist", "wide", "stage", "audience", "balcony", "gallery",
    };

    /// <summary>近/主麦位关键词（命中即视为「主麦位」）。</summary>
    private static readonly string[] NearMics =
    {
        "close", "main", "near", "direct", "dry", "solo", "spot", "mono",
        "_cl_", "-cl-", "cl_", "_m_", "-m-", "cardioid",
    };

    /// <summary>组合乐器名的信号词（出现则说明不是单一乐器）。</summary>
    private static readonly string[] CombinedWords = { " and ", "&", "gong", "menu", "preset", "combo", "kit" };

    /// <summary>是否为【真采样】——`.nksn`/`.nki` 预览（kind=demo）不算。</summary>
    /// <summary>
    /// **是否【无需密钥即可解码试听】的格式**（2026-09-27 新增）。
    ///
    /// 用户要求（原话）：「遇到选出的库无法解码这种情况，**不应当计入最终五个展示结果当中**…
    /// **那些实在无法解码的可以作为推荐项列出来，但是能供人试听的，应当凑够用户要求的数量。**」
    ///
    /// ⇒ 这是【试听名额】的准入条件：
    ///   · `.ncw` 自研解码器 · `.wav`/`.ogg`/`.mp3` 原生 —— 都【能】直接播；
    ///   · `.nkx` 加密容器**需要注册表里的产品密钥**，实测有 92 个库拿不到密钥 ⇒ 不能保证；
    ///   · `.aif`/`.aiff` 已加【转码成 WAV】分支（2026-09-27）⇒ 现在也能播。
    /// ⚠️ 这是【保守判定】：宁可少给几个、也不能承诺了却播不了。
    /// </summary>
    public static bool IsDirectlyDecodable(AudioClip c)
    {
        string e = (c.Ext ?? "").ToLowerInvariant().TrimStart('.');
        return e is "ncw" or "wav" or "ogg" or "mp3" or "aif" or "aiff";   // 🔴 aif 已能转码播（2026-09-27）
    }

    public static bool IsRealSample(AudioClip c)
        => !string.Equals(c.Kind, "demo", StringComparison.OrdinalIgnoreCase);

    /// <summary>名字是否像【组合乐器】（`Cymbals and Gongs` / `Cymbals and Bassdrum` 等）。</summary>
    public static bool IsCombinedName(string name)
    {
        string n = " " + (name ?? "").ToLowerInvariant() + " ";
        return CombinedWords.Any(w => n.Contains(w));
    }

    /// <summary>
    /// 麦位等级：**0 = 主/近麦（最优先）、1 = 无麦位标记（可用）、2 = 远距离（能避开就避开）**。
    /// 依据【文件名 + 相对路径】一起判断（麦位标记可能只在路径里）。
    /// </summary>
    public static int MicRank(AudioClip c)
    {
        string s = ((c.Name ?? "") + " " + (c.RelPath ?? "")).ToLowerInvariant();
        if (FarMics.Any(f => s.Contains(f))) return 2;
        if (NearMics.Any(n => s.Contains(n))) return 0;
        return 1;
    }

    /// <summary>
    /// **是否可用于「保底/主推」** —— 必须同时满足：
    ///   · 真采样（非预览）
    ///   · 名字不是组合乐器
    ///   · **不是纯远距离麦位**（用户要求：只能命中远距离就忽略）
    /// </summary>
    public static bool IsPrimaryPick(AudioClip c)
        => IsRealSample(c) && IsDirectlyDecodable(c) && !IsCombinedName(c.Name) && MicRank(c) < 2;

    /// <summary>
    /// **同族词表**（2026-09-27 新增，用户同意的方案 B）。
    ///
    /// 为什么要它：实测 cymbal 只有 4 个库有可试听采样、crash 只有 1 个，凑不够用户要的数量。
    /// 用户原话：「我这么大库的体量找五个目标采样应该问题不大」
    /// ⇒ 精确词不够时，**用同族词继续补**（仍属同一类乐器），而不是同库重复充数。
    /// ⚠️ 只在【精确词凑不够】时启用；命中来源通过 viaTerm 回报，回答里要说明。
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, string[]> Related = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cymbal"] = new[] { "crash", "ride", "hat", "sizzle", "splash", "china", "piatti" },
        ["cymbals"] = new[] { "crash", "ride", "hat", "sizzle", "splash", "china", "piatti" },
        ["crash"] = new[] { "cymbal", "ride", "splash", "china" },
        ["ride"] = new[] { "cymbal", "crash", "bell" },
        ["hat"] = new[] { "hihat", "hi-hat", "cymbal" },
        ["hihat"] = new[] { "hat", "hi-hat", "cymbal" },
        ["gong"] = new[] { "tam", "cymbal" },
        ["pad"] = new[] { "texture", "atmosphere", "drone" },
        ["strings"] = new[] { "violin", "viola", "cello", "ensemble" },
        ["brass"] = new[] { "trumpet", "horn", "trombone", "tuba" },
    };

    /// <summary>取同族词（不含原词本身）。没有登记时返回空。</summary>
    public static System.Collections.Generic.List<string> RelatedTermsFor(string term)
    {
        var list = new System.Collections.Generic.List<string>();
        string kk = (term ?? "").Trim().ToLowerInvariant();
        if (kk.Length == 0) return list;
        foreach (var kv in Related)
        {
            if (kk == kv.Key || kk.Contains(kv.Key) || kv.Key.Contains(kk))
            {
                foreach (var v in kv.Value)
                    if (!list.Contains(v) && !kk.Contains(v)) list.Add(v);
                break;
            }
        }
        return list;
    }


    /// <summary>
    /// **按库做麦位裁决**：若某库**完全没有**主/近麦或无标记的采样（即只有远距离麦位），
    /// 则**该库整个剔除**（用户要求）。有主麦位时，把远距离麦位的项也一并剔除 ——
    /// 这样批里不会混入「音量很小、音色也不对」的远麦位变体。
    /// </summary>
    public static System.Collections.Generic.List<AudioClip> ApplyMicPolicy(
        System.Collections.Generic.IEnumerable<AudioClip> clips)
    {
        var list = clips.Where(IsRealSample).Where(IsDirectlyDecodable).Where(c => !IsCombinedName(c.Name)).ToList();
        // 逐库裁决
        var keep = new System.Collections.Generic.List<AudioClip>();
        foreach (var grp in list.GroupBy(c => c.LibraryId))
        {
            bool hasPrimary = grp.Any(c => MicRank(c) < 2);
            if (!hasPrimary) continue;                       // 只有远麦位 ⇒ 整库剔除
            keep.AddRange(grp.Where(c => MicRank(c) < 2));   // 有主麦位时也要剔掉远麦位
        }
        return keep;
    }
}
