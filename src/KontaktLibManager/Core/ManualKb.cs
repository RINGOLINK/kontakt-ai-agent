using System.Text;
using System.Text.Json;

namespace KontaktLibManager.Core;

public sealed class KbPage
{
    public int N { get; set; }                  // 1 基页号
    public string Text { get; set; } = "";
    /// <summary>text = 直接抽取；vision = 视觉模型解读（图片页）；empty = 无内容</summary>
    public string Source { get; set; } = "text";
    public int Chars => Text.Length;
}

public sealed class KbChunk
{
    public int Id { get; set; }
    public int Page { get; set; }
    public string Text { get; set; } = "";
}

public sealed class LibraryKb
{
    public long LibraryId { get; set; }
    public string LibraryName { get; set; } = "";
    public string ManualPath { get; set; } = "";
    public string ManualName { get; set; } = "";
    public long ManualSize { get; set; }
    public long ManualMtimeTicks { get; set; }
    public int PageCount { get; set; }
    public string BuiltAt { get; set; } = "";
    public int VisionPages { get; set; }
    public int TotalChars { get; set; }
    /// <summary>库入门指引（建库时由模型基于前几页生成的概览），用于回答"介绍一下这个库"这类泛问。</summary>
    public string Overview { get; set; } = "";
    public List<KbPage> Pages { get; set; } = new();
    public List<KbChunk> Chunks { get; set; } = new();
}

/// <summary>建库完成摘要（供界面刷新「知识库」/「说明书」列表并显示结果）。</summary>
public sealed class KbDoneSummary
{
    public int Pages { get; set; }
    public int Chunks { get; set; }
    public long Chars { get; set; }
    public int VisionPages { get; set; }
}
public sealed class KbProgress
{
    public string Phase { get; set; } = "";
    public int Current { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 说明书知识库：把 PDF 手册拆解为「按页的知识条目 + 可检索分块」。
///
/// 拆解策略（对应用户要求）：
///   1. 先抽取文字 —— 文字型手册（如 Chris Hein，1343 字符/页）直接可用；
///   2. 图片型页面（文字 &lt; 阈值）才调用**多模态模型**解读，把截图内容转成文字知识；
///   3. 页面图按需渲染并缓存 —— 用户问到相关部分时，Agent 把该页例图展示给人看；
///   4. 检索用本地 BM25（无需 embedding，零额外 API 调用、可离线）。
/// </summary>
public static class ManualKb
{
    /// <summary>低于此字符数视为「图片页」，需要视觉解析。</summary>
    public const int ImagePageThreshold = 200;

    /// <summary>单次构建最多做多少页视觉解析（防失控成本）。</summary>
    public const int MaxVisionPages = 80;

    /// <summary>
    /// 本次建库允许的视觉页上限（默认取 MaxVisionPages）。
    /// 官方文档（Kontakt 手册等）以文字为主，逐页跑视觉模型既慢又贵，
    /// 可以把它设成 0 跳过视觉、只做文字抽取 —— 实测 416 页手册若跑满 80 页视觉约需 24 分钟。
    /// </summary>
    public static int VisionPageLimit = MaxVisionPages;

    private const int ChunkSize = 900;
    private const int ChunkOverlap = 120;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// 「Kontakt 官方文档」知识库的保留 libraryId。
    /// 用负数避免与数据库里的真实音色库 id（正数）冲突。
    /// 内容来自 Kontakt 安装目录 Documentation 下的官方 PDF
    /// （KONTAKT_Manual / KSP_Manual / LUA_API_Manual），
    /// 让 Agent 在「操作 Kontakt」时能查到官方说明，而不是凭猜测点界面。
    /// </summary>
    public const long KontaktDocLibraryId = -1;

    public static string DirFor(long libraryId) => Path.Combine(AppPaths.DataDir, "kb", libraryId.ToString());
    public static string KbPath(long libraryId) => Path.Combine(DirFor(libraryId), "kb.json");
    public static string PageImagePath(long libraryId, int pageNumber)
        => Path.Combine(DirFor(libraryId), "pages", $"p{pageNumber}.png");

    public static LibraryKb? Load(long libraryId)
    {
        string p = KbPath(libraryId);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<LibraryKb>(File.ReadAllText(p, Encoding.UTF8), Json); }
        catch { return null; }
    }

    /// <summary>知识库是否已是最新（手册文件未变）。</summary>
    public static bool IsFresh(LibraryKb? kb, string manualPath)
    {
        if (kb == null || !File.Exists(manualPath)) return false;

        // 🔴 **必须检查 KB 是否真的有内容（2026-09-24 修）** ——
        //   原实现只比「文件 mtime + 大小」，**不看 KB 里有没有页/块**。
        //   ⇒ 一个【建库失败留下的 0 页 0 块】的 KB 会被判为 fresh ⇒
        //     `build_manual_kb` 的幂等分支【永远拒绝重建】（除非显式 force）
        //     ⇒ 用户界面上那个异常状态**永远修不好**。
        //   实测症状（用户报）：「系统显示 Damage 已经有知识库了，但页数和块数都是 0，
        //   这看起来像是个异常状态」—— 正是本条。
        //   ⇒ 空 KB 一律视为「不 fresh」，从而允许自动重建。
        if (kb.PageCount <= 0 && (kb.Chunks == null || kb.Chunks.Count == 0)) return false;

        try
        {
            var fi = new FileInfo(manualPath);
            return kb.ManualMtimeTicks == fi.LastWriteTimeUtc.Ticks && kb.ManualSize == fi.Length;
        }
        catch { return false; }
    }

    /// <summary>构建知识库（文字抽取 + 图片页视觉解析 + 分块）。</summary>
    /// <summary>
    /// **读纯文本文档**（.txt / .rtf / .html / .md / .csv）并去掉标记。
    ///
    /// 库里 1,232 本说明书是这些格式（`.txt` 798 + `.rtf` 434），
    /// 旧实现只认 PDF，导致它们无法建库。RTF/HTML 用正则做**最小化**去标记
    ///（不引入依赖）：去掉控制字、注释、标签，保留可见文字。
    /// </summary>
    public static string ReadPlainManualPublic(string path, string ext) => ReadPlainManual(path, ext);
    private static string ReadPlainManual(string path, string ext)
    {
        string text;
        try
        {
            // 先按 UTF-8 读；若出现大量替换字符，退回系统默认编码（老手册常见 GBK/ANSI）
            text = File.ReadAllText(path, new UTF8Encoding(false));
            int bad = text.Count(c => c == '\uFFFD');
            if (bad > 20 || (text.Length > 0 && bad * 100.0 / text.Length > 0.5))
                text = File.ReadAllText(path, System.Text.Encoding.Default);
        }
        catch
        {
            try { text = File.ReadAllText(path, System.Text.Encoding.Default); } catch { return ""; }
        }
        if (ext == "rtf")
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\\'([0-9a-fA-F]{2})", " ");   // 十六进制转义
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\{\\\*[^{}]*\}", " ");          // 目标组
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\\[a-zA-Z]+-?\d* ?", " ");      // 控制字
            text = text.Replace("{", " ").Replace("}", " ").Replace("\\", " ");
        }
        else if (ext is "html" or "htm")
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<(script|style)[^>]*>.*?</\1>", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
        }
        // 压缩连续空白，保留段落换行
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t\u00A0]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\r?\n)\s*(\r?\n)+", "\n\n");
        return text.Trim();
    }
    /// <summary>把长文本按约 <paramref name="pageChars"/> 字符切成伪「页」（在段落边界切）。</summary>
    private static List<string> SplitPlainIntoPages(string text, int pageChars)
    {
        var pages = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return pages;
        var paras = text.Split('\n');
        var cur = new System.Text.StringBuilder();
        foreach (var p in paras)
        {
            if (cur.Length + p.Length > pageChars && cur.Length > 0)
            {
                pages.Add(cur.ToString().Trim());
                cur.Clear();
            }
            cur.AppendLine(p);
        }
        if (cur.Length > 0) pages.Add(cur.ToString().Trim());
        return pages.Where(x => x.Length > 0).ToList();
    }
    public static async Task<LibraryKb> BuildAsync(
        long libraryId, string libraryName, ManualRecord manual, AiSettings? ai,
        IProgress<KbProgress>? progress = null, CancellationToken ct = default)
    {
        string pdf = manual.FullPath;
        if (!File.Exists(pdf)) throw new FileNotFoundException($"手册不存在：{pdf}");

        var fi = new FileInfo(pdf);
        var kb = new LibraryKb
        {
            LibraryId = libraryId,
            LibraryName = libraryName,
            ManualPath = pdf,
            ManualName = manual.Name,
            ManualSize = fi.Length,
            ManualMtimeTicks = fi.LastWriteTimeUtc.Ticks,
            BuiltAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        // ══════════ 纯文本类手册：直接读文本，不走 PDF/视觉管线 ══════════
        //
        // **为什么必须支持**：库里 `.txt` 798 本 + `.rtf` 434 本 = **1,232 本**，
        // 而旧实现只认 `.pdf`（`AutoKb` 里 `if (!best.Ext.Equals("pdf")) continue;`），
        // 于是绝大多数库「入库后自动建库」根本不会发生，用户看到的是「知识库还是空的」。
        // 这些格式的正文就是文本，读出来按段落切成伪「页」即可（每 ~3,000 字符一页）。
        string ext = (manual.Ext ?? "").TrimStart('.').ToLowerInvariant();
        if (ext is "txt" or "rtf" or "html" or "htm" or "md" or "csv")
        {
            progress?.Report(new KbProgress { Phase = "读取文档", Total = 1, Current = 0, Message = manual.Name });
            string raw = await Task.Run(() => ReadPlainManual(pdf, ext), ct);
            var parts = SplitPlainIntoPages(raw, 3000);
            for (int i = 0; i < parts.Count; i++)
                kb.Pages.Add(new KbPage { N = i + 1, Text = parts[i], Source = "text" });
            kb.PageCount = parts.Count;

            progress?.Report(new KbProgress { Phase = "建立索引", Total = 1, Current = 0 });
            int cid0 = 0;
            foreach (var page in kb.Pages)
                foreach (var piece in SplitIntoChunks(page.Text))
                    kb.Chunks.Add(new KbChunk { Id = cid0++, Page = page.N, Text = piece });
            kb.TotalChars = kb.Pages.Sum(p => p.Text.Length);

            if (ai != null && ai.Configured && kb.Chunks.Count > 0)
            {
                try { kb.Overview = await BuildOverviewAsync(ai, kb, ct); } catch { }
            }

            Directory.CreateDirectory(DirFor(libraryId));
            File.WriteAllText(KbPath(libraryId), JsonSerializer.Serialize(kb, Json), new UTF8Encoding(false));
            progress?.Report(new KbProgress { Phase = "完成", Total = 1, Current = 1, Message = $"{kb.Pages.Count} 页 / {kb.Chunks.Count} 块" });
            return kb;
        }

        progress?.Report(new KbProgress { Phase = "读取页数", Total = 1, Current = 0, Message = manual.Name });
        int pageCount = PdfText.GetPageCount(pdf);
        kb.PageCount = pageCount;

        progress?.Report(new KbProgress { Phase = "抽取文字", Total = pageCount, Current = 0 });
        var texts = PdfText.ExtractAllText(pdf, (i, n) =>
            progress?.Report(new KbProgress { Phase = "抽取文字", Total = n, Current = i }));

        for (int i = 0; i < texts.Count; i++)
        {
            kb.Pages.Add(new KbPage { N = i + 1, Text = texts[i], Source = texts[i].Length > 0 ? "text" : "empty" });
        }

        // ── 图片页：渲染 + 视觉解析 ──
        //
        // **可续建（用户实测「1.49 MB 说明书建库等好久也没完成」的修复）**：
        // 一本扫描版 PDF 会有几百个图片页，每个都要调一次视觉模型（数秒），
        // 80 页上限也要十几分钟；旧实现**只在全部结束时才落盘**，
        // 一旦超时/失败/关窗口就**全部作废**，下次还得从头再来。
        //
        // 现在：① 若已有同手册的 KB，**先加载并把已解析过的图片页跳过**（断点续建）；
        //       ② 每解析若干页就**增量落盘**，中断后下次接着来；
        //       ③ 进度里明确报告「本次新解析 N 页 / 已跳过 M 页 / 还剩 K 页」。
        LibraryKb? prev = null;
        try
        {
            if (File.Exists(KbPath(libraryId)))
            {
                var old = JsonSerializer.Deserialize<LibraryKb>(File.ReadAllText(KbPath(libraryId)), Json);
                // 同一份手册（大小与修改时间一致）才允许续建
                if (old != null && old.ManualSize == fi.Length && old.ManualMtimeTicks == fi.LastWriteTimeUtc.Ticks
                    && old.Pages.Count == kb.Pages.Count)
                    prev = old;
            }
        }
        catch { }

        var imagePages = kb.Pages.Where(p => p.Text.Length < ImagePageThreshold).ToList();
        int alreadyDone = 0;
        if (prev != null)
        {
            // 把上次已解析成功的图片页结果搬过来
            foreach (var p in imagePages)
            {
                var old = prev.Pages.FirstOrDefault(x => x.N == p.N);
                if (old != null && old.Source == "vision" && old.Text.Length > 20)
                {
                    p.Text = old.Text;
                    p.Source = "vision";
                    kb.VisionPages++;
                    alreadyDone++;
                }
            }
            if (alreadyDone > 0)
                progress?.Report(new KbProgress
                {
                    Phase = "视觉解析",
                    Total = imagePages.Count,
                    Current = alreadyDone,
                    Message = $"续建：已复用上次解析好的 {alreadyDone} 页，跳过重复调用",
                });
        }
        // 本次真正需要解析的（排除已复用的）
        var todoPages = imagePages.Where(p => p.Source != "vision").ToList();

        if (ai != null && ai.Configured && todoPages.Count > 0)
        {
            if (todoPages.Count > VisionPageLimit)
            {
                progress?.Report(new KbProgress
                {
                    Phase = "视觉解析",
                    Total = VisionPageLimit,
                    Current = 0,
                    Message = $"本次还有 {todoPages.Count} 个图片页待解析，本次最多解析 {VisionPageLimit} 页（可再次点建库继续，已解析的会跳过）",
                });
                todoPages = todoPages.Take(VisionPageLimit).ToList();
            }

            int done = 0;
            foreach (var page in todoPages)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new KbProgress
                {
                    Phase = "视觉解析",
                    Total = todoPages.Count,
                    Current = done,
                    Message = $"第 {page.N} 页（图片页，需模型看图）｜本次 {done}/{todoPages.Count}，已跳过 {alreadyDone} 页",
                });

                try
                {
                    var png = PdfText.RenderPagePng(pdf, page.N - 1, 1.4);
                    if (png == null) continue;

                    // 页面图缓存（供后续「展示例图」直接使用）
                    try
                    {
                        string imgPath = PageImagePath(libraryId, page.N);
                        Directory.CreateDirectory(Path.GetDirectoryName(imgPath)!);
                        if (!File.Exists(imgPath)) File.WriteAllBytes(imgPath, png);
                    }
                    catch { }

                    string desc = await AiClient.DescribeImageAsync(ai, png, VisionPrompt, ct, maxTokens: 1500);
                    desc = StripThinking(desc);
                    if (desc.Length > 20)
                    {
                        page.Text = $"[第 {page.N} 页 · 图文页解读]\n{desc}";
                        page.Source = "vision";
                        kb.VisionPages++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    page.Text = $"[第 {page.N} 页 · 视觉解析失败：{ex.Message}]";
                    page.Source = "empty";
                }

                // **增量落盘**：每 3 页存一次，中断后下次可接着来（不丢已完成的解析）
                if (done % 3 == 0)
                {
                    try
                    {
                        Directory.CreateDirectory(DirFor(libraryId));
                        File.WriteAllText(KbPath(libraryId), JsonSerializer.Serialize(kb, Json), new UTF8Encoding(false));
                    }
                    catch { }
                }
            }
        }

        // ── 分块 ──
        progress?.Report(new KbProgress { Phase = "建立索引", Total = 1, Current = 0 });
        int chunkId = 0;
        foreach (var page in kb.Pages)
        {
            foreach (var piece in SplitIntoChunks(page.Text))
            {
                kb.Chunks.Add(new KbChunk { Id = chunkId++, Page = page.N, Text = piece });
            }
        }
        kb.TotalChars = kb.Pages.Sum(p => p.Text.Length);

        // ── 入门指引：用前几页内容生成库概览（供"介绍一下这个库"这类泛问使用）──
        if (ai != null && ai.Configured)
        {
            try
            {
                progress?.Report(new KbProgress { Phase = "生成入门指引", Total = 1, Current = 0 });
                kb.Overview = await BuildOverviewAsync(ai, kb, ct);
            }
            catch { /* 概览失败不影响知识库可用 */ }
        }

        Directory.CreateDirectory(DirFor(libraryId));
        File.WriteAllText(KbPath(libraryId), JsonSerializer.Serialize(kb, Json), new UTF8Encoding(false));

        progress?.Report(new KbProgress
        {
            Phase = "完成",
            Total = 1,
            Current = 1,
            Message = $"{kb.PageCount} 页 / {kb.Chunks.Count} 个知识块 / {kb.TotalChars:N0} 字符（视觉解析 {kb.VisionPages} 页）",
        });
        return kb;
    }

    private const string VisionPrompt =
        "你在为 Kontakt 音色库说明书的一页建立**可检索的知识条目**。请用中文输出该页的完整要点：\n" +
        "1) 本页主题（一句话）；\n" +
        "2) 界面元素、参数、按钮的名称（保留英文原文）；\n" +
        "3) 操作步骤或使用说明；\n" +
        "4) 截图/例图在演示什么。\n" +
        "只描述确实能看到的内容，不要编造。直接输出结果，不要复述思考过程。";

    /// <summary>去掉模型输出里的思考过程（部分端点会把 reasoning 混进 content）。</summary>
    public static string StripThinking(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Trim();

        // 去掉 "Thinking Process:" 之类的开头块
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"(Thinking Process:|思考过程[:：])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && m.Index < 200)
        {
            var tail = System.Text.RegularExpressions.Regex.Match(text,
                @"(最终答案|Final Answer|现在要整理|综上所述)[:：]?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (tail.Success && tail.Index > m.Index) return text[(tail.Index + tail.Length)..].Trim();
        }

        // 推理模型在 token 不足时会只返回 reasoning（没有正式 content）。
        // 特征：篇幅很长 + 出现自述式思考用语。此时取最后一个分隔线之后的正文。
        bool looksLikeThinking = text.Length > 700 && new[]
        {
            "让我", "数一下", "精简", "符合要求", "总计约", "现在我来", "我需要先",
            "let me", "i need to", "let's count", "first, i", "the user wants",
        }.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (looksLikeThinking)
        {
            int idx = text.LastIndexOf("\n---", StringComparison.Ordinal);
            if (idx > 0)
            {
                var tail = text[(idx + 4)..].Trim().TrimStart('-').Trim();
                if (tail.Length > 50) return tail;
            }
            // 退而求其次：取最后一段（按空行切分）
            var blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            for (int i = blocks.Length - 1; i >= 0; i--)
            {
                var b = blocks[i].Trim();
                if (b.Length is > 60 and < 900) return b;
            }
        }

        return text;
    }

    /// <summary>按段落边界切块，带重叠，避免切断句子。</summary>
    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        if (text.Length <= ChunkSize) { yield return text.Trim(); yield break; }

        int pos = 0;
        while (pos < text.Length)
        {
            int len = Math.Min(ChunkSize, text.Length - pos);
            int end = pos + len;

            if (end < text.Length)
            {
                // 优先在段落/句子边界断开
                int cut = text.LastIndexOf("\n\n", end - 1, Math.Min(len, end));
                if (cut <= pos) cut = text.LastIndexOf('\n', end - 1, Math.Min(len, end));
                if (cut <= pos) cut = text.LastIndexOf(". ", end - 1, Math.Min(len, end));
                if (cut > pos + ChunkSize / 3) end = cut + 1;
            }

            string piece = text[pos..end].Trim();
            if (piece.Length > 0) yield return piece;
            if (end >= text.Length) break;
            pos = Math.Max(pos + 1, end - ChunkOverlap);
        }
    }

    // ══════════════ 混合检索：BM25 + 本地向量 + RRF 融合 + MMR 去重 ══════════════
    //
    // 调研报告的「第三件事」：**把检索从 BM25 升级成混合检索** ——
    //   本地嵌入 + 向量与 BM25 各取 top-N、RRF(k=60) 融合、MMR 去重。
    //
    // **为什么 BM25 不够**：BM25 是**词面匹配**，用户问「按键怎么切换音色」而手册写
    // 「key-switch / keyswitch」时靠同义词表硬撑；一旦换个说法（「怎么换 articulation」）
    // 就完全命中不到。向量侧提供**词面之外的相似度**。
    //
    // **嵌入的取舍（重要）**：报告建议 120MB 级本地模型（ONNX）。本项目**当前不引入外部模型**，
    // 改用**零依赖的「哈希随机投影」嵌入**（hashing trick / random indexing）：
    //   · 把 token 与字符 n-gram 哈希到固定维度的稀疏向量，用**确定性伪随机符号**填充；
    //   · 数学上等价于「随机投影下的近似词袋余弦」，能捕捉**字符级/词形级相似**
    //     （articulation↔articulations、keyswitch↔key-switch），这正是 BM25 的软肋；
    //   · **不下载、不联网、无第三方依赖**，几 MB 内存，毫秒级。
    // **升级路径**：`Embed` 是可替换的单一入口 —— 将来接入 ONNX 模型只需替换该函数体，
    //   其余（RRF/MMR/缓存）完全不用动。

    /// <summary>向量维度（哈希桶数）。512 对小规模手册语料足够，且内存/速度友好。</summary>
    private const int VecDim = 512;

    /// <summary>RRF 的 k 常数（报告指定 k=60；越大越平滑、越不偏向单一排序的头部）。</summary>
    private const int RrfK = 60;

    /// <summary>每路召回条数（报告建议各取 top-50）。</summary>
    private const int RecallPerSide = 50;

    /// <summary>嵌入缓存：库 id → (chunk 数, 向量矩阵)。chunk 数变化即失效（重新抓取/重建知识库）。</summary>
    private static readonly Dictionary<long, (int Count, float[][] Vecs)> VecCache = new();

    /// <summary>
    /// **零依赖本地嵌入**：token + 字符 n-gram 的哈希随机投影，L2 归一化。
    /// 确定性（无随机种子漂移），因此同一段文本永远得到同一向量。
    /// </summary>
    private static float[] Embed(string text)
    {
        var v = new float[VecDim];
        if (string.IsNullOrWhiteSpace(text)) return v;

        var toks = Tokenize(text, expandSynonyms: false);
        foreach (var t in toks)
        {
            AddHashed(v, t, 1.0f);
            // 字符 3-gram：补足词形相近但拼写不同的情况（articulation / articulations）
            if (t.Length >= 3)
                for (int i = 0; i + 3 <= t.Length; i++)
                    AddHashed(v, "3:" + t.Substring(i, 3), 0.35f);
        }

        // L2 归一化 → 点积即余弦
        double norm = 0;
        for (int i = 0; i < VecDim; i++) norm += v[i] * (double)v[i];
        norm = Math.Sqrt(norm);
        if (norm > 1e-9) for (int i = 0; i < VecDim; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    /// <summary>把一个 token 哈希到 3 个桶（带确定性符号），降低碰撞影响。</summary>
    private static void AddHashed(float[] v, string token, float weight)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            foreach (char c in token) { h ^= c; h *= 1099511628211UL; }
            for (int k = 0; k < 3; k++)
            {
                ulong hh = h + (ulong)k * 0x9E3779B97F4A7C15UL;
                int idx = (int)(hh % (ulong)VecDim);
                float sign = ((hh >> 63) & 1) == 0 ? 1f : -1f;   // 确定性符号，抑制同向累积偏差
                v[idx] += sign * weight;
            }
        }
    }

    private static float[][] GetVectors(LibraryKb kb)
    {
        if (VecCache.TryGetValue(kb.LibraryId, out var cached) && cached.Count == kb.Chunks.Count)
            return cached.Vecs;
        var vecs = new float[kb.Chunks.Count][];
        for (int i = 0; i < kb.Chunks.Count; i++) vecs[i] = Embed(kb.Chunks[i].Text);
        VecCache[kb.LibraryId] = (kb.Chunks.Count, vecs);
        return vecs;
    }

    /// <summary>
    /// **混合检索**：BM25 与向量各取 <see cref="RecallPerSide"/> 条 → **RRF(k=60) 融合** → **MMR 去重** → 取 topK。
    ///
    /// RRF：`score(d) = Σ_i 1 / (k + rank_i(d))`（只对两路各自出现过的文档求和）。
    /// 好处是**不需要在两路分数之间做归一化**（BM25 是无界分数、余弦在 [-1,1]，直接加权必然要调参）。
    ///
    /// MMR：`argmax [ λ·rel(d) − (1−λ)·max_{s∈已选} sim(d, s) ]`，λ=0.7 —— 既相关又不重复。
    /// 手册里同一段话常被切成多个相邻块，不做 MMR 会把 topK 全塞满同一段。
    /// </summary>
    public static List<KbChunk> HybridSearch(LibraryKb kb, string query, int topK = 6, bool expandSynonyms = true)
    {
        if (kb == null || kb.Chunks.Count == 0 || string.IsNullOrWhiteSpace(query)) return new List<KbChunk>();
        if (kb.Chunks.Count <= topK) return kb.Chunks.ToList();   // 语料比 topK 还小，没必要算

        // ── 路 1：BM25（复用现有实现，取更多条作为召回池）──
        var bm25 = Search(kb, query, RecallPerSide, expandSynonyms);
        var bm25Rank = new Dictionary<int, int>();
        for (int r = 0; r < bm25.Count; r++) bm25Rank[IndexIn(kb, bm25[r])] = r;

        // ── 路 2：向量余弦 ──
        var vecs = GetVectors(kb);
        var qv = Embed(query);
        var cos = new double[kb.Chunks.Count];
        for (int i = 0; i < vecs.Length; i++)
        {
            double s = 0;
            var vi = vecs[i];
            for (int d = 0; d < VecDim; d++) s += vi[d] * qv[d];
            cos[i] = s;
        }
        var vecOrder = Enumerable.Range(0, cos.Length)
            .Where(i => cos[i] > 0.05)                  // 过滤噪声（归一化后 0.05 已很低）
            .OrderByDescending(i => cos[i])
            .Take(RecallPerSide)
            .ToList();
        var vecRank = new Dictionary<int, int>();
        for (int r = 0; r < vecOrder.Count; r++) vecRank[vecOrder[r]] = r;

        // ── RRF 融合 ──
        var fused = new Dictionary<int, double>();
        foreach (var kv in bm25Rank) fused[kv.Key] = fused.TryGetValue(kv.Key, out var s0) ? s0 + 1.0 / (RrfK + kv.Value + 1) : 1.0 / (RrfK + kv.Value + 1);
        foreach (var kv in vecRank) fused[kv.Key] = fused.TryGetValue(kv.Key, out var s0) ? s0 + 1.0 / (RrfK + kv.Value + 1) : 1.0 / (RrfK + kv.Value + 1);
        if (fused.Count == 0) return Search(kb, query, topK, expandSynonyms);   // 两路都空 → 退回 BM25

        // 归一化到 [0,1] 供 MMR 使用（RRF 分数上限约 2/(k+1)）
        double maxFused = fused.Values.Max();
        if (maxFused <= 0) maxFused = 1;
        var rel = fused.ToDictionary(kv => kv.Key, kv => kv.Value / maxFused);

        // ── MMR 去重选 topK ──
        const double lambda = 0.7;
        var candidates = rel.Keys.ToList();
        var chosen = new List<int>();
        while (chosen.Count < topK && candidates.Count > 0)
        {
            int best = -1; double bestScore = double.NegativeInfinity;
            foreach (var c in candidates)
            {
                double maxSim = 0;
                foreach (var s in chosen)
                {
                    double sim = 0;
                    var a = vecs[c]; var b = vecs[s];
                    for (int d = 0; d < VecDim; d++) sim += a[d] * b[d];
                    if (sim > maxSim) maxSim = sim;
                }
                double mmr = lambda * rel[c] - (1 - lambda) * maxSim;
                if (mmr > bestScore) { bestScore = mmr; best = c; }
            }
            if (best < 0) break;
            chosen.Add(best);
            candidates.Remove(best);
        }

        // 保持「相关性降序」输出，便于上游按序截断
        return chosen.OrderByDescending(i => rel[i]).Select(i => kb.Chunks[i]).ToList();
    }

    private static int IndexIn(LibraryKb kb, KbChunk c)
    {
        for (int i = 0; i < kb.Chunks.Count; i++) if (ReferenceEquals(kb.Chunks[i], c)) return i;
        int p = c.Page;   // 退化路径：按页码找第一个同页块
        for (int i = 0; i < kb.Chunks.Count; i++) if (kb.Chunks[i].Page == p) return i;
        return 0;
    }

    /// <summary>
    /// **BM25 索引缓存**：库 id → (chunk 数, 每块词频, 块长度, 文档频率, 平均块长)。
    ///
    /// **为什么必须缓存**：原实现**每次查询都把全部 chunk 重新分词一遍**
    /// （`for i in chunks: Tokenize(chunks[i].Text)`），而 `Tokenize` 含连字符双向展开、
    /// 复数归一、CJK bigram —— 一个 1000+ 块的库每次提问要白算几百万次字符操作，
    /// 这正是「回答问题过程卡死」的主因之一。索引只与 chunk 内容有关，与查询无关，缓存后
    /// 每次查询只做 O(查询词数 × 块数) 的查表。
    /// </summary>
    private sealed class Bm25Index
    {
        public int Count;
        public List<Dictionary<string, int>> Tf = new();
        public double[] DocLen = Array.Empty<double>();
        public Dictionary<string, int> Df = new();
        public double AvgDl = 1;
    }

    private static readonly Dictionary<long, Bm25Index> IndexCache = new();

    private static Bm25Index GetIndex(LibraryKb kb)
    {
        if (IndexCache.TryGetValue(kb.LibraryId, out var cached) && cached.Count == kb.Chunks.Count)
            return cached;

        int n = kb.Chunks.Count;
        var idx = new Bm25Index { Count = n, DocLen = new double[n] };
        for (int i = 0; i < n; i++)
        {
            var toks = Tokenize(kb.Chunks[i].Text);
            var tf = new Dictionary<string, int>();
            foreach (var t in toks) tf[t] = tf.TryGetValue(t, out int c) ? c + 1 : 1;
            idx.Tf.Add(tf);
            idx.DocLen[i] = toks.Count;
            foreach (var t in tf.Keys) idx.Df[t] = idx.Df.TryGetValue(t, out int c) ? c + 1 : 1;
        }
        idx.AvgDl = n > 0 ? idx.DocLen.Average() : 1;
        if (idx.AvgDl <= 0) idx.AvgDl = 1;
        IndexCache[kb.LibraryId] = idx;
        return idx;
    }

    // ══════════════ 本地 BM25 检索（无需 embedding） ══════════════

    /// <summary>检索与问题最相关的知识块。expandSynonyms 打开领域同义词扩展（查询侧）。</summary>
    public static List<KbChunk> Search(LibraryKb kb, string query, int topK = 6, bool expandSynonyms = true)
    {
        if (kb == null || kb.Chunks.Count == 0 || string.IsNullOrWhiteSpace(query)) return new List<KbChunk>();

        var qTokens = Tokenize(query, expandSynonyms).Distinct().ToList();
        if (qTokens.Count == 0) return kb.Chunks.Take(topK).ToList();

        var idx = GetIndex(kb);                    // ← 缓存：不再每次重新分词
        int n = idx.Count;
        var docTokens = idx.Tf;
        var docLen = idx.DocLen;
        var df = idx.Df;
        double avgdl = idx.AvgDl;
        const double k1 = 1.2, b = 0.75;
        var scores = new double[n];

        foreach (var t in qTokens)
        {
            if (!df.TryGetValue(t, out int dft)) continue;
            double idf = Math.Log(1 + (n - dft + 0.5) / (dft + 0.5));
            for (int i = 0; i < n; i++)
            {
                if (!docTokens[i].TryGetValue(t, out int f)) continue;
                double denom = f + k1 * (1 - b + b * docLen[i] / avgdl);
                scores[i] += idf * f * (k1 + 1) / denom;
            }
        }

        return kb.Chunks
            .Select((c, i) => (Chunk: c, Score: scores[i]))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Chunk)
            .ToList();
    }

    /// <summary>
    /// 分词：ASCII 单词 + 中日韩 bigram。
    /// 关键处理：
    ///   · 连字符/下划线**双向展开** —— "key-switch" 同时产出 "keyswitch"/"key"/"switch"，
    ///     这样用户搜 keyswitch 或 key switch 都能命中（实测手册写 key-switch 51 次、keyswitch 0 次）；
    ///   · 复数归一 —— "articulations" → "articulation"；
    ///   · 领域同义词（可选，用于查询侧扩展）—— mic↔microphone、ks↔keyswitch 等。
    /// </summary>
    public static List<string> Tokenize(string text, bool expandSynonyms = false)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;

        var word = new StringBuilder();     // 当前复合词（连字符视为连接）
        var segments = new List<string>();  // 复合词的分段
        var cjk = new List<char>();

        void FlushSegments()
        {
            if (segments.Count == 0) return;
            if (segments.Count == 1) AddWord(segments[0], list, expandSynonyms);
            else
            {
                AddWord(string.Concat(segments), list, expandSynonyms);   // keyswitch
                foreach (var s in segments) AddWord(s, list, expandSynonyms); // key, switch
            }
            segments.Clear();
        }
        void FlushWord()
        {
            if (word.Length > 0) { segments.Add(word.ToString()); word.Clear(); }
            FlushSegments();
        }
        void FlushCjk()
        {
            if (cjk.Count == 1) list.Add(cjk[0].ToString());
            for (int i = 0; i + 1 < cjk.Count; i++)
                list.Add(new string(new[] { cjk[i], cjk[i + 1] }));
            cjk.Clear();
        }

        foreach (char ch in text)
        {
            if (IsCjk(ch)) { FlushWord(); cjk.Add(ch); }
            else if (char.IsLetterOrDigit(ch)) { FlushCjk(); word.Append(ch); }
            else if (ch == '-' || ch == '_' || ch == '/' || ch == '.')
            {
                // 连接符：结束当前段但保留在同一复合词里
                FlushCjk();
                if (word.Length > 0) { segments.Add(word.ToString()); word.Clear(); }
            }
            else { FlushWord(); FlushCjk(); }
        }
        FlushWord();
        FlushCjk();
        return list;
    }

    private static void AddWord(string raw, List<string> list, bool expandSynonyms)
    {
        if (raw.Length < 2) return;
        string w = raw.ToLowerInvariant();
        list.Add(w);

        // 复数归一（简单规则；同时给出两种去尾，多出的 token 只会增加命中机会）
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss")) list.Add(w[..^1]);
        if (w.Length > 4 && w.EndsWith("es")) list.Add(w[..^2]);

        if (expandSynonyms && Synonyms.TryGetValue(w, out var syn))
            foreach (var s in syn) list.Add(s);
    }

    /// <summary>Kontakt 领域同义词（查询侧扩展用）。</summary>
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mic"] = new[] { "microphone", "mics" },
        ["mics"] = new[] { "mic", "microphone" },
        ["microphone"] = new[] { "mic", "mics" },
        ["ks"] = new[] { "keyswitch", "keyswitches", "key" },
        ["keyswitch"] = new[] { "ks", "key", "switch" },
        ["cc"] = new[] { "controller", "midi" },
        ["controller"] = new[] { "cc", "midi" },
        ["nki"] = new[] { "instrument", "patch" },
        ["patch"] = new[] { "instrument", "preset" },
        ["preset"] = new[] { "patch", "snapshot" },
        ["snapshot"] = new[] { "preset" },
        ["articulation"] = new[] { "technique", "playing" },
        ["velocity"] = new[] { "vel", "dynamic" },
        ["reverb"] = new[] { "room", "space" },
        ["legato"] = new[] { "slur", "smooth" },
        ["purge"] = new[] { "memory", "unload" },
    };

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK 统一表意
        (c >= 0x3400 && c <= 0x4DBF) ||   // 扩展 A
        (c >= 0x3040 && c <= 0x30FF) ||   // 日文假名
        (c >= 0xAC00 && c <= 0xD7AF);     // 韩文

    /// <summary>按需获取某页图片（缓存优先）。</summary>
    public static byte[]? GetPageImage(long libraryId, string manualPath, int pageNumber, double scale = 1.4)
    {
        string path = PageImagePath(libraryId, pageNumber);
        try
        {
            if (File.Exists(path))
            {
                var cached = File.ReadAllBytes(path);
                if (cached.Length > 100) return cached;
            }
            if (!File.Exists(manualPath)) return null;
            var png = PdfText.RenderPagePng(manualPath, pageNumber - 1, scale);
            if (png == null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            return png;
        }
        catch { return null; }
    }

    /// <summary>删除某库的知识库（含页面图）。</summary>
    public static bool Delete(long libraryId)
    {
        try
        {
            string d = DirFor(libraryId);
            if (Directory.Exists(d)) Directory.Delete(d, true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>生成「库入门指引」：取前若干页（含目录/简介）让模型总结成一段概览。</summary>
    private static async Task<string> BuildOverviewAsync(AiSettings ai, LibraryKb kb, CancellationToken ct)
    {
        var sb = new StringBuilder();
        int taken = 0;
        foreach (var p in kb.Pages.Where(p => p.Text.Length > 80).Take(5))
        {
            sb.AppendLine($"[第 {p.N} 页]").AppendLine(p.Text.Length > 2500 ? p.Text[..2500] : p.Text).AppendLine();
            taken++;
            if (sb.Length > 12000) break;
        }
        if (taken == 0) return "";

        var msgs = new List<ChatMessage>
        {
            new()
            {
                Role = "system",
                Content = "你在为音乐制作人整理 Kontakt 音色库的**入门指引**。" +
                          "依据给出的说明书前几页，用中文输出 150~250 字的概览，覆盖：" +
                          "① 这个库是什么/适合什么音乐；② 包含哪些主要乐器或音色分类；" +
                          "③ 初次使用要注意的 2~3 个要点（如键位切换、麦克风位、演奏法）。" +
                          "只依据材料，不要编造；直接输出结果，不要复述思考过程。",
            },
            new() { Role = "user", Content = sb.ToString() },
        };
        var text = await AiClient.ChatAsync(ai, msgs, ct, maxTokensOverride: 3000);
        var overview = StripThinking(text).Trim();

        // 若拿到的仍像思考过程（没有正式 content），提高预算重试一次
        if (overview.Length > 900 || overview.Contains("让我", StringComparison.Ordinal) ||
            overview.Contains("Let me", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var retry = await AiClient.ChatAsync(ai, msgs, ct, maxTokensOverride: 6000);
                var second = StripThinking(retry).Trim();
                if (second.Length > 40 && second.Length < overview.Length) overview = second;
            }
            catch { }
        }
        return overview.Length > 1500 ? overview[..1500] : overview;
    }

    /// <summary>
    /// 检索兜底：当关键词检索 0 命中时（例如"介绍一下这个库"这类泛问），
    /// 取「开头几页 + 均匀抽样」的块，让模型仍有上下文可用。
    /// </summary>
    public static List<KbChunk> SampleForOverview(LibraryKb kb, int topK = 6)
    {
        if (kb == null || kb.Chunks.Count == 0) return new List<KbChunk>();
        if (kb.Chunks.Count <= topK) return kb.Chunks.ToList();

        var picked = new List<KbChunk>();
        // 前 2 块（通常是封面/简介/目录）
        picked.AddRange(kb.Chunks.Take(2));
        // 均匀抽样补齐
        int step = Math.Max(1, kb.Chunks.Count / Math.Max(1, topK - 2));
        for (int i = 0; i < kb.Chunks.Count && picked.Count < topK; i += step)
        {
            var c = kb.Chunks[i];
            if (!picked.Contains(c)) picked.Add(c);
        }
        return picked.Take(topK).ToList();
    }

    /// <summary>判断是否属于「泛问/概览型」问题（需要额外注入整体背景）。</summary>
    public static bool IsOverviewQuestion(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return false;
        string s = q.ToLowerInvariant();

        // 含具体指向词的问题不算泛问（如"有哪些锣""怎么设置参数"）
        string[] specific =
        {
            "哪些", "哪个", "几种", "多少", "怎么设置", "如何设置", "参数", "按钮",
            "which", "how do i set", "parameter", "button",
        };
        if (specific.Any(k => s.Contains(k))) return false;

        string[] keys =
        {
            "介绍", "是什么", "什么是", "概览", "入门", "怎么用", "如何使用", "有什么用",
            "这个库", "这个音源", "有什么", "包含", "整体",
            "introduce", "overview", "what is", "getting started", "how to use this library",
        };
        return keys.Any(k => s.Contains(k));
    }
}
