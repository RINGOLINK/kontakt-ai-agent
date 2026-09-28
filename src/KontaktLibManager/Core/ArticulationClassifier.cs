namespace KontaktLibManager.Core;

/// <summary>
/// 乐器「演奏法」分类器：从 NKI/NKM 的文件名与相对路径推断这个音色是什么演奏法，
/// 用于在乐器中心按演奏法筛选（比按音色库浏览更贴近实际编曲时的选择方式）。
///
/// 设计要点：
///   · 规则按「越具体越靠前」排序，命中即返回（如 "Leg Sus" 判为连奏而非长音）；
///   · 关键词以**词边界或常见缩写**为主，避免误判（用 -/_/. 空格作为分隔符切词）；
///   · 未命中任何演奏法关键词的，归入「基础音色」——这类通常是整件乐器的完整补丁
///     （如 "01 Full Orchestrator"、"05 Violoncelli"），本身也是有效分类。
/// </summary>
public static class ArticulationClassifier
{
    public const string Multi = "多轨合奏";
    public const string Keyswitch = "键位切换";
    public const string Plucked = "拨弦/弹拨";
    public const string Short = "短音";
    public const string Tremolo = "颤音/震音";
    public const string Legato = "连奏";
    public const string Sustain = "长音";
    public const string Loop = "循环乐句";
    public const string Hit = "打击/单音";
    public const string Fx = "效果/氛围";
    public const string Base = "基础音色";

    /// <summary>规则顺序即优先级：越具体越靠前。</summary>
    private static readonly (string Category, string[] Keywords)[] Rules =
    {
        // ① 拨弦/弹拨（pizz 比短音更具体，先判）
        (Plucked, new[] {
            "pizz", "pizzicato", "pluck", "plucked", "picked", "strum", "strummed",
            "snap", "slap", "thumb", "fingerstyle", "secco",
        }),
        // ② 颤音/震音
        (Tremolo, new[] {
            "trem", "tremolo", "trill", "tr", "vib", "vibrato", "nv", "nonvib",
            "molto vib", "senza vib", "flatter", "buzz",
        }),
        // ③ 短音
        (Short, new[] {
            "stac", "stacc", "staccato", "spic", "spiccato", "marc", "marcato",
            "short", "sht", "detache", "dnt", "sautille", "ricochet", "portato",
            "chop", "stab", "punch", "tight",
        }),
        // ④ 连奏
        (Legato, new[] {
            "leg", "legato", "slur", "portamento", "glide", "slide", "grace",
            "connected", "smooth",
        }),
        // ⑤ 长音
        (Sustain, new[] {
            "sus", "sustain", "sustained", "long", "arco", "normal", "nrm",
            "sostenuto", "tenuto", "hold",
        }),
        // ⑥ 循环乐句
        (Loop, new[] {
            "loop", "phrase", "pattern", "riff", "groove", "ostinato", "arp",
            "arpegg", "sequence", "seq", "tempo", "sync", "tm sync", "beats",
            "turnaround", "lick", "progression", "rhythm", "tm",
        }),
        // ⑦ 打击/单音
        (Hit, new[] {
            "hit", "hits", "strike", "struck", "oneshot", "one shot", "one-shot",
            "impact", "slam", "boom", "thunder", "crash", "smash", "knock",
            "bang", "tap", "click", "snare", "kick", "tom", "cymbal", "gong",
        }),
        // ⑧ 效果/氛围
        (Fx, new[] {
            "fx", "effect", "effects", "riser", "whoosh", "swoosh", "noise",
            "cluster", "aleatoric", "atmo", "atmos", "drone", "texture", "textures",
            "pad", "ambient", "sweep", "transition", "sub drop", "reverse", "rev ",
            "granular", "glitch", "foley", "field",
        }),
        // ⑨ 键位切换（一般出现在补丁名里，放在偏后避免误伤）
        (Keyswitch, new[] {
            "ks", "keyswitch", "key-switch", "key switch", "keysw", "keyw",
            "articulation", "articulations", "multi art",
        }),
    };

    /// <summary>按文件名 + 相对路径推断演奏法。kind 为 nkm 时直接判为多轨合奏。</summary>
    public static string Classify(string name, string relPath, string kind)
    {
        if (string.Equals(kind, "nkm", StringComparison.OrdinalIgnoreCase)) return Multi;

        // 只依据**文件名**判断：路径中的文件夹名（如 "3 - Bowed Strings"）会引入误判
        string target = (name ?? "").ToLowerInvariant();
        // 统一分隔符，便于按词匹配
        target = target.Replace('-', ' ').Replace('_', ' ').Replace('.', ' ').Replace('/', ' ').Replace('\\', ' ');

        foreach (var (category, keywords) in Rules)
        {
            foreach (var kw in keywords)
            {
                if (kw.Contains(' '))
                {
                    if (target.Contains(kw, StringComparison.Ordinal)) return category;
                }
                else
                {
                    // 单词关键词按边界匹配，避免 "tr" 命中 "strings" 这类误判
                    if (ContainsWord(target, kw)) return category;
                }
            }
        }
        return Base;
    }

    /// <summary>按空白分词后做精确词匹配（可识别 "2INRU87 ST 1SEC" 里的 "st"）。</summary>
    private static bool ContainsWord(string target, string word)
    {
        int idx = 0;
        while (true)
        {
            idx = target.IndexOf(word, idx, StringComparison.Ordinal);
            if (idx < 0) return false;

            bool leftOk = idx == 0 || !char.IsLetterOrDigit(target[idx - 1]);
            int end = idx + word.Length;
            bool rightOk = end >= target.Length || !char.IsLetterOrDigit(target[end]);
            if (leftOk && rightOk) return true;
            idx = end;
        }
    }

    /// <summary>固定展示顺序（编曲时的常见取用顺序）。</summary>
    public static readonly string[] DisplayOrder =
    {
        Base, Keyswitch, Legato, Sustain, Short, Tremolo, Plucked,
        Loop, Hit, Fx, Multi,
    };
}
