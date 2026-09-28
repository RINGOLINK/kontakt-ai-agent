using System.Text;
using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>
/// Agent 的**直接动作工具**（区别于 <see cref="AgentTools"/> 的只读查询工具）。
///
/// **为什么要这一层**（用户实测报告）：
///   用户说「帮我加载一个 Heavyocity 适合摇滚的鼓组音源」，Agent 却走了
///   `ui_list_windows` → `ui_screenshot` → `ui_click` → `ui_type` 这一串
///   **模拟点击 + 视觉判断**的路径，花了 8 次工具调用还只能"部分完成"。
///   而**软件本身早就有这个能力**（`Process.Start(nki)` 直接打开、
///   `KontaktInfo.Launch(exe, nki)` 带文件启动 Kontakt）。
///
/// **设计原则**：
///   凡是本程序已能直接做到的事，就**封装成工具让 Agent 直接调用**，
///   不要让它去模拟鼠标。模拟点击只在「程序做不到」时才用（保底手段）。
///
/// **权限分级**：
///   · 只读/可逆（查、加载、打开、定位、标签、收藏、导出、Quick-Load）→ 直通；
///   · 破坏性（删库、清杂质、移动、取消入库、回滚快照）→ 走 <see cref="AgentToolContext.ConfirmUi"/> 确认。
/// </summary>
public static class AgentActions
{
    private static JsonElement Root(string argsJson)
    {
        try { using var d = JsonDocument.Parse(argsJson.Length > 0 ? argsJson : "{}"); return d.RootElement.Clone(); }
        catch { using var d = JsonDocument.Parse("{}"); return d.RootElement.Clone(); }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static long Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    private static string Ok(object o) => ToolJson.S(o);

    /// <summary>把乐器记录拼成完整文件路径。</summary>
    private static string FullPath(InstrumentRecord it)
        => Path.IsPathRooted(it.RelPath) ? it.RelPath : Path.Combine(it.LibraryPath, it.RelPath);

    // ══════════════════════════════════════════════════════════
    // 1. 加载音色（用户核心诉求）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// **查询 KSP 符号**（Agent 工具 lookup_ksp）—— 让模型「查了再写」，而不是凭印象编造命令。
    ///
    /// KSP 是冷门语言、训练数据覆盖不全，**模型最常见的错误就是编造不存在的命令**。
    /// 本工具检索 KSPCompiler 随包分发的权威符号表（265 个内置命令 + 回调 + UI 类型 + 变量，共 1813 个符号）。
    ///
    /// 三种用法：按名字精确查 / 按关键词搜（名字或描述命中）/ 按类别列（ui/math/midi/array/string/engine/file…）。
    /// </summary>
    // ── KSP 深化三件套（模板库 / 自纠闭环 / 交付）────────────────────

    /// <summary>**列出或取用 KSP 模板**（模板里的命令都已用符号表核对过）。</summary>
    public static string KspTemplates(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string key = Str(args, "key");

        if (string.IsNullOrWhiteSpace(key))
            return Ok(new
            {
                ok = true, count = global::KontaktLibManager.Core.KspTemplates.All().Count,
                note = "**写 KSP 前先取一个模板当起手式** —— 模板里的命令都已用符号表核对过，" +
                       "比凭空写可靠得多。取值时传 key。",
                templates = global::KontaktLibManager.Core.KspTemplates.All().Select(t => new { t.Key, t.Title, t.When, t.Uses }),
            });

        var t = global::KontaktLibManager.Core.KspTemplates.Get(key);
        if (t == null)
            return Ok(new
            {
                ok = false,
                error = $"没有名为「{key}」的模板。",
                available = global::KontaktLibManager.Core.KspTemplates.All().Select(x => x.Key),
            });

        // 支持传 values 填占位符（JSON 对象）
        Dictionary<string, string>? vals = null;
        if (args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty("values", out var vEl)
            && vEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            vals = new Dictionary<string, string>();
            foreach (var p in vEl.EnumerateObject())
                vals[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? (p.Value.GetString() ?? "") : p.Value.ToString();
        }

        var body = global::KontaktLibManager.Core.KspTemplates.Render(key, vals);
        return Ok(new
        {
            ok = true,
            key = t.Key, title = t.Title, when = t.When,
            uses = t.Uses,
            slots = t.Slots,
            script = body,
            note = "`%%名字%%` 是【待填占位符】（未填的会原样保留）。填好后用 compile_ksp 或 ksp_autofix 校验。",
        });
    }

    /// <summary>**KSP 生成→编译→自纠闭环**：编译、按规则机械修复、再编译（最多 N 轮）。</summary>
    public static string KspAutofix(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string code = Str(args, "code");
        int rounds = (int)Num(args, "maxRounds");
        if (rounds <= 0) rounds = 4;
        if (string.IsNullOrWhiteSpace(code)) return Ok(new { ok = false, error = "需要传 code（脚本内容）。" });

        var exe = KspCompiler.Resolve(null);
        if (exe == null) return Ok(new { ok = false, error = "找不到 KSP 编译器（kspc.exe）。" });

        var work = Path.Combine(AppPaths.DataDir, "ksp");
        var r = KspAutoFix.Run(exe, code, work, rounds);

        return Ok(new
        {
            ok = r.Ok,
            passed = r.Ok,
            rounds = r.Rounds.Count,
            summary = r.Summary,
            finalScript = r.FinalScript,
            detail = r.Rounds.Select(x => new { round = x.Index, compiled = x.Compiled, errors = x.ErrorCount, fixes = x.Fixes, remaining = x.Remaining }),
            note = "**只做确定性机械修复**（缺 end / 缺 declare）；逻辑错误修不了，需你自己改。" +
                   "若 passed=false，请读 remaining 与 summary，改完再调一次。",
        });
    }

    /// <summary>**交付 KSP 脚本到 Kontakt**：写到目标目录 + 给出加载指引。</summary>
    public static string KspDeliver(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string code = Str(args, "code");
        string dir = Str(args, "targetDir");
        string name = Str(args, "fileName");
        if (string.IsNullOrWhiteSpace(code)) return Ok(new { ok = false, error = "需要传 code（脚本内容）。" });

        var r = global::KontaktLibManager.Core.KspDeliver.Deliver(code, dir, name);
        return Ok(new
        {
            ok = r.Ok,
            path = r.Path,
            message = r.Message,
            steps = r.Steps,
            note = "**本工具不会自动注入 NKI**（专有格式，无可靠开源方案）—— 交付的是 .ksp 文件 + 加载指引。",
        });
    }

    public static string LookupKsp(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string name = Str(args, "name");
        string keyword = Str(args, "keyword");
        string category = Str(args, "category");
        int limit = (int)Num(args, "limit");
        if (limit <= 0) limit = 25;

        var all = KspSymbols.LoadAll();
        if (all.Count == 0)
            return Ok(new
            {
                ok = false,
                error = "找不到 KSP 符号表。",
                expected = KspSymbols.SymbolsDir,
                hint = "符号表随 kspc.exe 一起分发（Data\\Symbols\\*.yaml）。请确认 data\\kspc\\ 目录完整。",
            });

        // ① 按名字精确查
        if (!string.IsNullOrWhiteSpace(name))
        {
            var hit = KspSymbols.ByName(name);
            if (hit == null)
                return Ok(new
                {
                    ok = false,
                    error = $"**KSP 里没有名为「{name}」的符号** —— 不要使用它，这是编造的命令。",
                    similar = KspSymbols.Search(name, null, 8).Select(s2 => s2.Name).ToList(),
                    hint = "可以用 keyword 搜功能关键词（如 menu / note / array）来找真正可用的命令。",
                });
            return Ok(new { ok = true, found = new { kind = hit.Kind, name = hit.Name, category = hit.Category, signature = KspSymbols.Render(hit) } });
        }

        // ② 类别清单
        if (string.Equals(category, "*", StringComparison.Ordinal) || string.Equals(category, "list", StringComparison.OrdinalIgnoreCase))
            return Ok(new { ok = true, categories = KspSymbols.Categories().Select(c => new { category = c.Category, count = c.Count }).ToList() });

        // ③ 关键词搜 / 按类别列
        var hits = KspSymbols.Search(keyword, string.IsNullOrWhiteSpace(category) ? null : category, limit);
        return Ok(new
        {
            ok = true,
            count = hits.Count,
            total = all.Count,
            results = hits.Select(s2 => new { kind = s2.Kind, name = s2.Name, category = s2.Category, signature = KspSymbols.Render(s2) }).ToList(),
            hint = hits.Count == 0 ? "没匹配到。可以试试 category=\"*\" 看有哪些类别，或换个关键词。" : "",
        });
    }


    /// <summary>
    /// **编译校验 KSP 脚本**（Agent 工具 compile_ksp，调研报告「远期」第 9 项）。
    ///
    /// 把模型生成的 KSP 脚本写到 data\ksp\ 下，调 kspc.exe 编译，**解析输出文本**返回结构化诊断。
    ///
    /// **🔴 关键：KSPCompiler 的编译问题输出为 Warning，退出码仍为 0** ——
    /// 所以本工具**依据输出文本**判成败，并在返回里明确说明依据，避免模型误判「编译通过」。
    ///
    /// 典型用法（三步闭环）：① 生成脚本 → ② compile_ksp → ③ 把诊断回灌让模型改 → 再 compile_ksp。
    /// </summary>
    public static string CompileKsp(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string code = Str(args, "code");
        string name = Str(args, "name");
        string pathIn = Str(args, "path");
        bool obf = args.ValueKind == System.Text.Json.JsonValueKind.Object
                   && args.TryGetProperty("obfuscate", out var ob) && ob.ValueKind == System.Text.Json.JsonValueKind.True;

        string exe = KspCompiler.Resolve(ctx.Db.GetMeta("kspc_path", "")) ?? "";
        if (exe.Length == 0)
            return Ok(new
            {
                ok = false,
                error = "找不到 KSP 编译器（kspc.exe）。",
                expected = KspCompiler.DefaultPath,
                hint = "请把 kspc.exe 放到上述路径（自包含版，用户无需装 .NET），或在设置里指定 kspc_path。",
            });

        // 目标脚本路径
        string kspPath;
        if (!string.IsNullOrWhiteSpace(pathIn) && File.Exists(pathIn))
            kspPath = pathIn;
        else
        {
            if (string.IsNullOrWhiteSpace(code))
                return Ok(new { ok = false, error = "必须提供 code（脚本内容）或 path（已存在的脚本路径）。" });
            var dir = Path.Combine(AppPaths.DataDir, "ksp");
            Directory.CreateDirectory(dir);
            if (string.IsNullOrWhiteSpace(name)) name = "agent_" + DateTime.Now.ToString("HHmmss");
            if (!name.EndsWith(".ksp", StringComparison.OrdinalIgnoreCase)) name += ".ksp";
            kspPath = Path.Combine(dir, name);
            try { File.WriteAllText(kspPath, code, new UTF8Encoding(false)); }
            catch (Exception ex) { return Ok(new { ok = false, error = "写入脚本失败：" + ex.Message }); }
        }

        var r = KspCompiler.Compile(exe, kspPath, obf);
        return Ok(new
        {
            ok = r.Ok,
            script = kspPath,
            exitCode = r.ExitCode,
            summary = r.Summary,
            diagnostics = r.Diagnostics.Select(d => new { severity = d.Severity, line = d.Line, column = d.Column, message = d.Message }).ToList(),
            elapsedMs = r.ElapsedMs,
            judgeBy = "**判成败的依据是输出文本，不是退出码** —— 实测 kspc 对编译问题只输出 Warning 且退出码为 0。",
            next = r.Ok && r.Diagnostics.Count == 0
                ? "编译通过。可以把脚本交付给用户，并说明如何在 Kontakt 里加载/试听。"
                : "请**只改有问题的行**后重新调用 compile_ksp 验证；若判断某条是误报，请明确说明理由。",
        });
    }


    /// <summary>
    /// **提取音色声学特征**（Agent 工具 extract_audio_features，调研报告「远期」第 7 项 · 档 1）。
    ///
    /// 对指定库（或全部库）里【可解码】的音频片段（wav/ogg/aif）提取 32 维声学特征并存入索引库，
    /// 供 find_similar_audio 做余弦 KNN「找相似音色」。
    ///
    /// **⚠️ 硬边界**：Kontakt 专有的 .ncw 与加密容器 .nkx 用开源库解不了，会被跳过并计数 ——
    /// 本机约 45% 的片段可分析。**不要把跳过说成失败，也不要暗示覆盖了全部。**
    /// </summary>
    public static string ExtractAudioFeatures(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string libName = Str(args, "library");
        int maxClips = (int)Num(args, "maxClips");
        if (maxClips <= 0) maxClips = 400;

        var all = ctx.Db.GetLibraries().ToList();
        var targets = string.IsNullOrWhiteSpace(libName)
            ? all
            : all.Where(l => l.Name.Contains(libName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
            return Ok(new { ok = false, error = $"没找到名字含「{libName}」的库。" });

        ctx.Db.EnsureAudioFeaturesTable();
        int scanned = 0, saved = 0, skippedFmt = 0, failed = 0;
        var perLib = new List<object>();

        foreach (var lib in targets)
        {
            int libSaved = 0;
            List<AudioClip> clips;
            // **必须用 GetAnalyzableClips**：GetAudioClips 是给「试听换一批」用的（带随机 + 每库上限），
            // 拿来做批量提取会导致「每次清单都不同、续跑永远完不成」（已在音色地图那边踩过）。
            try { clips = ctx.Db.GetAnalyzableClips(lib.Id); } catch { continue; }
            foreach (var c in clips)
            {
                scanned++;
                // **用 CanAnalyze（含 .ncw）** —— .ncw 是单文件、解码快，值得支持；.nkx 是容器，这里不做。
                if (!AudioFeatures.CanAnalyze(c.Ext) || c.Ext.Equals("nkx", StringComparison.OrdinalIgnoreCase)) { skippedFmt++; continue; }
                var full = Path.Combine(lib.Path, c.RelPath);
                var v = MertFeatures.ExtractAny(full, c.Ext);
                if (v == null) { failed++; continue; }
                ctx.Db.SaveAudioFeatures(c.Id, lib.Id, v);
                saved++; libSaved++;
            }
            if (libSaved > 0) perLib.Add(new { library = lib.Name, extracted = libSaved });
        }

        return Ok(new
        {
            ok = true,
            scanned, extracted = saved, skippedUnsupportedFormat = skippedFmt, failed,
            totalInDb = ctx.Db.CountAudioFeatures(),
            byLibrary = perLib,
            note = "已含 .ncw 解码；跳过的是 .nkx 容器（本工具不做容器解包）与 .mp3。",
            next = "现在可以用 find_similar_audio 找相似音色了。",
        });
    }

    /// <summary>
    /// **找相似音色**（Agent 工具 find_similar_audio）—— 基于已提取的声学特征做余弦 KNN。
    /// 需要先跑过 extract_audio_features；没跑过会明确提示，而不是返回空结果让模型瞎猜。
    /// </summary>
    public static string FindSimilarAudio(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string seed = Str(args, "seed");          // 库名或片段名关键词
        int topK = (int)Num(args, "topK");
        if (topK <= 0) topK = 15;

        int have = ctx.Db.CountAudioFeatures();
        if (have == 0)
            return Ok(new
            {
                ok = false,
                error = "索引库里还没有任何声学特征 —— 请先调用 extract_audio_features 提取。",
                hint = "（只对 wav/ogg/aif 有效；.ncw/.nkx 是 Kontakt 专有格式，解不了。）",
            });

        // 找种子片段：按片段名或所属库名匹配
        var all = ctx.Db.GetLibraries().ToList();
        var seedLibIds = string.IsNullOrWhiteSpace(seed)
            ? all.Select(l => l.Id).ToHashSet()
            : all.Where(l => l.Name.Contains(seed, StringComparison.OrdinalIgnoreCase)).Select(l => l.Id).ToHashSet();

        AudioClip? seedClip = null;
        foreach (var lib in all)
        {
            List<AudioClip> clips;
            try { clips = ctx.Db.GetAnalyzableClips(lib.Id); } catch { continue; }
            foreach (var c in clips)
            {
                if (!AudioFeatures.CanDecode(c.Ext)) continue;
                bool nameHit = !string.IsNullOrWhiteSpace(seed) &&
                               (c.Name.Contains(seed, StringComparison.OrdinalIgnoreCase) || c.RelPath.Contains(seed, StringComparison.OrdinalIgnoreCase));
                bool libHit = seedLibIds.Contains(c.LibraryId);
                if (nameHit || (string.IsNullOrWhiteSpace(seed) && libHit)) { seedClip = c; break; }
            }
            if (seedClip != null) break;
        }
        if (seedClip == null)
            return Ok(new { ok = false, error = $"没找到匹配「{seed}」的可解码音频片段。" });

        var seedVec = ctx.Db.GetAudioFeatures(seedClip.Id);
        if (seedVec == null)
            return Ok(new { ok = false, error = $"片段「{seedClip.Name}」还没有特征（可能未被提取过）。", hint = "先跑 extract_audio_features。" });

        var nn = ctx.Db.FindSimilarAudio(seedVec, topK, seedClip.Id);
        var clipMap = new Dictionary<long, (string Lib, string Path)>();
        foreach (var lib in all)
        {
            try { foreach (var c in ctx.Db.GetAnalyzableClips(lib.Id)) clipMap[c.Id] = (lib.Name, c.RelPath); } catch { }
        }
        var hits = nn.Select(x => new
        {
            score = Math.Round(x.Score, 4),
            library = clipMap.TryGetValue(x.ClipId, out var m) ? m.Lib : "?",
            file = clipMap.TryGetValue(x.ClipId, out var m2) ? m2.Path : "?",
        }).ToList();

        return Ok(new
        {
            ok = true,
            seed = new { library = seedClip.LibraryName, file = seedClip.RelPath },
            count = hits.Count,
            hits,
            note = "相似度基于 32 维声学特征（MFCC + 谱 + 时域 + 起音）的余弦相似度，**只覆盖已提取特征的可解码片段**。",
        });
    }


    /// <summary>
    /// **批量操作的自然语言编排**（Agent 工具 batch_plan，调研报告「远期」第 10 项）。
    ///
    /// 两级用法：
    ///   · dryRun=true（默认）—— **只解析 + 展开**，返回「要动哪些库、为什么命中」，供模型展示给用户审阅；
    ///   · dryRun=false —— 经用户确认后**真正执行**。
    ///
    /// **安全边界**：dryRun=false 时必须过 ConfirmUi；且只有【纯本地、可逆、无副作用】的动作
    /// 会在这里直接执行（tag / rescan-manuals）；涉及注册表写入的 register 与后台建库的 build-kb
    /// 会**引导模型改用各自的专用工具**（那些工具有自己的前置检查与确认流程），不在批处理里偷偷做。
    /// </summary>
    public static async Task<string> BatchPlanTool(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string dsl = Str(args, "dsl");
        bool dryRun = !(args.ValueKind == System.Text.Json.JsonValueKind.Object
                        && args.TryGetProperty("dryRun", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.False);

        var clauses = BatchPlan.Parse(dsl, out string perr);
        if (perr.Length > 0)
            return Ok(new { ok = false, error = perr, hint = "DSL 形式：foreach lib where <条件>: <动作>，多个子句用换行或分号分隔。" });

        var ops = BatchPlan.Expand(ctx.Db, clauses);
        if (ops.Count == 0)
            return Ok(new { ok = true, count = 0, message = "按这个条件没有匹配到任何库。", clauses = clauses.Select(c => c.Raw).ToList() });

        // ── dry-run：只报告，不动任何东西 ──
        if (dryRun)
        {
            var byAction = ops.GroupBy(o => o.Action).Select(g => new
            {
                action = g.Key,
                count = g.Count(),
                samples = g.Take(10).Select(o => new { library = o.LibraryName, reason = o.Reason }).ToList(),
            }).ToList();
            return Ok(new
            {
                ok = true, dryRun = true, total = ops.Count,
                clauses = clauses.Select(c => c.Raw).ToList(), byAction,
                next = "把上面的清单用中文展示给用户（说明要动哪些库、为什么），**征得用户同意后再带 dryRun=false 调用一次**。",
            });
        }

        // ── 真正执行：先确认 ──
        string summary = string.Join("；", ops.GroupBy(o => o.Action).Select(g => $"{g.Key} × {g.Count()} 个库"));
        if (ctx.ConfirmUi == null)
            return Ok(new { ok = false, error = "当前环境没有用户确认通道，无法执行批量写操作。" });
        bool yes = await ctx.ConfirmUi($"即将执行批量操作：\n{summary}\n\n共 {ops.Count} 项。是否继续？");
        if (!yes)
            return Ok(new { ok = false, cancelled = true, message = "用户取消了批量操作。" });

        int done = 0, skipped = 0;
        var errors = new List<string>();
        var needTools = new List<string>();
        var all = ctx.Db.GetLibraries().ToList();

        foreach (var op in ops)
        {
            try
            {
                switch (op.Action)
                {
                    case "list":
                        done++;
                        break;

                    case "tag":
                        if (string.IsNullOrWhiteSpace(op.Arg)) { errors.Add($"{op.LibraryName}：tag 动作缺少标签名（写法 tag:标签名）"); skipped++; break; }
                        var existingTags = ctx.Db.GetEntityTags("library", op.LibraryId);
                        if (!existingTags.Contains(op.Arg)) existingTags.Add(op.Arg);
                        ctx.Db.SetEntityTags("library", op.LibraryId, existingTags);
                        done++;
                        break;

                    case "rescan-manuals":
                        {
                            var lib = all.FirstOrDefault(l => l.Id == op.LibraryId);
                            if (lib == null) { skipped++; break; }
                            var found = LibraryScanner.RescanManuals(lib.Path, lib.Name);
                            ctx.Db.ReplaceManuals(lib.Id, found);
                            done++;
                            break;
                        }

                    case "register":
                    case "build-kb":
                        // **不在批处理里偷偷做**：这两个动作有各自的专用工具与前置检查
                        needTools.Add(op.Action == "register" ? "registerLibraries" : "build_manual_kb");
                        skipped++;
                        break;

                    default:
                        skipped++;
                        break;
                }
            }
            catch (Exception ex) { errors.Add($"{op.LibraryName}：{ex.Message}"); skipped++; }
        }

        if (needTools.Count > 0)
            return Ok(new
            {
                ok = errors.Count == 0, total = ops.Count, done, skipped, errors = errors.Take(20).ToList(),
                needDedicatedTools = needTools.Distinct().ToList(),
                message = $"已完成 {done} 项、跳过 {skipped} 项。其中涉及「{string.Join("/", needTools.Distinct())}」的条目" +
                          "**没有在批处理里执行** —— 请改用对应的专用工具（它们有各自的确认与前置检查）逐个处理。",
            });
        return Ok(new { ok = errors.Count == 0, total = ops.Count, done, skipped, errors = errors.Take(20).ToList(), message = $"批量操作完成：成功 {done} 项，跳过 {skipped} 项。" });
    }


    /// <summary>
    /// **主动建议**（Agent 工具 `get_suggestions`）。
    /// 直接复用 <see cref="SuggestionEngine"/>，把体检结果整理成模型好读的 JSON。
    /// </summary>
    public static string GetSuggestions(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        bool slow = args.ValueKind == System.Text.Json.JsonValueKind.Object
                    && args.TryGetProperty("includeSlow", out var s)
                    && s.ValueKind == System.Text.Json.JsonValueKind.True;
        var list = SuggestionEngine.Analyze(ctx.Db, slow);
        if (list.Count == 0)
            return Ok(new { ok = true, count = 0, message = "体检通过：当前没有发现需要处理的事项。" });
        return Ok(new
        {
            ok = true,
            count = list.Count,
            note = "按影响量排序；每条含 level(high/info)、title、detail、action(可执行动作) 与 samples(涉及的库)。",
            items = list.Select(x => new
            {
                level = x.Level,
                title = x.Title,
                detail = x.Detail,
                action = x.Action,
                actionLabel = x.ActionLabel,
                samples = x.Samples,
            }).ToList(),
        });
    }
    /// <summary>
    /// **列出所有音色库及其说明书 + 知识库状态**（Agent 工具 `list_manuals`）。
    ///
    /// 用户洞察（原话）：Agent 对「我们总共有多少本说明书、分别针对哪个音源」**并不是真的理解** ——
    /// 它只会盲目检索，命中谁算谁。这个工具给它一张**全局地图**：先看清有哪些库、
    /// 每个库有哪些手册、哪些已经建好知识库，再决定该检索哪一本。
    /// </summary>
    public static string ListManuals(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string wantLib = Str(args, "library").Trim();
        bool onlyMissing = args.ValueKind == System.Text.Json.JsonValueKind.Object
                           && args.TryGetProperty("onlyMissingKb", out var om)
                           && om.ValueKind == System.Text.Json.JsonValueKind.True;
        // 🔴 **精简模式（2026-09-25 新增）** ——
        //   起因（用户实测）：问「把所有还没建知识库的库列出来」时，
        //   256 个库 × 每库最多 8 本手册明细 ⇒ JSON 达 7 万字符
        //   ⇒ 被 MaxToolResultChars(8000) 截断、**落盘**
        //   ⇒ Agent 只好**分 4 次读回落盘文件再自己解析**（绕了大弯、还多烧 token）。
        //   ✅ 加 `compact` 参数：只返回「库名 + 手册数 + 建库状态」，不含每本手册明细
        //     ⇒ 体积降到约 1/10，通常不再触发落盘。
        //   ⚠️ **大结果时自动切精简**：即使调用方没传，命中库数超过阈值也自动降级，
        //     免得模型忘记传这个参数又踩同一个坑。
        bool compactRequested = args.ValueKind == System.Text.Json.JsonValueKind.Object
                       && args.TryGetProperty("compact", out var cp)
                       && cp.ValueKind == System.Text.Json.JsonValueKind.True;
        var libs = ctx.Db.GetLibraries().ToList();
        if (libs.Count == 0) return Ok(new { error = "索引里还没有任何音色库，请先扫描。" });
        if (wantLib.Length > 0)
            libs = libs.Where(l => l.Name.Contains(wantLib, StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = new List<object>();
        int totalManuals = 0, builtKb = 0, missingKb = 0;
        foreach (var l in libs)
        {
            var mans = ctx.Db.GetManuals(l.Id);
            var kb = ManualKb.Load(l.Id);
            var best = AiAssistant.PickBestManual(mans);
            bool fresh = best != null && ManualKb.IsFresh(kb, best.FullPath);
            if (mans.Count > 0) totalManuals += mans.Count;
            if (fresh) builtKb++; else if (mans.Count > 0) missingKb++;
            if (onlyMissing && fresh) continue;
            if (mans.Count == 0 && onlyMissing) continue;
            rows.Add(new
            {
                library = l.Name,
                libraryId = l.Id,
                manualCount = mans.Count,
                kbBuilt = fresh,
                kbPages = fresh ? kb!.PageCount : 0,
                kbChunks = fresh ? kb!.Chunks.Count : 0,
                // 只列前 8 本，避免个别库塞满上下文
                // ⚠️ **精简模式下完全不带 manuals 明细**（那是体积的主要来源）
                manuals = compactRequested ? null : mans.OrderByDescending(m => m.IsPrimary).ThenByDescending(m => m.SizeBytes)
                              .Take(8)
                              .Select(m => new
                              {
                                  name = m.Name,
                                  ext = m.Ext,
                                  sizeMB = Math.Round(m.SizeBytes / 1024.0 / 1024.0, 2),
                                  primary = m.IsPrimary,
                              }).ToList(),
                manualsTruncated = !compactRequested && mans.Count > 8,
            });
        }
        // 🔴 **大结果自动切精简（2026-09-25）** ——
        //   实测：327 个库的**完整** JSON 约 82,649 字符、**带对象的精简版**仍有 51,533 字符
        //   ⇒ 都远超 `MaxToolResultChars`(8000) 预算、必然截断落盘。
        //   ⚠️ 关键发现：**体积大头是「库名 + 字段名」本身**（每行 ~150 字符），不是手册明细。
        //   ✅ 所以真正的精简是【只返回名字的纯数组】：
        //      327 个名字 ≈ 15KB 仍然偏大 ⇒ 再**按需截断到前 N 个**并明确告知还有多少。
        //      （模型要全量清单时，应按厂商/前缀分批查，而不是一次拉全部。）
        bool compact = compactRequested;
        bool autoCompacted = false;
        if (!compact && rows.Count > 60)
        {
            compact = true;
            autoCompacted = true;
        }
        if (compact)
        {
            // 只保留「名字」列表 —— 这是唯一能压到预算内的形态
            var names = new List<string>();
            foreach (var l in libs)
            {
                var mans2 = ctx.Db.GetManuals(l.Id);
                var kb2 = ManualKb.Load(l.Id);
                var best2 = AiAssistant.PickBestManual(mans2);
                bool fresh2 = best2 != null && ManualKb.IsFresh(kb2, best2.FullPath);
                if (onlyMissing && fresh2) continue;
                if (mans2.Count == 0 && onlyMissing) continue;
                names.Add(l.Name);
            }
            // 每行约 50 字符 ⇒ 120 个名字约 6KB，留出余量
            const int MaxNames = 120;
            int omitted = Math.Max(0, names.Count - MaxNames);
            return Ok(new
            {
                ok = true,
                compact = true,
                autoCompacted,
                libraryCount = names.Count,
                totalManuals,
                builtKb,
                missingKb,
                omitted,
                note = $"本次为**精简输出**（只给库名）。共 {names.Count} 个库" +
                       (omitted > 0 ? $"，受上下文预算限制只列出前 {MaxNames} 个、还有 {omitted} 个未列" : "") +
                       "。要更精确的清单，请**按厂商名或前缀分批查**（带 `library` 参数）；" +
                       "要看某个库的手册明细，也请单独带 `library` 查该库。",
                names = names.Take(MaxNames).ToList(),
            });
        }
        return Ok(new
        {
            ok = true,
            libraryCount = rows.Count,
            totalManuals,
            builtKb,
            missingKb,
            // **明确告诉模型「这次是精简输出」** —— 免得它以为拿不到明细是工具坏了
            compact = compact,
            autoCompacted,
            compactNote = compact
                ? "本次为**精简输出**（只含库名/手册数/建库状态，不含每本手册的明细）。" +
                  "要看某个库的手册明细，请带上 library 参数单独查该库。"
                : null,
            hint = "要检索某本手册的正文，先用 build_manual_kb 建库，再用 search_manual；" +
                   "search_manual 的每条命中都带 library/manual/page 字段，引用前请核对 library 是否与用户所问一致。",
            libraries = rows,
        });
    }
    /// <summary>
    /// **把某个音色库的说明书建成知识库**（Agent 工具 `build_manual_kb`）。
    ///
    /// 用户说「把说明书转成知识库」时，以前 AI 只能回答「没有这个功能」。
    /// 现在它能自己做：挑库 → 挑手册 → **后台建库并立即返回**。
    ///
    /// **为什么必须后台 + 立即返回**：一本扫描版 PDF 有几百个图片页，每页要调一次视觉模型，
    /// 整个建库可能十几分钟；若在工具里同步等待，Agent 循环会被卡死（用户会以为程序挂了）。
    /// 所以这里只**启动**任务，把进度交给界面的「知识库」面板下方日志窗口实时展示。
    /// </summary>
    public static string BuildManualKb(AgentToolContext ctx, string argsJson)
    {
        var args = Root(argsJson);
        string wantLib = Str(args, "library").Trim();
        string wantManual = Str(args, "manual").Trim();
        bool force = args.ValueKind == System.Text.Json.JsonValueKind.Object
                     && args.TryGetProperty("force", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.True;
        var libs = ctx.Db.GetLibraries().ToList();
        if (libs.Count == 0) return Ok(new { error = "索引里还没有任何音色库，请先扫描。"});
        // 选库：优先显式指定 → 其次当前会话所在库
        var lib = !string.IsNullOrWhiteSpace(wantLib)
            ? libs.FirstOrDefault(l => l.Name.Contains(wantLib, StringComparison.OrdinalIgnoreCase))
            : libs.FirstOrDefault(l => l.Id == ctx.CurrentLibraryId);
        if (lib == null && !string.IsNullOrWhiteSpace(wantLib))
            return Ok(new { error = $"没找到名字包含「{wantLib}」的音色库。可先用 query_libraries 看看有哪些库。"});
        if (lib == null) return Ok(new { error = "没指定库、当前会话也没有绑定库。请说明是哪个音色库。"});
        var manuals = ctx.Db.GetManuals(lib.Id);
        if (manuals.Count == 0)
            return Ok(new { error = $"「{lib.Name}」里没有抓到任何说明书文档（可能确实不带手册）。"});
        var target = !string.IsNullOrWhiteSpace(wantManual)
            ? manuals.FirstOrDefault(m => m.Name.Contains(wantManual, StringComparison.OrdinalIgnoreCase))
              ?? AiAssistant.PickBestManual(manuals)
            : AiAssistant.PickBestManual(manuals);
        if (target == null) return Ok(new { error = $"「{lib.Name}」里没找到可用的说明书。"});
        // 已建且未过期 → 不重复建（除非 force）
        var existing = ManualKb.Load(lib.Id);
        bool fresh = ManualKb.IsFresh(existing, target.FullPath);
        if (fresh && !force)
        {
            // 🔴 **大括号必须补（2026-09-24 修）** ——
            //   原代码【没有大括号】，而 C# 的 `if` 不带大括号时【只作用于下一条语句】⇒
            //   实际执行的是：`if (fresh && !force) ctx.Claim(...);` 然后【无条件 return】！
            //   ⇒ **这个函数永远返回 `alreadyBuilt = true`**，
            //     于是 **`force=true` 从来没生效过、知识库永远无法重建**。
            //   实测症状（用户报）：传 `force: true` 仍返回 alreadyBuilt=true；
            //   以及「0 页 / 0 块」的坏知识库永远修不好。
            //   ⚠️ 缩进把 `return` 伪装成在 if 内，是典型的【误导性缩进】。
                                                                // **登记断言**：声称「知识库已建好」—— 收尾前会复查 kb.json 是否真的落盘且最新
                ctx.Claim(AssertionVerifier.KbBuilt, lib.Name, "build_manual_kb");
                return ToolJson.S(new
                {
                    ok = true, alreadyBuilt = true, library = lib.Name, manual = target.Name,
                    pages = existing?.PageCount ?? 0, chunks = existing?.Chunks.Count ?? 0,
                    message = $"「{lib.Name}」的说明书「{target.Name}」**已经有最新知识库了**（{existing?.PageCount} 页 / {existing?.Chunks.Count} 块），无需重建。" +
                              "可以直接用 search_manual 检索它的正文。若确实要重建，请带 force=true。",
                });
        }
        // **后台启动**（与 MainWindow.KbBuildManual 同一套：推 kbProgress/kbDone/kbError）
        long libId = lib.Id;
        string libName = lib.Name;
        string manualName = target.Name;
        var manual = target;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var ai = AiSettings.Load(ctx.Db);
                // **把进度接给界面** —— 否则 Agent 触发的建库会「静默跑」，
                // 用户在知识库日志窗口里什么都看不到（实测反馈）。
                var prog = ctx.OnKbProgress == null
                    ? null
                    : new Progress<KbProgress>(p => ctx.OnKbProgress(libId, libName, p));
                await ManualKb.BuildAsync(libId, libName, manual, ai.Configured ? ai : null, prog);
                // 完成也推一条（否则日志里只看到进度、看不到落盘结果）
                ctx.OnKbProgress?.Invoke(libId, libName, new KbProgress
                {
                    Phase = "完成", Current = 1, Total = 1, Message = "知识库已落盘",
                });
                // **推 kbDone（带 summary）** —— 前端据此刷新「知识库」与「说明书」两个列表；
                // 只推进度不推完成事件的话，用户建完库后列表仍显示「未建库」（实测反馈）。
                var doneKb = ManualKb.Load(libId);
                ctx.OnKbDone?.Invoke(libId, libName, manualName, new KbDoneSummary
                {
                    Pages = doneKb?.PageCount ?? 0,
                    Chunks = doneKb?.Chunks.Count ?? 0,
                    Chars = doneKb?.TotalChars ?? 0,
                    VisionPages = doneKb?.VisionPages ?? 0,
                });
                ctx.Db.AddTrace(ctx.CurrentBranchId, 0, "build_manual_kb_done",
                    "", $"libraryId={libId}", true, 0, 0, "");
            }
            catch (Exception ex)
            {
                ctx.Db.AddTrace(ctx.CurrentBranchId, 0, "build_manual_kb_fail",
                    "", $"libraryId={libId}", false, 0, 0, ex.Message);
            }
        });
        return ToolJson.S(new
        {
            ok = true, started = true, library = lib.Name, manual = target.Name,
            ext = target.Ext, sizeBytes = target.SizeBytes,
            message = $"已**在后台开始**为「{lib.Name}」建立知识库（手册：{target.Name}）。" +
                      "建库可能需要几分钟（扫描版 PDF 的图片页要逐页交给视觉模型）。" +
                      "**进度实时显示在界面右侧「知识库」面板下方的日志窗口**，完成后会显示页数/块数/字符数。" +
                      "建好后你就可以用 search_manual 检索这本手册的正文了。",
        });
    }
    /// <summary>
    /// 直接加载一个 NKI 到 Kontakt。
    ///
    /// mode = "launch"（默认）：带该 NKI 启动 Kontakt（若已运行则用默认程序打开该文件）；
    /// mode = "open"：用系统默认程序打开该文件（即当前已运行的 Kontakt 会接管）。
    ///
    /// 支持用「乐器名 + 可选库名」模糊定位，也支持直接给绝对路径。
    /// </summary>
    public static string LoadInstrument(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string name = Str(a, "name").Trim();
        string library = Str(a, "library").Trim();
        string rawPath = Str(a, "path").Trim();
        string mode = Str(a, "mode").Trim();
        if (mode.Length == 0) mode = "launch";

        string? target = null;
        string picked = "";

        // ① 直接给了路径
        if (rawPath.Length > 0 && File.Exists(rawPath))
        {
            target = rawPath;
            picked = Path.GetFileName(rawPath);
        }
        else
        {
            // ② 按名称查索引
            long libId = 0;
            if (library.Length > 0)
            {
                var libs = ctx.Db.GetLibraries();
                var hit = libs.FirstOrDefault(l => l.Name.Contains(library, StringComparison.OrdinalIgnoreCase));
                if (hit != null) libId = hit.Id;
            }

            string q = name.Length > 0 ? name : rawPath;
            var (items, total) = ctx.Db.SearchInstruments(query: q, libraryId: libId, limit: 20);
            if (items.Count == 0 && name.Length > 0)
                (items, total) = ctx.Db.SearchInstruments(query: "", libraryId: libId, limit: 20);

            if (items.Count == 0)
                return Ok(new { ok = false, message = $"没找到匹配「{name}」的音色。可先用 query_instruments 查一下确切名称。" });

            var pick = items[0];
            target = FullPath(pick);
            picked = pick.Name;
            if (!File.Exists(target))
                return Ok(new
                {
                    ok = false,
                    message = $"索引里的「{picked}」文件已不存在：{target}",
                    hint = "该库可能被移动过，可先跑健康检查或重新扫描。",
                });
        }

        string exe = ctx.Db.GetMeta("kontakt_exe", "");
        try
        {
            // **绝不走系统「文件关联」打开 `.nki`** ——
            // 旧实现在 Kontakt 已运行时用 `Process.Start(psi) { UseShellExecute = true }` 打开 `.nki`，
            // 这实际是**交给 Windows 的文件关联**：若 `.nki` 没关联到 Kontakt，会弹出「你要用什么程序打开」
            // 对话框（用户实测遇到过），而代码**无条件返回 ok=true** ⇒ 报了个**假成功**
            //（AI 说「已成功交给 Kontakt 打开」，其实只是弹了个选择程序对话框）。
            //
            // 正确做法：把 `.nki` 作为**命令行参数**传给 Kontakt 主程序（Kontakt 会把文件交给已运行实例）。
            if (string.IsNullOrWhiteSpace(exe))
                return Ok(new
                {
                    ok = false,
                    message = "还没在设置里指定 Kontakt 主程序路径，无法加载。",
                    hint = "到「设置 → Kontakt」指定 Kontakt 8 的主程序路径（如 C:\\Program Files\\Native Instruments\\Kontakt 8\\Kontakt 8.exe）。",
                    file = target,
                });

            var (ok, msg) = KontaktInfo.Launch(exe, target);
            
            // **登记断言**：声称「已加载到 Kontakt」—— 收尾前会复查 Kontakt 进程是否真的在跑
            if (ok) ctx.Claim(AssertionVerifier.Loaded, picked, "load_instrument");
            return Ok(new
            {
                ok,
                mode = "launch",
                file = target,
                exe,
                message = ok ? $"已把「{picked}」作为参数交给 Kontakt（{Path.GetFileName(exe)}），由 Kontakt 打开。" : msg,
            });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, message = "打开失败：" + ex.Message, file = target });
        }
    }

    // ══════════════════════════════════════════════════════════
    // 2. Kontakt 程序控制
    // ══════════════════════════════════════════════════════════

    public static string KontaktApp(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string action = Str(a, "action").Trim();
        if (action.Length == 0) action = "status";
        string exe = ctx.Db.GetMeta("kontakt_exe", "");

        switch (action)
        {
            case "status":
                return Ok(new
                {
                    running = KontaktInfo.IsRunning(),
                    exe,
                    version = exe.Length > 0 ? KontaktInfo.ReadVersion(exe) : "",
                    isAdmin = KontaktInfo.IsAdmin(),
                });

            case "launch":
            {
                if (exe.Length == 0) return Ok(new { ok = false, message = "未指定 Kontakt 主程序路径" });
                string file = Str(a, "file");
                var (ok, msg) = KontaktInfo.Launch(exe, file.Length > 0 ? file : null);
                return Ok(new { ok, message = msg });
            }

            case "close":
            {
                var (ok, msg) = KontaktInfo.CloseKontakt();
                return Ok(new { ok, message = msg, running = KontaktInfo.IsRunning() });
            }

            case "library_manager":
            {
                var (ok, msg) = PortableKontaktStore.LaunchLibraryManager(exe);
                return Ok(new { ok, message = msg });
            }

            default:
                return Ok(new { error = "action 只能是 status / launch / close / library_manager" });
        }
    }

    // ══════════════════════════════════════════════════════════
    // 3. 统计看板
    // ══════════════════════════════════════════════════════════

    public static string LibraryStats(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var libs = ctx.Db.GetLibraries();
        long totalBytes = libs.Sum(l => l.SizeBytes);
        var byCat = libs.GroupBy(l => string.IsNullOrEmpty(l.Category) ? "未分类" : l.Category)
                        .Select(g => new { category = g.Key, count = g.Count(), gb = Math.Round(g.Sum(x => x.SizeBytes) / 1073741824.0, 1) })
                        .OrderByDescending(x => x.gb).ToList();
        var top = libs.OrderByDescending(l => l.SizeBytes).Take(10)
                      .Select(l => new { name = l.Name, gb = Math.Round(l.SizeBytes / 1073741824.0, 2), nki = l.NkiCount }).ToList();
        return Ok(new
        {
            libraries = libs.Count,
            totalTB = Math.Round(totalBytes / 1099511627776.0, 2),
            totalNki = libs.Sum(l => l.NkiCount),
            byCategory = byCat,
            top10 = top,
        });
    }

    // ══════════════════════════════════════════════════════════
    // 4. 标签与收藏
    // ══════════════════════════════════════════════════════════

    public static string ManageTags(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string action = Str(a, "action").Trim();
        string library = Str(a, "library").Trim();

        long LibId()
        {
            if (library.Length == 0) return 0;
            var hit = ctx.Db.GetLibraries().FirstOrDefault(l => l.Name.Contains(library, StringComparison.OrdinalIgnoreCase));
            return hit?.Id ?? 0;
        }

        switch (action)
        {
            case "list":
                return Ok(new { tags = ctx.Db.GetAllTags().Select(t => new { name = t.Name, count = t.Count }) });

            case "of":
            {
                long id = Num(a, "libraryId");
                if (id == 0) id = LibId();
                if (id == 0) return Ok(new { error = "未找到该音色库" });
                return Ok(new { libraryId = id, tags = ctx.Db.GetEntityTags("library", id) });
            }

            case "set":
            {
                long id = Num(a, "libraryId");
                if (id == 0) id = LibId();
                if (id == 0) return Ok(new { error = "未找到该音色库" });
                var tags = new List<string>();
                if (a.TryGetProperty("tags", out var tv) && tv.ValueKind == JsonValueKind.Array)
                    foreach (var t in tv.EnumerateArray()) if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString() ?? "");
                int n = ctx.Db.SetEntityTags("library", id, tags.Where(t => t.Length > 0));
                return Ok(new { ok = true, libraryId = id, applied = n, tags });
            }

            case "favorite":
            case "unfavorite":
            {
                long id = Num(a, "libraryId");
                if (id == 0) id = LibId();
                if (id == 0) return Ok(new { error = "未找到该音色库" });
                var lib = ctx.Db.GetLibraries().FirstOrDefault(l => l.Id == id);
                string? nki = ctx.Db.GetFirstInstrumentPath(id);
                if (nki == null) return Ok(new { ok = false, message = "该库没有 NKI 可收藏" });
                var (insts, _) = ctx.Db.SearchInstruments(libraryId: id, limit: 1);
                if (insts.Count == 0) return Ok(new { ok = false, message = "该库没有可收藏的乐器" });
                bool now = ctx.Db.ToggleFavorite(insts[0].Id);
                return Ok(new { ok = true, libraryId = id, libraryName = lib?.Name, favorited = now });
            }

            default:
                return Ok(new { error = "action 只能是 list / of / set / favorite / unfavorite" });
        }
    }

    // ══════════════════════════════════════════════════════════
    // 5. Quick-Load 管理
    // ══════════════════════════════════════════════════════════

    public static string QuickLoad(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string action = Str(a, "action").Trim();
        if (action.Length == 0) action = "list";
        string exe = ctx.Db.GetMeta("kontakt_exe", "");

        switch (action)
        {
            case "list":
            {
                var items = PortableKontaktStore.ListQuickLoadTargets(exe);
                return Ok(new { count = items.Count, items = items.Take(80) });
            }

            case "add":
            {
                string path = Str(a, "path").Trim();
                string lib = Str(a, "library").Trim();
                if (path.Length == 0 && lib.Length > 0)
                {
                    var hit = ctx.Db.GetLibraries().FirstOrDefault(l => l.Name.Contains(lib, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) return Ok(new { ok = false, message = $"未找到音色库「{lib}」" });
                    path = hit.Path;
                }
                if (path.Length == 0) return Ok(new { error = "需要 path 或 library 参数" });
                if (!Directory.Exists(path)) return Ok(new { ok = false, message = "目录不存在：" + path });
                return Ok(new { ok = false, message = "请让用户在界面上用「加入 Quick-Load」按钮操作（该动作需要界面确认）。" });
            }

            case "remove":
            {
                string name = Str(a, "name").Trim();
                if (name.Length == 0) return Ok(new { error = "需要 name 参数（快捷方式名）" });
                return Ok(new { ok = false, message = "请让用户在界面上移除 Quick-Load 项。" });
            }

            default:
                return Ok(new { error = "action 只能是 list / add / remove" });
        }
    }

    // ══════════════════════════════════════════════════════════
    // 6. 重复检测
    // ══════════════════════════════════════════════════════════

    public static string FindDuplicates(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string mode = Str(a, "mode").Trim();
        if (mode.Length == 0) mode = "similar";

        if (mode == "similar")
        {
            double min = 0.35;
            if (a.TryGetProperty("threshold", out var tv) && tv.ValueKind == JsonValueKind.Number && tv.TryGetDouble(out double d))
                min = Math.Clamp(d, 0.1, 0.95);
            var list = DuplicateFinder.FindSimilarLibraries(ctx.Db, min);
            return Ok(new
            {
                mode = "similar",
                count = list.Count,
                items = list.Take(40).Select(s => new
                {
                    a = s.NameA, b = s.NameB, relation = s.RelationLabel, jaccard = Math.Round(s.Jaccard, 3),
                    keep = s.KeepPath, remove = s.DeletePath, needTransferFirst = s.NeedTransferFirst,
                }),
            });
        }

        // 同名大文件扫描（可能很慢，给上限）
        var groups = DuplicateFinder.FindDuplicateFiles(ctx.Db, 50L * 1024 * 1024, null, CancellationToken.None);
        return Ok(new
        {
            mode = "files",
            count = groups.Count,
            items = groups.Take(30).Select(g => new
            {
                sizeMB = Math.Round(g.SizeBytes / 1048576.0, 1),
                fileCount = g.Files.Count,
                sample = g.Files.Take(3),
            }),
        });
    }

    // ══════════════════════════════════════════════════════════
    // 7. 健康检查
    // ══════════════════════════════════════════════════════════

    public static string HealthCheck(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var libs = ctx.Db.GetLibraries();
        int noCover = 0, noManual = 0, unknownVer = 0, missingPath = 0;
        foreach (var l in libs)
        {
            if (!Directory.Exists(l.Path)) { missingPath++; continue; }
            if (string.IsNullOrEmpty(l.CoverFile)) noCover++;
            if (l.ManualCount == 0) noManual++;
            if (string.IsNullOrEmpty(l.RequiredKontakt)) unknownVer++;
        }
        var junk = ctx.Db.GetJunkFiles();
        return Ok(new
        {
            libraries = libs.Count,
            pathMissing = missingPath,
            noCover,
            noManual,
            unknownVersion = unknownVer,
            junkFiles = junk.Count,
            junkMB = Math.Round(junk.Sum(j => j.SizeBytes) / 1048576.0, 1),
            hint = "补封面用 health_fix_covers，清杂质用 health_fix_junk（都会先让你确认）。",
        });
    }

    // ══════════════════════════════════════════════════════════
    // 8. 导出
    // ══════════════════════════════════════════════════════════

    public static string ExportList(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string format = Str(a, "format").Trim();
        if (format.Length == 0) format = "csv";
        string outPath = Str(a, "path").Trim();
        if (outPath.Length == 0)
            outPath = Path.Combine(AppPaths.DataDir, $"library-export-{DateTime.Now:yyyyMMdd-HHmmss}.{format}");

        var libs = ctx.Db.GetLibraries();
        var sb = new StringBuilder();
        if (format == "md")
        {
            sb.AppendLine("# Kontakt 音色库清单");
            sb.AppendLine();
            sb.AppendLine($"共 {libs.Count} 个库，合计 {libs.Sum(l => l.SizeBytes) / 1099511627776.0:F2} TB，{libs.Sum(l => l.NkiCount)} 个 NKI。");
            sb.AppendLine();
            sb.AppendLine("| 名称 | 分类 | 占用 | NKI | 版本要求 | 路径 |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var l in libs.OrderByDescending(l => l.SizeBytes))
                sb.AppendLine($"| {l.Name} | {l.Category} | {l.SizeBytes / 1073741824.0:F2} GB | {l.NkiCount} | {l.RequiredKontakt} | {l.Path} |");
        }
        else
        {
            sb.AppendLine("名称,分类,占用GB,NKI,版本要求,入库状态,路径");
            foreach (var l in libs.OrderByDescending(l => l.SizeBytes))
                sb.AppendLine($"\"{l.Name}\",\"{l.Category}\",{l.SizeBytes / 1073741824.0:F2},{l.NkiCount},\"{l.RequiredKontakt}\",\"{l.RegStatus}\",\"{l.Path}\"");
        }
        try
        {
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
            return Ok(new { ok = true, path = outPath, libraries = libs.Count, message = $"已导出 {libs.Count} 个库到 {outPath}" });
        }
        catch (Exception ex) { return Ok(new { ok = false, message = "导出失败：" + ex.Message }); }
    }

    // ══════════════════════════════════════════════════════════
    // 9. 快照
    // ══════════════════════════════════════════════════════════

    public static string Snapshot(AgentToolContext ctx, string argsJson)
    {
        var a = Root(argsJson);
        string action = Str(a, "action").Trim();
        if (action.Length == 0) action = "list";

        switch (action)
        {
            case "list":
            {
                var list = RegistrationSnapshot.List();
                return Ok(new { count = list.Count, items = list.Take(20) });
            }
            case "capture":
            {
                var (ok, msg, path) = RegistrationSnapshot.Capture(Str(a, "reason").Length > 0 ? Str(a, "reason") : "Agent 创建");
                return Ok(new { ok, message = msg, path });
            }
            default:
                return Ok(new { error = "action 只能是 list / capture（回滚请让用户在界面上确认后操作）" });
        }
    }

    // ══════════════════════════════════════════════════════════
    // 10. 打开路径 / 资源管理器定位（用户要求封装）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 打开一个音色库的真实路径（资源管理器）。用户常问「这个库装在哪」。
    /// </summary>
    public static string OpenPath(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string library = Str(a, "library").Trim();
        string path = Str(a, "path").Trim();
        string mode = Str(a, "mode").Trim();
        if (mode.Length == 0) mode = "open";

        if (path.Length == 0 && library.Length > 0)
        {
            var hit = ctx.Db.GetLibraries().FirstOrDefault(l => l.Name.Contains(library, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return Ok(new { ok = false, message = $"未找到音色库「{library}」" });
            path = hit.Path;
        }
        if (path.Length == 0) return Ok(new { error = "需要 library 或 path 参数" });

        if (!Directory.Exists(path) && !File.Exists(path))
            return Ok(new { ok = false, message = "路径不存在：" + path });

        try
        {
            if (mode == "reveal")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return Ok(new { ok = true, path, message = mode == "reveal" ? "已在资源管理器中定位" : "已打开路径" });
        }
        catch (Exception ex) { return Ok(new { ok = false, message = ex.Message }); }
    }

    // ══════════════════════════════════════════════════════════
    // 11. 版本兼容 / 注册表视图
    // ══════════════════════════════════════════════════════════

    public static string CompatCheck(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        string kv = ctx.Db.GetMeta("kontakt_exe", "").Length > 0 ? KontaktInfo.ReadVersion(ctx.Db.GetMeta("kontakt_exe", "")) : "";
        var libs = ctx.Db.GetLibraries();
        bool KOk(string s) => VersionUtil.IsValid(s);
        var tooOld = libs.Where(l => KOk(l.RequiredKontakt) && KOk(kv) && VersionUtil.Compare(l.RequiredKontakt, kv) > 0)
                         .Select(l => new { name = l.Name, required = l.RequiredKontakt }).Take(40).ToList();
        var unknown = libs.Where(l => !KOk(l.RequiredKontakt)).Select(l => l.Name).Take(40).ToList();
        return Ok(new
        {
            kontaktVersion = kv,
            total = libs.Count,
            tooOldCount = tooOld.Count,
            unknownCount = unknown.Count,
            tooOld,
            unknown,
        });
    }

    public static string RegistryView(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string view = Str(a, "view").Trim();
        if (view.Length == 0) view = "summary";

        if (view == "portable")
        {
            string exe = ctx.Db.GetMeta("kontakt_exe", "");
            var info = PortableKontaktStore.Inspect(exe);
            return Ok(new { isPortable = info != null, root = info?.Root ?? "", names = info?.RegisteredNames?.Take(80) });
        }

        var registered = LibraryRegistrar.ReadRegistered();
        var libs = ctx.Db.GetLibraries();
        LibraryRegistrar.Enrich(libs, registered, KontaktInfo.IsRunning() ? "" : "");
        var byStatus = libs.GroupBy(l => l.RegStatus).Select(g => new { status = g.Key, count = g.Count() }).ToList();
        return Ok(new
        {
            registryCount = registered.Count,
            libraryCount = libs.Count,
            byStatus,
            registered = registered.Keys.Take(60),
        });
    }

    // ══════════════════════════════════════════════════════════
    // 12. 试听（返回采样清单，界面内嵌播放器）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 取一个音色库的试听采样（返回可播放的音频清单）。
    /// 界面会据此渲染内嵌播放器，用户可直接点播。
    /// </summary>
    /// <summary>
    /// **find_diverse_candidates**（2026-09-26 新增，用户提出的架构）。
    /// 「先文本保底 → 再按声学特征做多样性扩展 → 每项给一批」。
    /// </summary>
    public static string FindDiverseCandidates(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string query = Str(a, "query").Trim();
        if (query.Length == 0) return Ok(new { error = "需要 query（要找的关键词，如 cymbal）" });
        int count = (int)Math.Clamp(Num(a, "count") is > 0 and <= 12 ? Num(a, "count") : 5, 1, 12);
        int guarantee = (int)Math.Clamp(Num(a, "guarantee") is >= 0 and <= 12 ? Num(a, "guarantee") : Math.Max(2, count / 2), 0, count);
        int perBatch = (int)Math.Clamp(Num(a, "per_batch") is > 0 and <= 6 ? Num(a, "per_batch") : 3, 1, 6);

        var found = ctx.Db.FindDiverseCandidates(query, count, guarantee, perBatch);
        if (found.Count == 0)
            return Ok(new { ok = true, query, count = 0, note = "没有文本匹配的候选。换更短的关键词（crash / ride / hat），或先用 query_instruments 看哪些库有对应乐器。" });

        return Ok(new
        {
            ok = true,
            query,
            requested = count,
            // 🔴 **凑够了吗**（2026-09-27）—— 用户要求：能试听的必须凑够数量；凑不够要如实说、
            //   且【不能把无法试听的算进试听名额】（它们只能作为推荐列出）。
            shortfall = Math.Max(0, count - found.Count),
            returned = found.Count,
            // **保底 vs 扩展** —— 让模型知道哪些是「正牌」、哪些是「多样性扩展」
            guaranteeCount = found.Count(f => f.MatchedBy == "name"),
            items = found.Select(f => new
            {
                seed = f.Seed.Name,
                library = f.Seed.LibraryName,
                libraryId = f.Seed.LibraryId,
                matchedBy = f.MatchedBy,
                // **与其它入选项的最小嵌入距离** —— 越大说明越「与众不同」
                diversity = Math.Round(f.Diversity, 3),
                batch = f.Batch.Select(b => new { name = b.Name, kind = b.Kind, sizeBytes = b.SizeBytes }).ToList(),
            }),
            note = "已按「文本保底 + 嵌入最远点采样」挑选：matchedBy=name 的是名字真含关键词的保底项，" +
                   "matchedBy=path 的只是所在目录名含关键词（可能不是你要的乐器，例如「Gongs」目录下的锣）。" +
                   "diversity 是它与其它入选项的最小嵌入距离（越大越不同）。",
            nextStep = "要用播放器试听，请对【其中的库】调用 audition（limit 设为 per_batch，match 用同一个关键词）。",
        });
    }

    public static string Audition(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        long libId = Num(a, "libraryId");
        string library = Str(a, "library").Trim();
        string matchName = Str(a, "match").Trim();
        int limit = (int)Math.Clamp(Num(a, "limit") is > 0 and <= 12 ? Num(a, "limit") : 4, 1, 12);

        if (libId == 0 && library.Length > 0)
        {
            var hit = ctx.Db.GetLibraries().FirstOrDefault(l => l.Name.Contains(library, StringComparison.OrdinalIgnoreCase));
            if (hit != null) libId = hit.Id;
        }
        if (libId == 0) return Ok(new { error = "需要 library 或 libraryId 参数" });

        var lib = ctx.Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        // 🔴 **`match` 非空时必须【强过滤】（2026-09-26 修）** ——
        //   旧实现走 GetAudioClips(preferName)，而那个参数**只影响排序、不过滤**
        //   ⇒ Agent 传 `match=cymbal` 想找镲片，却拿到一堆非镲片采样
        //   ⇒ 误判「audition 不灵」（用户实测反馈：「筛选精度还不是很高」）。
        //   ⇒ 有 match 时改用【要求真命中】的查询；没 match 时才用「随机换一批」。
        // 🔴 **先取原始候选，再单独做质量筛选**（2026-09-27）——
        //   分开才能解释「为什么返回 0 条」（是只有预览？还是全是远麦位？）。
        var rawClips = matchName.Length > 0
            ? ctx.Db.GetAudioClipsMatchingRaw(libId, limit * 4, matchName)
            : ctx.Db.GetAudioClips(libId, limit * 4);
        var clips = matchName.Length > 0
            ? AudioQuality.ApplyMicPolicy(rawClips).Take(limit * 4).ToList()
            : rawClips;
        var items = new List<object>();
        // ⚠ 用**库记录里的真实路径**，不要用 c.LibraryPath ——
        //    GetAudioClips 返回的 LibraryPath 可能为空，那样 Path.Combine 会得到相对路径、
        //    CacheAudio 必然失败，表现为「audition 返回 0 个采样」（用户实测报告）。
        string libRoot = lib?.Path ?? "";
        if (libRoot.Length == 0) return Ok(new { error = "该音色库没有可用路径" });
        if (ctx.ResolveAudioUrl == null)
            return Ok(new { error = "试听通道未就绪（ResolveAudioUrl 回调未接线）" });

        foreach (var c in clips)
        {
            if (items.Count >= limit) break;
            string full = Path.IsPathRooted(c.RelPath) ? c.RelPath : Path.Combine(libRoot, c.RelPath);
            string? url = null;
            try { url = ctx.ResolveAudioUrl(full, c.Ext, libRoot); } catch { }
            if (url == null) continue;   // 解不出来就跳过（过大 / 不可解码）
            items.Add(new
            {
                name = c.Name.Length > 0 ? c.Name : Path.GetFileName(c.RelPath),
                url,
                sizeMB = Math.Round(c.SizeBytes / 1048576.0, 2),
            });
        }
        // 通知界面**在对话里渲染内嵌播放器**（用户可直接点播）
        try { ctx.OnAudition?.Invoke(items); } catch { }
        // 🔴 累计本轮的组数（只在真的推了非空组时算）—— 见 AgentToolContext.AuditionRenderedGroups
        if (items.Count > 0) ctx.AuditionRenderedGroups++;
        return Ok(new
        {
            libraryId = libId,
            libraryName = lib?.Name,
            count = items.Count,
            scanned = clips.Count,
            items,
            // 🔴 **告诉模型「用户实际看到几个播放器」（2026-09-26 修）** ——
            //   实测问题：模型对 4 个库各调一次 audition（每次 3~4 条）⇒ **用户看到 14 个播放器**，
            //   但模型的回答只列了 5 个 ⇒ **它描述的是「它想推的」，不是「实际推过去的」**，
            //   表格与屏幕对不上（用户反馈）。
            //   ⇒ 明确回报条数，并在 uiHint 里要求**回答内容必须与实际渲染的一致**。
            rendered = items.Count,
            // 🔴 **本轮累计组数** —— 模型描述时必须用这个数，不能拿「调用次数」当组数（2026-09-27）
            renderedGroupsTotal = ctx.AuditionRenderedGroups,
            uiHint = items.Count > 0
                ? $"本次渲染 {items.Count} 个播放器；**本轮累计已渲染 {ctx.AuditionRenderedGroups} 组**（上一条回答/表格里提到的必须与这 {items.Count} 个一致）。" +
                  "⚠️ **不要在多个库上反复调 audition** —— 每次调用都会【再追加一组播放器】，" +
                  "调 4 次用户就会看到 4 组、和你的推荐数量对不上。" +
                  "推荐 N 个时请把本工具的 limit 设为 N、并尽量【一次调完】。"
                : $"该库没有【可用的真实采样】（候选 {rawClips.Count} 条 → 质量筛选后 0 条）。"
                  + "**注意：这不代表 .ncw 不能解码** —— .ncw/.wav/.ogg/.mp3 都可以解码（本机可试听率 ~99.6%）。"
                  + "真正的原因通常是：① 该库的镲片采样只有 .nksn/.nki **预览**（不是真采样，已按规则剔除）；"
                  + "② 该库的采样都是远距离麦位（Hall/Decca，已按规则剔除）；"
                  + "③ 关键词没匹配上。**请在回答里如实说明是哪种，不要笼统说「无法解码」。**",
        });
    }

    // ══════════════════════════════════════════════════════════
    // 13. 破坏性动作（**必须用户确权**）
    // ══════════════════════════════════════════════════════════

    private static async Task<bool> Confirm(AgentToolContext ctx, string what, string detail)
    {
        if (ctx.ConfirmUi == null) return false;
        return await ctx.ConfirmUi($"{what}\n\n{detail}");
    }

    /// <summary>入库 / 取消入库 / 移动 / 删除音色库。破坏性动作一律先确权。</summary>
    public static async Task<string> ManageLibrary(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string action = Str(a, "action").Trim();
        string library = Str(a, "library").Trim();
        long libId = Num(a, "libraryId");

        if (libId == 0 && library.Length > 0)
        {
            var hit = ctx.Db.GetLibraries().FirstOrDefault(l => l.Name.Contains(library, StringComparison.OrdinalIgnoreCase));
            if (hit != null) libId = hit.Id;
        }
        if (libId == 0) return Ok(new { ok = false, message = $"未找到音色库「{library}」" });
        var lib = ctx.Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return Ok(new { ok = false, message = "未找到该音色库" });

        switch (action)
        {
            case "unregister":
            {
                if (!await Confirm(ctx, $"取消入库「{lib.Name}」",
                        "这会删除该库在 Kontakt 注册表 / 便携版配置里的记录。音色文件不会被删除，之后可以重新入库。"))
                    return Ok(new { ok = false, denied = true, message = "用户拒绝了该操作。" });
                var keys = LibraryRegistrar.ResolveProductKeys(lib.Name, lib.Path, LibraryRegistrar.ReadRegistered());
                int n = 0; var msgs = new List<string>();
                foreach (var k in keys)
                {
                    var (ok, msg) = LibraryRegistrar.Unregister(k);
                    if (ok) n++; else msgs.Add(msg);
                }
                return Ok(new { ok = n > 0, unregistered = n, errors = msgs, message = $"已取消 {n} 个注册项。" });
            }

            case "move_plan":
            {
                string dest = Str(a, "destRoot");
                if (dest.Length == 0) return Ok(new { error = "需要 destRoot 参数（目标根目录）" });
                var plan = MoveService.Plan(ctx.Db, libId, dest);
                return Ok(new
                {
                    plan.LibraryName, plan.SourcePath, plan.DestPath, plan.DestRootKnown,
                    sizeGB = Math.Round(plan.SizeBytes / 1073741824.0, 2), plan.FileCount, plan.NkiCount,
                    freeGB = Math.Round(plan.FreeSpaceBytes / 1073741824.0, 2),
                    canExecute = plan.CanExecute, errors = plan.Errors,
                });
            }

            case "move":
            {
                string dest = Str(a, "destRoot");
                if (dest.Length == 0) return Ok(new { error = "需要 destRoot 参数" });
                var plan = MoveService.Plan(ctx.Db, libId, dest);
                if (!plan.CanExecute) return Ok(new { ok = false, message = string.Join("；", plan.Errors) });
                if (!await Confirm(ctx, $"移动音色库「{lib.Name}」",
                        $"从：{plan.SourcePath}\n到：{plan.DestPath}\n\n约 {plan.SizeBytes / 1073741824.0:F2} GB / {plan.FileCount} 个文件。\n" +
                        "移动完成后会自动更新注册表与索引。"))
                    return Ok(new { ok = false, denied = true, message = "用户拒绝了该操作。" });
                var prog = new Progress<MoveProgress>(p => { });
                var res = await MoveService.ExecuteAsync(plan, ctx.Db, prog, CancellationToken.None);
                return Ok(new { ok = res.Ok, message = res.Message, dest = plan.DestPath });
            }

            case "delete":
            {
                if (!await Confirm(ctx, $"删除音色库「{lib.Name}」",
                        $"路径：{lib.Path}\n占用：{lib.SizeBytes / 1073741824.0:F2} GB\n\n" +
                        "会同时解绑注册表与便携版配置，并把库文件**送进回收站**（可撤销）。"))
                    return Ok(new { ok = false, denied = true, message = "用户拒绝了该操作。" });
                return Ok(new { ok = false, needsUi = true, message = "删除动作涉及回收站与注册表解绑，请让用户在「音色库列表」里用删除按钮完成。" });
            }

            default:
                return Ok(new { error = "action 只能是 unregister / move_plan / move / delete" });
        }
    }

    /// <summary>杂质文件：列出 / 清理（清理必须确权）。</summary>
    public static async Task<string> CleanJunk(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        string action = Str(a, "action").Trim();
        if (action.Length == 0) action = "list";

        var all = ctx.Db.GetJunkFiles();
        if (action == "list")
        {
            var byLib = all.GroupBy(j => j.LibraryName)
                           .Select(g => new { library = g.Key, count = g.Count(), mb = Math.Round(g.Sum(x => x.SizeBytes) / 1048576.0, 1) })
                           .OrderByDescending(x => x.mb).Take(40).ToList();
            return Ok(new
            {
                total = all.Count,
                totalMB = Math.Round(all.Sum(j => j.SizeBytes) / 1048576.0, 1),
                byLibrary = byLib,
                sample = all.Take(25).Select(j => new { j.LibraryName, j.RelPath, mb = Math.Round(j.SizeBytes / 1048576.0, 2) }),
            });
        }

        if (action == "clean")
        {
            // 默认只清理「建议不保留」的；suggestedKeep 的一律跳过（那是用户可能想留的）
            bool all2 = a.TryGetProperty("all", out var av) && av.ValueKind == JsonValueKind.True;
            var targets = all.Where(j => all2 || !j.SuggestedKeep).ToList();
            if (targets.Count == 0) return Ok(new { ok = true, deleted = 0, message = "没有可清理的杂质。" });
            double mb = targets.Sum(j => j.SizeBytes) / 1048576.0;
            if (!await Confirm(ctx, "清理杂质文件",
                    $"将删除 {targets.Count} 个杂质文件，释放约 {mb:F1} MB。\n" +
                    "文件会**送进回收站**（可撤销）。\n" +
                    (all2 ? "⚠ 本次包含「建议保留」的文件。" : "已自动跳过「建议保留」的文件。")))
                return Ok(new { ok = false, denied = true, message = "用户拒绝了该操作。" });

            int deleted = 0, failed = 0; long freed = 0;
            foreach (var j in targets)
            {
                try
                {
                    string full = j.FullPath.Length > 0 ? j.FullPath : "";
                    if (full.Length == 0 || !File.Exists(full)) { failed++; continue; }
                    long sz = new FileInfo(full).Length;
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    deleted++; freed += sz;
                }
                catch { failed++; }
            }
            return Ok(new { ok = true, deleted, failed, freedMB = Math.Round(freed / 1048576.0, 1) });
        }

        return Ok(new { error = "action 只能是 list / clean" });
    }

    /// <summary>补全缺失封面（可逆，直通）。</summary>
    public static string FixCovers(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return Ok(new { error = "数据库不可用" });
        var a = Root(argsJson);
        int max = (int)Math.Clamp(Num(a, "max") is > 0 and <= 200 ? Num(a, "max") : 30, 1, 200);
        var targets = ctx.Db.GetLibraries().Where(l => l.CoverFile.Length == 0).Take(max).ToList();
        int ok = 0, fail = 0;
        foreach (var lib in targets)
        {
            if (string.IsNullOrEmpty(lib.Path) || !Directory.Exists(lib.Path)) { fail++; continue; }
            byte[]? art = null;
            try
            {
                foreach (var n in NicntReader.FindInLibrary(lib.Path, 4))
                {
                    var banner = NicntReader.ExtractArtwork(n.FilePath);
                    if (banner is { Length: > 0 }) { art = banner; break; }
                }
            }
            catch { }
            if (art == null)
            {
                string[] names = { "folder", "cover", "artwork", "front", "thumb", "poster", "box" };
                string[] exts = { ".jpg", ".jpeg", ".png", ".webp" };
                foreach (var nm in names)
                    foreach (var ex in exts)
                    {
                        string p = Path.Combine(lib.Path, nm + ex);
                        if (File.Exists(p)) { art = File.ReadAllBytes(p); break; }
                    }
                if (art == null) { fail++; continue; }
            }
            try { CoverStore.Save(lib.Path, art); ok++; } catch { fail++; }
        }
        return Ok(new { ok = true, filled = ok, failed = fail, remaining = ctx.Db.GetLibraries().Count(l => l.CoverFile.Length == 0) });
    }

    /// <summary>回滚注册表快照（破坏性，必须确权）。</summary>
    public static async Task<string> RestoreSnapshot(AgentToolContext ctx, string argsJson)
    {
        var a = Root(argsJson);
        string path = Str(a, "path").Trim();
        var list = RegistrationSnapshot.List();
        if (path.Length == 0)
            return Ok(new { ok = false, needPath = true, message = "请先指定要回滚的快照 path。", available = list.Take(10) });
        if (!await Confirm(ctx, "回滚注册表快照",
                $"将把注册表恢复到快照：{Path.GetFileName(path)}\n\n这会覆盖当前注册状态，建议先创建一份新快照。"))
            return Ok(new { ok = false, denied = true, message = "用户拒绝了该操作。" });
        var (ok, msg, details) = RegistrationSnapshot.Restore(path);
        return Ok(new { ok, message = msg, details = details.Take(30) });
    }
}
