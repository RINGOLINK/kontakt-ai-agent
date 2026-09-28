namespace KontaktLibManager.Core;

/// <summary>
/// 音色库分类器。
///
/// 分类粒度对齐 **Native Instruments Browser 的官方 Type 体系**
/// （Komplete Kontrol / Kontakt 浏览器左侧筛选器使用的类型标签）：
///   Acoustic 系：Brass / Guitar / Bass / Percussion / Piano / Keys / Strings /
///                Woodwinds / Vocal / World / Harp / Mallet / Organ
///   Electronic 系：Synth / Drum Machine / Bass / Lead / Pad / Arp / FX
///   Orchestral 系：Strings / Brass / Woodwinds / Percussion / Choir / Harp / Full Orchestra
///   Sound Design 系：Atmospheric / Texture / Riser / Drone / Cinematic
/// 另加一个库级特有的 **综合音源**（Factory Library、Komplete 这类包含全部乐器族的合集）。
///
/// 规则按「先具体后宽泛」排序，命中即返回——避免被 cinematic / factory 这类宽泛词截胡。
/// </summary>
public static class LibraryClassifier
{
    // ── 乐器族（Acoustic / Orchestral 子类）──
    public const string Strings = "弦乐";
    public const string Brass = "铜管";
    public const string Woodwinds = "木管";
    public const string Percussion = "打击乐";
    public const string DrumMachine = "鼓机/电子鼓";
    public const string PianoKeys = "钢琴/键盘";
    public const string HarpMallets = "竖琴/槌击";
    public const string GuitarBass = "吉他/贝斯";

    // ── Electronic 系 ──
    public const string Synth = "合成器";
    public const string AmbientFx = "氛围/音效";

    // ── 其他官方类型 ──
    public const string Vocal = "人声/合唱";
    public const string World = "世界民族";
    public const string Orchestral = "交响管弦";
    public const string Cinematic = "电影配乐";

    // ── 库级特有 ──
    public const string General = "综合音源";
    public const string Other = "其他";

    private static readonly (string Category, string[] Keywords)[] Rules =
    {
        // ① 综合合集（最优先：这类库什么都有，不能被乐器族关键词抢走）
        (General, new[] {
            "factory library", "factory selection", "komplete", "complete collection",
            "collector", "bundle", "all instruments", "general midi", "gm ",
        }),

        // ② 具体乐器族（比流派词更具体，先匹配）
        (HarpMallets, new[] {
            "harp", "celtic harp", "concert harp", "koto", "guzheng", "zither", "lyre",
            "mallet", "marimba", "vibraphone", "glockenspiel", "celesta", "music box",
            "kalimba", "steel drum", "handpan", "hang drum", "plucked", "pizzicato guitar",
        }),
        (Percussion, new[] {
            "damage", "odeholm", "percussion", "taiko", "cymbal", "gong", "timpani",
            "studio drummer", "abbey road drummer", "kabuki", "frame drum", "djembe",
            "tambourine", "snare", "world percussion",
        }),
        (DrumMachine, new[] {
            "drum machine", "drumcomputer", "drum computer", "battery", "maschine drum",
            "electronic drum", "analog drum", "909", "808", "707", "dmx", "drum synth",
        }),
        (Woodwinds, new[] {
            "woodwind", "winds", "clarinet", "flute", "oboe", "bassoon", "saxophone", "sax",
            "recorder", "bansuri", "duduk", "shakuhachi", "tin whistle", "uilleann",
        }),
        (Brass, new[] {
            "brass", "horns", "horn", "trumpet", "trombone", "tuba", "flugel",
            "euphonium", "french horn", "muted brass",
        }),
        (Strings, new[] {
            "strings", "string", "violin", "viola", "cello", "lass", "soaring",
            "stradivari", "amati", "bell violin", "tina guo", "quartet", "fiddle",
            "solo violin", "ensemble strings", "chamber strings",
        }),
        (GuitarBass, new[] {
            "guitar", "bassist", "strummed", "electric mint", "electric vintage", "sunburst",
            "picked acoustic", "prime bass", "bass", "ukulele", "mandolin", "banjo", "sitar",
            "dobro", "pedal steel",
        }),

        // ③ Electronic 系
        (Synth, new[] {
            "synth", "analog", "analogue", "serum", "massive", "absynth", "fm8", "reaktor",
            "monark", "super 8", "retro machines", "circuit", "elektron", "diva", "zebra",
            "omnisphere", "trilian", "keyscape", "modular", "wavetable", "prism", "hybrid",
            "sub bass", "arp ", "arpegg", "lead synth", "pad synth", "polysynth",
        }),
        (AmbientFx, new[] {
            "ambient", "atmosphere", "atmospheric", "texture", "sound design", "sounddesign",
            "fx", "effects", "riser", "whoosh", "evolve", "granular", "drone", "pad",
            "lumina", "aurora", "nocturne", "field recording", "foley", "noise",
            "cinematic tools", "scoring tools", "soundscape",
        }),

        // ④ 其余官方类型
        (Vocal, new[] {
            "choir", "choral", "vocal", "vocals", "voice", "voices", "syllable",
            "wordbuilder", "solo singer", "opera", "soprano", "alto", "tenor",
        }),
        (World, new[] {
            "ethno", "ethnic", "world", "oriental", "india", "africa", "asia", "gamelan",
            "middle east", "tribal", "balinese", "japanese", "chinese", "celtic",
            "klezmer", "flamenco", "arabic",
        }),
        (Orchestral, new[] {
            "symphobia", "orchestral", "orchestra", "symphony", "philharmonik", "berlin",
            "spitfire", "bbcso", "metropolis", "ark ", "opus", "full orchestra",
            "symphonic", "concert suite",
        }),
        (PianoKeys, new[] {
            "piano", "steinway", "vintage d", "kawai", "alicia", "grand", "keys", "organ",
            "rhodes", "wurli", "electric piano", "clavinet", "accordion", "harmonium",
            "melodica", "harpsichord", "celeste",
        }),
        (Cinematic, new[] {
            "cinematic", "score", "action strings", "heavyocity", "trailer",
            "hybrid tools", "production", "motion", "tension", "suspense",
        }),
    };

    public static string Classify(string libraryName)
    {
        string lower = (libraryName ?? "").ToLowerInvariant();
        foreach (var (category, keywords) in Rules)
        {
            foreach (var kw in keywords)
            {
                if (lower.Contains(kw, StringComparison.Ordinal))
                    return category;
            }
        }
        return Other;
    }

    /// <summary>
    /// 固定展示顺序（对齐 NI Browser 的分组习惯：管弦 → 键盘/拨弦 → 节奏 → 电子 → 声乐 → 合集）。
    /// 概览页图表按体积降序排列，与此顺序无关。
    /// </summary>
    public static readonly string[] DisplayOrder =
    {
        Strings, Orchestral, Cinematic, Brass, Woodwinds, Percussion,
        PianoKeys, HarpMallets, GuitarBass, DrumMachine, Synth, AmbientFx,
        Vocal, World, General, Other,
    };

    /// <summary>全部类别（用于图表补齐 0 值类别，保证类别始终可见）。</summary>
    public static IReadOnlyList<string> All => DisplayOrder;
}
