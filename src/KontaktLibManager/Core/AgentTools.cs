using System.Text;
using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>
/// Agent 工具的运行时上下文。
///
/// **权限模型**（用户拍板）：
///   · 只读类工具（查索引、读文件、列目录）走**白名单直通**，不打断用户；
///   · 写/删/改类工具必须走「预览改动 → 用户确认 → 执行 → 可回滚」的验权+提权流程，
///     由界面层负责确认，工具本身只负责返回「需要确认」的描述。
///
/// 文件读取被严格限制在 <see cref="AllowedRoots"/>（音色库根目录）之内，
/// 且拒绝路径穿越（`..`）与超出 <see cref="MaxFileBytes"/> 的大文件。
/// </summary>
public sealed class AgentToolContext
{
    public Database? Db { get; set; }

    /// <summary>长期记忆（跨会话）。</summary>
    public AgentMemory? Memory { get; set; }

    /// <summary>任务计划存储（按分支隔离）。</summary>
    public PlanStore? Plans { get; set; }

    /// <summary>当前分支 id（计划挂载点）。</summary>
    public long CurrentBranchId { get; set; }

    /// <summary>计划更新后的回调（界面据此渲染计划卡）。</summary>
    public Action<AgentPlan>? OnPlanChanged { get; set; }

    /// <summary>
    /// **建库进度回调** —— Core 层的建库工具（`build_manual_kb`）通过它把进度推给界面。
    /// 没有它时，Agent 触发的建库会「静默跑」，用户在知识库日志窗口里看不到任何东西
    ///（实测反馈）。由界面层在 BuildToolContext 里接到 Push。
    /// </summary>
    public Action<long, string, KbProgress>? OnKbProgress { get; set; }

    /// <summary>**建库完成回调** —— 界面据此刷新「知识库」与「说明书」列表。</summary>
    public Action<long, string, string, KbDoneSummary>? OnKbDone { get; set; }

    /// <summary>
    /// **取走用户插进来的引导语**（插话引导 / steering）。
    /// Agent 循环在每个步骤边界调用它；返回非空表示用户在 Agent 工作时补了话，
    /// 这些内容会作为 user 消息追加进对话，模型下一步即可据此调整方向。
    /// 由界面层在 BuildToolContext 里接到「每分支一个待办队列」。
    /// </summary>
    public Func<List<string>>? TakeSteering { get; set; }

    /// <summary>
    /// **结果断言登记**（断言式 verifier）—— 工具执行后登记它「声称了什么」，
    /// Agent 在给出最终回答前会复查这些断言，不一致就把差异回灌让模型自纠。
    /// 没登记断言就不校验（绝不误报）。
    /// </summary>
    public List<AssertionVerifier.Claim>? Claims { get; set; }

    /// <summary>登记一条断言（供各工具在返回成功时调用）。</summary>
    public void Claim(string kind, string subject, string source, int count = 0)
    {
        Claims ??= new List<AssertionVerifier.Claim>();
        Claims.Add(new AssertionVerifier.Claim { Kind = kind, Subject = subject ?? "", Source = source ?? "", Count = count });
    }

    /// <summary>当前会话绑定的音色库（用于记忆作用域）。</summary>
    public long? CurrentLibraryId { get; set; }

    /// <summary>只读允许访问的根目录（通常是所有音色库根）。</summary>
    public List<string> AllowedRoots { get; set; } = new();

    /// <summary>单次读取文件的最大字节数。</summary>
    public int MaxFileBytes { get; set; } = 200_000;

    /// <summary>目录列举的最大条目数。</summary>
    public int MaxDirEntries { get; set; } = 300;

    /// <summary>是否允许 Agent 联网（默认关闭，由界面层显式开启）。</summary>
    public bool AllowNetwork { get; set; } = false;

    /// <summary>是否允许 Agent 使用 Shell（默认关闭）。</summary>
    public bool AllowShell { get; set; } = false;
    /// <summary>🔴 权限档位=full 时，连【需要确认】的命令也直接执行（2026-09-26）。</summary>
    public bool ShellAutoApproveAll { get; set; } = false;
    /// <summary>
    /// 🔴 **本轮累计已渲染的播放器【组数】**（2026-09-27 新增）。
    ///   为什么要它：实测 Agent 调 5 次 audition、其中 1 次返回 0 条，
    ///   它却在回答里写「已渲染 5 组、共 20 个播放器」—— **把「调用次数」当成了「成功组数」**，
    ///   于是回答与屏幕对不上（用户反复发现）。
    ///   ⇒ 每次 audition 都回报【本轮累计】，模型就能如实描述。
    /// </summary>
    public int AuditionRenderedGroups { get; set; } = 0;


    /// <summary>Shell 命令的工作目录（默认第一个音色库根）。</summary>
    public string ShellWorkDir { get; set; } = "";

    /// <summary>
    /// 风险命令的用户确认回调：(命令原文, 风险理由) → 是否批准。
    /// 由界面层实现（弹出对话框），返回 false 表示用户拒绝。
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmShell { get; set; }

    /// <summary>
    /// 代码执行的用户确认回调：(语言, 脚本正文) → 是否批准。
    /// 与 ConfirmShell 分开，因为脚本要展示**完整正文**（预览改动）而不是一行命令。
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmScript { get; set; }

    /// <summary>
    /// 界面写操作的确认回调：(动作描述) → 是否批准。
    /// 与 Shell/脚本确认分开，因为界面操作要展示的是「要点哪个控件、什么坐标」。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmUi { get; set; }

    /// <summary>是否允许界面写操作（点击/输入/按键）。默认关。</summary>
    public bool AllowUiControl { get; set; }
    /// <summary>
    /// 试听音频的 URL 解析回调：(完整路径, 扩展名, 库路径) → 可播放 URL。
    /// 由界面层实现（它持有 WebView2 的 kontakt-audio 虚拟主机映射）。
    /// </summary>
    public Func<string, string, string, string?>? ResolveAudioUrl { get; set; }

    /// <summary>
    /// 试听清单就绪回调：Agent 调用 audition 后，界面据此**在对话里渲染内嵌播放器**。
    /// 参数是已经整理好的播放项（name / url / sizeMB）。
    /// </summary>
    public Action<List<object>>? OnAudition { get; set; }

    /// <summary>只允许读取的文本类扩展名（避免把采样当文本读进来）。</summary>
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".ini", ".cfg", ".log", ".html", ".htm",
        ".nki", ".nkm", ".nka", ".nkp", ".ksp", ".yaml", ".yml", ".rtf",
    };

    /// <summary>判断路径是否在白名单内，并返回规范化后的绝对路径。</summary>
    public bool TryResolve(string input, out string full, out string reason)
    {
        full = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(input)) { reason = "路径为空"; return false; }

        try
        {
            // 相对路径按第一个根目录解析
            string candidate = input;
            if (!Path.IsPathRooted(candidate) && AllowedRoots.Count > 0)
                candidate = Path.Combine(AllowedRoots[0], candidate);

            full = Path.GetFullPath(candidate);

            // 拒绝路径穿越
            if (input.Contains("..")) { reason = "路径中不允许包含 .."; return false; }

            foreach (var root in AllowedRoots)
            {
                string r = Path.GetFullPath(root).TrimEnd('\\', '/');
                if (full.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // **本应用自己的溢出目录也必须可读** ——
            // 工具结果过长时 `BudgetToolResult` 会落盘到 `data\tool-results\`，
            // 并把「结果已落盘，读这个路径」告诉模型。若白名单不含该目录，
            // 模型就**永远读不回自己的溢出结果**（实测：read_text_file 连错 7 次）。
            foreach (var own in new[]
            {
                Path.Combine(AppPaths.DataDir, "tool-results"),
                Path.Combine(AppPaths.DataDir, "spill"),
            })
            {
                try
                {
                    string r = Path.GetFullPath(own).TrimEnd('\\', '/');
                    if (full.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }

            reason = "该路径不在允许访问的音色库目录内（只读白名单限制）";
            return false;
        }
        catch (Exception ex)
        {
            reason = "路径解析失败：" + ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Agent 的**只读数据工具**：结构化查索引 + 读文件 + 列目录。
///
/// 设计意图：像「我有哪些弦乐库」「哪些库需要 Kontakt 8」「这个库里有什么乐器」
/// 这类问题**不应该走知识库检索**（既不精确又烧 token），直接查结构化索引最快最准。
/// 文件类工具则让 Agent 能自己翻说明书原文与乐器目录，而不必依赖向量检索。
/// </summary>
public static class AgentTools
{
    public static string QueryLibraries(AgentToolContext ctx, string argsJson)
    {
        var args = Parse(argsJson);
        if (ctx.Db == null) return "{\"error\":\"索引不可用\"}";

        string category = GetStr(args, "category");
        string keyword = GetStr(args, "keyword");
        string minKontakt = GetStr(args, "min_kontakt");
        long minSizeGb = GetLong(args, "min_size_gb");
        long maxSizeGb = GetLong(args, "max_size_gb");
        int limit = (int)Math.Clamp(GetLong(args, "limit") is > 0 and <= 100 ? GetLong(args, "limit") : 30, 1, 100);

        var libs = ctx.Db.GetLibraries();

        IEnumerable<LibraryRecord> q = libs;
        if (category.Length > 0)
            q = q.Where(l => l.Category.Contains(category, StringComparison.OrdinalIgnoreCase));
        if (keyword.Length > 0)
            q = q.Where(l => l.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (minSizeGb > 0)
            q = q.Where(l => l.SizeBytes >= minSizeGb * 1073741824L);
        if (maxSizeGb > 0)
            q = q.Where(l => l.SizeBytes <= maxSizeGb * 1073741824L);

        var list = q.ToList();
        if (minKontakt.Length > 0 && VersionUtil.IsValid(minKontakt))
        {
            list = list.Where(l => !VersionUtil.IsValid(l.RequiredKontakt) ||
                                   VersionUtil.Compare(l.RequiredKontakt, minKontakt) <= 0).ToList();
        }

        // 顺便给出分类汇总，方便回答「我有哪些分类、各多少个库」
        var byCategory = libs.GroupBy(l => l.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new
            {
                category = g.Key,
                libraries = g.Count(),
                totalGb = Math.Round(g.Sum(x => x.SizeBytes) / 1073741824.0, 1),
                names = g.Take(8).Select(x => x.Name),
            });

        var result = new
        {
            matched = list.Count,
            totalLibraries = libs.Count,
            items = list.OrderByDescending(l => l.SizeBytes).Take(limit).Select(l => new
            {
                name = l.Name,
                category = l.Category,
                sizeGb = Math.Round(l.SizeBytes / 1073741824.0, 2),
                nkiCount = l.NkiCount,
                requiredKontakt = l.RequiredKontakt,
                registered = l.RegStatus,
                hasNicnt = l.HasNicnt,
                manualCount = l.ManualCount,
                path = l.Path,
            }),
            categories = byCategory,
            note = list.Count > limit ? $"仅列出体积最大的 {limit} 个，共匹配 {list.Count} 个" : "",
        };
        return ToolJson.S(result);
    }

    public static string QueryInstruments(AgentToolContext ctx, string argsJson)
    {
        var args = Parse(argsJson);
        if (ctx.Db == null) return "{\"error\":\"索引不可用\"}";

        string libName = GetStr(args, "library");
        string kw = GetStr(args, "keyword");
        string articulation = GetStr(args, "articulation");
        string kind = GetStr(args, "kind");
        int limit = (int)Math.Clamp(GetLong(args, "limit") is > 0 and <= 200 ? GetLong(args, "limit") : 40, 1, 200);

        long libId = 0;
        if (libName.Length > 0)
        {
            var lib = ctx.Db.GetLibraries()
                .FirstOrDefault(l => l.Name.Contains(libName, StringComparison.OrdinalIgnoreCase));
            if (lib == null) return $"{{\"error\":\"未找到名称含「{libName}」的音色库\"}}";
            libId = lib.Id;
        }
        var (items, total) = ctx.Db.SearchInstruments(
            query: kw, libraryId: libId,   // 0 = 全部库
            category: "", kind: kind, limit: limit, offset: 0,
            sort: "name", articulation: articulation);

        // 演奏法分布，便于回答「这个库有哪些连奏音色」
        var arts = items.Where(i => i.Articulation.Length > 0)
            .GroupBy(i => i.Articulation)
            .OrderByDescending(g => g.Count())
            .Select(g => new { articulation = g.Key, count = g.Count() });

        return ToolJson.S(new
        {
            total,
            shown = items.Count,
            items = items.Select(i => new
            {
                name = i.Name,
                library = i.LibraryName,
                kind = i.Kind,
                articulation = i.Articulation,
                relPath = i.RelPath,
            }),
            articulationBreakdown = arts,
        });
    }

    /// <summary>
    /// **把「库名」解析成实际目录** —— 模型常把库名（如 `Heavyocity Damage 2`）当路径传进来，
    /// 直接报「目录不存在」会浪费一整轮（实测轨迹里有此失败）。这里在白名单根目录下
    /// 按「完全相等 → 前缀 → 包含」三级匹配目录名，命中即返回其完整路径。
    /// 只读、只扫一层子目录（不递归），代价可忽略。
    /// </summary>
    public static string? TryResolveByLibraryName(AgentToolContext ctx, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string needle = name.Trim().Trim('"', '\'').TrimEnd('\\', '/');
        if (needle.Length < 2 || needle.Contains('\\') || needle.Contains('/')) return null;

        var candidates = new List<string>();
        foreach (var root in ctx.AllowedRoots)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(root))
                    candidates.Add(d);
            }
            catch { }
        }
        if (candidates.Count == 0) return null;

        string leaf(string p) => Path.GetFileName(p.TrimEnd('\\', '/'));

        // ① 完全相等（忽略大小写与首尾空白）
        var hit = candidates.FirstOrDefault(d => leaf(d).Equals(needle, StringComparison.OrdinalIgnoreCase));
        // ② 前缀
        hit ??= candidates.FirstOrDefault(d => leaf(d).StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        // ③ 包含（取最短的那个，避免误配到更长的名字）
        hit ??= candidates.Where(d => leaf(d).Contains(needle, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(d => leaf(d).Length).FirstOrDefault();
        return hit;
    }

    public static string ListDirectory(AgentToolContext ctx, string argsJson)
    {
        var args = Parse(argsJson);
        string p = GetStr(args, "path");
        if (!ctx.TryResolve(p, out string full, out string why))
        {
            // 模型可能传的是「库名」而不是路径 → 尝试解析成目录
            string? byName = TryResolveByLibraryName(ctx, p);
            if (byName != null && ctx.TryResolve(byName, out full, out _))
            {
                // 解析成功，继续往下走
            }
            else
            {
                return $"{{\"error\":\"{Escape(why)}\",\"hint\":\"path 需要**音色库目录的完整路径**。" +
                       $"若你手上只有库名，请先用 query_libraries 查出它的 path 再调用本工具。\"}}";
            }
        }
        if (!Directory.Exists(full)) return "{\"error\":\"目录不存在\"}";

        // 🔴 **新增 pattern / recursive（2026-09-26）** ——
        //   起因（用户实测）：Agent 想「找某目录下的 wav」时，本工具只能列一层、不能按模式筛，
        //   于是它【被迫改用 shell】`Get-ChildItem -Recurse -Filter`，**每次都弹授权**、
        //   一轮里弹了十几次（用户反馈「同一类命令反复要授权」）。
        //   ⇒ **根因是本工具能力不足**，而不是权限模型有问题。
        string pattern = GetStr(args, "pattern").Trim();
        bool recursive = args.ValueKind == System.Text.Json.JsonValueKind.Object
                         && args.TryGetProperty("recursive", out var rc)
                         && rc.ValueKind == System.Text.Json.JsonValueKind.True;

        try
        {
            var dirs = new List<string>();
            var files = new List<(string Path, long Size)>();
            var opt = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                MaxRecursionDepth = 6,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            string match = pattern.Length == 0 ? "*" : pattern;

            foreach (var d in Directory.EnumerateDirectories(full, "*", opt))
            {
                if (pattern.Length > 0 && !WildcardMatch(Path.GetFileName(d), match)) continue;
                dirs.Add(recursive ? Path.GetRelativePath(full, d) : Path.GetFileName(d));
                if (dirs.Count >= ctx.MaxDirEntries) break;
            }
            foreach (var fp in Directory.EnumerateFiles(full, match, opt))
            {
                var fi = new FileInfo(fp);
                files.Add((recursive ? Path.GetRelativePath(full, fp) : fi.Name, fi.Length));
                if (files.Count >= ctx.MaxDirEntries * 20) break;
            }
            var fileRows = files.OrderByDescending(x => x.Size).Take(ctx.MaxDirEntries)
                                .Select(x => new { name = x.Path, size = x.Size }).ToList();

            return ToolJson.S(new
            {
                path = full,
                pattern = pattern.Length > 0 ? pattern : null,
                recursive,
                directories = dirs,
                files = fileRows,
                matchedFiles = files.Count,
                note = files.Count >= ctx.MaxDirEntries * 20
                    ? "命中过多，仅列出最大的若干；请把 pattern 写得更具体"
                    : (fileRows.Count >= ctx.MaxDirEntries ? "文件过多，仅列出最大的若干" : ""),
            });
        }
        catch (Exception ex) { return $"{{\"error\":\"{Escape(ex.Message)}\"}}"; }
    }

    /// <summary>简单通配符匹配（支持 * 和 ?，大小写不敏感）。仅用于文件名筛选。</summary>
    private static bool WildcardMatch(string name, string pattern)
    {
        try
        {
            string rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(
                name, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch { return true; }
    }

    public static string ReadTextFile(AgentToolContext ctx, string argsJson)
    {
        var args = Parse(argsJson);
        string p = GetStr(args, "path");
        if (!ctx.TryResolve(p, out string full, out string why))
            return $"{{\"error\":\"{Escape(why)}\"}}";
        if (!File.Exists(full)) return "{\"error\":\"文件不存在\"}";

        string ext = Path.GetExtension(full);
        if (!AgentToolContext.TextExtensions.Contains(ext))
            return $"{{\"error\":\"出于安全考虑，只允许读取文本类文件（当前为 {Escape(ext)}）。PDF 请用知识库。\"}}";

        try
        {
            var fi = new FileInfo(full);
            if (fi.Length > ctx.MaxFileBytes)
                return $"{{\"error\":\"文件过大（{fi.Length / 1024} KB），超过单次读取上限 {ctx.MaxFileBytes / 1024} KB\"}}";

            string text = File.ReadAllText(full);
            int maxChars = (int)Math.Clamp(GetLong(args, "max_chars") is > 0 and <= 20000 ? GetLong(args, "max_chars") : 8000, 500, 20000);
            bool truncated = text.Length > maxChars;
            if (truncated) text = text[..maxChars];

            return ToolJson.S(new
            {
                path = full,
                name = fi.Name,
                sizeBytes = fi.Length,
                chars = text.Length,
                truncated,
                content = text,
            });
        }
        catch (Exception ex) { return $"{{\"error\":\"{Escape(ex.Message)}\"}}"; }
    }

    /// <summary>把工具参数的 JSON 字符串解析成 JsonElement（失败返回 Undefined，不抛异常）。</summary>
    private static JsonElement Parse(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return default; }
    }

    private static string GetStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";

    private static long GetLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long n) ? n : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out long m) ? m : 0,
            _ => 0,
        };
    }

    private static string Escape(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
}
