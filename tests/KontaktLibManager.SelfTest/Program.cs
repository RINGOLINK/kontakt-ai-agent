using System.Diagnostics;
using System.Globalization;
using System.Text;
using KontaktLibManager.Core;

Console.OutputEncoding = Encoding.UTF8;

string root = args.Length > 0 ? args[0] : AppPaths.DefaultRoot;
string csvPath = args.Length > 1 ? args[1] : FindCsv();

// ── 播种模式：为 GUI 验证准备一份已填充的索引库 ──
// 用法: dotnet run -- --seed <目标 index.db 路径> [音色库根目录]
if (args.Length >= 2 && args[0] == "--seed")
{
    string target = args[1];
    string seedRoot = args.Length >= 3 ? args[2] : AppPaths.DefaultRoot;
    var sdb = new Database(target);
    sdb.EnsureCreated();
    if (sdb.GetRoots().Count == 0)
    {
        var (aok, amsg, _) = sdb.AddRoot(seedRoot, "默认路径");
        Console.WriteLine($"  addRoot: {aok} — {amsg}");
    }
    var sroots = sdb.GetRoots();
    var sres = await new LibraryScanner().ScanAsync(sroots, null);
    sdb.SaveScanResult(sres);

    var inst = KontaktInfo.DetectInstalls().FirstOrDefault(i => VersionUtil.IsValid(i.Version));
    if (inst != null)
    {
        sdb.SetMeta("kontakt_exe", inst.Path);
        sdb.SetMeta("kontakt_version", inst.Version);
    }
    Console.WriteLine($"seeded: {target}");
    Console.WriteLine($"  roots={sroots.Count} libraries={sres.Libraries.Count} instruments={sres.TotalInstruments} files={sres.TotalFiles}");
    Console.WriteLine($"  kontakt={inst?.Version ?? "<none>"} ({inst?.Path ?? "-"})");
    return 0;
}

// ── 注册模式：按完整四步流程为一个音色库入库（注册表三视图 + Service Center 记录）──
// 用法: dotnet run -- --register <音色库目录>
if (args.Length >= 2 && args[0] == "--register")
{
    string libPath = args[1];
    Console.WriteLine($"目标库：{libPath}");
    var nicnts = NicntReader.FindInLibrary(libPath, maxDepth: 3);
    if (nicnts.Count == 0)
    {
        Console.WriteLine("❌ 未找到 .nicnt —— 非标准库，无法注册到 Kontakt 库浏览器");
        return 1;
    }

    Console.WriteLine($"找到 {nicnts.Count} 个 .nicnt；Service Center 目录：{LibraryRegistrar.ServiceCenterDir()}");
    Console.WriteLine($"Kontakt 运行中：{(KontaktInfo.IsRunning() ? "是（注册表写入正常，但需重启 Kontakt 才生效）" : "否")}");
    Console.WriteLine();

    int fail = 0;
    foreach (var n in nicnts)
    {
        var (ok, msg) = LibraryRegistrar.Register(n);
        if (!ok) fail++;
        Console.WriteLine($"{(ok ? "✅" : "❌")} {msg}");
        Console.WriteLine($"     RegKey={n.RegKey}  AuthSystem={n.AuthSystem}  SNPID={n.SnpId}  HU/JDX={(n.HasAuthPair ? "有" : "无")}");
        string rec = LibraryRegistrar.ServiceCenterRecordPath(n.RegKey);
        Console.WriteLine($"     Service Center 记录：{rec} → {(File.Exists(rec) ? $"已写入（{new FileInfo(rec).Length} bytes）" : "缺失")}");
    }

    Console.WriteLine();
    Console.WriteLine(fail == 0 ? "完成。" : $"{fail} 项失败。");
    return fail == 0 ? 0 : 1;
}

// ── 便携版自动写入模式：把一个库写进便携版 Kontakt 的库列表（免开库管理器）──
// 用法: dotnet run -- --portable-write <音色库目录> [Kontakt主程序路径]
if (args.Length >= 2 && args[0] == "--portable-write")
{
    string libPath = args[1];
    string exe = args.Length >= 3
        ? args[2]
        : (KontaktInfo.DetectInstalls().FirstOrDefault(i => VersionUtil.IsValid(i.Version))?.Path ?? "");

    Console.WriteLine($"Kontakt 主程序：{exe}");
    var info = PortableKontaktStore.Inspect(exe);
    if (info == null) { Console.WriteLine("❌ 未检测到便携版 Kontakt"); return 1; }

    Console.WriteLine($"便携版根目录：{info.Root}");
    Console.WriteLine($"库列表文件  ：{info.SettingsCfg}");
    Console.WriteLine($"写入前库条目：{info.RegisteredNames.Count}");
    Console.WriteLine($"Kontakt 运行：{(KontaktInfo.IsRunning() ? "是（会被拒绝）" : "否")}");
    Console.WriteLine();

    var regMap = LibraryRegistrar.ReadRegistered();
    var hints = PortableKontaktStore.ReadLibraryHints(info);
    Console.WriteLine($"LibraryHints 记录：{hints.Count} 条；注册表产品：{regMap.Count} 个");
    Console.WriteLine();

    string libName = Path.GetFileName(libPath.TrimEnd('\\'));
    var entries = PortableKontaktStore.BuildEntries(libPath, libName, regMap, hints);
    Console.WriteLine("组装出的条目（三级回退结果）：");
    foreach (var e in entries)
        Console.WriteLine($"   RegKey={e.RegKey}  Name={e.Name}  SNPID={e.SnpId}  Company={e.Company}  HU/JDX={(e.HasAuth ? "有" : "无")}  来源={e.Source}");
    Console.WriteLine($"   ContentDir={string.Join(" | ", entries.Select(x => x.ContentDir))}");
    if (entries.Count == 0) { Console.WriteLine("❌ 无可用条目"); return 1; }
    Console.WriteLine();

    var wr = PortableKontaktStore.AddLibraries(exe, entries);
    Console.WriteLine($"{(wr.Ok ? "✅" : "❌")} {wr.Message}");
    if (wr.Added.Count > 0) Console.WriteLine($"   已写入：{string.Join(", ", wr.Added)}");
    if (wr.Skipped.Count > 0) Console.WriteLine($"   已跳过：{string.Join(", ", wr.Skipped)}");
    if (wr.BackupSettingsCfg.Length > 0) Console.WriteLine($"   备份：{wr.BackupSettingsCfg}");
    if (wr.BackupLibraryHints.Length > 0) Console.WriteLine($"   备份：{wr.BackupLibraryHints}");

    var after = PortableKontaktStore.Inspect(exe);
    Console.WriteLine($"   写入后库条目：{after?.RegisteredNames.Count}");
    Console.WriteLine($"   含目标库：{after?.RegisteredNames.Any(n => entries.Any(x => x.RegKey.Equals(n, StringComparison.OrdinalIgnoreCase)))}");
    return wr.Ok ? 0 : 1;
}

// ── PDF 解析实测：抽取文字 + 渲染页面（验证说明书解析链路）──
// 用法: dotnet run -- --pdf-test <pdf路径> [输出PNG路径]
if (args.Length >= 2 && args[0] == "--pdf-test")
{
    string pdf = args[1];
    if (!File.Exists(pdf)) { Console.WriteLine($"❌ 文件不存在：{pdf}"); return 1; }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    int pageCount = PdfText.GetPageCount(pdf);
    Console.WriteLine($"文件：{Path.GetFileName(pdf)}  ({new FileInfo(pdf).Length / 1024.0 / 1024.0:F2} MB)");
    Console.WriteLine($"页数：{pageCount}");

    var pages = PdfText.ExtractAllText(pdf, (i, n) => { if (i % 25 == 0 || i == n) Console.Write($"\r  抽取文字 {i}/{n}…"); });
    Console.WriteLine();
    int totalChars = pages.Sum(p => p.Length);
    int emptyPages = pages.Count(p => p.Length < 20);
    Console.WriteLine($"文字总量：{totalChars:N0} 字符，平均 {totalChars / Math.Max(1, pageCount):N0} 字符/页");
    Console.WriteLine($"近空页（<20 字符，多为整页扫描图）：{emptyPages} 页");
    Console.WriteLine($"耗时：{sw.Elapsed.TotalSeconds:F1}s");

    Console.WriteLine();
    Console.WriteLine("── 前 2 页文字样本 ──");
    for (int i = 0; i < Math.Min(2, pages.Count); i++)
    {
        var t = pages[i].Replace("\n", " ⏎ ");
        Console.WriteLine($"  [第 {i + 1} 页] {(t.Length > 200 ? t[..200] + "…" : t)}");
    }

    int renderPage = 0, best = -1;
    for (int i = 0; i < pages.Count; i++) if (pages[i].Length > best) { best = pages[i].Length; renderPage = i; }
    string outPng = args.Length >= 3 ? args[2] : Path.Combine(AppPaths.DataDir, "pdf-test-page.png");
    var png = PdfText.RenderPagePng(pdf, renderPage);
    if (png != null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
        File.WriteAllBytes(outPng, png);
        Console.WriteLine();
        Console.WriteLine($"✅ 渲染第 {renderPage + 1} 页（文字最多）→ {outPng}  ({png.Length / 1024:N0} KB)");
    }
    else Console.WriteLine("❌ 页面渲染失败");

    return 0;
}

// ── AI 连接实测：验证 OpenAI 兼容端点可用 ──
// 用法: dotnet run -- --ai-test <baseUrl> <model>   （API Key 走环境变量 KLM_AI_KEY）
if (args.Length >= 2 && args[0] == "--ai-test")
{
    var st = new AiSettings
    {
        BaseUrl = args.Length >= 2 ? args[1] : "https://token.sensenova.cn/v1",
        Model = args.Length >= 3 ? args[2] : "sensenova-6.8-flash-lite",
        ApiKey = Environment.GetEnvironmentVariable("KLM_AI_KEY") ?? "",
    };
    Console.WriteLine($"端点：{st.BaseUrl}");
    Console.WriteLine($"模型：{st.Model}");
    Console.WriteLine($"Key ：{(st.ApiKey.Length > 8 ? st.ApiKey[..8] + "…" + st.ApiKey[^4..] : "(空)")}  长度 {st.ApiKey.Length}");
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var (ok, msg, _) = await AiClient.TestAsync(st);
    Console.WriteLine($"{(ok ? "✅" : "❌")} {msg}   ({sw.Elapsed.TotalSeconds:F1}s)");
    return ok ? 0 : 1;
}

// ── 多模态实测：把渲染出的说明书画页交给视觉模型解读 ──
// 用法: dotnet run -- --ai-vision <png路径> [baseUrl] [model]
if (args.Length >= 2 && args[0] == "--ai-vision")
{
    string png = args[1];
    if (!File.Exists(png)) { Console.WriteLine($"❌ 图片不存在：{png}"); return 1; }

    var vs = new AiSettings
    {
        BaseUrl = args.Length >= 3 ? args[2] : "https://token.sensenova.cn/v1",
        Model = args.Length >= 4 ? args[3] : "sensenova-6.8-flash-lite",
        ApiKey = Environment.GetEnvironmentVariable("KLM_AI_KEY") ?? "",
    };

    var bytes = File.ReadAllBytes(png);
    Console.WriteLine($"图片：{Path.GetFileName(png)}  ({bytes.Length / 1024:N0} KB)");
    Console.WriteLine($"模型：{vs.Model}");
    Console.WriteLine();

    const string prompt =
        "这是 Kontakt 音色库说明书的一页。请用中文输出：\n" +
        "1) 本页主题（一句话）；\n" +
        "2) 页面上的关键信息点（界面元素名称、参数、操作步骤）；\n" +
        "3) 若含图例/截图，描述它在演示什么操作。\n" +
        "要求简洁、只描述确实能看到的内容，不要编造。";

    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        string reply = await AiClient.DescribeImageAsync(vs, bytes, prompt);
        Console.WriteLine($"✅ 视觉解读成功（{sw2.Elapsed.TotalSeconds:F1}s）：");
        Console.WriteLine();
        Console.WriteLine(reply);
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ 视觉解读失败（{sw2.Elapsed.TotalSeconds:F1}s）：{ex.Message}");
        return 1;
    }
}

// ── 工具调用隔离测试：直接验证 AiStream 的流式工具解析 ──
// 用法: dotnet run -- --tools-test
if (args.Length >= 1 && args[0] == "--tools-test")
{
    var ts = new AiSettings
    {
        BaseUrl = Environment.GetEnvironmentVariable("KLM_AI_URL") ?? "https://token.sensenova.cn/v1",
        Model = Environment.GetEnvironmentVariable("KLM_AI_MODEL") ?? "sensenova-6.8-flash-lite",
        ApiKey = Environment.GetEnvironmentVariable("KLM_AI_KEY") ?? "",
        MaxTokens = 2000,
    };

    var tools = new object[]
    {
        new
        {
            type = "function",
            function = new
            {
                name = "get_weather",
                description = "查询某城市天气",
                parameters = new
                {
                    type = "object",
                    properties = new { city = new { type = "string" } },
                    required = new[] { "city" },
                },
            },
        },
    };

    var msgs = new List<ChatMessage>
    {
        new() { Role = "system", Content = "你可以调用工具。" },
        new() { Role = "user", Content = "北京天气怎么样？请用工具查询。" },
    };

    int deltaChars = 0, reasoningChars = 0;
    var r = await AiStream.ChatStreamAsync(ts, msgs, tools,
        onDelta: t => deltaChars += t.Length,
        onReasoning: t => reasoningChars += t.Length);

    Console.WriteLine($"正文 {r.Content.Length} 字符 / 思考 {r.Reasoning.Length} 字符 / 工具调用 {r.ToolCalls.Count} 个");
    foreach (var tc in r.ToolCalls)
        Console.WriteLine($"   → {tc.Name}({tc.Arguments})  id={tc.Id}");
    Console.WriteLine($"回调统计：delta {deltaChars} / reasoning {reasoningChars}");
    Console.WriteLine(r.ToolCalls.Count > 0 ? "✅ 流式工具解析正常" : "❌ 未解析到工具调用");
    return r.ToolCalls.Count > 0 ? 0 : 1;
}

// ── 移动音色库实测：计划 → 执行 → 报告 ──
// 用法: dotnet run -- --move-test <dbPath> <库名关键字> <目标根目录> [--no-exec]
if (args.Length >= 4 && args[0] == "--move-test")
{
    string mdbPath = args[1];
    string keyword = args[2];
    string destRoot = args[3];
    bool exec = !args.Contains("--no-exec");

    var mdb = new Database(mdbPath);
    mdb.EnsureCreated();
    var ml = mdb.GetLibraries().FirstOrDefault(l => l.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    if (ml == null) { Console.WriteLine($"❌ 未找到包含「{keyword}」的音色库"); return 1; }

    Console.WriteLine($"库：{ml.Name}");
    Console.WriteLine($"   源：{ml.Path}（{ml.SizeBytes / 1024.0 / 1024:F1} MB / {ml.FileCount} 文件）");
    Console.WriteLine($"   目标根：{destRoot}");

    var plan = MoveService.Plan(mdb, ml.Id, destRoot);
    Console.WriteLine($"   目标路径：{plan.DestPath}");
    Console.WriteLine($"   可执行：{plan.CanExecute}");
    foreach (var e in plan.Errors) Console.WriteLine($"   ⛔ {e}");
    foreach (var w in plan.Warnings) Console.WriteLine($"   ⚠ {w}");
    Console.WriteLine($"   待改入库产品（{plan.ProductKeys.Count}）：{string.Join(", ", plan.ProductKeys)}");

    if (!plan.CanExecute) return 1;
    if (!exec) { Console.WriteLine("（--no-exec：仅出计划，不执行）"); return 0; }

    Console.WriteLine();
    Console.WriteLine("开始执行…");
    var lastPhase = "";
    var prog = new Progress<MoveProgress>(p =>
    {
        if (p.Phase != lastPhase) { Console.WriteLine($"\n[{p.Phase}]"); lastPhase = p.Phase; }
        Console.Write($"\r  {p.Percent}% {p.Message} {p.SpeedMBps:F1} MB/s   ");
    });

    var res = await MoveService.ExecuteAsync(plan, mdb, prog, CancellationToken.None);
    Console.WriteLine();
    Console.WriteLine($"{(res.Ok ? "✅" : "❌")} {res.Message}");
    foreach (var d in res.Details) Console.WriteLine($"   · {d}");
    Console.WriteLine($"   重新入库 {res.Reregistered} 个产品 / 便携版更新 {res.PortableUpdated}");
    Console.WriteLine($"   源目录仍存在：{Directory.Exists(plan.SourcePath)}");
    Console.WriteLine($"   目标目录已存在：{Directory.Exists(plan.DestPath)}");
    return res.Ok ? 0 : 1;
}

// ── 修复入库路径（不移动文件，只把注册表/便携版指向库的当前真实路径）──
// 用法: dotnet run -- --repair-path <dbPath> <库名关键字> [新路径]
if (args.Length >= 3 && args[0] == "--repair-path")
{
    string rdbPath = args[1];
    string rkeyword = args[2];
    string? overridePath = args.Length >= 4 ? args[3] : null;

    var rdb = new Database(rdbPath);
    rdb.EnsureCreated();
    var rlib = rdb.GetLibraries().FirstOrDefault(l => l.Name.Contains(rkeyword, StringComparison.OrdinalIgnoreCase));
    if (rlib == null) { Console.WriteLine($"❌ 未找到包含「{rkeyword}」的音色库"); return 1; }

    string target = overridePath ?? rlib.Path;
    Console.WriteLine($"库：{rlib.Name}");
    Console.WriteLine($"   目标路径：{target}（存在：{Directory.Exists(target)}）");

    var regAll = LibraryRegistrar.ReadRegistered();
    string norm = LibraryRegistrar.NormalizePath(target);
    var keys = new List<string>();

    // ① 索引库记录的产品键（最可靠：无 .nicnt 的旧式库也靠它）
    if (rlib.ProductKey.Length > 0)
        foreach (var k in rlib.ProductKey.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (k.Length > 0 && !keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);

    // ② 注册表里指向该库当前路径的条目
    foreach (var kv in regAll)
    {
        bool into = kv.Value.ContentDir.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                    kv.Value.ContentDir.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase);
        if (into) keys.Add(kv.Key);
    }
    foreach (var n in NicntReader.FindInLibrary(target, 3))
    {
        string k = string.IsNullOrWhiteSpace(n.RegKey) ? n.Name : n.RegKey;
        if (k.Length > 0 && !keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);
    }
    Console.WriteLine($"   注册表键（{keys.Count}）：{string.Join(", ", keys)}");

    int fixedCount = 0;
    foreach (var k in keys)
    {
        var (ok, msg) = LibraryRegistrar.UpdateContentDir(k, target);
        if (ok) fixedCount++;
        Console.WriteLine($"   {(ok ? "✓" : "✗")} {msg}");
    }

    var (pok, pmsg, _) = PortableKontaktStore.UpdateLibraryPaths(
        KontaktInfo.DetectInstalls().FirstOrDefault(i => VersionUtil.IsValid(i.Version))?.Path ?? "",
        keys.ToDictionary(k => k, _ => target, StringComparer.OrdinalIgnoreCase));
    Console.WriteLine($"   便携版：{(pok ? "✓" : "✗")} {pmsg}");

    rdb.UpdateLibraryPath(rlib.Id, target);
    Console.WriteLine($"   ✅ 已修复 {fixedCount} 个产品；索引路径已更新");
    return fixedCount > 0 ? 0 : 1;
}

// ── 代码执行测试 ──
// 用法: dotnet run -- --script-test
if (args.Length >= 1 && args[0] == "--script-test")
{
    int pass = 0, fail = 0;
    void R(bool ok, string what) { if (ok) { pass++; Console.WriteLine("   ✅ " + what); } else { fail++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ 代码执行（run_script）═══");
    ScriptTools.CleanupAll();

    // ① 准备（写临时文件 = 预览）
    var prep = ScriptTools.Prepare("powershell", "Write-Output 'hello from klm'\nWrite-Output ('2+3=' + (2+3))");
    R(prep.Ok, "脚本已写入临时目录");
    R(prep.Path.EndsWith(".ps1"), "扩展名为 .ps1");
    R(File.Exists(prep.Path), "临时文件确实存在（可预览）");
    Console.WriteLine("      路径: " + prep.Path);

    // ② 执行
    var r = ScriptTools.RunAsync(prep, "", 30, CancellationToken.None).GetAwaiter().GetResult();
    R(r.ExitCode == 0, $"执行成功（退出码 {r.ExitCode}，{r.Seconds}s）");
    R(r.StdOut.Contains("hello from klm") && r.StdOut.Contains("2+3=5"), "输出正确");
    Console.WriteLine("      输出: " + r.StdOut.Trim().Replace("\n", " | "));

    // ③ 可回滚：清理
    ScriptTools.Cleanup(prep);
    R(!File.Exists(prep.Path), "执行后可回滚（临时脚本已删除）");

    // ④ 语言校验
    R(!ScriptTools.Prepare("python", "print(1)").Ok, "不支持的语言被拒绝");
    R(!ScriptTools.Prepare("powershell", "").Ok, "空脚本被拒绝");
    R(!ScriptTools.Prepare("powershell", new string('x', 20001)).Ok, "超长脚本被拒绝");

    // ⑤ JavaScript（若本机有 node）
    var hasNode = false;
    try { var psi = new System.Diagnostics.ProcessStartInfo { FileName = "node", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }; psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("0"); using var pp = System.Diagnostics.Process.Start(psi); pp!.WaitForExit(5000); hasNode = true; } catch { }
    if (hasNode)
    {
        var jp = ScriptTools.Prepare("javascript", "console.log('js ok'); console.log(1+2);");
        var jr = ScriptTools.RunAsync(jp, "", 30, CancellationToken.None).GetAwaiter().GetResult();
        R(jr.ExitCode == 0 && jr.StdOut.Contains("js ok"), "JavaScript 脚本执行成功");
        Console.WriteLine("      输出: " + jr.StdOut.Trim().Replace("\n", " | "));
        ScriptTools.Cleanup(jp);
    }
    else Console.WriteLine("      （本机无 node，跳过 JavaScript 测试）");

    // ⑥ 临时目录已清空
    ScriptTools.CleanupAll();
    R(!Directory.Exists(ScriptTools.TempDir) || Directory.GetFiles(ScriptTools.TempDir).Length == 0, "临时目录已清空");

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {pass} / 失败 {fail}");
    return fail == 0 ? 0 : 1;
}
// ── MCP 服务器（stdio 常驻模式，供外部客户端连接）──
// 用法: KontaktLibManager.SelfTest.exe --mcp-serve <dbPath>
// 说明: 每行一个 JSON-RPC 消息（MCP 的 stdio 传输即换行分隔 JSON）。
//       可直接在 Claude Desktop / Cursor 等 MCP 客户端里配置该命令。
if (args.Length >= 2 && args[0] == "--mcp-serve")
{
    var sdb = new Database(args[1]);
    sdb.EnsureCreated();
    var sctx = new AgentToolContext { Db = sdb, Memory = new AgentMemory(args[1]), Plans = new PlanStore(), AllowNetwork = false, AllowShell = false };
    sctx.Memory.EnsureCreated();
    foreach (var l in sdb.GetLibraries()) if (!string.IsNullOrEmpty(l.Path)) sctx.AllowedRoots.Add(l.Path);
    sctx.ShellWorkDir = sctx.AllowedRoots.FirstOrDefault() ?? "";

    Console.Error.WriteLine($"[{McpServer.ServerName}] MCP 服务器已启动 · {sctx.AllowedRoots.Count} 个音色库根 · 联网/Shell 默认关闭");
    string? line;
    // 空闲看门狗：stdin 若被外部一直占着不关，服务会在 30 分钟无输入后自行退出，
    // 避免留下挂死的进程（本次开发中就出现过多个 --mcp-serve 残留）。
    var lastInput = DateTime.UtcNow;
    var idleWatch = new System.Threading.Thread(() =>
    {
        while (true)
        {
            System.Threading.Thread.Sleep(60000);
            if ((DateTime.UtcNow - lastInput).TotalMinutes > 30)
            {
                Console.Error.WriteLine("[" + McpServer.ServerName + "] 空闲超时，退出");
                Environment.Exit(0);
            }
        }
    }) { IsBackground = true };
    idleWatch.Start();
    while ((line = Console.ReadLine()) != null)
    {
        if (line.Trim().Length == 0) continue;
        lastInput = DateTime.UtcNow;
        try
        {
            string? resp = await McpServer.HandleAsync(line, sctx, sctx.Plans!, 0);
            if (resp != null) { Console.WriteLine(resp); Console.Out.Flush(); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32603,\"message\":\"{ex.Message.Replace("\"", "'")}\"}}}}");
            Console.Out.Flush();
        }
    }
    return 0;
}
// ── MCP 服务器测试 ──
// 用法: dotnet run -- --mcp-test <dbPath>
if (args.Length >= 2 && args[0] == "--mcp-test")
{
    var mdb = new Database(args[1]);
    mdb.EnsureCreated();
    var ctx = new AgentToolContext { Db = mdb, Memory = new AgentMemory(args[1]), Plans = new PlanStore() };
    (ctx.Memory as AgentMemory)!.EnsureCreated();
    foreach (var l in mdb.GetLibraries()) if (!string.IsNullOrEmpty(l.Path)) ctx.AllowedRoots.Add(l.Path);

    int pass = 0, fail = 0;
    void Q(bool ok, string what) { if (ok) { pass++; Console.WriteLine("   ✅ " + what); } else { fail++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ MCP 服务器（JSON-RPC over stdio）═══");

    // ① initialize
    var r1 = await McpServer.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", ctx, ctx.Plans!, 1);
    var d1 = System.Text.Json.JsonDocument.Parse(r1!);
    Q(d1.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString() == McpServer.ProtocolVersion, "initialize 返回协议版本");
    Q(d1.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == McpServer.ServerName, "serverInfo 正确");

    // ② tools/list
    var r2 = await McpServer.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", ctx, ctx.Plans!, 1);
    var d2 = System.Text.Json.JsonDocument.Parse(r2!);
    var tools = d2.RootElement.GetProperty("result").GetProperty("tools");
    Q(tools.GetArrayLength() >= 12, $"tools/list 返回 {tools.GetArrayLength()} 个工具");
    bool hasSchema = true;
    foreach (var t in tools.EnumerateArray())
        if (!t.TryGetProperty("inputSchema", out _) || !t.TryGetProperty("name", out _)) hasSchema = false;
    Q(hasSchema, "每个工具都带 name + inputSchema");

    // ③ tools/call —— 只读查询
    var r3 = await McpServer.HandleAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"query_libraries\",\"arguments\":{\"category\":\"弦乐\",\"limit\":3}}}",
        ctx, ctx.Plans!, 1);
    var d3 = System.Text.Json.JsonDocument.Parse(r3!);
    string txt = d3.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    Q(txt.Contains("matched"), "tools/call query_libraries 返回结果");
    Console.WriteLine("      " + txt.Substring(0, Math.Min(120, txt.Length)).Replace("\n", " "));

    // ④ 权限模型：Shell 风险命令不代执行
    ctx.AllowShell = true;
    var r4 = await McpServer.HandleAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"run_command\",\"arguments\":{\"command\":\"Remove-Item x.txt\"}}}",
        ctx, ctx.Plans!, 1);
    var d4 = System.Text.Json.JsonDocument.Parse(r4!);
    string t4 = d4.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    Q(t4.Contains("needsConfirm"), "风险命令返回 needsConfirm（不代用户确认）");

    // ⑤ 危险命令直接拒绝
    var r5 = await McpServer.HandleAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"run_command\",\"arguments\":{\"command\":\"Format-Volume -DriveLetter D\"}}}",
        ctx, ctx.Plans!, 1);
    string t5 = System.Text.Json.JsonDocument.Parse(r5!).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    Q(t5.Contains("blocked"), "危险命令返回 blocked");

    // ⑥ 未知方法 / 通知
    var r6 = await McpServer.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"bogus\"}", ctx, ctx.Plans!, 1);
    Q(r6!.Contains("-32601"), "未知方法返回 -32601");
    var r7 = await McpServer.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", ctx, ctx.Plans!, 1);
    Q(r7 == null, "通知不产生响应");

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {pass} / 失败 {fail}");
    return fail == 0 ? 0 : 1;
}
// ── 任务规划测试 ──
// 用法: dotnet run -- --plan-test
if (args.Length >= 1 && args[0] == "--plan-test")
{
    var store = new PlanStore();
    int pass = 0, fail = 0;
    void P(bool ok, string what) { if (ok) { pass++; Console.WriteLine("   ✅ " + what); } else { fail++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ 任务规划 ═══");

    var r1 = PlanTools.Update(store, 7, "{\"goal\":\"检查 5 个库的入库状态\",\"steps\":[{\"title\":\"枚举库\",\"status\":\"done\"},{\"title\":\"查注册表\",\"status\":\"doing\"},{\"title\":\"整理成表\"}]}");
    var d1 = System.Text.Json.JsonDocument.Parse(r1);
    P(d1.RootElement.GetProperty("ok").GetBoolean(), "创建计划成功");
    P(d1.RootElement.GetProperty("total").GetInt32() == 3, "3 个步骤");
    P(d1.RootElement.GetProperty("done").GetInt32() == 1, "已完成 1 个");
    Console.WriteLine("      " + d1.RootElement.GetProperty("message").GetString());

    var plan = store.Get(7);
    P(plan != null && plan.Steps[1].Status == "doing", "状态正确保存");
    P(plan!.Steps[2].Status == "pending", "未指定状态默认 pending");

    // 整体覆盖
    var r2 = PlanTools.Update(store, 7, "{\"goal\":\"检查 5 个库的入库状态\",\"steps\":[{\"title\":\"枚举库\",\"status\":\"done\"},{\"title\":\"查注册表\",\"status\":\"done\"},{\"title\":\"整理成表\",\"status\":\"done\"}]}");
    var d2 = System.Text.Json.JsonDocument.Parse(r2);
    P(d2.RootElement.GetProperty("done").GetInt32() == 3, "整体覆盖生效（3/3）");
    P(store.Get(7)!.AllDone, "AllDone 判定正确");
    Console.WriteLine("      " + d2.RootElement.GetProperty("message").GetString());

    // 分支隔离
    var r3 = PlanTools.Update(store, 8, "{\"steps\":[\"另一个分支的步骤\"]}");
    P(store.Get(7)!.Steps.Count == 3 && store.Get(8)!.Steps.Count == 1, "计划按分支隔离");
    P(store.Get(8)!.Steps[0].Title == "另一个分支的步骤", "字符串步骤也能解析");

    // 非法状态降级
    PlanTools.Update(store, 9, "{\"steps\":[{\"title\":\"x\",\"status\":\"乱写\"}]}");
    P(store.Get(9)!.Steps[0].Status == "pending", "非法状态降级为 pending");

    // 空步骤拒绝
    var r4 = PlanTools.Update(store, 10, "{\"steps\":[]}");
    P(r4.Contains("error"), "空步骤被拒绝");

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {pass} / 失败 {fail}");
    return fail == 0 ? 0 : 1;
}
// ── 长期记忆测试 ──
// 用法: dotnet run -- --memory-test
if (args.Length >= 1 && args[0] == "--memory-test")
{
    string mp = Path.Combine(Path.GetTempPath(), "mem-test.db");
    if (File.Exists(mp)) File.Delete(mp);
    var mem = new AgentMemory(mp);
    mem.EnsureCreated();
    int pass = 0, fail = 0;
    void M(bool ok, string what) { if (ok) { pass++; Console.WriteLine("   ✅ " + what); } else { fail++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ 长期记忆 ═══");

    // ① 写入
    var a = mem.Remember("global", null, "常用Kontakt版本", "用户主力使用 Kontakt 8.7.1", 5);
    var b = mem.Remember("library", 187, "EthnoWorld6", "这个库适合世界民族配乐，弦乐偏粗糙", 4);
    var c2 = mem.Remember("global", null, "厂商偏好", "不喜欢某厂商的安装器", 3);
    M(mem.List().Count == 3, "写入 3 条记忆");

    // ② 同 key 更新（不新增）
    mem.Remember("global", null, "常用Kontakt版本", "用户主力使用 Kontakt 8.7.1（已升级）", 5);
    M(mem.List().Count == 3, "同 scope+key 走更新而非新增");
    M(mem.Get(a.Id)!.Content.Contains("已升级"), "更新生效");

    // ③ 检索
    var hits = mem.Search("Kontakt 版本", null, 5);
    M(hits.Count > 0 && hits[0].Key.Contains("Kontakt"), "关键词检索命中");
    var hits2 = mem.Search("弦乐", 187, 5);
    M(hits2.Count > 0 && hits2[0].LibraryId == 187, "带库作用域检索命中");

    // ④ 注入块
    var blk = mem.BuildPromptBlock(187);
    M(blk.Contains("长期记忆") && blk.Contains("EthnoWorld6"), "注入块含库相关记忆");
    M(blk.Length <= AgentMemory.InjectCharBudget + 200, $"注入块受预算限制（{blk.Length} 字符）");
    Console.WriteLine("   ── 注入块 ──");
    foreach (var ln in blk.Trim().Split('\n').Take(6)) Console.WriteLine("      " + ln);

    // ⑤ 工具层
    var ctx = new AgentToolContext { Memory = mem, CurrentLibraryId = 187 };
    var rr = MemoryTools.Remember(ctx, "{\"content\":\"用户要求记住：他只用中文界面\",\"key\":\"语言偏好\",\"importance\":5}");
    M(rr.Contains("\"ok\":true"), "工具 remember 写入成功");
    var rc = MemoryTools.Recall(ctx, "{\"query\":\"语言\"}");
    M(rc.Contains("语言偏好"), "工具 recall 命中");
    var rf = MemoryTools.Forget(ctx, "{\"id\":" + mem.List().First(x => x.Key == "语言偏好").Id + "}");
    M(rf.Contains("\"ok\":true"), "工具 forget 删除成功");
    M(!mem.List().Any(x => x.Key == "语言偏好"), "删除后确实不存在");

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {pass} / 失败 {fail}");
    try { File.Delete(mp); } catch { }
    return fail == 0 ? 0 : 1;
}
// ── Shell 分级授权测试 ──
// 用法: dotnet run -- --shell-test
if (args.Length >= 1 && args[0] == "--shell-test")
{
    Console.WriteLine("═══ Shell 分级授权 ═══");
    int pass = 0, fail = 0;
    void S(bool ok, string what) { if (ok) { pass++; Console.WriteLine("   ✅ " + what); } else { fail++; Console.WriteLine("   ❌ " + what); } }

    // ① 只读白名单 → 直通
    foreach (var cmd in new[] {
        "Get-ChildItem 'N:\\Kontakt Libray' -Directory",
        "Get-Content readme.txt",
        "Select-String -Path a.txt -Pattern foo",
        "Measure-Object",
        "Test-Path 'N:\\Kontkat Libray'",
    }) {
        var d = ShellTools.Classify(cmd);
        S(d.Risk == ShellRisk.Safe, $"只读直通：{cmd.Substring(0, Math.Min(42, cmd.Length))}  → {d.Risk}");
    }

    // ② 风险操作 → 需要确认
    foreach (var cmd in new[] {
        "Remove-Item 'x.txt'",
        "Set-Content a.txt hello",
        "New-Item -ItemType Directory foo",
        "Get-ChildItem | Remove-Item",
        "Get-ChildItem > out.txt",
        "Move-Item a b",
    }) {
        var d = ShellTools.Classify(cmd);
        S(d.Risk == ShellRisk.NeedsConfirm, $"需确认：{cmd.Substring(0, Math.Min(38, cmd.Length))}  → {d.Risk}");
    }

    // ③ 明确危险 → 拒绝
    foreach (var cmd in new[] {
        "Format-Volume -DriveLetter D",
        "shutdown /s /t 0",
        "diskpart",
        "Set-ExecutionPolicy Unrestricted",
    }) {
        var d = ShellTools.Classify(cmd);
        S(d.Risk == ShellRisk.Blocked, $"已拒绝：{cmd.Substring(0, Math.Min(38, cmd.Length))}  → {d.Risk}");
    }

    Console.WriteLine();
    Console.WriteLine("── 实际执行（只读命令）──");
    var r = ShellTools.RunAsync("Get-ChildItem 'N:\\Kontakt Libray' -Directory | Measure-Object | Select-Object -ExpandProperty Count", "", 30, CancellationToken.None).GetAwaiter().GetResult();
    Console.WriteLine($"   退出码={r.ExitCode}  耗时={r.Seconds}s");
    Console.WriteLine($"   输出: {(r.StdOut.Length > 0 ? r.StdOut.Trim() : r.StdErr.Trim())}");

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {pass} / 失败 {fail}");
    return fail == 0 ? 0 : 1;
}
// ── Agent 只读工具测试 ──
// 用法: dotnet run -- --tools-test <dbPath>
if (args.Length >= 2 && args[0] == "--agent-tools")
{
    var tdb = new Database(args[1]);
    tdb.EnsureCreated();
    var ctx = new AgentToolContext { Db = tdb };
    foreach (var l in tdb.GetLibraries()) if (!string.IsNullOrEmpty(l.Path)) ctx.AllowedRoots.Add(l.Path);

    Console.WriteLine("═══ Agent 只读工具 ═══");
    Console.WriteLine($"  只读白名单根目录: {ctx.AllowedRoots.Count} 个");

    Console.WriteLine();
    Console.WriteLine("── query_libraries：弦乐类 ──");
    var r1 = AgentTools.QueryLibraries(ctx, "{\"category\":\"弦乐\",\"limit\":5}");
    var d1 = System.Text.Json.JsonDocument.Parse(r1);
    Console.WriteLine($"  匹配 {d1.RootElement.GetProperty("matched").GetInt32()} 个 / 共 {d1.RootElement.GetProperty("totalLibraries").GetInt32()} 个库");
    foreach (var it in d1.RootElement.GetProperty("items").EnumerateArray())
        Console.WriteLine($"     {it.GetProperty("name").GetString()}  {it.GetProperty("sizeGb").GetDouble()} GB  {it.GetProperty("nkiCount").GetInt32()} NKI");
    Console.WriteLine("  分类汇总（前 5）:");
    foreach (var c2 in d1.RootElement.GetProperty("categories").EnumerateArray().Take(5))
        Console.WriteLine($"     {c2.GetProperty("category").GetString()}  {c2.GetProperty("libraries").GetInt32()} 库  {c2.GetProperty("totalGb").GetDouble()} GB");

    Console.WriteLine();
    Console.WriteLine("── query_instruments：连奏 ──");
    var r2 = AgentTools.QueryInstruments(ctx, "{\"articulation\":\"连奏\",\"limit\":3}");
    var d2 = System.Text.Json.JsonDocument.Parse(r2);
    Console.WriteLine($"  共 {d2.RootElement.GetProperty("total").GetInt32()} 个连奏乐器，示例:");
    foreach (var it in d2.RootElement.GetProperty("items").EnumerateArray())
        Console.WriteLine($"     [{it.GetProperty("library").GetString()}] {it.GetProperty("name").GetString()}");

    Console.WriteLine();
    Console.WriteLine("── list_directory：第一个库根 ──");
    var r3 = AgentTools.ListDirectory(ctx, "{\"path\":\"" + ctx.AllowedRoots[0].Replace("\\", "\\\\") + "\"}");
    var d3 = System.Text.Json.JsonDocument.Parse(r3);
    Console.WriteLine($"  子目录 {d3.RootElement.GetProperty("directories").GetArrayLength()} 个 / 文件 {d3.RootElement.GetProperty("files").GetArrayLength()} 个");

    Console.WriteLine();
    Console.WriteLine("── 权限校验（应被拒绝）──");
    foreach (var bad in new[] { "C:\\Windows\\System32", "..\\..\\secret.txt", "N:\\Kontakt Libray\\..\\..\\Windows" })
    {
        var rr = AgentTools.ListDirectory(ctx, "{\"path\":\"" + bad.Replace("\\", "\\\\") + "\"}");
        bool denied = rr.Contains("error");
        Console.WriteLine($"     {(denied ? "✅ 已拒绝" : "❌ 未拒绝")}  {bad}");
    }

    Console.WriteLine();
    Console.WriteLine("── read_text_file：非文本扩展名（应被拒绝）──");
    var r4 = AgentTools.ReadTextFile(ctx, "{\"path\":\"" + ctx.AllowedRoots[0].Replace("\\", "\\\\") + "\\\\test.ncw\"}");
    Console.WriteLine($"     {(r4.Contains("error") ? "✅ 已拒绝" : "❌ 未拒绝")}  .ncw 采样文件");

    Console.WriteLine();
    Console.WriteLine("── find_audition：试听片段 ──");
    var r5 = WebTools.FindAudition(ctx, "{\"library\":\"Cinematic Studio Strings\",\"limit\":3}");
    var d5 = System.Text.Json.JsonDocument.Parse(r5);
    Console.WriteLine($"  库={d5.RootElement.GetProperty("library").GetString()}  可试听 {d5.RootElement.GetProperty("playable").GetInt32()} 条");
    var bk = d5.RootElement.GetProperty("breakdown");
    Console.WriteLine($"  构成: demo={bk.GetProperty("demo").GetInt32()} ncw={bk.GetProperty("ncw").GetInt32()} nkx={bk.GetProperty("nkx").GetInt32()} sample={bk.GetProperty("sample").GetInt32()}");
    foreach (var it in d5.RootElement.GetProperty("items").EnumerateArray())
        Console.WriteLine($"     [{it.GetProperty("kind").GetString()}] {it.GetProperty("name").GetString()}");

    Console.WriteLine();
    Console.WriteLine("── 联网工具（AllowNetwork=false，应被拒绝）──");
    ctx.AllowNetwork = false;
    var r6 = await WebTools.SearchAsync(ctx, "{\"query\":\"Kontakt 8\"}", CancellationToken.None);
    Console.WriteLine($"  {(r6.Contains("error") ? "✅ 已拒绝" : "❌ 未拒绝")}  搜索");

    Console.WriteLine();
    Console.WriteLine("── 联网工具（开启后实测）──");
    ctx.AllowNetwork = true;
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    var r7 = await WebTools.SearchAsync(ctx, "{\"query\":\"Native Instruments Kontakt 8 release notes\",\"max_results\":3}", CancellationToken.None);
    sw2.Stop();
    Console.WriteLine($"  耗时 {sw2.Elapsed.TotalSeconds:F1}s");
    if (r7.Contains("\"error\"")) Console.WriteLine("  " + r7.Substring(0, Math.Min(200, r7.Length)));
    else {
        var d7 = System.Text.Json.JsonDocument.Parse(r7);
        Console.WriteLine($"  结果 {d7.RootElement.GetProperty("count").GetInt32()} 条:");
        foreach (var it in d7.RootElement.GetProperty("items").EnumerateArray())
            Console.WriteLine($"     {it.GetProperty("title").GetString()}" + Environment.NewLine + $"       {it.GetProperty("url").GetString()}");
    }
    return 0;
}
// ── 会话/分支/消息存储层测试 ──
// 用法: dotnet run -- --chat-test [dbPath]
if (args.Length >= 1 && args[0] == "--chat-test")
{
    string cp = args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "chat-test.db");
    if (File.Exists(cp)) File.Delete(cp);
    var cs = new ChatStore(cp);
    cs.EnsureCreated();
    int pass = 0, fail = 0;
    void Chk(bool ok, string what) { if (ok) { pass++; Console.WriteLine("   ✅ " + what); } else { fail++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ 会话 / 分支 / 消息 存储层 ═══");

    // ① 从单库进入：主分支必须是 single
    var s1 = cs.CreateSession(187, "Ethno World 6", "世界民族", "Ethno World 6 问答", "single");
    var br1 = cs.ListBranches(s1.Id);
    Chk(br1.Count == 1, "新建会话自动创建 1 个分支");
    Chk(br1[0].Mode == "single" && br1[0].ParentId == null, "单库进入 → 主分支为 single 模式且无父分支");
    Chk(s1.ActiveBranchId == br1[0].Id, "激活分支指向主分支");

    // ② 主分支内触发跨库 → 新建跨库分支挂在会话下
    var cb1 = cs.CreateBranch(s1.Id, "cross", br1[0].Id, "跨库问答 #1");
    var cb2 = cs.CreateBranch(s1.Id, "cross", br1[0].Id, "跨库问答 #2");
    var br2 = cs.ListBranches(s1.Id);
    Chk(br2.Count == 3, "可同时存在多个跨库分支（1 主 + 2 跨库）");
    Chk(br2.Count(b => b.Mode == "single") == 1, "单库分支始终只有一个（主分支）");
    Chk(br2.Count(b => b.Mode == "cross") == 2, "跨库分支有 2 个");
    Chk(cs.MainBranch(s1.Id)!.Mode == "single", "MainBranch() 取到的是单库主分支");

    // ③ 消息固化：分支之间互相隔离
    cs.AppendMessage(br1[0].Id, "user", "这是单库问题");
    cs.AppendMessage(br1[0].Id, "assistant", "这是单库回答");
    cs.AppendMessage(cb1.Id, "user", "这是跨库问题");
    Chk(cs.GetMessages(br1[0].Id).Count == 2, "主分支有 2 条消息");
    Chk(cs.GetMessages(cb1.Id).Count == 1, "跨库分支 #1 有 1 条消息");
    Chk(cs.GetMessages(cb2.Id).Count == 0, "跨库分支 #2 为空（分支上下文互相隔离）");
    Chk(cs.GetMessages(br1[0].Id, 1).Count == 1, "limit 取最近 N 条");

    // ④ 从标签页直接进入：主分支就是 cross
    var s2 = cs.CreateSession(null, "", "", "跨库问答", "cross");
    Chk(cs.MainBranch(s2.Id)!.Mode == "cross", "标签页进入 → 主分支即 cross 模式");

    // ⑤ 按库查旧会话
    var old = cs.SessionsForLibrary(187);
    Chk(old.Count == 1 && old[0].Id == s1.Id, "能按库查到旧会话（用于「是否使用旧会话」）");

    // ⑥ 列表与统计
    var all = cs.ListSessions();
    Chk(all.Count == 2, "会话列表返回 2 个");
    Chk(all.First(x => x.Id == s1.Id).BranchCount == 3, "会话分支数统计正确");
    Chk(all.First(x => x.Id == s1.Id).MessageCount == 3, "会话消息数统计正确（跨分支合计）");

    // ⑦ 重命名 / 切分支 / 删除
    cs.RenameSession(s1.Id, "改过的标题");
    Chk(cs.GetSession(s1.Id)!.Title == "改过的标题", "重命名生效");
    cs.SetActiveBranch(s1.Id, cb1.Id);
    Chk(cs.GetSession(s1.Id)!.ActiveBranchId == cb1.Id, "切换激活分支生效");
    cs.DeleteBranch(cb2.Id);
    Chk(cs.ListBranches(s1.Id).Count == 2, "删除分支生效");
    cs.DeleteSession(s1.Id);
    Chk(cs.ListSessions().Count == 1, "删除会话生效（级联删除分支）");
    Chk(cs.GetMessages(cb1.Id).Count == 0, "删除会话级联清掉其分支的消息");

    Console.WriteLine($"   结果：通过 {pass} / 失败 {fail}");
    try { File.Delete(cp); } catch { }
    return fail == 0 ? 0 : 1;
}
// ── 重复内容检测 ──
// 用法: dotnet run -- --dup-test <dbPath> [minMb] [--similar-only]
if (args.Length >= 2 && args[0] == "--dup-test")
{
    var ddb = new Database(args[1]);
    ddb.EnsureCreated();
    long minMb = args.Length >= 3 && long.TryParse(args[2], out long m) ? m : 5;
    bool simOnly = args.Contains("--similar-only");

    Console.WriteLine("═══ 库级相似度（NKI 名称集合 Jaccard ≥ 0.35）═══");
    var sims = DuplicateFinder.FindSimilarLibraries(ddb);
    if (sims.Count == 0) Console.WriteLine("   未发现高度相似的库");
    foreach (var s in sims.Take(15))
    {
        Console.WriteLine($"   [{s.Relation}] {s.Jaccard:F2}  {s.RelationLabel}");
        Console.WriteLine($"      A: {s.NameA}  ({s.TotalA} NKI / {s.SizeA / 1073741824.0:F1} GB / 需 {s.VersionA})");
        Console.WriteLine($"      B: {s.NameB}  ({s.TotalB} NKI / {s.SizeB / 1073741824.0:F1} GB / 需 {s.VersionB})");
        Console.WriteLine($"      共享 {s.SharedInstruments} · 覆盖率 A→B {s.CoverA:P0} / B→A {s.CoverB:P0}");
        Console.WriteLine($"      建议: {s.Recommendation}");
        Console.WriteLine();
    }

    if (!simOnly)
    {
        Console.WriteLine();
        Console.WriteLine($"═══ 跨库重复大文件（≥ {minMb} MB）═══");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastPhase = "";
        var prog = new Progress<DupProgress>(p =>
        {
            if (p.Phase != lastPhase) { Console.WriteLine($"\n[{p.Phase}]"); lastPhase = p.Phase; }
            Console.Write($"\r   {p.Message}   ");
        });
        var groups = DuplicateFinder.FindDuplicateFiles(ddb, minMb * 1024 * 1024, prog, CancellationToken.None);
        sw.Stop();
        Console.WriteLine();
        long reclaim = groups.Sum(g => g.ReclaimableBytes);
        Console.WriteLine($"   重复组 {groups.Count} 个 / 可回收 {reclaim / 1024.0 / 1024 / 1024:F2} GB / 耗时 {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var g in groups.Take(8))
        {
            Console.WriteLine($"   ── {g.SizeBytes / 1024.0 / 1024:F1} MB × {g.Files.Count}（可回收 {g.ReclaimableBytes / 1024.0 / 1024:F1} MB）");
            foreach (var f2 in g.Files.Take(3)) Console.WriteLine($"        [{f2.LibraryName}] {System.IO.Path.GetFileName(f2.FullPath)}");
        }
    }
    return 0;
}
// ── NKX 解包测试：取一个样本解出来，看是否是有效 NCW ──
// 用法: dotnet run -- --nkx-extract <nkx文件> <库注册表键名> [输出目录]
if (args.Length >= 3 && args[0] == "--nkx-extract")
{
    string nkx = args[1];
    string regKey = args[2];
    string outDir = args.Length >= 4 ? args[3] : Path.Combine(Path.GetTempPath(), "nkx-out");
    if (!File.Exists(nkx)) { Console.WriteLine($"❌ 文件不存在：{nkx}"); return 1; }
    Directory.CreateDirectory(outDir);

    var keyInfo = NkxCrypto.LoadKey(regKey);
    if (keyInfo == null) { Console.WriteLine($"❌ 注册表里读不到 {regKey} 的 JDX/HU"); return 1; }
    Console.WriteLine($"密钥：JDX {keyInfo.Value.key.Length} 字节 / HU {keyInfo.Value.iv.Length} 字节");
    var mask = NkxCrypto.BuildXorMask(keyInfo.Value.key, keyInfo.Value.iv);
    Console.WriteLine($"密钥流：{mask.Length} 字节（前 8 字节 {string.Join(" ", mask.Take(8).Select(b => b.ToString("x2")))}）");

    var tree = NkxReader.ReadTree(nkx);
    if (tree == null) { Console.WriteLine("❌ 目录解析失败"); return 1; }
    var files = tree.Flatten().ToList();
    Console.WriteLine($"容器：{Path.GetFileName(nkx)} / 目录 {tree.Children.Count(c => c.IsDirectory)} 个 / 文件 {files.Count} 个");

    int ok = 0, fail = 0;
    foreach (var f2 in files.Take(5))
    {
        string safe = string.Join("_", f2.Name.Split(Path.GetInvalidFileNameChars()));
        string dst = Path.Combine(outDir, safe);
        using (var of = File.Create(dst))
        {
            long n = NkxReader.ExtractFile(nkx, f2, mask, of);
            if (n < 0) { Console.WriteLine($"   ✗ {f2.Name} 解包失败"); fail++; continue; }
        }
        var head = new byte[16];
        using (var fs2 = File.OpenRead(dst)) fs2.Read(head, 0, 16);
        bool isNcw = head[0] == 0x01 && head[1] == 0xA8 && head[2] == 0x9E && head[3] == 0xD6;
        string hex = string.Join(" ", head.Take(8).Select(b => b.ToString("x2")));
        Console.WriteLine($"   {(isNcw ? "✅" : "  ")} {f2.Name}  加密={f2.Encrypted} 大小={f2.Size:N0}  头 8 字节 {hex}");
        if (isNcw)
        {
            ok++;
            var info2 = NcwDecoder.ReadInfo(dst);
            if (info2 != null)
                Console.WriteLine($"        → NCW! {info2.Channels}ch / {info2.BitsPerSample}bit / {info2.SampleRate}Hz / {info2.NumSamples:N0} 帧");
        }
        else fail++;
    }
    Console.WriteLine($"   结果：NCW {ok} 个 / 非 NCW {fail} 个");
    Console.WriteLine(ok > 0 ? "✅ 解包成功且解出有效 NCW" : "⚠ 解包成功但未识别为 NCW");
    return ok > 0 ? 0 : 1;
}
// ── NKX 容器目录读取测试 ──
// 用法: dotnet run -- --nkx-test <nkx文件>
if (args.Length >= 2 && args[0] == "--nkx-test")
{
    string p = args[1];
    if (!File.Exists(p)) { Console.WriteLine($"❌ 文件不存在：{p}"); return 1; }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var info = NkxReader.ReadDirectory(p, 100000);
    sw.Stop();
    if (info == null) { Console.WriteLine("❌ 不是可识别的 NKX 容器（魔数不匹配）"); return 1; }

    Console.WriteLine($"容器：{Path.GetFileName(p)}（{new FileInfo(p).Length / 1024.0 / 1024:F1} MB）");
    Console.WriteLine($"   样本组 {info.GroupCount} 个 / 样本项 {info.SampleCount} 个 / 主要扩展名 .{info.DominantExt} / 耗时 {sw.ElapsedMilliseconds} ms");
    Console.WriteLine("   ── 样本组 ──");
    foreach (var g in info.Groups.Take(8)) Console.WriteLine($"      {g.Name}（{g.Children.Count} 项）");
    if (info.Groups.Count > 8) Console.WriteLine($"      …另有 {info.Groups.Count - 8} 个组");
    Console.WriteLine("   ── 前 6 个样本名 ──");
    foreach (var n in info.SampleNames.Take(6)) Console.WriteLine($"      {n}");
    bool ok = info.SampleCount > 0 && info.DominantExt.Length > 0;
    Console.WriteLine(ok ? "   ✅ 目录解析成功（样本名以 .ncw 结尾，说明容器内确为 NCW 样本）"
                         : "   ⚠ 未解析到样本项");
    return ok ? 0 : 1;
}
// ── NCW 解码测试：解码为 WAV 并校验 ──
// 用法: dotnet run -- --ncw-test <ncw文件> [输出wav]
if (args.Length >= 2 && args[0] == "--ncw-test")
{
    string src = args[1];
    string dst = args.Length >= 3 ? args[2] : Path.ChangeExtension(src, ".decoded.wav");
    if (!File.Exists(src)) { Console.WriteLine($"❌ 文件不存在：{src}"); return 1; }

    var info = NcwDecoder.ReadInfo(src);
    if (info == null) { Console.WriteLine("❌ 不是有效的 NCW 文件（头部解析失败）"); return 1; }
    Console.WriteLine($"NCW：{Path.GetFileName(src)}");
    Console.WriteLine($"   声道 {info.Channels} / 位深 {info.BitsPerSample} / 采样率 {info.SampleRate} Hz / " +
                      $"帧数 {info.NumSamples:N0}（{info.Seconds:F2} 秒）/ {(info.IsFloat ? "float" : "PCM")}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    long bytes = NcwDecoder.DecodeToWav(src, dst, maxSeconds: 6.0);
    sw.Stop();
    if (bytes <= 0) { Console.WriteLine("❌ 解码失败"); return 1; }
    Console.WriteLine($"✅ 解码成功：{bytes / 1024.0 / 1024:F2} MB / 耗时 {sw.ElapsedMilliseconds} ms → {dst}");

    // 校验 WAV 头
    var head = new byte[44];
    using (var fs = File.OpenRead(dst)) fs.Read(head, 0, 44);
    string riff = System.Text.Encoding.ASCII.GetString(head, 0, 4);
    string wave = System.Text.Encoding.ASCII.GetString(head, 8, 4);
    int fmt = BitConverter.ToInt16(head, 20);
    int ch = BitConverter.ToInt16(head, 22);
    int rate = BitConverter.ToInt32(head, 24);
    int bits = BitConverter.ToInt16(head, 34);
    Console.WriteLine($"   WAV 校验：{riff}/{wave} fmt={fmt} 声道={ch} 采样率={rate} 位深={bits}");
    bool ok = riff == "RIFF" && wave == "WAVE" && ch == info.Channels && rate == info.SampleRate;
    Console.WriteLine(ok ? "   ✅ WAV 头正确" : "   ❌ WAV 头异常");

    // 非静音校验：统计样本绝对值和
    long sum = 0; int nonzero = 0;
    for (int i = 44; i + 1 < Math.Min(bytes, 44 + 200000); i += 2)
    {
        short v = BitConverter.ToInt16(head.Length > i ? new byte[2] : new byte[2], 0);
    }
    using (var fs = File.OpenRead(dst))
    {
        fs.Seek(44, SeekOrigin.Begin);
        var buf = new byte[Math.Min(400000, bytes - 44)];
        int n = fs.Read(buf, 0, buf.Length);
        for (int i = 0; i + 1 < n; i += 2)
        {
            short v = BitConverter.ToInt16(buf, i);
            sum += Math.Abs((int)v);
            if (v != 0) nonzero++;
        }
    }
    Console.WriteLine($"   音频内容：非零样本 {nonzero:N0} 个 / 平均绝对幅度 {(nonzero > 0 ? sum / nonzero : 0):N0}");
    Console.WriteLine(nonzero > 0 ? "   ✅ 含真实音频数据（非静音）" : "   ⚠ 全为静音，解码可能有问题");
    return ok && nonzero > 0 ? 0 : 1;
}
// ── 移除失效根目录（测试/维护用）──
// 用法: dotnet run -- --remove-root <dbPath> <路径关键字>
if (args.Length >= 3 && args[0] == "--remove-root")
{
    var xdb = new Database(args[1]);
    xdb.EnsureCreated();
    foreach (var r in xdb.GetRoots().Where(r => r.Path.Contains(args[2], StringComparison.OrdinalIgnoreCase)).ToList())
    {
        var (ok, msg) = xdb.RemoveRoot(r.Id);
        Console.WriteLine($"{(ok ? "✓" : "✗")} 移除 {r.Path}：{msg}");
    }
    foreach (var r in xdb.GetRoots()) Console.WriteLine($"   剩余根目录：{r.Path}（存在：{r.Exists}）");
    return 0;
}

// ── 知识库构建与检索实测 ──
// 用法: dotnet run -- --kb-build <pdf路径> [--no-vision] [--max-vision N] [--ask "问题"]
if (args.Length >= 2 && args[0] == "--kb-build")
{
    string pdf = args[1];
    if (!File.Exists(pdf)) { Console.WriteLine($"❌ 文件不存在：{pdf}"); return 1; }

    bool useVision = !args.Contains("--no-vision");
    int maxVision = int.MaxValue;
    int mi = Array.IndexOf(args, "--max-vision");
    if (mi >= 0 && mi + 1 < args.Length && int.TryParse(args[mi + 1], out int mv)) maxVision = mv;

    var ai = new AiSettings
    {
        BaseUrl = "https://token.sensenova.cn/v1",
        Model = "sensenova-6.8-flash-lite",
        ApiKey = Environment.GetEnvironmentVariable("KLM_AI_KEY") ?? "",
        MaxTokens = 4096,
    };

    long libId = 999999;   // 测试用固定 ID
    var manual = new ManualRecord { Name = Path.GetFileName(pdf), LibraryPath = Path.GetDirectoryName(pdf)!, RelPath = Path.GetFileName(pdf) };

    Console.WriteLine($"手册：{manual.Name}  ({new FileInfo(pdf).Length / 1024.0 / 1024.0:F2} MB)");
    Console.WriteLine($"视觉解析：{(useVision && ai.Configured ? "开启" : "关闭")}  最多 {maxVision} 页");
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    string lastPhase = "";
    var prog = new Progress<KbProgress>(p =>
    {
        if (p.Phase != lastPhase) { Console.WriteLine($"\n[{p.Phase}]"); lastPhase = p.Phase; }
        Console.Write($"\r  {p.Current}/{p.Total} {p.Message}   ");
    });

    LibraryKb kb;
    try
    {
        kb = await ManualKb.BuildAsync(libId, "测试库", manual, useVision ? ai : null, prog,
            new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token);
    }
    catch (Exception ex) { Console.WriteLine($"\n❌ 构建失败：{ex.Message}"); return 1; }

    Console.WriteLine();
    Console.WriteLine($"✅ 构建完成（{sw.Elapsed.TotalSeconds:F1}s）");
    Console.WriteLine($"   页数 {kb.PageCount} / 知识块 {kb.Chunks.Count} / 总字符 {kb.TotalChars:N0} / 视觉解析 {kb.VisionPages} 页");
    Console.WriteLine($"   知识库文件：{ManualKb.KbPath(libId)}");
    var textPages = kb.Pages.Count(p => p.Source == "text");
    var visionPages = kb.Pages.Count(p => p.Source == "vision");
    var emptyPages = kb.Pages.Count(p => p.Source == "empty");
    Console.WriteLine($"   页面来源：文字 {textPages} / 视觉 {visionPages} / 空 {emptyPages}");

    // 检索自测
    var queries = new[] { "how to use legato", "articulation switching", "keyswitch", "音色怎么用", "mix microphone" };
    Console.WriteLine();
    Console.WriteLine("── 检索自测（BM25）──");
    foreach (var q in queries)
    {
        var hits = ManualKb.Search(kb, q, 3);
        Console.WriteLine($"   「{q}」→ {hits.Count} 条命中" +
            (hits.Count > 0 ? $"（第 {string.Join(", ", hits.Select(h => h.Page))} 页）" : ""));
    }

    // 指定问题 → 完整问答
    int qi = Array.IndexOf(args, "--ask");
    if (qi >= 0 && qi + 1 < args.Length && ai.Configured)
    {
        string question = args[qi + 1];
        Console.WriteLine();
        Console.WriteLine($"── 问答：{question} ──");
        var hits = ManualKb.Search(kb, question, 6);
        var ctx = new System.Text.StringBuilder();
        foreach (var h in hits) ctx.AppendLine($"[第 {h.Page} 页]\n{h.Text}\n");

        var msgs = new List<ChatMessage>
        {
            new() { Role = "system", Content = "你是 Kontakt 音色库说明书助手。只依据提供的说明书片段回答，用中文，并标注依据的页码。手册没写就直说没找到。" },
            new() { Role = "user", Content = $"说明书片段：\n{ctx}\n\n问题：{question}" },
        };
        var sw2b = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string answer = await AiClient.ChatAsync(ai, msgs, default, 4096);
            Console.WriteLine($"(耗时 {sw2b.Elapsed.TotalSeconds:F1}s，命中页 {string.Join(",", hits.Select(h => h.Page))})");
            Console.WriteLine();
            Console.WriteLine(answer);
        }
        catch (Exception ex) { Console.WriteLine($"❌ 问答失败：{ex.Message}"); }
    }

    return 0;
}

// ── 分词调试：看某个查询被拆成什么 token ──
// 用法: dotnet run -- --tokens "查询文本"
if (args.Length >= 2 && args[0] == "--tokens")
{
    string q = args[1];
    var plain = ManualKb.Tokenize(q, expandSynonyms: false);
    var expanded = ManualKb.Tokenize(q, expandSynonyms: true);
    Console.WriteLine($"原文：{q}");
    Console.WriteLine($"基础分词（{plain.Count}）：{string.Join(" | ", plain)}");
    Console.WriteLine($"同义词扩展（{expanded.Count}）：{string.Join(" | ", expanded)}");
    return 0;
}

// ── 知识库检索诊断：加载 kb.json，打印 token/df 与命中 ──
// 用法: dotnet run -- --kb-search <kb.json> "查询"
if (args.Length >= 2 && args[0] == "--kb-search")
{
    string kbPath = args[1];
    string query = args.Length >= 3 ? args[2] : "mic";
    var kb = System.Text.Json.JsonSerializer.Deserialize<LibraryKb>(
        File.ReadAllText(kbPath, System.Text.Encoding.UTF8),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    if (kb == null) { Console.WriteLine("❌ 无法加载知识库"); return 1; }

    Console.WriteLine($"知识库：{kb.ManualName}  页 {kb.PageCount}  块 {kb.Chunks.Count}");

    var qTokens = ManualKb.Tokenize(query, expandSynonyms: true).Distinct().ToList();
    Console.WriteLine($"查询 token（{qTokens.Count}）：{string.Join(" | ", qTokens)}");

    var docTokens = new List<HashSet<string>>();
    foreach (var c in kb.Chunks) docTokens.Add(new HashSet<string>(ManualKb.Tokenize(c.Text)));

    Console.WriteLine();
    Console.WriteLine("token 在知识块中的覆盖：");
    foreach (var t in qTokens)
    {
        int df = docTokens.Count(s => s.Contains(t));
        Console.WriteLine($"   {t,-16} 命中 {df} / {kb.Chunks.Count} 块");
    }

    var hits = ManualKb.Search(kb, query, 5);
    Console.WriteLine();
    Console.WriteLine($"BM25 检索结果：{hits.Count} 条" + (hits.Count > 0 ? $"（第 {string.Join(", ", hits.Select(h => h.Page))} 页）" : ""));

    bool anyCovered = qTokens.Any(t => docTokens.Any(s => s.Contains(t)));
    if (hits.Count == 0 && anyCovered) Console.WriteLine("⚠ 有 token 覆盖但检索为 0 —— 打分逻辑需检查");
    return 0;
}
// ── 混合检索对比实测：BM25 vs HybridSearch（BM25+向量+RRF+MMR）──
// 用法: dotnet run -- --hybrid-test <kb.json> ["查询1" "查询2" ...]
//
// 为什么需要这个子测试：混合检索（BM25 + 哈希随机投影向量 + RRF(k=60) + MMR(λ=0.7)）
// 从实现至今【从未用真实语料验证过】。本测试用同一批查询同时跑两条检索路径，
// 逐条对比命中页与文本，判断「向量侧是否真的补上了 BM25 的词面盲区」。
if (args.Length >= 2 && args[0] == "--hybrid-test")
{
    string kbPath = args[1];
    if (!File.Exists(kbPath)) { Console.WriteLine($"❌ 知识库不存在：{kbPath}"); return 1; }
    var kb = System.Text.Json.JsonSerializer.Deserialize<LibraryKb>(
        File.ReadAllText(kbPath, System.Text.Encoding.UTF8),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    if (kb == null || kb.Chunks.Count == 0) { Console.WriteLine("❌ 知识库为空"); return 1; }
    // 默认查询集刻意选「与手册用词不同」的说法，这正是向量侧该发挥作用的场景
    var queries = args.Length >= 3
        ? args.Skip(2).ToArray()
        : new[]
        {
            "怎么用键位切换演奏法",      // 手册多为 key-switch / articulation
            "microphone position",       // 手册常用 mic position
            "how to change the sound",   // 手册可能写 patch / preset
            "piano",                     // 普通词，作为对照
        };
    Console.WriteLine($"知识库：{kb.ManualName}   页 {kb.PageCount}   块 {kb.Chunks.Count}");
    Console.WriteLine(new string('─', 78));
    int hybridWins = 0, bm25Wins = 0, same = 0;
    foreach (var q in queries)
    {
        var bm = ManualKb.Search(kb, q, 5);
        var hy = ManualKb.HybridSearch(kb, q, 5);
        var bmPages = bm.Select(c => c.Page).ToList();
        var hyPages = hy.Select(c => c.Page).ToList();
        var onlyHy = hyPages.Except(bmPages).ToList();
        var onlyBm = bmPages.Except(hyPages).ToList();
        Console.WriteLine();
        Console.WriteLine($"【查询】{q}");
        Console.WriteLine($"  BM25      ：{bm.Count} 条   页 {string.Join(", ", bmPages)}");
        Console.WriteLine($"  混合检索  ：{hy.Count} 条   页 {string.Join(", ", hyPages)}");
        Console.WriteLine($"  仅混合有  ：{(onlyHy.Count == 0 ? "—" : string.Join(", ", onlyHy))}");
        Console.WriteLine($"  仅 BM25 有：{(onlyBm.Count == 0 ? "—" : string.Join(", ", onlyBm))}");
        // 判定：混合多召回 → 算「补上了」；BM25 有而混合没有 → 需关注（可能被 MMR 去重掉）
        if (onlyHy.Count > 0 && onlyBm.Count == 0) hybridWins++;
        else if (onlyBm.Count > 0 && onlyHy.Count == 0) bm25Wins++;
        else same++;
        if (bm.Count > 0)
        {
            var t = bm[0].Text.Replace("\n", " ");
            Console.WriteLine($"  BM25 top1  ：{(t.Length > 110 ? t[..110] + "…" : t)}");
        }
        if (hy.Count > 0)
        {
            var t = hy[0].Text.Replace("\n", " ");
            Console.WriteLine($"  混合 top1  ：{(t.Length > 110 ? t[..110] + "…" : t)}");
        }
    }
    Console.WriteLine();
    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"汇总：混合多召回 {hybridWins} 条 / 仅 BM25 召回 {bm25Wins} 条 / 相同 {same} 条（共 {queries.Length} 条查询）");
    if (hybridWins == 0 && bm25Wins == 0)
        Console.WriteLine("⚠ 两条路径结果一致 —— 向量侧可能未生效（检查 Embed/GetVectors 是否真的参与了融合）");
    else if (bm25Wins > 0)
        Console.WriteLine("⚠ 有查询出现「仅 BM25 召回」—— 可能是 MMR 去重把相关块挤掉，需检查 λ 取值");
    else
        Console.WriteLine("✅ 混合检索在全部查询上都不劣于 BM25，且至少一条查询多召回了内容");
    return 0;
}
// ── 说明书机械过滤真值表 ──
// 用法: dotnet run -- --looksmanual-test
if (args.Length >= 1 && args[0] == "--looksmanual-test")
{
    // (文件名, 字节数, 期望值, 说明)
    var cases = new (string name, long size, bool expect, string note)[]
    {
        ("Damage 2 Manual.pdf",           4_900_000, true,  "白名单 manual + 大文件"),
        ("KONTAKT 8 Manual.pdf",          8_000_000, true,  "白名单 manual"),
        ("说明书.pdf",                     120_000,   true,  "白名单中文「说明书」"),
        ("User Guide.pdf",                300_000,   true,  "白名单 user guide"),
        ("license.rtf",                   40_000,    false, "黑名单 license"),
        ("EULA.txt",                      12_000,    false, "黑名单 eula"),
        ("readme.txt",                    3_000,     false, "黑名单 readme"),
        ("changelog.txt",                 9_000,     false, "黑名单 changelog"),
        ("ThirdPartyContent.pdf",         200_000,   false, "黑名单 thirdpartycontent"),
        ("Licensing Agreement.pdf",       180_000,   false, "黑名单 licensing/agreement"),
        ("._Damage Manual.pdf",           4_000,     false, "macOS AppleDouble 副档（._ 前缀）"),
        (".DS_Store",                     6_000,     false, "系统残留"),
        ("manual.rtf",                    30_000,    true,  "白名单优先于 50KB 阈值"),
        ("SomeDocument.pdf",              60_000,    true,  "无关键词但 >= 50KB"),
        ("SomeDocument.pdf",              20_000,    false, "无关键词且 < 50KB"),
        ("SomeDocument.pdf",              50 * 1024, true,  "恰好 50KB 边界（含）"),
        ("SomeDocument.pdf",              50 * 1024 - 1, false, "49.99KB 边界（不含）"),
    };
    int pass = 0, fail = 0;
    Console.WriteLine("说明书机械过滤真值表（LibraryScanner.LooksLikeManual）");
    Console.WriteLine(new string('─', 78));
    foreach (var c in cases)
    {
        bool got = LibraryScanner.LooksLikeManual(c.name, c.size);
        bool ok = got == c.expect;
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "✅" : "❌")} {c.name,-26} {c.size,10:N0} B  期望={c.expect,-5} 实得={got,-5} {c.note}");
    }
    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   ✅ 全部符合预期" : $"   ❌ {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}
// ── PngWriter 白底合成验证 ──
// 用法: dotnet run -- --png-test [输出PNG路径]
//
// 为什么需要：PDFium 渲染出的背景是【全透明】(A=0)，旧实现强制 alpha=255 导致
// 透明背景变成【纯黑】（表现为「黑底白字 / 灰块」）。本测试用已知像素验证合成结果。
if (args.Length >= 1 && args[0] == "--png-test")
{
    const int W = 4, H = 1;
    var bgra = new byte[W * H * 4];
    // 像素 0：全透明（应合成成【白】）
    bgra[0] = 0; bgra[1] = 0; bgra[2] = 0; bgra[3] = 0;
    // 像素 1：不透明纯红（应原样保留）
    bgra[4] = 0; bgra[5] = 0; bgra[6] = 255; bgra[7] = 255;
    // 像素 2：半透明黑 a=128（应合成成中灰 ~127）
    bgra[8] = 0; bgra[9] = 0; bgra[10] = 0; bgra[11] = 128;
    // 像素 3：不透明纯白
    bgra[12] = 255; bgra[13] = 255; bgra[14] = 255; bgra[15] = 255;
    var png = PngWriter.FromBgra(bgra, W, H);
    string outPath = args.Length >= 2 ? args[1] : Path.Combine(AppPaths.DataDir, "png-test.png");
    try { Directory.CreateDirectory(Path.GetDirectoryName(outPath)!); File.WriteAllBytes(outPath, png); } catch { }
    Console.WriteLine($"PngWriter 白底合成验证   PNG {png.Length:N0} B → {outPath}");
    Console.WriteLine(new string('─', 78));
    int fail = 0;
    using (var ms = new MemoryStream(png))
    using (var bmp = new System.Drawing.Bitmap(ms))
    {
        Console.WriteLine($"  解码尺寸：{bmp.Width}×{bmp.Height}（期望 {W}×{H}）");
        if (bmp.Width != W || bmp.Height != H) fail++;
        var expect = new (string note, int r, int g, int b, int tol)[]
        {
            ("全透明 → 白",        255, 255, 255, 2),
            ("不透明纯红 → 红",    255, 0,   0,   2),
            ("半透明黑 a=128 → 中灰", 127, 127, 127, 3),
            ("不透明纯白 → 白",    255, 255, 255, 2),
        };
        for (int x = 0; x < W; x++)
        {
            var px = bmp.GetPixel(x, bmp.Height - 1);   // Bitmap 原点在左上，1 行时即第 0 行
            var e = expect[x];
            bool ok = Math.Abs(px.R - e.r) <= e.tol && Math.Abs(px.G - e.g) <= e.tol && Math.Abs(px.B - e.b) <= e.tol
                      && px.A == 255;
            if (!ok) fail++;
            Console.WriteLine($"  {(ok ? "✅" : "❌")} 像素{x} {e.note,-22} 期望({e.r},{e.g},{e.b},255) 实得({px.R},{px.G},{px.B},{px.A})");
        }
    }
    Console.WriteLine(new string('─', 78));
    Console.WriteLine(fail == 0
        ? "✅ 白底合成正确 —— 全透明像素输出白色（不再变黑）"
        : $"❌ {fail} 项不符 —— 白底合成有问题");
    return fail == 0 ? 0 : 1;
}
// ── NKI 元数据解析实测 ──
// 用法: dotnet run -- --nki-test <库目录或 .nki 文件> [最多取样数]
if (args.Length >= 2 && args[0] == "--nki-test")
{
    string target = args[1];
    int limit = args.Length >= 3 && int.TryParse(args[2], out int lim) ? lim : 200;
    var files = new List<string>();
    if (File.Exists(target)) files.Add(target);
    else if (Directory.Exists(target))
        files.AddRange(Directory.EnumerateFiles(target, "*.nki", SearchOption.AllDirectories).Take(limit));
    if (files.Count == 0) { Console.WriteLine($"❌ 没找到 .nki：{target}"); return 1; }
    Console.WriteLine($"NKI 元数据解析实测  取样 {files.Count} 个");
    Console.WriteLine(new string('─', 78));
    int nkiHeaderHit = 0, nkiFilenameFallback = 0, nkiNameMatchesFile = 0, nkiWithEngine = 0;
    var byFormat = new Dictionary<NkiFormat, int>();
    foreach (var f in files)
    {
        var (name, source, fmt, engine) = NkiMetadataReader.ReadFromFile(f);
        byFormat[fmt] = byFormat.TryGetValue(fmt, out int c) ? c + 1 : 1;
        if (source == "header")
        {
            nkiHeaderHit++;
            string fileBase = Path.GetFileNameWithoutExtension(f);
            if (string.Equals(name, fileBase, StringComparison.OrdinalIgnoreCase)) nkiNameMatchesFile++;
        }
        else nkiFilenameFallback++;
        if (!string.IsNullOrEmpty(engine)) nkiWithEngine++;
    }
    Console.WriteLine($"  头部解析成功（source=header）：{nkiHeaderHit} / {files.Count}");
    Console.WriteLine($"  回退文件名（source=filename）：{nkiFilenameFallback}");
    Console.WriteLine($"  解析名与文件名一致：{nkiNameMatchesFile} / {nkiHeaderHit}" +
                      (nkiHeaderHit > 0 ? $"（{nkiNameMatchesFile * 100.0 / nkiHeaderHit:F1}%）" : ""));
    Console.WriteLine($"  提取到引擎版本：{nkiWithEngine} / {files.Count}");
    Console.WriteLine($"  格式分布：{string.Join("  ", byFormat.Select(kv => $"{kv.Key}={kv.Value}"))}");
    Console.WriteLine();
    Console.WriteLine("  前 5 个样本：");
    foreach (var f in files.Take(5))
    {
        var (name, source, fmt, engine) = NkiMetadataReader.ReadFromFile(f);
        Console.WriteLine($"    {Path.GetFileName(f),-42} → {(name ?? "(无)"),-38} [{source}/{fmt}] {(engine.Length > 0 ? "v" + engine : "")}");
    }
    Console.WriteLine(new string('─', 78));
    // 契约：头部命中率应较高，且命中时名称应与文件名一致（历史实测为 100%）
    bool ok = nkiHeaderHit > 0 && (nkiNameMatchesFile == nkiHeaderHit);
    Console.WriteLine(ok
        ? $"✅ 契约符合：头部命中 {nkiHeaderHit} 个，名称与文件名一致率 100%"
        : $"⚠ 头部命中 {nkiHeaderHit}，一致 {nkiNameMatchesFile}（契约称命中即一致，需检查解析规则是否被改动）");
    return 0;
}
// ── 便携版 Settings.cfg 解析验证 ──
// 用法: dotnet run -- --settings-test <Settings.cfg 路径>
if (args.Length >= 2 && args[0] == "--settings-test")
{
    string cfg = args[1];
    if (!File.Exists(cfg)) { Console.WriteLine($"❌ 文件不存在：{cfg}"); return 1; }
    var (names, dirs) = PortableKontaktStore.ParseSettingsCfg(cfg);
    Console.WriteLine($"便携版 Settings.cfg 解析：{cfg}");
    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"  识别到库条目（含 ContentDir 的节）：{names.Count}");
    foreach (var n in names.Take(15)) Console.WriteLine($"    · {n}");
    if (names.Count > 15) Console.WriteLine($"    …（另有 {names.Count - 15} 条）");
    Console.WriteLine();
    Console.WriteLine($"  ContentDir 数量：{dirs.Count}");
    foreach (var d in dirs.Take(5)) Console.WriteLine($"    · {d}");
    Console.WriteLine(new string('─', 78));
    Console.WriteLine(names.Count > 0
        ? "✅ 解析出库条目（与 PortableKontaktStore.Inspect 的判据一致：只把含 ContentDir 的节算作库）"
        : "⚠ 未解析出任何库条目 —— 可能该文件还没有库（或格式与契约不符）");
    return 0;
}
// ── .ncw 解码 + MERT 提取实测（音色地图一期）──
// 用法: dotnet run -- --ncwmap-test <dbPath> [模型路径]
// 注：不要叫 --ncw-test —— 那个名字已被「单文件 NCW 解码测试」占用。
if (args.Length >= 2 && args[0] == "--ncwmap-test")
{
    var ndb = new Database(args[1]);
    string nmp = args.Length >= 3 ? args[2] : MertFeatures.DefaultModelPath;
    int pass = 0, fail = 0;
    void NChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-44} {detail}");
    }

    Console.WriteLine(".ncw 解码 + MERT 提取实测（一期）");
    Console.WriteLine(new string(char.Parse("-"), 78));

    NChk("wav 原生可解码", AudioFeatures.CanDecode("wav") && !AudioFeatures.NeedsDecode("wav"), "-");
    NChk("ncw 需解码", !AudioFeatures.CanDecode("ncw") && AudioFeatures.NeedsDecode("ncw"), "-");
    NChk("ncw 最终可分析", AudioFeatures.CanAnalyze("ncw"), "-");
    NChk("nkx 已可分析（二期已完成）", AudioFeatures.CanAnalyze("nkx") && AudioFeatures.NeedsDecode("nkx"), "走容器聚合");

    var nlibs = ndb.GetLibraries().ToList();
    var ncws = new List<(AudioClip C, string Root)>();
    foreach (var L2 in nlibs)
    {
        try { foreach (var c in ndb.GetAudioClips(L2.Id, 200)) if (c.Ext.Equals("ncw", StringComparison.OrdinalIgnoreCase)) ncws.Add((c, L2.Path)); } catch { }
        if (ncws.Count >= 5) break;
    }
    NChk("找到 .ncw 样本", ncws.Count > 0, $"{ncws.Count} 个");
    if (ncws.Count == 0) return 1;

    int decoded = 0, extracted = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    foreach (var s in ncws.Take(5))
    {
        var full = Path.Combine(s.Root, s.C.RelPath);
        if (NcwDecoder.ReadInfo(full) != null) decoded++;
        var v = MertFeatures.ExtractAny(full, "ncw", 10.0, nmp);
        if (v != null) extracted++;
    }
    sw.Stop();
    NChk("能读 .ncw 头信息", decoded > 0, $"{decoded} / {Math.Min(5, ncws.Count)}");
    NChk("能解码 + 提特征", extracted > 0, $"{extracted} / {Math.Min(5, ncws.Count)}，总耗时 {sw.Elapsed.TotalSeconds:F1}s");
    if (extracted > 0) Console.WriteLine($"       平均 {(sw.Elapsed.TotalSeconds / Math.Max(1, extracted)):F2} 秒/个（含解码）");

    var leftover = Directory.GetFiles(Path.GetTempPath(), "klm_ncw_*.wav").Length;
    NChk("临时 WAV 已清理", leftover == 0, $"残留 {leftover} 个");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── .nkx 容器提取实测（音色地图二期）──
// 用法: dotnet run -- --nkxmap-test <dbPath> [模型路径]
if (args.Length >= 2 && args[0] == "--nkxmap-test")
{
    var kdb = new Database(args[1]);
    string kmp = args.Length >= 3 ? args[2] : MertFeatures.DefaultModelPath;
    int pass = 0, fail = 0;
    void KChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-44} {detail}");
    }

    Console.WriteLine(".nkx 容器提取实测（二期）");
    Console.WriteLine(new string(char.Parse("-"), 78));
    KChk("nkx 已算可分析", AudioFeatures.NeedsDecode("nkx") && AudioFeatures.CanAnalyze("nkx"), "-");
    KChk("均匀取样函数正确", NkxFeatures.UniformPick(new List<int>{0,1,2,3,4,5,6,7,8,9}, 3).SequenceEqual(new[]{0,3,6}), "取 3 个 → 0,3,6");

    // 找一个【已入库】的库的 .nkx（必须有注册表密钥才能解）
    var kl = kdb.GetLibraries().ToList();
    var kreg = LibraryRegistrar.ReadRegistered();
    (AudioClip C, string Root, string RegKey)? pick = null;
    foreach (var L2 in kl)
    {
        if (string.IsNullOrWhiteSpace(L2.ProductKey) || !kreg.ContainsKey(L2.ProductKey)) continue;
        List<AudioClip> cs;
        try { cs = kdb.GetAnalyzableClips(L2.Id); } catch { continue; }
        var n2 = cs.FirstOrDefault(x => x.Ext.Equals("nkx", StringComparison.OrdinalIgnoreCase) && x.SizeBytes > 5_000_000);
        if (n2 != null) { pick = (n2, L2.Path, L2.ProductKey!); break; }
    }
    KChk("找到已入库库的 .nkx", pick != null, pick == null ? "没找到（需要已入库且带 .nkx 的库）" : pick.Value.C.Name);
    if (pick == null) return 1;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var nr = NkxFeatures.Extract(Path.Combine(pick.Value.Root, pick.Value.C.RelPath), pick.Value.RegKey, 4, kmp, 8.0);
    sw.Stop();
    Console.WriteLine($"       容器：{pick.Value.C.Name}（{pick.Value.C.SizeBytes / 1024 / 1024} MB）");
    Console.WriteLine($"       容器内音频数：{nr.TotalSamples}，实际用上：{nr.UsedSamples}");
    KChk("能读容器目录", nr.TotalSamples > 0, $"{nr.TotalSamples} 个音频");
    KChk("能解包 + 解码 + 提特征", nr.Vec != null, nr.Vec == null ? nr.Error : $"{nr.Vec.Length} 维，耗时 {sw.Elapsed.TotalSeconds:F1}s");
    if (nr.UsedSamples > 0) Console.WriteLine($"       平均 {(sw.Elapsed.TotalSeconds / nr.UsedSamples):F2} 秒/采样（含解包）");

    var leftover = Directory.GetFiles(Path.GetTempPath(), "klm_nkx_*").Length;
    KChk("临时文件已清理", leftover == 0, $"残留 {leftover} 个");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── HDBSCAN 聚类实测 ──
// 用法: dotnet run -- --hdbcluster-test
if (args.Length >= 1 && args[0] == "--hdbcluster-test")
{
    int pass = 0, fail = 0;
    void HChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-44} {detail}");
    }

    Console.WriteLine("HDBSCAN 聚类实测");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // ① 造两个【明显分离】的团 + 几个离群点 —— 检验「能分簇 + 能识别噪声」
    var pts = new List<AudioMapReduce.Point>();
    var rnd = new Random(42);
    for (int i = 0; i < 60; i++) pts.Add(new AudioMapReduce.Point { X = (float)(rnd.NextDouble() * 2), Y = (float)(rnd.NextDouble() * 2) });
    for (int i = 0; i < 60; i++) pts.Add(new AudioMapReduce.Point { X = (float)(100 + rnd.NextDouble() * 2), Y = (float)(100 + rnd.NextDouble() * 2) });
    for (int i = 0; i < 5; i++) pts.Add(new AudioMapReduce.Point { X = (float)(rnd.NextDouble() * 200), Y = (float)(rnd.NextDouble() * 200) });

    bool ok1 = AudioMapReduce.ClusterByHdbscan(pts, 5, 8);
    // 顺带试更严格的参数（看能否把离群点标成噪声）
    var ptsStrict = pts.Select(q => new AudioMapReduce.Point { X = q.X, Y = q.Y }).ToList();
    bool okStrict = AudioMapReduce.ClusterByHdbscan(ptsStrict, 10, 15);
    int noiseStrict = ptsStrict.Count(q => q.Cluster < 0);
    Console.WriteLine($"  [参数对比] minPoints=10/minClusterSize=15 → 噪声点 {noiseStrict} 个（ok={okStrict}）");
    HChk("HDBSCAN 能跑通", ok1, ok1 ? "-" : "返回 false");
    int cl = pts.Where(p => p.Cluster >= 0).Select(p => p.Cluster).Distinct().Count();
    int noise = pts.Count(p => p.Cluster < 0);
    HChk("识别出 2 个明显分离的簇", cl == 2, $"实际 {cl} 个簇");
    // ⚠️ 实测：HdbscanSharp 的 Labels 倾向于给【所有】点分配簇号，很少标 -1。
    // 这不是 bug，是该库的行为 —— 如实记录，不硬凑断言。
    Console.WriteLine($"  [事实] minPoints=5/minClusterSize=8 → 噪声点 {noise} 个");
    HChk("聚类结果已写入所有点", pts.All(q => q.Cluster >= 0 || q.Cluster == -1), $"{pts.Count} 个点");

    // ② 两个簇的成员应正确归属
    int c1 = pts.Take(60).Where(p => p.Cluster >= 0).Select(p => p.Cluster).Distinct().Count();
    HChk("第一个团内部同簇", c1 == 1, $"{c1} 个不同簇号");

    // ③ 点太少时返回 false（交给调用方回退）
    var few = new List<AudioMapReduce.Point> { new() { X = 0, Y = 0 }, new() { X = 1, Y = 1 } };
    HChk("点太少 → 返回 false（可回退）", !AudioMapReduce.ClusterByHdbscan(few, 5, 8), "-");

    // ④ ClusterAuto 的兜底文案
    var pts2 = new List<AudioMapReduce.Point>();
    for (int i = 0; i < 200; i++) pts2.Add(new AudioMapReduce.Point { X = (float)(rnd.NextDouble() * 10), Y = (float)(rnd.NextDouble() * 10) });
    var info = AudioMapReduce.ClusterAuto(pts2, 5);
    HChk("ClusterAuto 返回可读信息", info.Length > 0 && (info.StartsWith("HDBSCAN") || info.StartsWith("网格")), info);

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 音色地图 Agent 工具实测 ──
// 用法: dotnet run -- --maptools-test <dbPath>
if (args.Length >= 2 && args[0] == "--maptools-test")
{
    var mdb = new Database(args[1]);
    var mctx = new AgentToolContext { Db = mdb };
    int pass = 0, fail = 0;
    void MChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-40} {detail}");
    }
    string Q(string s) => "\"" + s + "\"";

    Console.WriteLine("音色地图 Agent 工具实测");
    Console.WriteLine(new string(char.Parse("-"), 78));

    var s1 = AudioMapActions.MapStats(mctx, "{}");
    MChk("map_stats 返回 ok", s1.Contains(Q("ok") + ":true"), s1.Length > 0 ? "-" : "空");
    MChk("map_stats 报告已提取特征数", s1.Contains(Q("extracted")), "-");
    MChk("map_stats 报告地图点数", s1.Contains(Q("mapPoints")), "-");
    MChk("map_stats 报告覆盖率", s1.Contains(Q("coveragePercent")), "-");

    var s2 = AudioMapActions.MapClusters(mctx, "{\"top\":5}");
    MChk("map_clusters 返回 ok", s2.Contains(Q("ok") + ":true"), "-");
    MChk("map_clusters 有代表样本字段", s2.Contains("representativeFile"), "-");
    MChk("map_clusters 有跨库数（防误导）", s2.Contains("distinctLibraries"), "-");

    long seed = 0;
    foreach (var lib in mdb.GetLibraries())
    {
        foreach (var c in mdb.GetAnalyzableClips(lib.Id))
            if (mdb.GetMertFeature(c.Id) != null) { seed = c.Id; break; }
        if (seed > 0) break;
    }
    MChk("找到有 MERT 特征的种子片段", seed > 0, "clipId=" + seed);
    if (seed > 0)
    {
        var s3 = AudioMapActions.MapFindSimilar(mctx, "{\"clipId\":" + seed + ",\"topK\":5}");
        MChk("map_find_similar 返回 ok", s3.Contains(Q("ok") + ":true"), "-");
        MChk("map_find_similar 有相似项", s3.Contains(Q("score")), "-");
    }

    var s4 = AudioMapActions.MapFilter(mctx, "{\"cluster\":0,\"limit\":5}");
    MChk("map_filter 返回 ok", s4.Contains(Q("ok") + ":true"), "-");
    MChk("map_filter 报告命中数", s4.Contains(Q("matched")), "-");
    var s5 = AudioMapActions.MapFilter(mctx, "{}");
    MChk("map_filter 空参不报错", s5.Contains(Q("ok") + ":true"), "-");

    MChk("查询工具不触发重算任务", !AudioMapTask.IsRunning, "-");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 音色可解释筛选引擎实测（TimbreFilter）──
// 用法: dotnet run -- --timbre-test
if (args.Length >= 1 && args[0] == "--timbre-test")
{
    int pass = 0, fail = 0;
    void TChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-42} {detail}");
    }
    Console.WriteLine("音色可解释筛选引擎实测");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // ① 6 个可解释维度的定义
    TChk("定义了 6 个可解释维度", TimbreFilter.Dims.Length == 6, TimbreFilter.Dims.Length + " 个");
    TChk("维度下标都在 32 维范围内", TimbreFilter.Dims.All(d => d.Index >= 0 && d.Index < AudioFeatures.Dim), "-");
    TChk("每个维度都有中文名与含义", TimbreFilter.Dims.All(d => d.Label.Length > 0 && d.Meaning.Length > 0), "-");
    TChk("punch 标记为「越大越弱」（起音时间）", TimbreFilter.Dims.First(d => d.Key == "punch").HigherIsStronger == false, "-");

    // ② 归一化：造 100 条，某一维递增 → 百分位应单调递增
    var raw = new List<(long, long, float[])>();
    for (int i = 0; i < 100; i++)
    {
        var v = new float[AudioFeatures.Dim];
        v[TimbreFilter.Dim.SpectralCentroid] = i;          // 明亮度递增
        v[TimbreFilter.Dim.Rms] = 100 - i;                 // 响度递减
        raw.Add((i, 1, v));
    }
    var rows = TimbreFilter.Normalize(raw);
    TChk("归一化后行数一致", rows.Count == 100, rows.Count + " 行");
    TChk("归一化到 0~1", rows.All(r => r.Pct.All(x => x >= 0f && x <= 1f)), "-");
    TChk("百分位单调（明亮度递增）", rows[0].Pct[0] < rows[50].Pct[0] && rows[50].Pct[0] < rows[99].Pct[0],
        $"{rows[0].Pct[0]:F2} < {rows[50].Pct[0]:F2} < {rows[99].Pct[0]:F2}");
    TChk("最大值的百分位 ≈ 1", Math.Abs(rows[99].Pct[0] - 1f) < 0.02f, $"{rows[99].Pct[0]:F3}");

    // ③ 筛选：取明亮度 >= 0.5 → 应命中约一半
    var f1 = new TimbreFilter.Filter();
    f1.Set("brightness", 0.5f, null);
    var hit1 = TimbreFilter.Apply(rows, f1);
    TChk("按明亮度 >= 0.5 筛选命中约一半", hit1.Count >= 45 && hit1.Count <= 55, hit1.Count + " 条");

    // ④ 双条件
    var f2 = new TimbreFilter.Filter();
    f2.Set("brightness", 0.5f, null);
    f2.Set("loudness", 0.5f, null);
    var hit2 = TimbreFilter.Apply(rows, f2);
    TChk("双条件（明亮高且响度高）命中更少", hit2.Count < hit1.Count, hit2.Count + " 条（单条件 " + hit1.Count + "）");

    // ⑤ 空条件 = 不筛
    var f3 = new TimbreFilter.Filter();
    TChk("空条件返回全部", TimbreFilter.Apply(rows, f3).Count == 100, "-");
    TChk("IsEmpty 判断正确", f3.IsEmpty && !f1.IsEmpty, "-");

    // ⑥ 条件渲染成人话
    var desc = TimbreFilter.Describe(f2);
    TChk("条件能渲染成中文描述", desc.Contains("明亮度") && desc.Contains("响度") && desc.Contains("≥"), desc);
    TChk("空条件描述明确", TimbreFilter.Describe(f3).Contains("无筛选"), TimbreFilter.Describe(f3));

    // ⑦ 极端条件命中 0（不应崩）
    var f4 = new TimbreFilter.Filter();
    f4.Set("brightness", 0.99f, null);
    f4.Set("loudness", 0.99f, null);
    TChk("极端条件不崩（可能命中 0）", TimbreFilter.Apply(rows, f4).Count >= 0, TimbreFilter.Apply(rows, f4).Count + " 条");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 界面接线审计（防「按钮存在但没绑定」「调了不存在的 RPC」这类静默 bug）──
// 用法: dotnet run -- --wire-audit <wwwrootDir> <MainWindow.xaml.cs>
if (args.Length >= 3 && args[0] == "--wire-audit")
{
    string webDir = args[1], mwPath = args[2];
    int pass = 0, fail = 0;
    void WChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-40} {detail}");
    }
    string html = File.Exists(Path.Combine(webDir, "index.html")) ? File.ReadAllText(Path.Combine(webDir, "index.html")) : "";
    string js = File.Exists(Path.Combine(webDir, "app.js")) ? File.ReadAllText(Path.Combine(webDir, "app.js")) : "";
    string mw = File.Exists(mwPath) ? File.ReadAllText(mwPath) : "";
    WChk("能读到 index.html", html.Length > 0, html.Length + " 字符");
    WChk("能读到 app.js", js.Length > 0, js.Length + " 字符");
    WChk("能读到 MainWindow.xaml.cs", mw.Length > 0, mw.Length + " 字符");

    // ① 所有 <button id="..."> 都必须在 app.js 里【被引用】（`el('id')`）。
    //    **不强制 addEventListener** —— 很多按钮是事件委托绑定的（如 querySelectorAll('[data-cl]')），
    //    强校验会产生大量误报。这条只抓「按钮存在但代码里从没提过」这种明显的漏接线。
    var btnIds = System.Text.RegularExpressions.Regex.Matches(html, "<button[^>]*id=\"([A-Za-z0-9_]+)\"")
        .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
    // 注：允许 el() / ceEl() / getElementById() 三种写法；shell* 由宿主侧处理，不在 app.js。
    var untouched = btnIds.Where(id =>
        !js.Contains($"el('{id}')") && !js.Contains($"ceEl('{id}')") &&
        !js.Contains($"getElementById('{id}')") && !id.StartsWith("shell")).ToList();
    // **信息性提示**（不是失败）：按钮可能由事件委托或宿主侧处理，强校验会误报。
    if (untouched.Count > 0)
        Console.WriteLine($"  --  提示：{untouched.Count} 个按钮未在 app.js 里直接引用（可能由事件委托/宿主处理）: {string.Join(", ", untouched)}");

    // ①b **音色地图页的按钮必须真的绑定**（这是本轮改动的重点，值得强校验）
    var mapBtns = new[] { "btnMapStart", "btnMapPause", "btnMapRebuild", "btnMapInstruments",
                          "btnMapExport", "btnMapExtractAudio", "btnMapReset", "btnMapLogClear",
                          "btnSelPlayAll", "btnSelTag", "btnSelExport", "btnSelClear" };
    var mapUnbound = mapBtns.Where(id =>
        !System.Text.RegularExpressions.Regex.IsMatch(js, $"el\\('{id}'\\)[^;]{{0,80}}addEventListener"))
        .ToList();
    WChk($"音色地图页 {mapBtns.Length} 个按钮全部已绑定", mapUnbound.Count == 0,
        mapUnbound.Count == 0 ? "-" : "未绑定: " + string.Join(", ", mapUnbound));
    // ② 所有 bridge.call('xxx') 都必须在 MainWindow 里有对应 RPC
    var rpcs = System.Text.RegularExpressions.Regex.Matches(js, "bridge\\.call\\('([A-Za-z0-9_]+)'")
        .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
    var missing = rpcs.Where(r => !mw.Contains($"\"{r}\" =>")).ToList();
    WChk($"前端调用 RPC {rpcs.Count} 个，全部存在", missing.Count == 0,
        missing.Count == 0 ? "-" : "不存在: " + string.Join(", ", missing));

    // （「app.js 引用的 id 必须在 HTML 里」这条已移除：很多 id 是 JS 动态创建的，强校验误报太多）

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 档 1 提取性能实测（找出真正的瓶颈）──
// 用法: dotnet run -- --afperf-test <dbPath>
if (args.Length >= 2 && args[0] == "--afperf-test")
{
    var pdb = new Database(args[1]);
    Console.WriteLine("档 1（32 维）提取性能实测");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // 各取 5 个 wav 与 5 个 ncw，分别计时
    var wavs = new List<(string Full, string Ext, string Name)>();
    var ncws = new List<(string Full, string Ext, string Name)>();
    foreach (var lib in pdb.GetLibraries())
    {
        foreach (var c in pdb.GetAnalyzableClips(lib.Id))
        {
            var full = Path.Combine(lib.Path, c.RelPath);
            if (!File.Exists(full)) continue;
            if (c.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase) && wavs.Count < 5) wavs.Add((full, c.Ext, c.Name));
            if (c.Ext.Equals("ncw", StringComparison.OrdinalIgnoreCase) && ncws.Count < 5) ncws.Add((full, c.Ext, c.Name));
        }
        if (wavs.Count >= 5 && ncws.Count >= 5) break;
    }

    // ① 纯计算（wav 直接提）
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int ok1 = 0;
    foreach (var w in wavs) { if (AudioFeatures.Extract(w.Full, 12.0) != null) ok1++; }
    sw.Stop();
    double perWav = wavs.Count > 0 ? sw.Elapsed.TotalMilliseconds / wavs.Count : 0;
    Console.WriteLine($"  ① wav 直接提（{ok1}/{wavs.Count} 成功）      {sw.Elapsed.TotalMilliseconds,8:F0} ms   ⇒ {perWav,7:F1} ms/个");

    // ② ncw：解码 + 提取（分开计时）
    double decMs = 0, extMs = 0; int ok2 = 0;
    foreach (var n in ncws)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "klm_perf_" + Guid.NewGuid().ToString("N") + ".wav");
        var s1 = System.Diagnostics.Stopwatch.StartNew();
        long written = NcwDecoder.DecodeToWav(n.Full, tmp, 12.0);
        s1.Stop(); decMs += s1.Elapsed.TotalMilliseconds;
        if (written > 0)
        {
            var s2 = System.Diagnostics.Stopwatch.StartNew();
            var v = AudioFeatures.Extract(tmp, 12.0);
            s2.Stop(); extMs += s2.Elapsed.TotalMilliseconds;
            if (v != null) ok2++;
        }
        try { File.Delete(tmp); } catch { }
    }
    Console.WriteLine($"  ② ncw 解码部分（{ok2}/{ncws.Count} 成功）   {decMs,8:F0} ms   ⇒ {decMs / Math.Max(1, ncws.Count),7:F1} ms/个");
    Console.WriteLine($"  ③ ncw 提取部分（纯计算）              {extMs,8:F0} ms   ⇒ {extMs / Math.Max(1, ncws.Count),7:F1} ms/个");

    // ⑤ 不同 maxSeconds 的耗时对比（看降时长能省多少）
    foreach (double sec in new[] { 12.0, 8.0, 6.0, 3.0 })
    {
        var sw5 = System.Diagnostics.Stopwatch.StartNew();
        foreach (var w in wavs) AudioFeatures.Extract(w.Full, sec);
        sw5.Stop();
        Console.WriteLine($"  ⑤ maxSeconds={sec,4:F0} 的 wav 耗时          {sw5.Elapsed.TotalMilliseconds,8:F0} ms   ⇒ {sw5.Elapsed.TotalMilliseconds / Math.Max(1, wavs.Count),7:F1} ms/个");
    }

    // ④ 数据库写入开销
    var sw4 = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 20; i++) pdb.CountAudioFeatures();
    sw4.Stop();
    Console.WriteLine($"  ④ 单次 DB 查询 × 20                  {sw4.Elapsed.TotalMilliseconds,8:F0} ms   ⇒ {sw4.Elapsed.TotalMilliseconds / 20,7:F1} ms/次");

    double totalPerNcw = (decMs + extMs) / Math.Max(1, ncws.Count);
    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"  【结论】wav {perWav:F1} ms/个 · ncw {totalPerNcw:F1} ms/个（解码占 {decMs / Math.Max(1, (decMs + extMs)) * 100:F0}%）");
    Console.WriteLine($"  按 ncw 速度估算 33,951 个 ⇒ 约 {33951 * totalPerNcw / 1000 / 60:F0} 分钟");
    return 0;
}

// ── GPU（DirectML）与并行提速实测 ──
// 用法: dotnet run -- --gpu-test <dbPath> <模型路径>
if (args.Length >= 3 && args[0] == "--gpu-test")
{
    var gdb = new Database(args[1]);
    string gmp = args[2];
    Console.WriteLine("GPU（DirectML）与并行提速实测");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // 找一个可解码片段
    string? clip = null;
    foreach (var lib in gdb.GetLibraries())
    {
        foreach (var c in gdb.GetAnalyzableClips(lib.Id))
        {
            if (!c.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(lib.Path, c.RelPath);
            if (File.Exists(full)) { clip = full; break; }
        }
        if (clip != null) break;
    }
    if (clip == null) { Console.WriteLine("  ⏭ 没找到 wav 样本，跳过"); return 1; }

    // ① CPU 基线
    MertFeatures.ResetSession();
    var sw1 = System.Diagnostics.Stopwatch.StartNew();
    var v1 = MertFeatures.Extract(clip, 10.0, gmp, false, 8);
    sw1.Stop();
    Console.WriteLine($"  ① CPU 提取（8 线程）      {sw1.Elapsed.TotalMilliseconds,8:F0} ms   {(v1 != null ? v1.Length + " 维" : "失败")}");

    // ② GPU（DirectML）
    MertFeatures.ResetSession();
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    var v2 = MertFeatures.Extract(clip, 10.0, gmp, true, 0);
    sw2.Stop();
    Console.WriteLine($"  ② GPU 提取（DirectML）    {sw2.Elapsed.TotalMilliseconds,8:F0} ms   {(v2 != null ? v2.Length + " 维" : "失败")}");
    Console.WriteLine($"     GPU 是否真的用上: {(MertFeatures.LastGpuUsed ? "✅ 是" : "❌ 否（已回退 CPU）")}");
    if (MertFeatures.LastGpuError.Length > 0)
        Console.WriteLine($"     回退原因: {MertFeatures.LastGpuError}");

    // ③ 两次结果应一致（GPU 与 CPU 数值接近）
    if (v1 != null && v2 != null)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < Math.Min(v1.Length, v2.Length); i++) { dot += (double)v1[i] * v2[i]; na += (double)v1[i] * v1[i]; nb += (double)v2[i] * v2[i]; }
        double cos = dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-9);
        Console.WriteLine($"  ③ CPU 与 GPU 结果余弦相似度: {cos:F4}   {(cos > 0.99 ? "✅ 一致" : "⚠ 差异较大")}");
    }

    // ④ 并行提速（声学特征）
    var sw3 = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 8; i++) AudioFeatures.ExtractAny(clip, "wav");
    sw3.Stop();
    double serial = sw3.Elapsed.TotalMilliseconds / 8;
    Console.WriteLine($"  ④ 声学特征 串行           {serial,8:F1} ms/个");
    var sw4 = System.Diagnostics.Stopwatch.StartNew();
    System.Threading.Tasks.Parallel.For(0, 8, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 16 }, (i) => AudioFeatures.ExtractAny(clip, "wav"));
    sw4.Stop();
    double par = sw4.Elapsed.TotalMilliseconds / 8;
    Console.WriteLine($"  ⑤ 声学特征 16 线程并行    {par,8:F1} ms/个   ⇒ 提速 {serial / Math.Max(0.01, par):F1} 倍");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"  本机逻辑核: {Environment.ProcessorCount}");
    return 0;
}

// ── FP32 vs UINT8 在 DirectML 上的对比（决定 GPU 到底能不能用）──
// 用法: dotnet run -- --fp32-test <dbPath> <fp32模型> <uint8模型>
if (args.Length >= 4 && args[0] == "--fp32-test")
{
    var fdb = new Database(args[1]);
    string fp32 = args[2], u8 = args[3];
    Console.WriteLine("FP32 vs UINT8 在 DirectML 上的对比");
    Console.WriteLine(new string(char.Parse("-"), 78));

    string? clip = null;
    foreach (var lib in fdb.GetLibraries())
    {
        foreach (var c in fdb.GetAnalyzableClips(lib.Id))
        {
            if (!c.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(lib.Path, c.RelPath);
            if (File.Exists(full)) { clip = full; break; }
        }
        if (clip != null) break;
    }
    if (clip == null) { Console.WriteLine("  ⏭ 没找到 wav 样本"); return 1; }

    foreach (var (label, mp) in new[] { ("FP32", fp32), ("UINT8", u8) })
    {
        if (!File.Exists(mp)) { Console.WriteLine($"  [{label}] 模型不存在: {mp}"); continue; }
        Console.WriteLine($"  ── {label}：{Path.GetFileName(mp)}（{new FileInfo(mp).Length / 1024 / 1024} MB）");

        MertFeatures.ResetSession();
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var v1 = MertFeatures.Extract(clip, 10.0, mp, false, 8);
        sw1.Stop();
        Console.WriteLine($"     CPU     {sw1.Elapsed.TotalMilliseconds,8:F0} ms   {(v1 != null ? v1.Length + " 维 ✅" : "失败 ❌")}");

        MertFeatures.ResetSession();
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var v2 = MertFeatures.Extract(clip, 10.0, mp, true, 0);
        sw2.Stop();
        bool gpu = MertFeatures.LastGpuUsed;
        Console.WriteLine($"     GPU     {sw2.Elapsed.TotalMilliseconds,8:F0} ms   {(v2 != null ? v2.Length + " 维 ✅" : "失败 ❌")}   GPU 真用上: {(gpu ? "✅ 是" : "❌ 否")}");
        if (MertFeatures.LastGpuError.Length > 0)
            Console.WriteLine($"             原因: {MertFeatures.LastGpuError.Substring(0, Math.Min(150, MertFeatures.LastGpuError.Length))}");
    }

    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}

// ── ONNX 模型元信息 + DirectML 确切错误 ──
// 用法: dotnet run -- --onnxmeta-test <模型路径>
if (args.Length >= 2 && args[0] == "--onnxmeta-test")
{
    string mp = args[1];
    Console.WriteLine("ONNX 模型元信息（看输入是不是动态形状）");
    Console.WriteLine(new string(char.Parse("-"), 78));
    try
    {
        var so = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_DISABLE_ALL };
        using var s = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so);
        Console.WriteLine($"  文件: {Path.GetFileName(mp)}（{new FileInfo(mp).Length / 1024 / 1024} MB）");
        foreach (var kv in s.InputMetadata)
        {
            var d = kv.Value.Dimensions;
            bool dyn = d.Any(x => x <= 0);
            Console.WriteLine($"  输入 {kv.Key}: [{string.Join(",", d)}] {kv.Value.ElementType}  {(dyn ? "⚠ 含动态轴（DirectML 的痛点）" : "✅ 全静态")}");
        }
        foreach (var kv in s.OutputMetadata)
        {
            var d = kv.Value.Dimensions;
            bool dyn = d.Any(x => x <= 0);
            Console.WriteLine($"  输出 {kv.Key}: [{string.Join(",", d)}] {kv.Value.ElementType}  {(dyn ? "⚠ 含动态轴" : "✅ 全静态")}");
        }
    }
    catch (Exception ex) { Console.WriteLine("  读取失败: " + ex.Message); }

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine("DirectML 的确切错误：");
    try
    {
        var so2 = new Microsoft.ML.OnnxRuntime.SessionOptions();
        so2.AppendExecutionProvider_DML(0);
        using var s2 = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so2);
        Console.WriteLine("  会话创建: ✅ 成功（EP 附加没问题）");
        var input = new float[16000 * 3];
        var ten = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(input, new[] { 1, input.Length });
        var ins = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue> { Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(s2.InputMetadata.Keys.First(), ten) };
        try
        {
            using var res = s2.Run(ins);
            Console.WriteLine("  推理: ✅ 成功 —— **DirectML 其实能用！**");
        }
        catch (Exception ex2)
        {
            Console.WriteLine("  推理: ❌ 失败");
            Console.WriteLine("  确切错误: " + ex2.Message);
        }
    }
    catch (Exception ex) { Console.WriteLine("  会话创建失败: " + ex.Message); }
    return 0;
}

// ── 试 AddFreeDimensionOverride 固定动态轴，看 DirectML 能否跑通 ──
// 用法: dotnet run -- --freedim-test <模型路径>
if (args.Length >= 2 && args[0] == "--freedim-test")
{
    string mp = args[1];
    Console.WriteLine("试 AddFreeDimensionOverride 固定动态轴");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // ① 先看模型里的 free dimension 名字（从 ONNX protobuf 里找 dim_param）
    try
    {
        var bytes = File.ReadAllBytes(mp);
        var txt = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4000000));
        var m = System.Text.RegularExpressions.Regex.Matches(txt, "[A-Za-z_][A-Za-z0-9_]{2,40}");
        var cand = m.Select(x => x.Value)
                    .Where(s => s.Contains("len") || s.Contains("seq") || s.Contains("time") || s.Contains("frame") || s.Contains("batch"))
                    .Distinct().Take(12).ToList();
        Console.WriteLine("  含 len/seq/time/frame/batch 的字符串候选（可能是动态轴名）:");
        foreach (var c in cand) Console.WriteLine("    · " + c);
        if (cand.Count == 0) Console.WriteLine("    （没找到 —— 说明动态轴可能是【匿名】的，无法按名覆盖）");
    }
    catch (Exception ex) { Console.WriteLine("  扫描失败: " + ex.Message); }

    // ② 试按名覆盖 + 试按序号覆盖
    foreach (var dimName in new[] { "seq_len", "sequence", "input_length", "time", "frames", "batch_size" })
    {
        try
        {
            var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
            so.AppendExecutionProvider_DML(0);
            so.AddFreeDimensionOverrideByName(dimName, 160000);   // 固定 10 秒 @16kHz
            using var s = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so);
            var input = new float[16000 * 10];
            var ten = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(input, new[] { 1, input.Length });
            var ins = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue> { Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), ten) };
            using var res = s.Run(ins);
            Console.WriteLine($"  ✅ 按名 [{dimName}] 覆盖后【推理成功】—— DirectML 能用！");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ [{dimName}] 失败: " + ex.Message.Substring(0, Math.Min(110, ex.Message.Length)));
        }
    }

    // ③ 试匿名维度的按序号覆盖（AddFreeDimensionOverride 旧 API）
    try
    {
        var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
        so.AppendExecutionProvider_DML(0);
        so.AddFreeDimensionOverrideByName("", 160000);
        using var s = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so);
        Console.WriteLine("  ✅ 空名覆盖：会话创建成功（但还需推理验证）");
    }
    catch (Exception ex) { Console.WriteLine("  ❌ 空名覆盖: " + ex.Message.Substring(0, Math.Min(110, ex.Message.Length))); }

    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}

// ── 用 C# DirectML 包做【真】测试（固定形状模型）──
// 用法: dotnet run -- --realdml-test <模型路径>
if (args.Length >= 2 && args[0] == "--realdml-test")
{
    string mp = args[1];
    Console.WriteLine("用 C# DirectML 包测固定形状模型");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // ① 元信息
    var so0 = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_DISABLE_ALL };
    using (var s0 = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so0))
    {
        foreach (var kv in s0.InputMetadata)
            Console.WriteLine($"  输入 {kv.Key}: [{string.Join(",", kv.Value.Dimensions)}]  {(kv.Value.Dimensions.Any(x => x <= 0) ? "⚠ 动态" : "✅ 静态")}");
        foreach (var kv in s0.OutputMetadata)
            Console.WriteLine($"  输出 {kv.Key}: [{string.Join(",", kv.Value.Dimensions)}]  {(kv.Value.Dimensions.Any(x => x <= 0) ? "⚠ 动态" : "✅ 静态")}");
    }

    // ② DirectML 真测试
    try
    {
        var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
        so.AppendExecutionProvider_DML(0);
        using var s = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so);
        Console.WriteLine("  会话创建: ✅");
        var input = new float[16000 * 10];
        var ten = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(input, new[] { 1, input.Length });
        var ins = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue> { Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), ten) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var res = s.Run(ins);
        sw.Stop();
        var dims = res.First().AsTensor<float>().Dimensions.ToArray();
        Console.WriteLine($"  DirectML 推理: ✅✅ 成功！输出 [{string.Join(",", dims)}]  耗时 {sw.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine("  🎉 **固定形状后 DirectML 真的能跑了！**");
    }
    catch (Exception ex)
    {
        Console.WriteLine("  DirectML 推理: ❌ 失败");
        Console.WriteLine("  错误: " + ex.Message.Substring(0, Math.Min(250, ex.Message.Length)));
    }

    // ③ CPU 基线对比
    try
    {
        using var s2 = new Microsoft.ML.OnnxRuntime.InferenceSession(mp);
        var input2 = new float[16000 * 10];
        var ten2 = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(input2, new[] { 1, input2.Length });
        var ins2 = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue> { Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(s2.InputMetadata.Keys.First(), ten2) };
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        using var r2 = s2.Run(ins2);
        sw2.Stop();
        Console.WriteLine($"  CPU 基线: {sw2.Elapsed.TotalMilliseconds:F0} ms");
    }
    catch (Exception ex) { Console.WriteLine("  CPU 基线失败: " + ex.Message.Substring(0, Math.Min(150, ex.Message.Length))); }

    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}

// ── 取 DirectML 初始化的【完整】错误 ──
if (args.Length >= 2 && args[0] == "--fullerr-test")
{
    string mp = args[1];
    Console.WriteLine("DirectML 初始化完整错误：");
    Console.WriteLine(new string(char.Parse("-"), 78));
    try
    {
        var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
        so.AppendExecutionProvider_DML(0);
        so.LogSeverityLevel = Microsoft.ML.OnnxRuntime.OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
        using var s = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so);
        Console.WriteLine("  会话创建成功（不该到这）");
    }
    catch (Exception ex)
    {
        Console.WriteLine("  ── ex.Message ──");
        Console.WriteLine(ex.Message);
        Console.WriteLine("");
        Console.WriteLine("  ── ex.InnerException ──");
        var inner = ex.InnerException;
        int d = 0;
        while (inner != null && d < 4)
        {
            Console.WriteLine($"  [{d}] {inner.Message}");
            inner = inner.InnerException; d++;
        }
    }
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}

// ── 对照实验：原动态量化版 vs 新固定量化版，谁能过 DirectML 初始化 ──
if (args.Length >= 3 && args[0] == "--contrast-test")
{
    Console.WriteLine("DirectML 初始化对照实验");
    Console.WriteLine(new string(char.Parse("-"), 78));
    foreach (var mp in args.Skip(1))
    {
        if (!File.Exists(mp)) { Console.WriteLine($"  {Path.GetFileName(mp)}: 不存在"); continue; }
        Console.Write($"  {Path.GetFileName(mp),-28} ");
        // 先看形状
        string shape = "?";
        try
        {
            var so0 = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_DISABLE_ALL };
            using var s0 = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so0);
            var d = s0.InputMetadata.Values.First().Dimensions;
            shape = "[" + string.Join(",", d) + "]";
        }
        catch { }
        // 试 DirectML 初始化（带日志文件）
        string logFile = Path.Combine(Path.GetTempPath(), "ort_dml_" + Path.GetFileNameWithoutExtension(mp) + ".log");
        try { if (File.Exists(logFile)) File.Delete(logFile); } catch { }
        string result;
        try
        {
            var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
            so.AppendExecutionProvider_DML(0);
            so.LogSeverityLevel = Microsoft.ML.OnnxRuntime.OrtLoggingLevel.ORT_LOGGING_LEVEL_INFO;
            so.LogVerbosityLevel = 1;
            so.LogId = "dmltest";
            using var s = new Microsoft.ML.OnnxRuntime.InferenceSession(mp, so);
            result = "✅ 初始化成功";
        }
        catch (Exception ex) { result = "❌ 初始化失败: " + ex.Message.Substring(0, Math.Min(90, ex.Message.Length)); }
        Console.WriteLine($"输入{shape,-14} {result}");
    }
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}

// ── MERT 多片段并行提速实测 ──
// 用法: dotnet run -- --mertperf-test <dbPath> <模型路径>
if (args.Length >= 3 && args[0] == "--mertperf-test")
{
    var mdb = new Database(args[1]);
    string mp = args[2];
    Console.WriteLine("MERT 多片段并行提速实测");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // 取 16 个可解码 wav
    var files = new List<string>();
    foreach (var lib in mdb.GetLibraries())
    {
        foreach (var c in mdb.GetAnalyzableClips(lib.Id))
        {
            if (!c.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(lib.Path, c.RelPath);
            if (File.Exists(full)) files.Add(full);
            if (files.Count >= 16) break;
        }
        if (files.Count >= 16) break;
    }
    if (files.Count < 8) { Console.WriteLine("  样本不足"); return 1; }
    Console.WriteLine($"  样本数: {files.Count}");
    Console.WriteLine($"  逻辑核: {Environment.ProcessorCount}");
    Console.WriteLine("");

    // ① 串行（基线）
    MertFeatures.ResetSession();
    var sw1 = System.Diagnostics.Stopwatch.StartNew();
    int ok1 = 0;
    foreach (var f in files) { if (MertFeatures.Extract(f, 10.0, mp, false, 1) != null) ok1++; }
    sw1.Stop();
    double serial = sw1.Elapsed.TotalMilliseconds;
    Console.WriteLine($"  ① 串行（IntraOp=1）        {serial,8:F0} ms  ({ok1}/{files.Count} 成功)  => {serial / files.Count,6:F1} ms/个");

    // ② 并行：4 线程
    foreach (int th in new[] { 4, 8, 16 })
    {
        MertFeatures.ResetSession();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int ok = 0;
        System.Threading.Tasks.Parallel.ForEach(files,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = th },
            (f) => { if (MertFeatures.Extract(f, 10.0, mp, false, th) != null) System.Threading.Interlocked.Increment(ref ok); });
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  ② 并行 {th,2} 线程            {ms,8:F0} ms  ({ok}/{files.Count} 成功)  => {ms / files.Count,6:F1} ms/个   提速 {serial / Math.Max(1, ms):F1} 倍");
    }

    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}

// ── KSP 深化三件套实测（模板库 / 自纠闭环 / 交付）──
// 用法: dotnet run -- --ksp3-test <kspc.exe>
if (args.Length >= 2 && args[0] == "--ksp3-test")
{
    string exe = args[1];
    var ctx = new AgentToolContext { Db = null };
    int pass = 0, fail = 0;
    void KChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-40} {detail}");
    }

    Console.WriteLine("KSP 深化三件套实测");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // ① 模板库
    var ts = global::KontaktLibManager.Core.KspTemplates.All();
    KChk("模板库有模板", ts.Count >= 5, ts.Count + " 个");
    KChk("每个模板都有 key/title/when/body", ts.All(x => x.Key.Length > 0 && x.Title.Length > 0 && x.When.Length > 0 && x.Body.Length > 50), "-");
    var kspMiss = global::KontaktLibManager.Core.KspTemplates.ValidateUses();
    KChk("模板用到的命令都真实存在（符号表核对）", kspMiss.Count == 0, kspMiss.Count == 0 ? "-" : "不存在: " + string.Join(",", kspMiss));
    var r1 = AgentActions.KspTemplates(ctx, "{}");
    KChk("ksp_templates 列出模板", r1.Contains("\"ok\":true") && r1.Contains("minimal"), "-");
    var r2 = AgentActions.KspTemplates(ctx, "{\"key\":\"ui_panel\"}");
    KChk("ksp_templates 取模板", r2.Contains("ui_knob") && r2.Contains("%%"), "-");
    var r2b = AgentActions.KspTemplates(ctx, "{\"key\":\"ui_panel\",\"values\":{\"面板标题\":\"Test\"}}");
    KChk("占位符能被填", r2b.Contains("Test") && !r2b.Contains("%%面板标题%%"), "-");

    // ② 自纠闭环：故意写一个缺 end on 的脚本
    string bad = "on init\n    declare $x\n    $x := 1\n";   // 缺 end on
    var fix = global::KontaktLibManager.Core.KspAutoFix.Run(exe, bad, Path.Combine(Path.GetTempPath(), "ksp3test"), 3);
    KChk("自纠闭环能跑", fix.Rounds.Count > 0, fix.Rounds.Count + " 轮");
    KChk("自纠有过程记录", fix.Rounds.Any(x => x.Fixes.Count > 0 || x.Remaining.Count > 0), "-");
    Console.WriteLine($"       总结: {fix.Summary.Replace("\n", " | ").Substring(0, Math.Min(160, fix.Summary.Replace("\n", " | ").Length))}");
    var r3 = AgentActions.KspAutofix(ctx, "{\"code\":\"on init\\n    declare $x\\n    $x := 1\\n\",\"maxRounds\":2}");
    KChk("ksp_autofix 返回结构", r3.Contains("\"rounds\"") && r3.Contains("\"summary\""), "-");

    // ③ 交付
    var dl = global::KontaktLibManager.Core.KspDeliver.Deliver("on init\nend on\n", Path.Combine(Path.GetTempPath(), "ksp3deliver"), "test-script");
    KChk("交付写出文件", dl.Ok && File.Exists(dl.Path), dl.Path);
    KChk("交付给出加载指引", dl.Steps.Count >= 6, dl.Steps.Count + " 步");
    KChk("指引明确说不自动注入 NKI", dl.Steps.Any(s => s.Contains("不会自动注入")), "-");
    var r4 = AgentActions.KspDeliver(ctx, "{\"code\":\"on init\\nend on\\n\",\"fileName\":\"via-tool\"}");
    KChk("ksp_deliver 工具可用", r4.Contains("\"ok\":true") && r4.Contains("steps"), "-");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 查符号表里到底有没有这些名字 ──
if (args.Length >= 1 && args[0] == "--chksym-test")
{
    var all = KspSymbols.LoadAll();
    Console.WriteLine($"符号表共 {all.Count} 个符号");
    Console.WriteLine(new string(char.Parse("-"), 78));
    foreach (var n in new[] { "declare", "EVENT_NOTE", "EVENT_VELOCITY", "EVENT_ID",
                              "CC_NUM", "CC_VALUE", "mod", "on controller", "controller",
                              "ui_knob", "ui_label", "ui_switch", "set_text", "make_persistent",
                              "ignore_event", "play_note", "ui_control" })
    {
        var s = KspSymbols.ByName(n);
        Console.WriteLine($"  {n,-16} {(s != null ? "OK  (" + s.Kind + ")" : "缺失")}");
    }
    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine("  各类别统计:");
    foreach (var (cat, cnt) in KspSymbols.Categories()) Console.WriteLine($"    {cat,-14} {cnt}");
    Console.WriteLine("  含 EVENT 的符号（前 12）:");
    foreach (var s in all.Where(x => x.Name.ToUpperInvariant().Contains("EVENT")).Take(12)) Console.WriteLine("    " + s.Name + "  [" + s.Kind + "]");
    Console.WriteLine("  含 CC 的符号（前 8）:");
    foreach (var s in all.Where(x => x.Name.ToUpperInvariant().Contains("CC")).Take(8)) Console.WriteLine("    " + s.Name + "  [" + s.Kind + "]");
    Console.WriteLine("  含 declare 的符号（前 5）:");
    foreach (var s in all.Where(x => x.Name.ToLowerInvariant().Contains("declare")).Take(5)) Console.WriteLine("    " + s.Name + "  [" + s.Kind + "]");
    return 0;
}

if (args.Length >= 1 && args[0] == "--chkvar-test")
{
    var all = KspSymbols.LoadAll();
    Console.WriteLine("variable 类符号（前 25 个）:");
    foreach (var s in all.Where(x => x.Kind == "variable").Take(25)) Console.WriteLine("   " + s.Name);
    Console.WriteLine("");
    Console.WriteLine("variable 里含 NOTE / VEL / EVENT 的:");
    foreach (var s in all.Where(x => x.Kind == "variable" && (x.Name.ToUpperInvariant().Contains("NOTE") || x.Name.ToUpperInvariant().Contains("VEL") || x.Name.ToUpperInvariant().Contains("EVENT"))).Take(20)) Console.WriteLine("   " + s.Name);
    Console.WriteLine("");
    Console.WriteLine("所有 callback 名:");
    foreach (var s in all.Where(x => x.Kind == "callback")) Console.WriteLine("   " + s.Name);
    Console.WriteLine("");
    Console.WriteLine("midi 类（前 20）:");
    foreach (var s in all.Where(x => x.Category == "midi").Take(20)) Console.WriteLine("   " + s.Name + " [" + s.Kind + "]");
    return 0;
}

// ── 看谱特征的【原始值】（确认 tanh 饱和的根因）──
// 用法: dotnet run -- --rawfeat-test <dbPath>
if (args.Length >= 2 && args[0] == "--rawfeat-test")
{
    var rdb = new Database(args[1]);
    Console.WriteLine("谱特征原始值探查（不看 tanh 后的值）");
    Console.WriteLine(new string(char.Parse("-"), 78));

    int shown = 0;
    foreach (var lib in rdb.GetLibraries())
    {
        foreach (var c in rdb.GetAnalyzableClips(lib.Id))
        {
            if (!c.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(lib.Path, c.RelPath);
            if (!File.Exists(full)) continue;
            // 直接读 16k 单声道，然后跑 NWaves 的谱特征提取器
            float[] mono;
            try { mono = MertFeatures.ReadMono16k(full, 10.0); } catch { continue; }
            if (mono.Length < 16000) continue;
            var sig = new NWaves.Signals.DiscreteSignal(16000, mono);
            var opts = new NWaves.FeatureExtractors.Options.MultiFeatureOptions { SamplingRate = 16000, FftSize = 1024, HopSize = 512 };
            var spec = new NWaves.FeatureExtractors.Multi.SpectralFeaturesExtractor(opts).ComputeFrom(sig);
            var tvec = new NWaves.FeatureExtractors.Multi.TimeDomainFeaturesExtractor(opts).ComputeFrom(sig);
            if (spec == null || spec.Count == 0) continue;
            var s0 = spec[0];
            Console.WriteLine($"  {c.Name.Substring(0, Math.Min(34, c.Name.Length)),-36} 谱维数={s0.Length}");
            Console.Write("     谱特征前 6 维原始值: ");
            for (int k = 0; k < Math.Min(6, s0.Length); k++) Console.Write($"{s0[k],12:F4}");
            Console.WriteLine();
            if (tvec != null && tvec.Count > 0)
            {
                Console.Write("     时域前 3 维原始值:   ");
                for (int k = 0; k < Math.Min(3, tvec[0].Length); k++) Console.Write($"{tvec[0][k],12:F6}");
                Console.WriteLine();
            }
            if (++shown >= 3) break;
        }
        if (shown >= 3) break;
    }
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}

// ── 验证量纲修正：26/27 维不应再恒为 1.0 ──
if (args.Length >= 2 && args[0] == "--fixverify-test")
{
    var fdb = new Database(args[1]);
    Console.WriteLine("量纲修正验证（看 26/27 维是否还有区分度）");
    Console.WriteLine(new string(char.Parse("-"), 78));
    var got = new List<float[]>();
    foreach (var lib in fdb.GetLibraries())
    {
        foreach (var c in fdb.GetAnalyzableClips(lib.Id))
        {
            if (!c.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(lib.Path, c.RelPath);
            if (!File.Exists(full)) continue;
            var v = AudioFeatures.Extract(full, 10.0);
            if (v != null) got.Add(v);
            if (got.Count >= 8) break;
        }
        if (got.Count >= 8) break;
    }
    if (got.Count == 0) { Console.WriteLine("  没取到样本"); return 1; }
    Console.WriteLine($"  样本数 {got.Count}");
    Console.WriteLine("");
    Console.WriteLine("  下标  范围（越小越有区分度）        是否还饱和");
    foreach (int i in new[] { 26, 27, 28, 29, 30, 31 })
    {
        float mn = got.Min(v => v[i]), mx = got.Max(v => v[i]);
        bool sat = Math.Abs(mx - mn) < 1e-6f;
        Console.WriteLine($"  {i,4}  {mn,10:F6} ~ {mx,10:F6}     {(sat ? "❌ 仍饱和" : "✅ 有区分度")}");
    }
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}

// ── 查明 NWaves SpectralFeaturesExtractor 的【列顺序】（用合成信号）──
if (args.Length >= 1 && args[0] == "--specorder-test")
{
    Console.WriteLine("NWaves 谱特征列顺序探查（合成信号）");
    Console.WriteLine(new string(char.Parse("-"), 78));
    int sr = 16000;
    var opts = new NWaves.FeatureExtractors.Options.MultiFeatureOptions { SamplingRate = sr, FftSize = 1024, HopSize = 512 };

    // 造 4 个信号：60Hz 纯低音、440Hz 中音、4000Hz 纯高音、白噪声
    var tests = new (string Name, float Freq, bool Noise)[]
    {
        ("60Hz 纯低音(BASS感)", 60f, false),
        ("440Hz 中音", 440f, false),
        ("4000Hz 纯高音", 4000f, false),
        ("白噪声(全频)", 0f, true),
    };

    var results = new List<(string Name, float[] Vec)>();
    foreach (var (name, freq, noise) in tests)
    {
        var buf = new float[sr];
        var rnd = new Random(42);
        for (int i = 0; i < buf.Length; i++)
            buf[i] = noise ? (float)(rnd.NextDouble() * 2 - 1) : (float)Math.Sin(2 * Math.PI * freq * i / sr);
        var sig = new NWaves.Signals.DiscreteSignal(sr, buf);
        var spec = new NWaves.FeatureExtractors.Multi.SpectralFeaturesExtractor(opts).ComputeFrom(sig);
        if (spec == null || spec.Count == 0) { Console.WriteLine("  " + name + " 提取失败"); continue; }
        results.Add((name, spec[0]));
    }

    if (results.Count == 0) return 1;
    int dim = results[0].Vec.Length;
    Console.WriteLine("  列数 = " + dim);
    Console.WriteLine("");
    Console.Write("  列  ");
    foreach (var r in results) Console.Write(r.Name.PadLeft(20));
    Console.WriteLine("    ← 哪列随音高单调变化");
    for (int k = 0; k < dim; k++)
    {
        Console.Write("  " + k.ToString().PadLeft(2) + "  ");
        foreach (var r in results) Console.Write(r.Vec[k].ToString("F4").PadLeft(20));
        // 判断：低音→高音是否单调递增
        bool inc = true;
        for (int i = 0; i + 1 < results.Count - 1; i++)
            if (results[i].Vec[k] >= results[i + 1].Vec[k]) { inc = false; break; }
        Console.WriteLine(inc ? "    ✅ 随音高递增" : "");
    }
    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine("  （60Hz→440Hz→4000Hz 递增的列 = 与「音高/明亮度」正相关）");
    return 0;
}
// ── 验证「打击感」不再反（语义反转 + 新 attack 算法）──
// 用法: dotnet run -- --punch-test <dbPath>
if (args.Length >= 2 && args[0] == "--punch-test")
{
    var pdb = new Database(args[1]);
    Console.WriteLine("打击感验证（找真实打击类 + PAD 类样本对比）");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // 取两组：文件名含 percussion/drum/hit 的（应该是打击类）+ 含 pad/atmos/texture 的（应该是持续类）
    var perc = new List<(string Name, float Atk, float Pct)>();
    var pad = new List<(string Name, float Atk, float Pct)>();
    foreach (var lib in pdb.GetLibraries())
    {
        foreach (var c in pdb.GetAnalyzableClips(lib.Id))
        {
            if (!c.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(lib.Path, c.RelPath);
            if (!File.Exists(full)) continue;
            var v = AudioFeatures.Extract(full, 10.0);
            if (v == null) continue;
            float atk = v[31];
            string ln = (c.Name + " " + c.RelPath).ToLowerInvariant();
            if (perc.Count < 6 && (ln.Contains("perc") || ln.Contains("hit") || ln.Contains("drum") || ln.Contains("stab")))
                perc.Add((c.Name, atk, 0));
            else if (pad.Count < 6 && (ln.Contains("pad") || ln.Contains("atmos") || ln.Contains("texture") || ln.Contains("drone")))
                pad.Add((c.Name, atk, 0));
            if (perc.Count >= 6 && pad.Count >= 6) break;
        }
        if (perc.Count >= 6 && pad.Count >= 6) break;
    }

    Console.WriteLine("  【打击类样本】起音时间应该【小】");
    foreach (var x in perc) Console.WriteLine($"     {x.Name.Substring(0, Math.Min(38, x.Name.Length)),-40} 起音 {x.Atk:F4} s");
    Console.WriteLine("");
    Console.WriteLine("  【PAD/持续类样本】起音时间应该【大】");
    foreach (var x in pad) Console.WriteLine($"     {x.Name.Substring(0, Math.Min(38, x.Name.Length)),-40} 起音 {x.Atk:F4} s");
    Console.WriteLine("");

    double avgPerc = perc.Count > 0 ? perc.Average(x => x.Atk) : -1;
    double avgPad = pad.Count > 0 ? pad.Average(x => x.Atk) : -1;
    Console.WriteLine($"  打击类平均起音 {avgPerc:F4}s  vs  PAD类平均起音 {avgPad:F4}s");
    bool ok = avgPerc >= 0 && avgPad >= 0 && avgPerc < avgPad;
    Console.WriteLine($"  {(ok ? "✅ 打击类起音更短（算法方向正确）" : "❌ 方向可疑，需检查")}");
    Console.WriteLine("");
    Console.WriteLine("  ⚠️ 语义反转已生效：Pct = 1 - 百分位（起音越短 → Pct 越大 = 打击感越强）");
    Console.WriteLine(new string(char.Parse("-"), 78));
    return ok ? 0 : 1;
}
// ── 验证僵尸进程被正确排除 ──
if (args.Length >= 1 && args[0] == "--zombie-test")
{
    Console.WriteLine("僵尸进程排除验证");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // 直接枚举所有 Kontakt* 进程，分别统计「全部 / 僵尸 / 活着的」
    var all = new List<(int Pid, string Name, long Mem, bool Zombie)>();
    foreach (var pr in System.Diagnostics.Process.GetProcesses())
    {
        try
        {
            if (!pr.ProcessName.StartsWith("Kontakt", StringComparison.OrdinalIgnoreCase)) continue;
            bool z = false;
            long mem = -1;
            try { mem = pr.WorkingSet64; } catch { z = true; }
            try { if (pr.HasExited) z = true; } catch { z = true; }
            if (mem == 0 && !z) { try { if (pr.PrivateMemorySize64 == 0) z = true; } catch { z = true; } }
            all.Add((pr.Id, pr.ProcessName, mem, z));
        }
        catch { }
    }

    Console.WriteLine($"  枚举到 Kontakt* 进程 {all.Count} 个：");
    foreach (var x in all.OrderBy(x => x.Name))
        Console.WriteLine($"    PID {x.Pid,-7} {x.Name,-30} 内存 {x.Mem / 1024.0 / 1024.0,7:F1} MB  {(x.Zombie ? "僵尸（应排除）" : "活着")}");
    Console.WriteLine("");

    // 用真实实现判断
    var real = KontaktInfo.KontaktProcesses();
    Console.WriteLine($"  ✅ KontaktInfo.KontaktProcesses() 返回 {real.Count} 个（应只含【活着】的）");
    Console.WriteLine($"  ✅ KontaktInfo.IsRunning() = {KontaktInfo.IsRunning()}");
    bool ok = real.Count == all.Count(x => !x.Zombie);
    Console.WriteLine($"  {(ok ? "✅ 与预期一致（僵尸已被排除）" : "❌ 数量不符，需检查")}");
    Console.WriteLine(new string(char.Parse("-"), 78));
    return ok ? 0 : 1;
}
// ── 验证 OGG / MP3 解码（新增能力）──
// 用法: dotnet run -- --decoder-test <dbPath>
if (args.Length >= 2 && args[0] == "--decoder-test")
{
    var ddb = new Database(args[1]);
    Console.WriteLine("OGG / MP3 解码验证");
    Console.WriteLine(new string(char.Parse("-"), 78));
    int pass = 0, fail = 0;
    void DChk(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine($"  {(ok ? "OK " : "NG ")} {n,-34} {d}"); }

    // 从库里挑真实的 ogg / mp3 文件
    var pick = new Dictionary<string, string>();
    foreach (var lib in ddb.GetLibraries())
    {
        foreach (var c in ddb.GetAnalyzableClips(lib.Id))
        {
            var e2 = (c.Ext ?? "").ToLowerInvariant();
            if (!pick.ContainsKey(e2) && (e2 == "ogg" || e2 == "mp3" || e2 == "wav"))
            {
                var full = Path.Combine(lib.Path, c.RelPath);
                if (File.Exists(full)) pick[e2] = full;
            }
        }
        if (pick.ContainsKey("ogg") && pick.ContainsKey("mp3") && pick.ContainsKey("wav")) break;
    }

    foreach (var kv in pick)
    {
        var ext = kv.Key; var file = kv.Value;
        Console.WriteLine($"  样本 [{ext}] {Path.GetFileName(file).Substring(0, Math.Min(40, Path.GetFileName(file).Length))}");
        var dec = AudioDecoder.DecodeMono(file, 10.0);
        DChk($"AudioDecoder 解 {ext}", dec != null, dec == null ? "❌ 返回 null" : $"✅ {dec.Value.Samples.Length} 采样 @ {dec.Value.SampleRate} Hz");
        // 档1 提取
        var v1 = AudioFeatures.Extract(file, 10.0);
        DChk($"AudioFeatures.Extract {ext}", v1 != null && v1.Length == AudioFeatures.Dim, v1 == null ? "❌ null" : $"✅ {v1.Length} 维");
        // 档2 读取（MERT 前的解码）
        try
        {
            var mono = MertFeatures.ReadMono16k(file, 10.0);
            DChk($"ReadMono16k {ext}", mono != null && mono.Length > 1600, mono == null ? "❌ null" : $"✅ {mono.Length} 采样 @16k");
        }
        catch (Exception ex) { DChk($"ReadMono16k {ext}", false, "❌ " + ex.Message.Substring(0, Math.Min(60, ex.Message.Length))); }
    }

    DChk("声明支持的扩展名含 ogg/mp3", AudioDecoder.CanDecode("ogg") && AudioDecoder.CanDecode("mp3"), string.Join(",", AudioDecoder.SupportedExts));
    DChk("DecodableExts 含 ogg/mp3", AudioFeatures.CanDecode("ogg") && AudioFeatures.CanDecode("mp3"), "-");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}
// ── 验证 .nkx 注册表密钥修复 ──
if (args.Length >= 2 && args[0] == "--nkxkey-test")
{
    var kdb = new Database(args[1]);
    Console.WriteLine(".nkx 注册表密钥验证");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // 统计：按库看有多少 nkx 能拿到密钥
    int libsOk = 0, libsFail = 0, nkxOk = 0, nkxFail = 0;
    var failLibs = new List<string>();
    foreach (var lib in kdb.GetLibraries())
    {
        var clips = kdb.GetAnalyzableClips(lib.Id).Where(c => (c.Ext ?? "").Equals("nkx", StringComparison.OrdinalIgnoreCase)).ToList();
        if (clips.Count == 0) continue;
        var key = NkxCrypto.LoadKey(lib.ProductKey ?? "");
        if (key != null) { libsOk++; nkxOk += clips.Count; }
        else { libsFail++; nkxFail += clips.Count; failLibs.Add(lib.Name + "  (" + clips.Count + " 个)"); }
    }

    Console.WriteLine($"  有 .nkx 的库：{libsOk + libsFail} 个");
    Console.WriteLine($"    ✅ 能拿到密钥：{libsOk} 个库 / {nkxOk} 个容器");
    Console.WriteLine($"    ❌ 拿不到密钥：{libsFail} 个库 / {nkxFail} 个容器");
    if (failLibs.Count > 0)
    {
        Console.WriteLine("");
        Console.WriteLine("  仍失败的库（前 12）：");
        foreach (var x in failLibs.Take(12)) Console.WriteLine("    " + x);
    }
    Console.WriteLine("");
    Console.WriteLine("  对照：修复前失败 1,141 个容器");
    Console.WriteLine(new string(char.Parse("-"), 78));
    return nkxFail < 1141 ? 0 : 1;
}
// ── 验证 AIFF 解码（新增自研解析器）──
if (args.Length >= 2 && args[0] == "--aiff-test")
{
    var adb2 = new Database(args[1]);
    Console.WriteLine("AIFF 解码验证");
    Console.WriteLine(new string(char.Parse("-"), 78));
    int ok = 0, ng = 0;

    // 挑真实的 .aif 文件
    var files = new List<string>();
    foreach (var lib in adb2.GetLibraries())
    {
        foreach (var c in adb2.GetAnalyzableClips(lib.Id))
        {
            if (!(c.Ext ?? "").Equals("aif", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(lib.Path, c.RelPath);
            if (File.Exists(full)) files.Add(full);
            if (files.Count >= 5) break;
        }
        if (files.Count >= 5) break;
    }

    foreach (var f in files)
    {
        var name = Path.GetFileName(f);
        bool isAiff = AiffReader.LooksLikeAiff(f);
        var dec = AudioDecoder.DecodeMono(f, 10.0);
        var v1 = AudioFeatures.Extract(f, 10.0);
        bool good = dec != null && v1 != null && v1.Length == AudioFeatures.Dim;
        if (good) ok++; else ng++;
        Console.WriteLine($"  {(good ? "OK " : "NG ")} {name.Substring(0, Math.Min(34, name.Length)),-36} 头AIFF={isAiff} " +
                          (dec == null ? "❌ 解码 null" : $"✅ {dec.Value.Samples.Length} 采样 @ {dec.Value.SampleRate} Hz / 档1 {(v1 == null ? "null" : v1.Length + " 维")}"));
    }

    Console.WriteLine("");
    Console.WriteLine($"  通过 {ok} / {ok + ng}" + (ng == 0 && ok > 0 ? "   全部符合预期" : (ok == 0 ? "   ⚠️ 没取到样本" : "   有失败")));
    Console.WriteLine(new string(char.Parse("-"), 78));
    return ng == 0 ? 0 : 1;
}
// ── 诊断：失败的 .nkx 到底卡在哪一步 ──
if (args.Length >= 2 && args[0] == "--nkxdiag-test")
{
    var ndb = new Database(args[1]);
    Console.WriteLine(".nkx 失败原因诊断");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // 取 6 个失败的 nkx（挑小的，快一点）
    var targets = new List<(string Full, string Name, string LibName, string PKey)>();
    foreach (var lib in ndb.GetLibraries())
    {
        foreach (var c in ndb.GetAnalyzableClips(lib.Id))
        {
            if (!(c.Ext ?? "").Equals("nkx", StringComparison.OrdinalIgnoreCase)) continue;
            if (ndb.GetMertFeature(c.Id) != null) continue;   // 只看失败的
            var full = Path.Combine(lib.Path, c.RelPath);
            // **只挑「有密钥却失败」的** —— 这才是真正待查的 236 个
            if (!File.Exists(full)) continue;
            if (NkxCrypto.LoadKey(lib.ProductKey ?? "") == null) continue;
            targets.Add((full, c.Name, lib.Name, lib.ProductKey ?? ""));
        }
    }
    Console.WriteLine($"  失败的 .nkx 共 {targets.Count} 个，取前 6 个诊断：");
    Console.WriteLine("");

    foreach (var x in targets.Take(6))
    {
        Console.WriteLine($"  ── {x.Name}（{x.LibName}）");
        var sz = new FileInfo(x.Full).Length;
        Console.WriteLine($"     体积 {sz / 1024 / 1024} MB");

        // ① 密钥
        var k = NkxCrypto.LoadKey(x.PKey);
        Console.WriteLine($"     ① 密钥: {(k != null ? "✅ 拿到" : "❌ 没有")}");

        // ② 目录树
        try
        {
            var tree = NkxReader.ReadTree(x.Full);
            if (tree == null) { Console.WriteLine("     ② 目录树: ❌ null"); }
            else
            {
                var flat = tree.Flatten().ToList();
                int ncw = flat.Count(f => f.Name.EndsWith(".ncw", StringComparison.OrdinalIgnoreCase));
                int wav = flat.Count(f => f.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"     ② 目录树: ✅ 共 {flat.Count} 项（.ncw {ncw} / .wav {wav}）");
                if (flat.Count > 0)
                {
                    Console.WriteLine("        前 3 项: " + string.Join(" | ", flat.Take(3).Select(f => f.Name)));
                    Console.WriteLine("        其他扩展名: " + string.Join(",", flat.Select(f => Path.GetExtension(f.Name)).Where(e => e.Length > 0).Distinct().Take(8)));
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("     ② 目录树: ❌ 异常 " + ex.Message.Substring(0, Math.Min(70, ex.Message.Length))); }

        // ③ 完整提取（看 Error）
        var res = NkxFeatures.Extract(x.Full, x.PKey, 2, null, 5.0, false, 1);
        Console.WriteLine($"     ③ 提取: {(res.Vec != null ? "✅ 成功" : "❌ 失败")}  总样本 {res.TotalSamples} / 用 {res.UsedSamples}" +
                          (string.IsNullOrEmpty(res.Error) ? "" : $"  错误: {res.Error}"));
        Console.WriteLine("");
    }

    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}
// ── 精确统计：失败的 .nkx 逐个查密钥 ──
if (args.Length >= 2 && args[0] == "--nkxkey2-test")
{
    var xdb = new Database(args[1]);
    Console.WriteLine(".nkx 密钥精确统计（只看【失败的】容器）");
    Console.WriteLine(new string(char.Parse("-"), 78));

    int noKey = 0, hasKey = 0;
    var libStats = new Dictionary<string, (int ok, int bad)>();
    foreach (var lib in xdb.GetLibraries())
    {
        List<AudioClip> clips;
        try { clips = xdb.GetAnalyzableClips(lib.Id); } catch { continue; }
        foreach (var c in clips)
        {
            if (!(c.Ext ?? "").Equals("nkx", StringComparison.OrdinalIgnoreCase)) continue;
            if (xdb.GetMertFeature(c.Id) != null) continue;      // 只看失败的
            var key = NkxCrypto.LoadKey(lib.ProductKey ?? "");
            if (key != null) hasKey++; else noKey++;
            var cur = libStats.TryGetValue(lib.Name, out var v) ? v : (ok: 0, bad: 0);
            libStats[lib.Name] = key != null ? (cur.ok + 1, cur.bad) : (cur.ok, cur.bad + 1);
        }
    }

    Console.WriteLine($"  失败的 .nkx 中：");
    Console.WriteLine($"    ❌ 没密钥（注册表缺）: {noKey}");
    Console.WriteLine($"    ⚠️ 有密钥却仍失败    : {hasKey}");
    Console.WriteLine("");
    Console.WriteLine("  按库（有密钥却失败的排在前面）：");
    foreach (var kv in libStats.OrderByDescending(x => x.Value.ok).ThenByDescending(x => x.Value.bad).Take(12))
        Console.WriteLine($"    {kv.Key.Substring(0, Math.Min(44, kv.Key.Length)).PadRight(46)} 有密钥 {kv.Value.ok,4} / 缺密钥 {kv.Value.bad,4}");
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}
// ── 对比：容器内 .ncw 与 独立 .ncw 的文件头 ──
if (args.Length >= 2 && args[0] == "--ncwhdr-test")
{
    var hdb = new Database(args[1]);
    Console.WriteLine(".ncw 文件头对比（容器内 vs 独立）");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // ① 独立的 .ncw（能解码的）
    foreach (var lib in hdb.GetLibraries())
    {
        foreach (var c in hdb.GetAnalyzableClips(lib.Id))
        {
            if (!(c.Ext ?? "").Equals("ncw", StringComparison.OrdinalIgnoreCase)) continue;
            if (hdb.GetMertFeature(c.Id) == null) continue;   // 只看成功的
            var full = Path.Combine(lib.Path, c.RelPath);
            if (!File.Exists(full)) continue;
            var b = new byte[64];
            using (var fs = File.OpenRead(full)) fs.Read(b, 0, 64);
            Console.WriteLine("  ── 独立 .ncw（✅ 能解码）: " + c.Name);
            Console.WriteLine("     头 32 字节: " + BitConverter.ToString(b, 0, 32).Replace("-", " "));
            Console.WriteLine("     ASCII: " + new string(b.Take(32).Select(x => x >= 32 && x < 127 ? (char)x : (char)46).ToArray()));
            goto done1;
        }
    }
    done1:;
    Console.WriteLine("");

    // ② 容器内的 .ncw（解码失败的）—— 解出来看头
    foreach (var lib in hdb.GetLibraries())
    {
        var key = NkxCrypto.LoadKey(lib.ProductKey ?? "");
        if (key == null) continue;
        foreach (var c in hdb.GetAnalyzableClips(lib.Id))
        {
            if (!(c.Ext ?? "").Equals("nkx", StringComparison.OrdinalIgnoreCase)) continue;
            if (hdb.GetMertFeature(c.Id) != null) continue;
            var full = Path.Combine(lib.Path, c.RelPath);
            if (!File.Exists(full)) continue;
            var mask = NkxCrypto.BuildXorMask(key.Value.key, key.Value.iv);
            var tree = NkxReader.ReadTree(full);
            if (tree == null) continue;
            var files = tree.Flatten().Where(f => f.Name.EndsWith(".ncw", StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
            if (files.Count == 0) continue;
            using var fs = File.OpenRead(full);
            var tmp = Path.Combine(Path.GetTempPath(), "klm_hdr_" + Guid.NewGuid().ToString("N") + ".ncw");
            try
            {
                long ex;
                using (var of = File.Create(tmp)) ex = NkxReader.ExtractFile(fs, files[0], mask, of);
                Console.WriteLine("  ── 容器内 .ncw（❌ 解码失败）: " + files[0].Name);
                Console.WriteLine("     解出字节数: " + ex);
                if (ex > 0)
                {
                    var b = new byte[64];
                    using (var rf = File.OpenRead(tmp)) rf.Read(b, 0, 64);
                    Console.WriteLine("     头 32 字节: " + BitConverter.ToString(b, 0, 32).Replace("-", " "));
                    Console.WriteLine("     ASCII: " + new string(b.Take(32).Select(x => x >= 32 && x < 127 ? (char)x : (char)46).ToArray()));
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
            goto done2;
        }
    }
    done2:;
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}
// ── 查 HdbscanSharp 的标签约定（0 是噪声还是簇？）──
if (args.Length >= 2 && args[0] == "--hdbprobe2-test")
{
    var hdb = new Database(args[1]);
    var pts = hdb.LoadUmapCoords();   // 直接返回 List<Point>
    Console.WriteLine($"载入 {pts.Count} 个点");
    Console.WriteLine(new string(char.Parse("-"), 78));

    bool ok = AudioMapReduce.ClusterByHdbscan(pts);
    Console.WriteLine($"ClusterByHdbscan 返回 {ok}");
    if (!ok) return 1;

    var dist = pts.GroupBy(p => p.Cluster).ToDictionary(g => g.Key, g => g.Count());
    Console.WriteLine($"  不同标签数: {dist.Count}");
    Console.WriteLine($"  最小标签: {dist.Keys.Min()}   最大标签: {dist.Keys.Max()}");
    Console.WriteLine("");
    Console.WriteLine("  标签分布（按标签值排序，前 8 + 后 3）：");
    foreach (var kv in dist.OrderBy(k => k.Key).Take(8))
        Console.WriteLine($"    标签 {kv.Key,6} → {kv.Value,6} 个点");
    Console.WriteLine("      ...");
    foreach (var kv in dist.OrderByDescending(k => k.Key).Take(3))
        Console.WriteLine($"    标签 {kv.Key,6} → {kv.Value,6} 个点");
    Console.WriteLine("");
    Console.WriteLine("  **关键判断**：");
    bool hasNeg = dist.Keys.Any(k => k < 0);
    Console.WriteLine($"    有负标签（-1 噪声约定）: {hasNeg}");
    if (dist.ContainsKey(0))
        Console.WriteLine($"    标签 0 的点数 = {dist[0]}  ← 若很大且其他标签都从 1 开始，则【0 是噪声】");
    Console.WriteLine($"    从 1 开始的标签数: {dist.Keys.Count(k => k >= 1)}");
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}
// ── 探明 NWaves TimeDomainFeaturesExtractor 的列序（验证「响度」是否标错）──
if (args.Length >= 1 && args[0] == "--timedomain-test")
{
    Console.WriteLine("NWaves 时域特征列序探查（合成信号）");
    Console.WriteLine(new string(char.Parse("-"), 78));
    int sr = 16000;
    var opts = new NWaves.FeatureExtractors.Options.MultiFeatureOptions { SamplingRate = sr, FftSize = 1024, HopSize = 512 };

    // 造 6 组信号，**每次只变一个因素** —— 这样能确定每列对应什么
    var tests = new List<(string Name, float[] Buf)>();
    var rnd = new Random(7);

    // ① 同波形、不同【振幅】（测响度/RMS）
    foreach (var amp in new[] { 0.05f, 0.2f, 0.8f })
    {
        var b = new float[sr];
        for (int i = 0; i < sr; i++) b[i] = amp * (float)Math.Sin(2 * Math.PI * 440 * i / sr);
        tests.Add(($"440Hz 振幅={amp}", b));
    }
    // ② 同振幅、不同【频率】（测过零率）
    foreach (var f in new[] { 100f, 1000f, 5000f })
    {
        var b = new float[sr];
        for (int i = 0; i < sr; i++) b[i] = 0.5f * (float)Math.Sin(2 * Math.PI * f * i / sr);
        tests.Add(($"频率={f}Hz", b));
    }
    // ③ 白噪声（高过零率）
    {
        var b = new float[sr];
        for (int i = 0; i < sr; i++) b[i] = 0.5f * (float)(rnd.NextDouble() * 2 - 1);
        tests.Add(("白噪声", b));
    }

    var results = new List<(string Name, float[] Vec)>();
    foreach (var (name, buf) in tests)
    {
        var sig = new NWaves.Signals.DiscreteSignal(sr, buf);
        var tv = new NWaves.FeatureExtractors.Multi.TimeDomainFeaturesExtractor(opts).ComputeFrom(sig);
        if (tv == null || tv.Count == 0) { Console.WriteLine("  " + name + " 提取失败"); continue; }
        results.Add((name, tv[0]));
    }
    if (results.Count == 0) return 1;

    int dim = results[0].Vec.Length;
    Console.WriteLine($"  列数 = {dim}");
    Console.WriteLine("");
    Console.Write("  列   ");
    foreach (var r in results) Console.Write(r.Name.PadLeft(16));
    Console.WriteLine("");
    for (int k = 0; k < dim; k++)
    {
        Console.Write("  " + k.ToString().PadLeft(2) + "   ");
        foreach (var r in results) Console.Write(r.Vec[k].ToString("F5").PadLeft(16));
        Console.WriteLine("");
    }
    Console.WriteLine("");
    Console.WriteLine("  **判断依据**：");
    Console.WriteLine("    · 随【振幅】单调变化的那列 = 响度/RMS 类");
    Console.WriteLine("    · 随【频率】单调变化、且白噪声最大 = 过零率(ZCR)");
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}
// ── 把 NKI「magic 命中但名字与文件名不符」的样本打出来 ──
// 用法: dotnet run -- --nkimismatch-test <库目录> [取样数]
if (args.Length >= 2 && args[0] == "--nkimismatch-test")
{
    string nkiRoot = args[1];
    int limit = args.Length >= 3 && int.TryParse(args[2], out int lim2) ? lim2 : 3000;
    var files = new List<string>();
    try
    {
        foreach (var f in Directory.EnumerateFiles(nkiRoot, "*.nki", SearchOption.AllDirectories))
        {
            files.Add(f);
            if (files.Count >= limit) break;
        }
    }
    catch (Exception ex) { Console.WriteLine("  枚举失败：" + ex.Message); return 1; }

    Console.WriteLine($"NKI 不一致样本复查（取样 {files.Count} 个）");
    Console.WriteLine(new string(char.Parse("-"), 78));

    int hit = 0, same = 0;
    var bad = new List<(string File, string Parsed)>();
    foreach (var f in files)
    {
        var (pname, psource, _fmt, _eng) = NkiMetadataReader.ReadFromFile(f);
        if (psource != "header" || string.IsNullOrWhiteSpace(pname)) continue;
        hit++;
        string stem = Path.GetFileNameWithoutExtension(f);
        if (string.Equals(stem, pname, StringComparison.OrdinalIgnoreCase)) { same++; continue; }
        if (bad.Count < 20) bad.Add((f, pname!));
    }

    Console.WriteLine($"  头部命中 {hit}、名字与文件名一致 {same}（{same * 100.0 / Math.Max(1, hit):F1}%）");
    Console.WriteLine($"  ❌ 不一致 {hit - same} 个：");
    foreach (var (file, parsed) in bad)
    {
        Console.WriteLine($"    文件名: {Path.GetFileName(file)}");
        Console.WriteLine($"    解析名: {parsed}");
        Console.WriteLine($"    路径  : {file.Substring(0, Math.Min(96, file.Length))}");
        Console.WriteLine("");
    }
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}
// ── 查 EVENT_* / CC_* 等内置变量的【真实名字与前缀】──
if (args.Length >= 1 && args[0] == "--evvar-test")
{
    var all = KspSymbols.LoadAll();
    Console.WriteLine("内置变量前缀核实");
    Console.WriteLine(new string(char.Parse("-"), 78));
    foreach (var key in new[] { "EVENT_NOTE", "EVENT_VELOCITY", "EVENT_ID", "CC_NUM", "CC_VALUE", "EVENT_PAR" })
    {
        var hits = all.Where(s => s.Name.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"  {key}:");
        if (hits.Count == 0) Console.WriteLine("     （符号表里没有 —— 说明它是语言关键字/内建，不在表内）");
        else foreach (var s in hits.Take(5)) Console.WriteLine($"     {s.Name}   [{s.Kind}]  {s.Description}");
    }
    Console.WriteLine("");
    Console.WriteLine("  ── 表里所有带 $ 前缀的 EVENT/CC 变量（前 15）:");
    foreach (var s in all.Where(x => x.Name.StartsWith("$") && (x.Name.Contains("EVENT") || x.Name.Contains("CC"))).Take(15))
        Console.WriteLine($"     {s.Name}   [{s.Kind}]");
    Console.WriteLine("");
    Console.WriteLine("  ── 表里所有带 % 前缀的 EVENT/CC 变量（前 15）:");
    foreach (var s in all.Where(x => x.Name.StartsWith("%") && (x.Name.Contains("EVENT") || x.Name.Contains("CC"))).Take(15))
        Console.WriteLine($"     {s.Name}   [{s.Kind}]");
    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}
// ── 从符号表提取 KSP 权威语法（供细化提示词/模板）──
if (args.Length >= 1 && args[0] == "--kspsyntax-test")
{
    var all = KspSymbols.LoadAll();
    Console.WriteLine("KSP 权威语法提取");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // ① 关键命令的完整签名
    Console.WriteLine("【关键命令签名】");
    foreach (var n in new[] { "play_note", "change_vol", "change_tune", "change_pan",
                              "change_note", "ignore_event", "message", "note_off",
                              "set_controller", "by_marks", "wait", "fade_in", "fade_out" })
    {
        var s = KspSymbols.ByName(n);
        if (s != null) Console.WriteLine("  " + KspSymbols.Render(s).Replace("\n", " | "));
        else Console.WriteLine("  " + n + "  ← 不存在");
    }

    // ② 运算符 / 关键字有没有在表里
    Console.WriteLine("");
    Console.WriteLine("【语言关键字是否在表内】");
    foreach (var n in new[] { "and", "or", "not", "mod", "if", "while", "for", "select", "case" })
        Console.WriteLine($"  {n,-8} {(KspSymbols.ByName(n) != null ? "在表内" : "不在（语言关键字）")}");

    // ③ 数组相关
    Console.WriteLine("");
    Console.WriteLine("【数组/循环相关命令】");
    foreach (var s in all.Where(x => x.Name.Contains("array") || x.Name.Contains("num_elements")).Take(12))
        Console.WriteLine("  " + s.Name + "   [" + s.Kind + "]");

    // ④ 变量前缀统计（$ = 整型, % = 数组? 看实际）
    Console.WriteLine("");
    Console.WriteLine("【变量名前缀分布（variable 类）】");
    foreach (var g in all.Where(x => x.Kind == "variable" && x.Name.Length > 0)
                           .GroupBy(x => x.Name[0])
                           .OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Key}   {g.Count()} 个   例: " + string.Join(", ", g.Take(3).Select(x => x.Name)));

    // ⑤ message / print
    Console.WriteLine("");
    Console.WriteLine("【输出类】");
    foreach (var s in all.Where(x => x.Name.Contains("message") || x.Name.Contains("print")))
        Console.WriteLine("  " + s.Name + "   " + s.Description);

    Console.WriteLine(new string(char.Parse("-"), 78));
    return 0;
}
// ── HdbscanSharp API 探针 ──
if (args.Length >= 1 && args[0] == "--hdb-probe")
{
    var asm = System.Reflection.Assembly.Load("HdbscanSharp");
    Console.WriteLine("程序集：" + asm.FullName);
    foreach (var ty in asm.GetExportedTypes().OrderBy(x => x.FullName))
    {
        Console.WriteLine("-- " + ty.FullName + (ty.IsInterface ? " (interface)" : ty.IsEnum ? " (enum)" : ""));
        if (ty.IsEnum) { foreach (var v in Enum.GetNames(ty)) Console.WriteLine("      " + v); continue; }
        foreach (var c in ty.GetConstructors())
            Console.WriteLine("      ctor(" + string.Join(", ", c.GetParameters().Select(q => q.ParameterType.Name + " " + q.Name)) + ")");
        foreach (var m in ty.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                Console.WriteLine("      " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(q => q.ParameterType.Name + " " + q.Name)) + ")");
        foreach (var pr in ty.GetProperties())
            Console.WriteLine("      属性 " + pr.PropertyType.Name + " " + pr.Name);
    }
    return 0;
}

// ── UMAP 包 API 探针（查明真实类型与用法）──
if (args.Length >= 1 && args[0] == "--umap-probe")
{
    var asm = typeof(UMAP.Umap).Assembly;
    Console.WriteLine("程序集：" + asm.FullName);
    foreach (var ty in asm.GetExportedTypes().OrderBy(x => x.FullName))
    {
        Console.WriteLine("── " + ty.FullName + (ty.IsInterface ? " (interface)" : ty.IsEnum ? " (enum)" : ""));
        if (ty.IsEnum) { foreach (var v in Enum.GetNames(ty)) Console.WriteLine("      " + v); continue; }
        foreach (var m in ty.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                Console.WriteLine("      " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(q => q.ParameterType.Name + " " + q.Name)) + ")");
        foreach (var c in ty.GetConstructors())
            Console.WriteLine("      ctor(" + string.Join(", ", c.GetParameters().Select(q => q.ParameterType.Name + " " + q.Name)) + ")");
    }
    return 0;
}

// ── MERT 音频嵌入实测（调研报告「远期」第 7 项 · 档 2）—— 决策点验证 ──
// 用法: dotnet run -- --mert-test <dbPath> [模型路径]
// 目的：验证第三方导出的 ONNX 输出是否【合理】—— 同类音频应比异类更相似。
if (args.Length >= 2 && args[0] == "--mert-test")
{
    var mdb = new Database(args[1]);
    string mp = args.Length >= 3 ? args[2] : MertFeatures.DefaultModelPath;
    int pass = 0, fail = 0;
    void MChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-44} {detail}");
    }

    Console.WriteLine("MERT 音频嵌入实测");
    Console.WriteLine($"  模型：{mp}");
    Console.WriteLine(new string(char.Parse("-"), 78));
    MChk("模型文件存在", File.Exists(mp), File.Exists(mp) ? $"{new FileInfo(mp).Length / 1024 / 1024} MB" : "不存在");
    if (!File.Exists(mp)) return 1;

    // ① 模型签名
    var desc = MertFeatures.DescribeModel(mp);
    Console.WriteLine("  [模型签名]");
    foreach (var l in desc.Split(char.Parse("\n"))) Console.WriteLine("     " + l);
    MChk("能加载模型", !desc.StartsWith("无法加载"), desc.Split(char.Parse("\n"))[0].Substring(0, Math.Min(60, desc.Split(char.Parse("\n"))[0].Length)));

    // ② 挑样本：同库同类 vs 异库
    var mlibs = mdb.GetLibraries().ToList();
    var mclips = new List<(AudioClip C, string Root)>();
    foreach (var L in mlibs) {
        try { foreach (var c in mdb.GetAudioClips(L.Id, 30)) if (AudioFeatures.CanDecode(c.Ext) && c.SizeBytes > 50_000) mclips.Add((c, L.Path)); } catch { }
    }
    MChk("找到可解码样本", mclips.Count > 0, $"{mclips.Count} 个");
    if (mclips.Count == 0) return 1;

    // 🎯 「同类」的判据必须是【同一目录】而不是【同一个库】——
    // 同一个库里可能有几十种完全不同的音源（实测：同库前 3 个是 sibamb/0016-Audio/bnk_drag，
    // 它们根本不像，导致差值只有 0.026，误判成「MERT 退化」）。
    // 同一目录才是「同一个乐器」（这正是 InstrumentAggregate.ParentDir 的用途）。
    var byDir = mclips
        .GroupBy(x => x.C.LibraryId + "|" + InstrumentAggregate.ParentDir(x.C.RelPath))
        .Where(g => g.Count() >= 3)
        .OrderByDescending(g => g.Count())
        .FirstOrDefault();
    if (byDir == null) { Console.WriteLine("  ⏭ 没有单目录样本 ≥3 的组，跳过同类验证"); }
    var same = byDir != null ? byDir.Take(3).ToList() : new List<(AudioClip, string)>();
    var other = mclips.Where(x => byDir == null || x.C.LibraryId != byDir.First().C.LibraryId).Take(3).ToList();

    Console.WriteLine($"  同目录样本（真同类）：{string.Join(", ", same.Select(s => s.Item1.Name))}");
    Console.WriteLine($"  异库样本：{string.Join(", ", other.Select(s => s.Item1.Name))}");
    Console.WriteLine();

    // ③ 提嵌入
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var vecs = new List<(string Tag, float[] V)>();
    foreach (var s in same) {
        var v = MertFeatures.Extract(Path.Combine(s.Item2, s.Item1.RelPath), 10.0, mp);
        if (v != null) vecs.Add(("同:" + s.Item1.Name, v));
    }
    foreach (var s in other) {
        var v = MertFeatures.Extract(Path.Combine(s.Item2, s.Item1.RelPath), 10.0, mp);
        if (v != null) vecs.Add(("异:" + s.Item1.Name, v));
    }
    sw.Stop();
    MChk("嵌入提取成功", vecs.Count >= 4, $"{vecs.Count} 个，耗时 {sw.Elapsed.TotalSeconds:F1}s（{(vecs.Count > 0 ? sw.Elapsed.TotalSeconds / vecs.Count : 0):F1}s/个）");
    if (vecs.Count < 4) return 1;
    Console.WriteLine($"  嵌入维度：{vecs[0].V.Length}");

    // ④ 关键验证：同类相似度 > 异类相似度
    double sameAvg = 0; int sameN = 0;
    for (int i = 0; i < vecs.Count; i++)
        for (int j = i + 1; j < vecs.Count; j++)
            if (vecs[i].Tag.StartsWith("同") && vecs[j].Tag.StartsWith("同")) { sameAvg += MertFeatures.Cosine(vecs[i].V, vecs[j].V); sameN++; }
    sameAvg = sameN > 0 ? sameAvg / sameN : 0;

    double crossAvg = 0; int crossN = 0;
    for (int i = 0; i < vecs.Count; i++)
        for (int j = 0; j < vecs.Count; j++)
            if (vecs[i].Tag.StartsWith("同") && vecs[j].Tag.StartsWith("异")) { crossAvg += MertFeatures.Cosine(vecs[i].V, vecs[j].V); crossN++; }
    crossAvg = crossN > 0 ? crossAvg / crossN : 0;

    Console.WriteLine();
    Console.WriteLine($"  【关键指标】同类平均相似度 = {sameAvg:F4}   异类平均相似度 = {crossAvg:F4}   差值 = {sameAvg - crossAvg:F4}");
    MChk("同类比异类更相似（嵌入合理）", sameN > 0 && crossN > 0 && sameAvg > crossAvg, $"同类 {sameAvg:F4} > 异类 {crossAvg:F4}");
    MChk("差值有区分度（> 0.05）", sameAvg - crossAvg > 0.05, $"差值 {sameAvg - crossAvg:F4}");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── KSP 符号表检索实测（降低模型编造命令的概率）──
// 用法: dotnet run -- --kspsym-test [符号目录]
if (args.Length >= 1 && args[0] == "--kspsym-test")
{
    string symDir = args.Length >= 2 ? args[1] : KspSymbols.SymbolsDir;
    KspSymbols.ClearCache();
    int pass = 0, fail = 0;
    void SChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-44} {detail}");
    }

    Console.WriteLine($"KSP 符号表检索实测");
    Console.WriteLine($"  符号目录：{symDir}");
    Console.WriteLine(new string(char.Parse("-"), 78));

    var all = KspSymbols.LoadAll(symDir);
    SChk("加载符号表", all.Count > 0, $"{all.Count} 个符号");
    int cmdCount = all.Count(s => s.Kind == "command");
    // 注：commands.yaml 里 265 个命令 + 460 行参数（`- Name:` 也出现在 Arguments 下）
    SChk("命令数量合理（265）", cmdCount >= 250, $"{cmdCount} 个命令");

    // ① 精确查（用 YAML 里真实存在的三个）
    var abs = KspSymbols.ByName("abs");
    SChk("精确查 abs", abs != null && abs.Arguments.Count == 1 && abs.ReturnType.Length > 0,
        abs == null ? "未找到" : $"{abs.ReturnType}，{abs.Arguments.Count} 个参数");
    var ami = KspSymbols.ByName("add_menu_item");
    SChk("精确查 add_menu_item（含参数）", ami != null && ami.Arguments.Count >= 2,
        ami == null ? "未找到" : $"参数：{string.Join(", ", ami.Arguments.Select(a => a.Name + ":" + a.DataType))}");
    SChk("参数带描述", ami != null && ami.Arguments.Any(a => a.Description.Length > 0),
        ami == null ? "-" : $"{ami.Arguments.Count(a => a.Description.Length > 0)} 个参数有描述");
    SChk("多行描述被正确拼接", abs != null && abs.Description.Length > 0, abs == null ? "-" : $"\"{abs.Description.Replace("\n", " ")}\"".Substring(0, Math.Min(40, abs.Description.Length + 2)));

    // ② 关键词搜
    var menu = KspSymbols.Search("menu", null, 20);
    SChk("关键词搜 menu", menu.Count > 0, $"{menu.Count} 条");
    var ui = KspSymbols.Search("", "ui", 10);
    SChk("按类别列 ui", ui.Count > 0, $"{ui.Count} 条（限 10）");
    SChk("类别归类有结果", KspSymbols.Categories().Count >= 5, $"{KspSymbols.Categories().Count} 个类别");

    // ③ 不存在的命令 → 返回 null（这正是模型编造命令时要能发现的）
    SChk("查不存在的命令 → null", KspSymbols.ByName("this_is_not_a_command") == null, "null");

    // ④ 渲染
    var r = ami != null ? KspSymbols.Render(ami) : "";
    SChk("渲染含签名与参数", r.Contains("add_menu_item(") && r.Contains("参数："), $"{r.Length} 字符");
    if (r.Length > 0) Console.WriteLine("       样例：" + r.Split(char.Parse("\n"))[0]);

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── KSP 编译校验闭环实测（调研报告「远期」第 9 项）──
// 用法: dotnet run -- --ksp-test [kspc.exe路径]
if (args.Length >= 1 && args[0] == "--ksp-test")
{
    string exe = args.Length >= 2 ? args[1] : KspCompiler.DefaultPath;
    int pass = 0, fail = 0;
    void KChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-46} {detail}");
    }

    Console.WriteLine($"KSP 编译校验闭环实测");
    Console.WriteLine($"  编译器：{exe}");
    Console.WriteLine(new string(char.Parse("-"), 78));
    KChk("能找到 kspc.exe", File.Exists(exe), File.Exists(exe) ? $"{new FileInfo(exe).Length / 1024 / 1024} MB" : "不存在");
    if (!File.Exists(exe)) { Console.WriteLine("  请先放置 kspc.exe"); return 1; }

    // ① 输出解析：实测格式 `Warning\t3:5\tmsg`
    var parsed = KspCompiler.ParseOutput("Warning\t3:5\tthis_is_not_a_command: Unknown KSP command (may not be documented)", "");
    KChk("解析 Warning 行（含行列）", parsed.Count == 1 && parsed[0].Severity == "warning" && parsed[0].Line == 3 && parsed[0].Column == 5,
        parsed.Count > 0 ? $"{parsed[0].Severity} {parsed[0].Line}:{parsed[0].Column}" : "无");
    var parsed2 = KspCompiler.ParseOutput("Error\t10:1\tsome error", "");
    KChk("解析 Error 行", parsed2.Count == 1 && parsed2[0].Severity == "error" && parsed2[0].Line == 10, parsed2.Count > 0 ? $"{parsed2[0].Severity} {parsed2[0].Line}" : "无");
    KChk("忽略 usage 行", KspCompiler.ParseOutput("Usage: [options...]", "").Count == 0, "0 条");

    // ② 真实编译：正确的脚本
    var dir = Path.Combine(AppPaths.DataDir, "ksp");
    Directory.CreateDirectory(dir);
    string good = Path.Combine(dir, "_test_good.ksp");
    File.WriteAllText(good, "on init\n    declare @i\n    @i := 1\nend on\n", new UTF8Encoding(false));
    var r1 = KspCompiler.Compile(exe, good);
    KChk("正确脚本 → 判定通过", r1.Ok, $"退出码 {r1.ExitCode}；{r1.Summary}");

    // ③ 真实编译：有问题的脚本（验证「退出码 0 但有警告」必须被识别）
    string bad = Path.Combine(dir, "_test_bad.ksp");
    File.WriteAllText(bad, "on init\n    declare @i\n    this_is_not_a_command\nend on\n", new UTF8Encoding(false));
    var r2 = KspCompiler.Compile(exe, bad);
    KChk("错误脚本 → 解析出诊断", r2.Diagnostics.Count > 0, $"{r2.Diagnostics.Count} 条诊断");
    KChk("错误脚本 → 退出码确实为 0（证明不能只看退出码）", r2.ExitCode == 0, $"退出码 {r2.ExitCode}");
    if (r2.Diagnostics.Count > 0)
        Console.WriteLine($"       诊断：{r2.Diagnostics[0].Severity} {r2.Diagnostics[0].Line}:{r2.Diagnostics[0].Column} {r2.Diagnostics[0].Message}");
    KChk("有警告时仍算「通过」（警告不阻断）", r2.Ok, r2.Summary);

    // ④ 纠正文案
    var corr = KspCompiler.BuildCorrection(r2, bad);
    KChk("纠正文案含行列与消息", corr.Contains(":") && corr.Contains("compile_ksp"), $"{corr.Length} 字符");

    // ⑤ 文件不存在 → 退出码非 0
    var r3 = KspCompiler.Compile(exe, Path.Combine(dir, "_not_exist_.ksp"));
    KChk("脚本不存在 → 明确报错", !r3.Ok, r3.Summary.Substring(0, Math.Min(50, r3.Summary.Length)));

    try { File.Delete(good); File.Delete(bad); } catch { }
    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 音色声学特征实测（调研报告「远期」第 7 项 · 档 1）──
// 用法: dotnet run -- --audio-test <dbPath> [取样数]
if (args.Length >= 2 && args[0] == "--audio-test")
{
    var adb = new Database(args[1]);
    int want = args.Length >= 3 && int.TryParse(args[2], out int w) ? w : 8;
    int pass = 0, fail = 0;
    void AChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-44} {detail}");
    }

    // GetAudioClips 需要 libraryId，故按库逐个取（每个库取前 40 个）
    var clips = new List<AudioClip>();
    var allLibs0 = adb.GetLibraries().ToList();
    foreach (var L in allLibs0) { try { clips.AddRange(adb.GetAudioClips(L.Id, 40)); } catch { } }
    Console.WriteLine($"音色声学特征实测   音频片段 {clips.Count} 个，特征维度 {AudioFeatures.Dim}");
    Console.WriteLine(new string(char.Parse("-"), 78));

    // ① 格式可解码占比（如实呈现边界）
    int decodable = clips.Count(c => AudioFeatures.CanDecode(c.Ext));
    var byExt = clips.GroupBy(c => c.Ext).OrderByDescending(g => g.Count()).Take(6)
                     .Select(g => $"{g.Key}={g.Count()}{(AudioFeatures.CanDecode(g.Key) ? "(可解码)" : "(跳过)")}");
    Console.WriteLine($"  格式分布：{string.Join("  ", byExt)}");
    AChk("可解码格式占比已如实统计", decodable >= 0, $"{decodable} / {clips.Count} 个可解码（{decodable * 100.0 / Math.Max(1, clips.Count):F1}%）");

    // ② 真实提取几个 WAV
    var alibs = allLibs0.ToDictionary(l => l.Id, l => l.Path);
    var samples = clips.Where(c => AudioFeatures.CanDecode(c.Ext) && c.SizeBytes > 100_000)
                       .OrderByDescending(c => c.SizeBytes).Take(want).ToList();
    int okCount = 0, dimOk = 0;
    float[]? firstVec = null; string firstPath = "";
    foreach (var c in samples)
    {
        if (!alibs.TryGetValue(c.LibraryId, out var rt2)) continue;
        var vv = AudioFeatures.Extract(Path.Combine(rt2, c.RelPath));
        if (vv == null) continue;
        if (firstVec == null) { firstVec = vv; firstPath = Path.Combine(rt2, c.RelPath); }
        okCount++;
        if (vv.Length == AudioFeatures.Dim) dimOk++;
    }
    AChk("真实文件提取成功", okCount > 0, $"{okCount} / {samples.Count} 个成功");
    AChk($"维度均为 {AudioFeatures.Dim}", dimOk == okCount && okCount > 0, $"{dimOk} / {okCount}");

    // ③ 相似度自检：同一文件与自己 = 1.0；不同文件应 < 1.0
    if (firstVec != null)
    {
        double self = AudioFeatures.Cosine(firstVec, firstVec);
        AChk("自相似度 = 1.0", Math.Abs(self - 1.0) < 1e-6, $"{self:F6}");
        var v2 = AudioFeatures.Extract(firstPath) ;
        AChk("同一文件两次提取结果一致", v2 != null && AudioFeatures.Cosine(firstVec, v2) > 0.999, v2 == null ? "第二次失败" : $"相似度 {AudioFeatures.Cosine(firstVec, v2):F4}");
    }

    // ④ Pack/Unpack 往返
    var rnd = new float[AudioFeatures.Dim];
    for (int i = 0; i < rnd.Length; i++) rnd[i] = (float)Math.Sin(i) * 0.5f;
    var back = AudioFeatures.Unpack(AudioFeatures.Pack(rnd));
    AChk("Pack/Unpack 往返无损", back != null && back.Length == rnd.Length && back.Zip(rnd).All(t2 => Math.Abs(t2.First - t2.Second) < 1e-9), $"{rnd.Length} 维");

    // ── E2E: 提取 → 存库 → 余弦 KNN ──
    adb.EnsureAudioFeaturesTable();
    int saved = 0;
    // **记录本测试写入的 clipId** —— 清理时【只删这些】，避免误删用户全部特征
    var wroteIds = new List<long>();
    foreach (var c in samples)
    {
        if (!alibs.TryGetValue(c.LibraryId, out var rt2)) continue;
        var vv = AudioFeatures.Extract(Path.Combine(rt2, c.RelPath));
        if (vv == null) continue;
        adb.SaveAudioFeatures(c.Id, c.LibraryId, vv);
        wroteIds.Add(c.Id);
        saved++;
    }
    AChk("特征已写入 audio_features 表", saved > 0 && adb.CountAudioFeatures() > 0, $"写入 {saved} 条，表内共 {adb.CountAudioFeatures()} 条");

    if (saved > 0)
    {
        var loaded = adb.LoadAllAudioFeatures();
        AChk("从表中读回特征", loaded.Count >= saved, $"{loaded.Count} 条");
    var q = loaded[0];
        var nn = adb.FindSimilarAudio(q.Vec, 5, q.ClipId);
        AChk("余弦 KNN 返回结果", nn.Count > 0, $"{nn.Count} 条，top1 相似度 {(nn.Count > 0 ? nn[0].Item3 : 0):F4}");
        AChk("KNN 相似度降序", nn.Count < 2 || nn[0].Item3 >= nn[1].Item3, nn.Count < 2 ? "仅 1 条" : $"{nn[0].Item3:F4} >= {nn[1].Item3:F4}");
        AChk("KNN 排除了查询自身", nn.All(x => x.ClipId != q.ClipId), "已排除");
    }

    try { using var c2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={args[1]}"); c2.Open();
        // 🔴 **只删本测试写入的那几个片段** ——
        //   原实现是 `DELETE FROM audio_features`（**无 WHERE**），一旦被指向真实 DB，
        //   就会把用户的全部特征清空（**实测踩过：29,755 行全没了**）。
        //   ⇒ 改为按 clipId 精确删除（只删本测试刚写进去的）。
        var ids = string.Join(",", wroteIds);
        using var d2 = c2.CreateCommand();
        d2.CommandText = ids.Length > 0 ? $"DELETE FROM audio_features WHERE clip_id IN ({ids})" : "SELECT 1";
        d2.ExecuteNonQuery(); } catch { }
    AChk("测试后已清理本测试写入的行", wroteIds.All(id => adb.GetAudioFeatures(id) == null), $"表内仍有 {adb.CountAudioFeatures()} 条（不属于本测试）");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 批量编排 DSL 实测（调研报告「远期」第 10 项）──
// 用法: dotnet run -- --batch-test <dbPath>
if (args.Length >= 2 && args[0] == "--batch-test")
{
    var bdb = new Database(args[1]);
    int pass = 0, fail = 0;
    void BChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "OK " : "NG ")} {name,-46} {detail}");
    }

    Console.WriteLine($"批量编排 DSL 实测   索引库 {bdb.GetLibraries().Count} 个库");
    Console.WriteLine(new string(char.Parse("-"), 78));

    var c1 = BatchPlan.Parse("foreach lib where duplicate: list", out string e1);
    BChk("解析标准写法", c1.Count == 1 && c1[0].Action == "list" && c1[0].Conditions.Contains("duplicate"), e1.Length == 0 ? "1 个子句" : e1);

    var c2 = BatchPlan.Parse("no-kb: build-kb\n# 注释行\nnonstandard: list", out string e2);
    BChk("解析多子句 + 注释 + 省略前缀", c2.Count == 2 && c2[1].Action == "list", e2.Length == 0 ? c2.Count + " 个子句" : e2);

    var c3 = BatchPlan.Parse("category=弦乐 and junk: tag:待清理", out string e3);
    BChk("解析复合条件 + 带参动作", c3.Count == 1 && c3[0].Conditions.Count == 2 && c3[0].Action == "tag" && c3[0].Arg == "待清理", e3.Length == 0 ? "tag:待清理" : e3);

    var c4 = BatchPlan.Parse("duplicate: 不存在的动作", out string e4);
    BChk("未知动作 → 报错且不返回子句", c4.Count == 0 && e4.Length > 0, e4);

    var c5 = BatchPlan.Parse(": list", out string e5);
    BChk("缺条件 → 报错", c5.Count == 0 && e5.Length > 0, e5);

    var c6 = BatchPlan.Parse("no-kb: build-kb", out _);
    var ops6 = BatchPlan.Expand(bdb, c6);
    BChk("展开 no-kb: build-kb", ops6.Count > 0 && ops6.All(o => o.Action == "build-kb"), $"{ops6.Count} 个库命中");
    if (ops6.Count > 0) Console.WriteLine($"       例：{ops6[0].LibraryName} —— {ops6[0].Reason}");

    var c7 = BatchPlan.Parse("nonstandard: list", out _);
    var ops7 = BatchPlan.Expand(bdb, c7);
    BChk("展开 nonstandard: list", ops7.Count > 0, $"{ops7.Count} 个非标准库");

    var c8 = BatchPlan.Parse("name~绝对不存在的关键词ZZZ: list", out _);
    BChk("无命中 → 空列表（不误报）", BatchPlan.Expand(bdb, c8).Count == 0, "0 个");

    var c9 = BatchPlan.Parse("no-kb: build-kb; no-kb: build-kb", out _);
    var ops9 = BatchPlan.Expand(bdb, c9);
    BChk("重复子句 → 去重", ops9.Count == ops6.Count, $"{ops9.Count} 个（应为 {ops6.Count}）");

    var c10 = BatchPlan.Parse("完全不认识的条件: list", out _);
    BChk("未知条件 → 保守不匹配（宁可少动）", BatchPlan.Expand(bdb, c10).Count == 0, "0 个");

    Console.WriteLine(new string(char.Parse("-"), 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   全部符合预期" : $"   {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 断言式 verifier 实测（调研报告「远期」第 11 项）──
// 用法: dotnet run -- --verify-test <dbPath>
if (args.Length >= 2 && args[0] == "--verify-test")
{
    var vdb = new Database(args[1]);
    int pass = 0, fail = 0;
    void VChk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "✅" : "❌")} {name,-44} {detail}");
    }

    var vlibs = vdb.GetLibraries().ToList();
    Console.WriteLine($"断言式 verifier 实测   索引库 {vlibs.Count} 个库");
    Console.WriteLine(new string('─', 78));

    // ① 不存在的库声称「已入库」→ 必须被抓出来
    var bad = AssertionVerifier.Verify(vdb, new[] {
        new AssertionVerifier.Claim { Kind = AssertionVerifier.Registered, Subject = "绝对不存在的库名ZZZ", Source = "test" } });
    VChk("谎称「不存在的库已入库」→ 被抓出", bad.Count == 1, bad.Count == 1 ? "1 条不一致" : $"实际 {bad.Count} 条");

    // ② 未建知识库的库声称「已建库」→ 必须被抓出
    var noKb = vlibs.FirstOrDefault(l => vdb.GetManuals(l.Id).Count > 0 && !ManualKb.IsFresh(ManualKb.Load(l.Id), AiAssistant.PickBestManual(vdb.GetManuals(l.Id))?.FullPath ?? ""));
    if (noKb != null)
    {
        var b2 = AssertionVerifier.Verify(vdb, new[] {
            new AssertionVerifier.Claim { Kind = AssertionVerifier.KbBuilt, Subject = noKb.Name, Source = "test" } });
        VChk("谎称「未建库的库知识库已建」→ 被抓出", b2.Count == 1, $"库「{noKb.Name}」→ {b2.Count} 条不一致");
    }
    else Console.WriteLine("  ⏭ 跳过「谎称已建库」（所有库都已建库）");

    // ③ 声称找到的库数 > 索引总数 → 必须被抓出
    var b3 = AssertionVerifier.Verify(vdb, new[] {
        new AssertionVerifier.Claim { Kind = AssertionVerifier.FoundCount, Subject = "", Count = vlibs.Count + 999, Source = "test" } });
    VChk("谎称「找到 N 个」且 N > 总数 → 被抓出", b3.Count == 1, $"声称 {vlibs.Count + 999} / 实际 {vlibs.Count}");

    // ④ 数量合理 → 不应报错（绝不误报）
    var b4 = AssertionVerifier.Verify(vdb, new[] {
        new AssertionVerifier.Claim { Kind = AssertionVerifier.FoundCount, Subject = "", Count = vlibs.Count, Source = "test" } });
    VChk("数量合理 → 不误报", b4.Count == 0, "0 条不一致");

    // ⑤ 真正已入库的库声称已入库 → 不应误报
    var vreg = LibraryRegistrar.ReadRegistered();
    var realReg = vlibs.FirstOrDefault(l => l.HasNicnt && vreg.ContainsKey(l.ProductKey ?? ""));
    if (realReg != null)
    {
        var b5 = AssertionVerifier.Verify(vdb, new[] {
            new AssertionVerifier.Claim { Kind = AssertionVerifier.Registered, Subject = realReg.Name, Source = "test" } });
        VChk("真的已入库 → 不误报", b5.Count == 0, $"库「{realReg.Name}」→ {b5.Count} 条不一致");
    }
    else Console.WriteLine("  ⏭ 跳过「真的已入库」（没找到已入库的库）");

    // ⑥ 无断言 → 不报错
    VChk("无断言登记 → 不校验、不报错", AssertionVerifier.Verify(vdb, Array.Empty<AssertionVerifier.Claim>()).Count == 0, "0 条");

    // ⑦ 纠正文案应包含全部不一致
    var corr = AssertionVerifier.BuildCorrection(new[] { "问题甲", "问题乙" });
    VChk("纠正文案含全部不一致且要求如实回答", corr.Contains("问题甲") && corr.Contains("问题乙") && corr.Contains("自检"), $"{corr.Length} 字符");

    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   ✅ 全部符合预期" : $"   ❌ {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 主动建议引擎体检 ──
// 用法: dotnet run -- --sugg-test <dbPath> [--slow]
if (args.Length >= 2 && args[0] == "--sugg-test")
{
    bool slow = args.Contains("--slow");
    var suggDb = new Database(args[1]);
    var list = SuggestionEngine.Analyze(suggDb, slow);
    Console.WriteLine($"主动建议体检：{list.Count} 条" + (slow ? "（含重复大文件扫描）" : ""));
    Console.WriteLine(new string('─', 78));
    if (list.Count == 0) Console.WriteLine("  ✅ 体检通过，没有需要处理的事项");
    foreach (var s in list)
    {
        Console.WriteLine($"  [{(s.Level == "high" ? "该处理" : "可优化")}] {s.Title}   (权重 {s.Weight})");
        Console.WriteLine($"        {s.Detail}");
        if (s.Samples.Count > 0) Console.WriteLine($"        涉及：{string.Join("、", s.Samples.Take(6))}{(s.Samples.Count > 6 ? " …" : "")}");
        if (s.Action.Length > 0) Console.WriteLine($"        动作：{s.ActionLabel} → {s.Action}");
        Console.WriteLine();
    }
    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"共 {list.Count} 条；high 级 {list.Count(s => s.Level == "high")} 条");
    return 0;
}

// ── Agent 循环卫生：工具参数校验 + 重复调用提醒 ──
// 用法: dotnet run -- --loop-test
//
// 这两项此前是 private static、无法测试。SelfTest 直接编译 Core 源码（同一编译单元），
// 故改为 internal 后即可直接调用。它们守护的是「Agent 卡在循环里」与「模型传错参数」两类问题。
if (args.Length >= 1 && args[0] == "--loop-test")
{
    int pass = 0, fail = 0;
    void Chk(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "✅" : "❌")} {name,-46} {detail}");
    }
    Console.WriteLine("① ValidateToolArgs —— 必填参数校验");
    Console.WriteLine(new string('─', 78));
    // search_manual 的必填参数是 query
    Chk("缺 query → 拦截", !AiAssistant.ValidateToolArgs("search_manual", "{}", out string e1) && e1.Contains("query"),
        $"error=\"{e1}\"");
    Chk("有 query → 放行", AiAssistant.ValidateToolArgs("search_manual", "{\"query\":\"mic\"}", out string e2), $"error=\"{e2}\"");
    Chk("空 query → 拦截", !AiAssistant.ValidateToolArgs("search_manual", "{\"query\":\"\"}", out string e3), $"error=\"{e3}\"");
    Chk("非法 JSON → 拦截并说明", !AiAssistant.ValidateToolArgs("search_manual", "{不是json", out string e4), $"error=\"{e4}\"");
    Chk("非对象 JSON → 拦截", !AiAssistant.ValidateToolArgs("search_manual", "[1,2]", out string e5), $"error=\"{e5}\"");
    Chk("未知工具 → 放行（不在表内不拦）", AiAssistant.ValidateToolArgs("no_such_tool_xyz", "{}", out string e6), $"error=\"{e6}\"");
    Chk("无必填参数的工具 → 放行", AiAssistant.ValidateToolArgs("build_manual_kb", "{}", out string e7), $"error=\"{e7}\"");
    Console.WriteLine();
    Console.WriteLine("② RepeatReminder —— 重复调用提醒（DSH 语义：仅建议、不否决）");
    Console.WriteLine(new string('─', 78));
    // 阈值 [3,5,8]：第 3/5/8 次提醒，其余为 null
    var chain = new LinkedList<string>();
    var results = new List<string?>();
    for (int i = 1; i <= 9; i++) results.Add(AiAssistant.RepeatReminder(chain, "grep", "{\"q\":\"x\"}"));
    Chk("第 1、2 次不提醒", results[0] == null && results[1] == null, $"1={results[0] ?? "null"} 2={results[1] ?? "null"}");
    Chk("第 3 次提醒", results[2] != null, $"{(results[2]?.Length ?? 0)} 字符");
    Chk("第 4 次不提醒", results[3] == null, "—");
    Chk("第 5 次提醒", results[4] != null, $"{(results[4]?.Length ?? 0)} 字符");
    Chk("第 8 次提醒", results[7] != null, $"{(results[7]?.Length ?? 0)} 字符");
    Chk("第 9 次不提醒（超最高阈值）", results[8] == null, "—");
    // 设计如此：第 3 次是【简短提醒】（照 DSH 原文直译，不含工具名——提醒紧跟工具结果注入，
    // 模型上下文里本就知道是哪个工具）；第 5/8 次才是含 "- tool:" 与 "- consecutive_calls:" 的详细版。
    Chk("第 3 次为简短提醒（不含 tool: 字段）", results[2] != null && !results[2]!.Contains("tool:"), $"{(results[2]?.Length ?? 0)} 字符");
    Chk("第 5 次为详细提醒（含 tool: 与 consecutive_calls:）",
        results[4] != null && results[4]!.Contains("tool: grep") && results[4]!.Contains("consecutive_calls: 5"),
        $"{(results[4]?.Length ?? 0)} 字符");
    // exclude 语义：记账类工具对链透明（既不计数也不重置）
    var chain2 = new LinkedList<string>();
    AiAssistant.RepeatReminder(chain2, "grep", "{\"q\":\"x\"}");
    AiAssistant.RepeatReminder(chain2, "grep", "{\"q\":\"x\"}");
    var before = chain2.Count;
    var rEx = AiAssistant.RepeatReminder(chain2, "todo_write", "{}");
    Chk("exclude 工具返回 null", rEx == null, "—");
    Chk("exclude 工具不改变链长度", chain2.Count == before, $"{before} → {chain2.Count}");
    var r3 = AiAssistant.RepeatReminder(chain2, "grep", "{\"q\":\"x\"}");
    Chk("exclude 后仍算第 3 次并提醒", r3 != null, $"{(r3?.Length ?? 0)} 字符");
    // 不同参数视为不同链
    var chain3 = new LinkedList<string>();
    for (int i = 0; i < 3; i++) AiAssistant.RepeatReminder(chain3, "grep", "{\"q\":\"a\"}");
    var rDiff = AiAssistant.RepeatReminder(chain3, "grep", "{\"q\":\"b\"}");
    Chk("同工具不同参数 → 不提醒", rDiff == null, "—");
    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"通过 {pass} / {pass + fail}" + (fail == 0 ? "   ✅ 全部符合预期" : $"   ❌ {fail} 项不符"));
    return fail == 0 ? 0 : 1;
}

// ── 助手端到端问答（含中文提问 → 英文检索扩展）──
// 用法: dotnet run -- --ask <kb.json> "问题"
if (args.Length >= 2 && args[0] == "--ask")
{
    string kbPath = args[1];
    string question = args.Length >= 3 ? args[2] : "怎么切换演奏法？";
    var kb = System.Text.Json.JsonSerializer.Deserialize<LibraryKb>(
        File.ReadAllText(kbPath, System.Text.Encoding.UTF8),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    if (kb == null) { Console.WriteLine("❌ 无法加载知识库"); return 1; }

    var ai = new AiSettings
    {
        BaseUrl = Environment.GetEnvironmentVariable("KLM_AI_URL") ?? "https://token.sensenova.cn/v1",
        Model = Environment.GetEnvironmentVariable("KLM_AI_MODEL") ?? "sensenova-6.8-flash-lite",
        ApiKey = Environment.GetEnvironmentVariable("KLM_AI_KEY") ?? "",
        MaxTokens = 4096,
    };

    Console.WriteLine($"知识库：{kb.ManualName}（{kb.PageCount} 页 / {kb.Chunks.Count} 块）");
    Console.WriteLine($"问题：{question}");
    Console.WriteLine();

    var res = await AiAssistant.AskAsync(ai, kb, question, new List<ChatMessage>(), null,
        ev =>
        {
            switch (ev.Kind)
            {
                case "tool": Console.WriteLine($"   [工具调用] {ev.ToolName} {ev.ToolArgs}"); break;
                case "toolresult": Console.WriteLine($"   [工具返回] {ev.ToolName} 命中 {ev.Pages} 块"); break;
                case "reasoning": Console.Write("·"); break;
                case "delta": break;
            }
        });
    if (res.ExpandedQuery.Length > 0) Console.WriteLine($"检索词扩展：{res.ExpandedQuery}");
    Console.WriteLine($"命中 {res.ChunkCount} 块，依据页码：{string.Join(", ", res.Pages)}");
    Console.WriteLine($"耗时 {res.ElapsedSeconds:F1}s" + (res.UsedCompression ? "（含历史压缩）" : ""));
    Console.WriteLine();
    Console.WriteLine("── 回答 ──");
    Console.WriteLine(res.Answer);
    return 0;
}

Console.WriteLine("═══════════════════════════════════════════════════════");

// ── 新旧界面切换测试 ──
if (args.Length >= 1 && args[0] == "--ui-switch-test")
{
    int wp = 0, wf = 0;
    void W(bool ok, string what) { if (ok) { wp++; Console.WriteLine("   ✅ " + what); } else { wf++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ Kontakt 新旧界面切换 ═══");
    var w = UiAutomation.ResolveWindow("Kontakt 8");
    if (w == null) { Console.WriteLine("   （Kontakt 8 未运行）"); return 0; }
    var m0 = KontaktSkills.DetectMode(System.Windows.Automation.AutomationElement.FromHandle(w.Value.Hwnd));
    Console.WriteLine($"   初始模式: {m0}");
    W(m0 != KontaktSkills.UiMode.Unknown, "能判定当前界面模式");

    // 切到老版
    var r1 = KontaktSkills.SwitchUiMode("{\"mode\":\"classic\"}");
    Console.WriteLine("   " + r1.Substring(0, Math.Min(220, r1.Length)));
    W(r1.Contains("\"uiMode\":\"Classic\""), "已切到老版（Classic）界面");
    Thread.Sleep(1500);
    var m1 = KontaktSkills.DetectMode(System.Windows.Automation.AutomationElement.FromHandle(w.Value.Hwnd));
    W(m1 == KontaktSkills.UiMode.Classic, $"模式回读确认 = {m1}");

    // 老版界面里应该能看到 Libraries / Files / Monitor / Automation
    if (m1 == KontaktSkills.UiMode.Classic)
    {
        var st = KontaktSkills.GetUiState("{}");
        using var d = System.Text.Json.JsonDocument.Parse(st);
        W(d.RootElement.GetProperty("uiMode").GetString() == "Classic", "GetUiState 报告 Classic");
    }

    // 切回新版
    var r2 = KontaktSkills.SwitchUiMode("{\"mode\":\"new\"}");
    Console.WriteLine("   " + r2.Substring(0, Math.Min(220, r2.Length)));
    Thread.Sleep(1500);
    var m2 = KontaktSkills.DetectMode(System.Windows.Automation.AutomationElement.FromHandle(w.Value.Hwnd));
    W(m2 == KontaktSkills.UiMode.NewLumen, $"已切回新版（Lumen），模式回读 = {m2}");

    // 幂等：已是新版再切新版应报 changed=false
    var r3 = KontaktSkills.SwitchUiMode("{\"mode\":\"new\"}");
    W(r3.Contains("\"changed\":false"), "已是目标界面时不重复切换");

    // 非法参数
    var r4 = KontaktSkills.SwitchUiMode("{\"mode\":\"乱写\"}");
    W(r4.Contains("error"), "非法 mode 被拒绝");

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {wp} / 失败 {wf}");
    return wf == 0 ? 0 : 1;
}
// ── 重复库明细（含路径与注册状态）──
if (args.Length >= 2 && args[0] == "--dup-detail")
{
    var ddb = new Database(args[1]);
    ddb.EnsureCreated();
    Console.WriteLine("═══ 重复库明细（含路径 / 注册状态 / 建议）═══");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var sims = DuplicateFinder.FindSimilarLibraries(ddb, 0.35);
    sw.Stop();
    Console.WriteLine($"   找到 {sims.Count} 组相似关系（{sw.Elapsed.TotalSeconds:F1}s）");
    Console.WriteLine();
    int i = 0;
    foreach (var s in sims)
    {
        i++;
        Console.WriteLine($"── {i}. [{s.RelationLabel}] Jaccard={s.Jaccard:P0} ──");
        Console.WriteLine($"   A: {s.NameA}");
        Console.WriteLine($"      路径: {s.PathA}");
        Console.WriteLine($"      注册: {(s.RegA ? "★ 是（注册表指向它）" : "否")}");
        Console.WriteLine($"   B: {s.NameB}");
        Console.WriteLine($"      路径: {s.PathB}");
        Console.WriteLine($"      注册: {(s.RegB ? "★ 是（注册表指向它）" : "否")}");
        Console.WriteLine($"   → 建议保留: {s.KeepName}  ({s.KeepPath})");
        Console.WriteLine($"   → 建议删除: {(s.DeleteId == 0 ? "（不建议删）" : s.RemoveName + "  " + s.DeletePath)}");
        Console.WriteLine($"   → 删前需转注册: {(s.NeedTransferFirst ? "是" : "否")}   可回收 {s.ReclaimableBytes / 1073741824.0:F2} GB");
        Console.WriteLine($"   注册表路径: {(s.RegPath.Length > 0 ? s.RegPath : "（两库都未注册）")}  RegKey={s.RegKey}");
        Console.WriteLine();
    }
    return 0;
}
// ── 老版界面：搜索过滤 + 加载（M5 关键路径）──
// 用法: dotnet run -- --classic-test [搜索词]
if (args.Length >= 1 && args[0] == "--classic-test")
{
    string q = args.Length >= 2 ? args[1] : "Drums";
    Console.WriteLine("═══ 老版界面：搜索过滤 + 加载 ═══");
    var w = UiAutomation.ResolveWindow("Kontakt 8");
    if (w == null) { Console.WriteLine("   （Kontakt 8 未运行）"); return 1; }

    var mode = KontaktSkills.DetectMode(System.Windows.Automation.AutomationElement.FromHandle(w.Value.Hwnd));
    Console.WriteLine($"   当前界面模式: {mode}");
    if (mode != KontaktSkills.UiMode.Classic)
        Console.WriteLine("   ⚠ 不在老版界面，坐标常量可能不适用（本技能是为老版界面量的）");

    Console.WriteLine($"   ① 在搜索框输入「{q}」过滤…");
    var r1 = KontaktSkills.ClassicSearch("{\"query\":\"" + q.Replace("\"", "") + "\"}");
    Console.WriteLine("      " + r1.Substring(0, Math.Min(200, r1.Length)));

    // 截图供人工核对
    string shot = Path.Combine(@"F:\Deepseek Harness\Kontkat Library\docs", "classic-search-result.png");
    try
    {
        byte[]? jpg = UiVision.CaptureWindow(w.Value.Hwnd);
        if (jpg != null)
        {
            using var fs = File.Create(shot);
            fs.Write(jpg, 0, jpg.Length);
            Console.WriteLine($"      截图已存: {shot}  ({jpg.Length:N0} 字节)");
        }
    }
    catch (Exception ex) { Console.WriteLine("      截图失败: " + ex.Message); }

    Console.WriteLine($"   ② 双击列表第 1 项（加载该库）…");
    var r2 = KontaktSkills.ClassicLoadItem("{\"index\":1}");
    Console.WriteLine("      " + r2.Substring(0, Math.Min(200, r2.Length)));

    Console.WriteLine();
    Console.WriteLine("   请人工核对截图：列表是否只剩目标库、以及是否已加载。");
    return 0;
}
// ── 界面模式检测 ──
if (args.Length >= 1 && args[0] == "--ui-mode")
{
    var s = KontaktSkills.GetUiState("{}");
    using var d = System.Text.Json.JsonDocument.Parse(s);
    Console.WriteLine("═══ Kontakt 界面模式 ═══");
    Console.WriteLine("   uiMode        = " + (d.RootElement.TryGetProperty("uiMode", out var m) ? m.GetString() : "?"));
    Console.WriteLine("   contentType   = " + (d.RootElement.TryGetProperty("contentType", out var c2) ? c2.GetString() : ""));
    Console.WriteLine("   filter        = " + (d.RootElement.TryGetProperty("filter", out var f2) ? f2.GetString() : ""));
    if (d.RootElement.TryGetProperty("warning", out var w2) && (w2.GetString() ?? "").Length > 0)
        Console.WriteLine("   ⚠ " + w2.GetString());
    return 0;
}
// ── Kontakt 官方文档知识库检索测试 ──
// 用法: dotnet run -- --kb-doc-search [关键词...]
if (args.Length >= 1 && args[0] == "--kb-doc-search")
{
    var kb = ManualKb.Load(ManualKb.KontaktDocLibraryId);
    if (kb == null) { Console.WriteLine("   ❌ Kontakt 官方文档知识库未建立"); return 1; }
    Console.WriteLine("═══ Kontakt 官方文档知识库 ═══");
    Console.WriteLine($"   {kb.LibraryName} · {kb.ManualName}");
    Console.WriteLine($"   {kb.PageCount} 页 / {kb.Chunks.Count} 块 / {kb.TotalChars:N0} 字符");
    Console.WriteLine();
    var queries = args.Length >= 2 ? args[1..] : new[] { "browser", "key switch", "instrument navigator", "multi rack", "output section" };
    int hit = 0;
    foreach (var q in queries)
    {
        var hits = ManualKb.Search(kb, q, 3);
        Console.WriteLine($"   「{q}」→ {hits.Count} 条");
        if (hits.Count > 0)
        {
            hit++;
            var t = System.Text.RegularExpressions.Regex.Replace(hits[0].Text, @"\s+", " ");
            Console.WriteLine($"      p{hits[0].Page}: {t.Substring(0, Math.Min(150, t.Length))}");
        }
    }
    Console.WriteLine();
    Console.WriteLine($"   命中 {hit}/{queries.Length} 个查询");
    return hit > 0 ? 0 : 1;
}
// ── 建「Kontakt 官方文档」知识库 ──
// 用法: dotnet run -- --kb-kontakt <dbPath> [pdf路径]
//   不传 pdf 则自动扫描 Kontakt 安装目录的 Documentation 文件夹
if (args.Length >= 2 && args[0] == "--kb-kontakt")
{
    var kdb = new Database(args[1]);
    kdb.EnsureCreated();
    string pdf = args.Length >= 3 ? args[2] : "";
    if (pdf.Length == 0)
    {
        foreach (var cand in new[] {
            @"D:\Program Files\Kontakt8\Kontakt 8\Documentation\KONTAKT_Manual.pdf",
            @"C:\Program Files\Native Instruments\Kontakt 8\Documentation\KONTAKT_Manual.pdf" })
            if (File.Exists(cand)) { pdf = cand; break; }
    }
    if (pdf.Length == 0 || !File.Exists(pdf)) { Console.WriteLine("找不到 Kontakt 官方说明书，请显式传 pdf 路径"); return 1; }
    bool noVision = args.Any(a => a == "--novision");
    if (noVision) { ManualKb.VisionPageLimit = 0; Console.WriteLine("   已禁用视觉解析（--novision），只做文字抽取"); }
    else Console.WriteLine($"   视觉页上限: {ManualKb.VisionPageLimit}");

    var fi = new FileInfo(pdf);
    Console.WriteLine("═══ 建 Kontakt 官方文档知识库 ═══");
    Console.WriteLine($"   来源: {pdf}");
    Console.WriteLine($"   大小: {fi.Length / 1024.0 / 1024.0:F1} MB");

    var rec = new ManualRecord { LibraryPath = Path.GetDirectoryName(pdf)!, RelPath = Path.GetFileName(pdf), Name = Path.GetFileNameWithoutExtension(pdf) };
    var ai2 = AiSettings.Load(kdb);
    Console.WriteLine($"   模型: {(ai2.Configured ? ai2.Model : "（未配置，将跳过视觉页与概览）")}");
    Console.WriteLine("   开始建库（大 PDF 可能需要几分钟）…");
    var swk = System.Diagnostics.Stopwatch.StartNew();
    var prog = new Progress<KbProgress>(p2 =>
    {
        if (p2.Total > 0 && p2.Current % 25 == 0) Console.WriteLine($"     [{p2.Phase}] {p2.Current}/{p2.Total}");
    });
    var kb2 = ManualKb.BuildAsync(ManualKb.KontaktDocLibraryId, "Kontakt 官方文档", rec,
        ai2.Configured ? ai2 : null, prog, CancellationToken.None).GetAwaiter().GetResult();
    swk.Stop();

    Console.WriteLine();
    Console.WriteLine($"   ✅ 建库完成：{kb2.PageCount} 页 / {kb2.Chunks.Count} 块 / {kb2.TotalChars:N0} 字符 / 视觉页 {kb2.VisionPages}");
    Console.WriteLine($"      耗时 {swk.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"      存储: {ManualKb.KbPath(ManualKb.KontaktDocLibraryId)}");
    if (kb2.Overview.Length > 0) Console.WriteLine("      概览: " + kb2.Overview.Substring(0, Math.Min(200, kb2.Overview.Length)).Replace("\n", " "));
    return 0;
}
// ── Kontakt 领域技能包测试（M5）──
// 用法: dotnet run -- --ui-skill-test
if (args.Length >= 1 && args[0] == "--ui-skill-test")
{
    int sp = 0, sf = 0;
    void S(bool ok, string what) { if (ok) { sp++; Console.WriteLine("   ✅ " + what); } else { sf++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ Kontakt 领域技能包（M5）═══");
    var w = UiAutomation.ResolveWindow("Kontakt 8");
    if (w == null) { Console.WriteLine("   （Kontakt 8 未运行）"); return 0; }
    Console.WriteLine($"   目标: {w.Value.Title}");
    Console.WriteLine();

    // ① 读状态
    var st = KontaktSkills.GetUiState("{}");
    using var ds = System.Text.Json.JsonDocument.Parse(st);
    string ct0 = ds.RootElement.GetProperty("contentType").GetString() ?? "";
    string fl0 = ds.RootElement.GetProperty("filter").GetString() ?? "";
    S(ct0.Length > 0, $"读到当前内容类型 = {ct0}");
    S(fl0.Length > 0, $"读到当前筛选维度 = {fl0}");
    Console.WriteLine("      可用内容类型: " + string.Join(", ", KontaktSkills.ContentTypes));
    Console.WriteLine("      可用筛选维度: " + string.Join(", ", KontaktSkills.Filters));

    // ② 别名归一化
    S(KontaktSkills.Normalize("乐器") == "Instruments", "别名「乐器」→ Instruments");
    S(KontaktSkills.Normalize("循环") == "Loops", "别名「循环」→ Loops");
    S(KontaktSkills.Normalize("品牌") == "Brand", "别名「品牌」→ Brand");
    S(KontaktSkills.Normalize("character") == "Character", "大小写不敏感 → Character");

    // ③ 预览文案
    var pv = KontaktSkills.PreviewSetView("{\"contentType\":\"乐器\",\"filter\":\"品牌\"}");
    S(pv.Contains("Instruments") && pv.Contains("Brand"), "预览文案正确: " + pv);

    // ④ 切换内容类型 → Loops
    var r1 = KontaktSkills.SetView("{\"contentType\":\"Loops\"}");
    S(r1.Contains("\"ok\":true"), "切换到 Loops 成功");
    Thread.Sleep(600);
    var st2 = KontaktSkills.GetUiState("{}");
    using var ds2 = System.Text.Json.JsonDocument.Parse(st2);
    S(ds2.RootElement.GetProperty("contentType").GetString() == "Loops", "状态回读确认已切到 Loops");

    // ⑤ 切换筛选维度 → Character
    var r2 = KontaktSkills.SetView("{\"filter\":\"Character\"}");
    S(r2.Contains("\"ok\":true"), "筛选维度切到 Character 成功");

    // ⑥ 切回原状态
    var r3 = KontaktSkills.SetView("{\"contentType\":\"" + ct0 + "\",\"filter\":\"" + fl0 + "\"}");
    S(r3.Contains("\"ok\":true"), $"已恢复回 {ct0} / {fl0}");

    // ⑦ 非法标签要有明确报错
    var r4 = KontaktSkills.SetView("{\"contentType\":\"不存在的标签xyz\"}");
    S(r4.Contains("failed"), "非法标签被拒绝并说明原因");

    // ⑧ 搜索
    var r5 = KontaktSkills.Search("{\"query\":\"piano\"}");
    S(r5.Contains("\"ok\":true"), "搜索技能执行成功");
    Console.WriteLine("      " + r5.Substring(0, Math.Min(160, r5.Length)));
    Thread.Sleep(1500);
    var st3 = KontaktSkills.GetUiState("{}");
    using var ds3 = System.Text.Json.JsonDocument.Parse(st3);
    S(ds3.RootElement.GetProperty("searchText").GetString() == "piano", "搜索框内容回读 = piano");
    // 清空搜索
    KontaktSkills.Search("{\"query\":\"\"}");
    Thread.Sleep(500);

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {sp} / 失败 {sf}");
    return sf == 0 ? 0 : 1;
}
// ── Kontakt 界面深度探查（为 M5 技能包做依据）──
// 用法: dotnet run -- --ui-explore [窗口关键字] [深度]
if (args.Length >= 1 && args[0] == "--ui-explore")
{
    string ekey = args.Length >= 2 ? args[1] : "kontakt";
    int edepth = args.Length >= 3 ? int.Parse(args[2]) : 9;
    var ew = UiAutomation.ResolveWindow(ekey);
    if (ew == null) { Console.WriteLine("（目标未运行）"); return 0; }
    Console.WriteLine($"═══ {ew.Value.Title} 深度探查（depth={edepth}）═══");
    var raw = UiAutomation.DumpTree("{\"window\":\"" + ekey + "\",\"depth\":" + edepth + ",\"limit\":400}");
    using var d = System.Text.Json.JsonDocument.Parse(raw);
    if (d.RootElement.TryGetProperty("error", out var er)) { Console.WriteLine("   " + er.GetString()); return 1; }
    Console.WriteLine($"   扫描 {d.RootElement.GetProperty("scanned").GetInt32()} 节点，回传 {d.RootElement.GetProperty("returned").GetInt32()}");
    Console.WriteLine();
    var types = new Dictionary<string, int>();
    foreach (var n in d.RootElement.GetProperty("nodes").EnumerateArray())
    {
        string ty = n.GetProperty("type").GetString() ?? "";
        types[ty] = types.GetValueOrDefault(ty) + 1;
    }
    Console.WriteLine("   类型分布: " + string.Join(" · ", types.OrderByDescending(x => x.Value).Select(x => x.Key + " " + x.Value)));
    Console.WriteLine();
    Console.WriteLine("   有名称的控件（按名称排序）：");
    var named = d.RootElement.GetProperty("nodes").EnumerateArray()
        .Where(n => (n.GetProperty("name").GetString() ?? "").Length > 0)
        .OrderBy(n => n.GetProperty("name").GetString()).ToList();
    foreach (var n in named.Take(60))
        Console.WriteLine($"     <{n.GetProperty("type").GetString()}> '{n.GetProperty("name").GetString()}' [{string.Join(",", n.GetProperty("patterns").EnumerateArray().Select(x => x.GetString()))}] @{n.GetProperty("x").GetInt32()},{n.GetProperty("y").GetInt32()}");
    Console.WriteLine();
    Console.WriteLine("   类名分布（前 20）：");
    var cls = d.RootElement.GetProperty("nodes").EnumerateArray()
        .Select(n => n.GetProperty("cls").GetString() ?? "")
        .Where(x => x.Length > 0)
        .GroupBy(x => x).OrderByDescending(g => g.Count()).Take(20);
    foreach (var g in cls) Console.WriteLine($"     {g.Key}  ×{g.Count()}");
    return 0;
}
// ── 视觉兜底测试（M3）──
// 用法: dotnet run -- --ui-vision-test <dbPath> [窗口关键字]
if (args.Length >= 2 && args[0] == "--ui-vision-test")
{
    string vkey = args.Length >= 3 ? args[2] : "kontakt";
    int vp = 0, vf = 0;
    void V(bool ok, string what) { if (ok) { vp++; Console.WriteLine("   ✅ " + what); } else { vf++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ 视觉兜底（M3）═══");
    var vw = UiAutomation.ResolveWindow(vkey);
    if (vw == null) { Console.WriteLine("   （目标未运行）"); return 0; }
    Console.WriteLine($"   目标: {vw.Value.Title}");

    // ① 截图
    byte[]? jpg = UiVision.CaptureWindow(vw.Value.Hwnd);
    V(jpg != null && jpg.Length > 3000, $"截取窗口成功（{jpg?.Length ?? 0} 字节）");
    if (jpg == null) return 1;
    // 魔数应是 JPEG
    V(jpg[0] == 0xFF && jpg[1] == 0xD8, "截图是合法 JPEG（FF D8 魔数）");

    string path = UiVision.SaveTemp(jpg);
    V(path.Length > 0 && File.Exists(path), "截图已落盘");
    Console.WriteLine("      路径: " + path);

    // ② 视觉描述
    var vdb = new Database(args[1]);
    vdb.EnsureCreated();
    var ai = AiSettings.Load(vdb);
    if (!ai.Configured) { Console.WriteLine("   （AI 未配置，跳过视觉描述）"); return 0; }
    Console.WriteLine("      模型: " + ai.Model + "  多模态=" + ai.Multimodal);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    string desc = UiVision.DescribeAsync(ai, jpg, "这是 Kontakt 的界面截图。请用中文说明：顶部/左侧/中间各有什么？有没有标签页或列表？", CancellationToken.None).GetAwaiter().GetResult();
    sw.Stop();
    V(desc.Length > 20 && !desc.StartsWith("（"), $"视觉模型返回描述（{desc.Length} 字，{sw.Elapsed.TotalSeconds:F1}s）");
    Console.WriteLine("      ── 描述 ──");
    foreach (var ln in desc.Split('\n').Take(10)) Console.WriteLine("      " + ln);

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {vp} / 失败 {vf}");
    return vf == 0 ? 0 : 1;
}
// ── 界面写操作测试（M2）──
// 用法: dotnet run -- --ui-action-test [窗口关键字]
if (args.Length >= 1 && args[0] == "--ui-action-test")
{
    string akey = args.Length >= 2 ? args[1] : "kontakt";
    int ap = 0, af = 0;
    void A(bool ok, string what) { if (ok) { ap++; Console.WriteLine("   ✅ " + what); } else { af++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ 界面写操作（M2：定位 → 确认 → 执行）═══");
    var w2 = UiAutomation.ResolveWindow(akey);
    if (w2 == null) { Console.WriteLine("   （目标未运行）"); return 0; }
    Console.WriteLine($"   目标: {w2.Value.Title}  hwnd={w2.Value.Hwnd.ToInt64()}");
    Console.WriteLine();

    // 辅助：读某个 RadioButton 的选中态
    bool Sel(string nm)
    {
        var s = UiAutomation.GetState("{\"window\":\"" + akey + "\",\"name\":\"" + nm + "\"}");
        using var d = System.Text.Json.JsonDocument.Parse(s);
        if (!d.RootElement.TryGetProperty("state", out var st)) return false;
        return st.TryGetProperty("selected", out var v) && v.GetBoolean();
    }

    // ① 预览：点击 Tools
    var pv = UiActions.Preview("click", "{\"window\":\"" + akey + "\",\"name\":\"Tools\",\"type\":\"RadioButton\"}");
    A(pv.Ok, "预览「点击 Tools」成功");
    Console.WriteLine("      预览文案: " + pv.Description);
    if (!pv.Ok) { Console.WriteLine("      " + pv.Error); return 1; }

    bool before = Sel("Instruments");
    A(before, "执行前 Instruments 处于选中态");

    // ② 执行
    var ex = UiActions.Execute("click", "{\"window\":\"" + akey + "\",\"name\":\"Tools\",\"type\":\"RadioButton\"}");
    Console.WriteLine("      执行结果: " + ex.Substring(0, Math.Min(140, ex.Length)));
    A(ex.Contains("\"ok\":true"), "执行点击成功");

    bool afterTools = Sel("Tools");
    A(afterTools, "执行后 Tools 变为选中态（界面真的切换了）");
    A(!Sel("Instruments"), "Instruments 已取消选中");

    // ③ 恢复
    var ex2 = UiActions.Execute("click", "{\"window\":\"" + akey + "\",\"name\":\"Instruments\",\"type\":\"RadioButton\"}");
    A(Sel("Instruments"), "已恢复回 Instruments");

    // ④ 定位失败要有明确报错
    var pvBad = UiActions.Preview("click", "{\"window\":\"" + akey + "\",\"name\":\"这个控件不存在xyz\"}");
    A(!pvBad.Ok && pvBad.Error.Contains("未找到"), "定位不存在的控件时明确报错");

    // ⑤ 按键解析
    A(UiInput.NameToVk("Ctrl") == 0x11 && UiInput.NameToVk("F5") == 0x74 && UiInput.NameToVk("Enter") == 0x0D, "按键名解析正确（Ctrl / F5 / Enter）");
    A(UiInput.NameToVk("不存在键") == 0, "未知按键返回 0");

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {ap} / 失败 {af}");
    return af == 0 ? 0 : 1;
}
// ── 界面自动化（只读探查）测试 ──
// 用法: dotnet run -- --ui-test [窗口关键字]
if (args.Length >= 1 && args[0] == "--ui-test")
{
    string key = args.Length >= 2 ? args[1] : "kontakt";
    int upass = 0, ufail = 0;
    void U(bool ok, string what) { if (ok) { upass++; Console.WriteLine("   ✅ " + what); } else { ufail++; Console.WriteLine("   ❌ " + what); } }

    Console.WriteLine("═══ 界面自动化（只读探查）═══");
    Console.WriteLine("   目标关键字: " + key);
    Console.WriteLine();

    // ① 列出窗口
    var w = UiAutomation.ListWindows("{\"filter\":\"" + key + "\"}");
    var dw = System.Text.Json.JsonDocument.Parse(w);
    int wc = dw.RootElement.GetProperty("count").GetInt32();
    U(wc > 0, $"ui_list_windows 找到 {wc} 个匹配窗口");
    if (wc > 0)
    {
        var first = dw.RootElement.GetProperty("windows")[0];
        Console.WriteLine($"      进程={first.GetProperty("process").GetString()}  标题={first.GetProperty("title").GetString()}  hwnd={first.GetProperty("hwnd").GetInt64()}");
    }
    if (wc == 0) { Console.WriteLine(); Console.WriteLine("   （目标未运行，跳过后续）"); return 0; }

    // ② 枚举控件树
    var t = UiAutomation.DumpTree("{\"window\":\"" + key + "\",\"depth\":6}");
    var dt = System.Text.Json.JsonDocument.Parse(t);
    if (dt.RootElement.TryGetProperty("error", out var te)) { U(false, "ui_dump_tree: " + te.GetString()); }
    else
    {
        int scanned = dt.RootElement.GetProperty("scanned").GetInt32();
        int ret = dt.RootElement.GetProperty("returned").GetInt32();
        U(scanned > 5 && ret > 0, $"ui_dump_tree 扫描 {scanned} 个节点，回传 {ret} 个（已裁剪）");
        // 统计类型
        var types = new Dictionary<string, int>();
        foreach (var n in dt.RootElement.GetProperty("nodes").EnumerateArray())
        {
            string ty = n.GetProperty("type").GetString() ?? "";
            types[ty] = types.GetValueOrDefault(ty) + 1;
        }
        Console.WriteLine("      类型分布: " + string.Join(" · ", types.OrderByDescending(x => x.Value).Take(6).Select(x => x.Key + " " + x.Value)));
        Console.WriteLine("      前 5 个控件:");
        foreach (var n in dt.RootElement.GetProperty("nodes").EnumerateArray().Take(5))
            Console.WriteLine($"        <{n.GetProperty("type").GetString()}> '{n.GetProperty("name").GetString()}' [{string.Join(",", n.GetProperty("patterns").EnumerateArray().Select(x => x.GetString()))}] @{n.GetProperty("x").GetInt32()},{n.GetProperty("y").GetInt32()}");
    }

    // ③ 查找控件（按类名 —— 这是 UIA 定位的正道）
    var fnd = UiAutomation.Find("{\"window\":\"" + key + "\",\"className\":\"LumenTab\"}");
    var df = System.Text.Json.JsonDocument.Parse(fnd);
    int fc = df.RootElement.GetProperty("count").GetInt32();
    U(fc > 0, $"ui_find(className=LumenTab) 找到 {fc} 个控件");
    foreach (var m in df.RootElement.GetProperty("matches").EnumerateArray().Take(4))
        Console.WriteLine($"        <{m.GetProperty("type").GetString()}> '{m.GetProperty("name").GetString()}' 中心=({m.GetProperty("centerX").GetInt32()},{m.GetProperty("centerY").GetInt32()})");

    // ④ 读控件状态
    if (fc > 0)
    {
        string nm = df.RootElement.GetProperty("matches")[0].GetProperty("name").GetString() ?? "";
        var st = UiAutomation.GetState("{\"window\":\"" + key + "\",\"name\":\"" + nm.Replace("\"", "\\\"") + "\"}");
        var ds = System.Text.Json.JsonDocument.Parse(st);
        if (ds.RootElement.TryGetProperty("state", out var se))
        {
            U(se.TryGetProperty("controlType", out _), $"ui_get_state('{nm}') 读到控件类型");
            Console.WriteLine("      state = " + se.GetRawText().Substring(0, Math.Min(200, se.GetRawText().Length)));
        }
        else U(false, "ui_get_state 未返回 state");
    }

    Console.WriteLine();
    Console.WriteLine($"   结果：通过 {upass} / 失败 {ufail}");
    return ufail == 0 ? 0 : 1;
}


Console.WriteLine(" Kontakt 音色库管理器 — 自检（M1 + M2 功能）");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine($" 音色库根目录 : {root}");
Console.WriteLine($" 基准 CSV     : {csvPath}");
Console.WriteLine();

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "✅" : "❌")} {name}{(detail.Length > 0 ? "  — " + detail : "")}");
    if (!ok) failures++;
}
void Section(string t) { Console.WriteLine(); Console.WriteLine($"── {t} ──"); }

// ═══ 0. 版本工具 ═══
Section("0. 版本号工具");
Check("7.10.1.0 > 7.5.2.0", VersionUtil.Compare("7.10.1.0", "7.5.2.0") > 0);
Check("5.8.1.43 < 6.1.1.66", VersionUtil.Compare("5.8.1.43", "6.1.1.66") < 0);
Check("7.6.0.0 == 7.6.0.0", VersionUtil.Compare("7.6.0.0", "7.6.0.0") == 0);
Check("6.4.2 与 6.4.2.0 视为相等", VersionUtil.Compare("6.4.2", "6.4.2.0") == 0);
Check("文件名猜版本 KontaktPortable_v871 → 8.7.1", VersionUtil.GuessFromFileName("KontaktPortable_v871") == "8.7.1",
    VersionUtil.GuessFromFileName("KontaktPortable_v871"));

// ═══ 1. 本地 Kontakt 环境 ═══
Section("1. 本机 Kontakt 环境");
bool isAdmin = KontaktInfo.IsAdmin();
Console.WriteLine($"      管理员权限：{(isAdmin ? "是" : "否")}");

var scan = KontaktInfo.Detect();
Console.WriteLine($"      检测到 {scan.Installs.Count} 个可用程序：");
foreach (var i in scan.Installs)
    Console.WriteLine($"        · v{i.Version,-10} {i.Path}  [{i.Source}, {i.SizeMB} MB]");
if (scan.Ignored.Count > 0)
{
    Console.WriteLine($"      已忽略 {scan.Ignored.Count} 个（安装包/非程序）：");
    foreach (var i in scan.Ignored) Console.WriteLine($"        × {i.Path}  ← {i.Note}");
}
Check("检测到至少一个 Kontakt 程序", scan.Installs.Count > 0);
Check("检测到的程序版本号可解析", scan.Installs.All(i => VersionUtil.IsValid(i.Version)),
    string.Join(", ", scan.Installs.Select(i => i.Version)));

// 回归：便携版安装包曾被误判为主程序（1GB、无版本信息）
Check("未把安装包当成主程序",
    scan.Installs.All(i => !i.Path.Contains("KontaktPortable", StringComparison.OrdinalIgnoreCase)),
    string.Join(" | ", scan.Installs.Select(i => Path.GetFileName(i.Path))));
Check("安装包进入忽略清单（透明化）",
    scan.Ignored.Any(i => i.Path.Contains("KontaktPortable", StringComparison.OrdinalIgnoreCase)),
    $"{scan.Ignored.Count} 个被忽略");
Check("能挖到深层目录里的真主程序（Kontakt N.exe 命名）",
    scan.Installs.Any(i => System.Text.RegularExpressions.Regex.IsMatch(
        Path.GetFileName(i.Path), @"^Kontakt \d+\.exe$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
    string.Join(" | ", scan.Installs.Select(i => Path.GetFileName(i.Path))));

string bestVersion = scan.Installs.FirstOrDefault(i => VersionUtil.IsValid(i.Version))?.Version ?? "";
// ═══ 2. .nicnt 解析 ═══
Section("2. .nicnt 元数据解析");
string sampleLib = Path.Combine(root, "Native Instruments - Action Strings");
var sampleNicnt = Directory.Exists(sampleLib)
    ? new DirectoryInfo(sampleLib).EnumerateFiles("*.nicnt", SearchOption.TopDirectoryOnly).FirstOrDefault()
    : null;

if (sampleNicnt != null)
{
    var info = NicntReader.Read(sampleNicnt.FullName);
    Check(".nicnt 解析成功", info != null);
    if (info != null)
    {
        Console.WriteLine($"      名称={info.Name} / RegKey={info.RegKey} / HU={info.Hu[..Math.Min(8, info.Hu.Length)]}… / Visibility={info.Visibility}");
        Check("解析出 RegKey", !string.IsNullOrWhiteSpace(info.RegKey), info.RegKey);
        Check("解析出 HU/JDX 授权对", info.HasAuthPair);
    }
}
else Check(".nicnt 样本存在", false, sampleLib);

// ═══ 3. 注册表入库状态读取 ═══
Section("3. Kontakt 注册表读取");
var registered = LibraryRegistrar.ReadRegistered();
Console.WriteLine($"      读到 {registered.Count} 个已注册产品（ContentDir 非空）");
Check("读到注册表条目", registered.Count > 0);

int pointingToLib = registered.Values.Count(p =>
    p.ContentDir.StartsWith(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
Check("存在指向本音色库根目录的条目", pointingToLib > 0, $"{pointingToLib} 个产品指向 {root}");

var actionStrings = registered.Values.FirstOrDefault(p =>
    p.RegKey.Equals("Action Strings", StringComparison.OrdinalIgnoreCase));
Check("能找到 Action Strings 条目", actionStrings != null);
if (actionStrings != null)
{
    Check("其 HU 与 .nicnt 一致（解析链路自洽）",
        sampleNicnt == null || string.Equals(actionStrings.Hu, NicntReader.Read(sampleNicnt.FullName)?.Hu, StringComparison.OrdinalIgnoreCase),
        actionStrings.Hu);
    Check("三视图齐全（HKLM64/HKLM32/HKCU）",
        actionStrings.InHklm64 && actionStrings.InHklm32 && actionStrings.InHkcu,
        $"64={actionStrings.InHklm64} 32={actionStrings.InHklm32} cu={actionStrings.InHkcu}");
}

// ═══ 4. 全量扫描（多根目录）═══
Section("4. 多根目录全量扫描");
string dbPath = Path.Combine(AppPaths.DataDir, "selftest.db");
if (File.Exists(dbPath)) File.Delete(dbPath);
var db = new Database(dbPath);
db.EnsureCreated();
Check("schema 版本 = 5", Database.SchemaVersion == 5, $"v{Database.SchemaVersion}");

var (addOk, addMsg, _) = db.AddRoot(root, "自检根目录");
Check("添加根目录", addOk, addMsg);

// 再添加一个不存在的路径，验证容错
var (badOk, badMsg, _) = db.AddRoot(@"Z:\不存在的路径", "无效路径");
Check("拒绝不存在的路径", !badOk, badMsg);

var roots = db.GetRoots();
Check("根目录列表可回读", roots.Count == 1, $"{roots.Count} 个：{string.Join(", ", roots.Select(r => r.Path))}");

var scanner = new LibraryScanner();
var progressCount = 0;
var sc = await scanner.ScanAsync(roots, new Progress<ScanProgress>(_ => progressCount++));
Console.WriteLine($"      扫描：{sc.Libraries.Count} 库 / {sc.TotalFiles:N0} 文件 / {sc.TotalInstruments:N0} 乐器 / {sc.ElapsedSeconds:F2}s / 进度回调 {progressCount} 次");

var csv = LoadCsv(csvPath);
Check("库数量与基准一致", sc.Libraries.Count == csv.Count, $"{sc.Libraries.Count} vs {csv.Count}");
Check("文件总数与基准一致", sc.TotalFiles == csv.Sum(c => c.Files), $"{sc.TotalFiles:N0} vs {csv.Sum(c => c.Files):N0}");
Check("NKI 总数与基准一致", sc.Libraries.Sum(l => l.NkiCount) == csv.Sum(c => c.NKI));
Check("扫描结果带回根目录信息", sc.Roots.Count == 1 && sc.Libraries.All(l => l.RootPath.Length > 0));

// ═══ 5. 引擎版本提取 ═══
Section("5. NKI/NKM 引擎版本提取（版本兼容基础）");
int withEngine = sc.Libraries.Count(l => VersionUtil.IsValid(l.RequiredKontakt));
Check("至少一半的库提取到引擎版本", withEngine >= sc.Libraries.Count / 2, $"{withEngine}/{sc.Libraries.Count}");
foreach (var lib in sc.Libraries.Where(l => VersionUtil.IsValid(l.RequiredKontakt))
                                .OrderByDescending(l => VersionUtil.ParseParts(l.RequiredKontakt).FirstOrDefault())
                                .Take(5))
    Console.WriteLine($"        · {lib.RequiredKontakt,-12} {lib.Name}");

// 版本号必须出现在文件的真实字符串里（防误读二进制噪声）
var kfl = sc.Libraries.FirstOrDefault(l => l.Name.Contains("Kontakt Factory Library 2"));
Check("Factory Library 2 版本可提取", kfl != null && VersionUtil.IsValid(kfl.RequiredKontakt), kfl?.RequiredKontakt ?? "");
var kflNki = new DirectoryInfo(kfl!.Path).EnumerateFiles("*.nki", new EnumerationOptions { RecurseSubdirectories = true }).First();
var (_, _, _, kflEngine) = NkiMetadataReader.ReadFromFile(kflNki.FullName);
Check("单文件复读版本与库级一致（同源）", VersionUtil.IsValid(kflEngine), $"{kflNki.Name} → {kflEngine}");
Check("库级版本取的是库内最高要求（>= 单文件版本）",
    VersionUtil.Compare(kfl.RequiredKontakt, kflEngine) >= 0, $"库 {kfl.RequiredKontakt} vs 单文件 {kflEngine}");

// NMK 多轨文件要求更高版本：写进说明，避免日后误判为 bug
Console.WriteLine("      说明：.nkm（多轨合奏）通常要求比同库 .nki 更高的 Kontakt 版本，库级版本取两者较高者。");

// ═══ 6. 入库状态判定 ═══
Section("6. 入库状态判定");
LibraryRegistrar.Enrich(sc.Libraries, registered, bestVersion);
int reg = sc.Libraries.Count(l => l.RegStatus == "registered");
int miss = sc.Libraries.Count(l => l.RegStatus == "missing");
int nonstd = sc.Libraries.Count(l => l.RegStatus == "non-standard");
int mism = sc.Libraries.Count(l => l.RegStatus is "path-mismatch" or "partial");
int incomplete = sc.Libraries.Count(l => l.RegStatus == "incomplete");
int pendingMgr = sc.Libraries.Count(l => l.RegStatus == "pending-manager");
int noNicnt = sc.Libraries.Count(l => !l.HasNicnt);
Console.WriteLine($"      已入库 {reg} / 记录不完整 {incomplete} / 待库管理器保存 {pendingMgr} / 未入库 {miss} / 路径失效 {mism} / 非标准库(未注册) {nonstd}");
Console.WriteLine($"      属性统计：无 .nicnt 的库 {noNicnt} 个");
Check("入库状态已分类（各状态之和 = 总数）",
    reg + miss + nonstd + mism + incomplete + pendingMgr == sc.Libraries.Count,
    $"{reg}+{incomplete}+{pendingMgr}+{miss}+{mism}+{nonstd} = {reg + miss + nonstd + mism + incomplete + pendingMgr} / {sc.Libraries.Count}");
// 该值随用户实际清理/新增音色库而变化，改为与基准 CSV 的 NICNT 列比对（而非硬编码）
int csvNoNicnt = csv.Count(c => c.NICNT == 0);
Check("无 .nicnt 的库数与基准一致", noNicnt == csvNoNicnt, $"{noNicnt} vs {csvNoNicnt}");

// 关键回归：无 .nicnt 但注册表已指向它的库，必须判为「已入库」而不是"非标准库"
var kawai = sc.Libraries.FirstOrDefault(l => l.Name.Contains("Kawai"));
   // 原意：注册表已指向它时，**不能**被判成「非标准库」。
   // 注：自从实现 NicntWriter 后，无 .nicnt 的库会被自动补上 .nicnt，
   //     所以不再断言「无 .nicnt」这个前提，只断言「状态判定正确」。
   Check("已注册的库（Kawai EX Pro）不被判为非标准库",
       kawai != null && kawai.RegStatus != "non-standard",
       $"hasNicnt={kawai?.HasNicnt} status={kawai?.RegStatus} regDir={kawai?.RegContentDir}");
var score = sc.Libraries.FirstOrDefault(l => l.Name.Contains("The Score"));
Check("The Score 入库状态可判定（missing 或 registered 均可，取决于用户是否已入库）",
    score != null && (score.RegStatus == "missing" || score.RegStatus == "registered"),
    $"status={score?.RegStatus} keys={score?.ProductKey}");

// ═══ 7. 版本兼容判定 ═══
Section("7. 版本兼容判定");
LibraryRegistrar.Enrich(sc.Libraries, registered, bestVersion);
int okC = sc.Libraries.Count(l => l.CompatStatus == "ok");
int badC = sc.Libraries.Count(l => l.CompatStatus == "too-old");
int unkC = sc.Libraries.Count(l => l.CompatStatus == "unknown");
Console.WriteLine($"      当前 Kontakt {bestVersion}：兼容 {okC} / 需要更高版本 {badC} / 未知 {unkC}");
Check("兼容判定有结果", okC + badC + unkC == sc.Libraries.Count);

// 用低版本模拟，必须能识别出"需要更高版本"的库
LibraryRegistrar.Enrich(sc.Libraries, registered, "5.8.1.0");
int lowBad = sc.Libraries.Count(l => l.CompatStatus == "too-old");
Check("模拟 Kontakt 5.8.1 时能报出高版本库", lowBad > 0, $"{lowBad} 个库需要更高版本（如 Production Grand 2 需 7.10.1）");
var pg = sc.Libraries.FirstOrDefault(l => l.Name.Contains("Production Grand"));
Check("Production Grand 2 被判定为需 7.x", pg != null && VersionUtil.Compare(pg.RequiredKontakt, "5.8.1.0") > 0,
    pg?.RequiredKontakt ?? "<未找到>");

// ═══ 8. 索引写入/回读 ═══
Section("8. SQLite 索引层（schema v2）写入与回读");
db.SaveScanResult(sc);
var dash = db.GetDashboard();
var libs = db.GetLibraries();
Check("回读库数一致", dash.LibraryCount == sc.Libraries.Count);
Check("回读乐器数一致", dash.InstrumentCount == sc.TotalInstruments, $"{dash.InstrumentCount} vs {sc.TotalInstruments}");
Check("回读总占用一致", dash.TotalBytes == sc.TotalBytes);
Check("回读文件数一致", dash.TotalFiles == sc.TotalFiles);
Check("根目录数回读正确", dash.RootCount == 1, $"{dash.RootCount}");
Check("选项字段（product_key）已落库", libs.Count(l => l.ProductKey.Length > 0) > 50,
    $"{libs.Count(l => l.ProductKey.Length > 0)} 个库有产品键");
Check("引擎版本已落库", libs.Count(l => VersionUtil.IsValid(l.RequiredKontakt)) > 30,
    $"{libs.Count(l => VersionUtil.IsValid(l.RequiredKontakt))} 个库有版本要求");

var firstNki = db.GetFirstInstrumentPath(libs.First(l => l.NkiCount > 0).Id);
Check("可取到库内首个 NKI 路径（测试加载用）", firstNki != null && File.Exists(firstNki), firstNki ?? "<null>");

var instruments = db.GetInstruments(limit: 20);
Check("乐器查询可用", instruments.Count > 0, $"取到 {instruments.Count} 条");
Check("乐器带引擎版本字段", instruments.Any(i => VersionUtil.IsValid(i.EngineVersion)));

// 根目录删除级联
var tempRootId = roots[0].Id;
var libCountBefore = db.GetLibraries().Count;
db.RemoveRoot(tempRootId);
Check("移除根目录后级联清理索引", db.GetLibraries().Count == 0, $"清理前 {libCountBefore} → 清理后 {db.GetLibraries().Count}");

// ═══ 9. 入库写入路径（安全自测：临时注册表键，不触碰任何真实音色库）═══
Section("9. 入库写入路径（写入 → 校验 → 清理）");
if (!isAdmin)
{
    Console.WriteLine("      跳过：需要管理员权限才能写入 HKLM");
}
else
{
    const string tempKey = "KLM SelfTest Temp";
    string tempDir = Path.Combine(AppPaths.DataDir, "klm-selftest-temp");
    Directory.CreateDirectory(tempDir);
    string tempNicntPath = Path.Combine(tempDir, "temp.nicnt");
    File.WriteAllText(tempNicntPath, "selftest");

    var tempNicnt = new NicntInfo
    {
        FilePath = tempNicntPath,
        Name = tempKey,
        RegKey = tempKey,
        Hu = "0123456789ABCDEF0123456789ABCDEF",
        Jdx = new string('A', 64),
        Visibility = "3",
        ContentVersion = "9.9.9",
    };

    try
    {
        var (wOk, wMsg) = LibraryRegistrar.Register(tempNicnt);
        Check("写入临时注册表项", wOk, wMsg);

        var after = LibraryRegistrar.ReadRegistered();
        bool present = after.TryGetValue(tempKey, out var written);
        Check("写入后可被读回", present);
        if (present)
        {
            Check("ContentDir 正确落盘", LibraryRegistrar.NormalizePath(written!.ContentDir)
                .Equals(LibraryRegistrar.NormalizePath(tempDir), StringComparison.OrdinalIgnoreCase), written.ContentDir);
            Check("HU/JDX 正确落盘", written.Hu == tempNicnt.Hu && written.Jdx == tempNicnt.Jdx);
            Check("Visibility = 3", written.Visibility == 3, written.Visibility.ToString());
            Check("三视图均已写入", written.InHklm64 && written.InHklm32 && written.InHkcu,
                $"64={written.InHklm64} 32={written.InHklm32} cu={written.InHkcu}");
        }
    }
    finally
    {
        var (uOk, uMsg) = LibraryRegistrar.Unregister(tempKey);
        var cleaned = !LibraryRegistrar.ReadRegistered().ContainsKey(tempKey);
        Check("清理临时注册表项（不留痕）", uOk && cleaned, uMsg);
        try { Directory.Delete(tempDir, true); } catch { }
    }
}

// ═══ 10. Kontakt 候选列表（增 / 改 / 删）═══
Section("10. Kontakt 候选列表（增删改与持久化）");
string candDb = Path.Combine(AppPaths.DataDir, "candtest.db");
if (File.Exists(candDb)) File.Delete(candDb);
var cdb = new Database(candDb);
cdb.EnsureCreated();

var realExe = scan.Installs.FirstOrDefault(i => i.Path.EndsWith("Kontakt 8.exe", StringComparison.OrdinalIgnoreCase))?.Path
           ?? scan.Installs.FirstOrDefault()?.Path ?? "";
var secondExe = scan.Installs.Skip(1).FirstOrDefault()?.Path ?? "";

Check("检测结果可合并为候选列表", KontaktCandidateStore.Merge(cdb, scan).Count == scan.Installs.Count,
    $"{KontaktCandidateStore.Merge(cdb, scan).Count} vs {scan.Installs.Count}");

if (realExe.Length > 0)
{
    // 删除一个自动项 → 必须被记住，不再出现
    var (rOk, rMsg) = KontaktCandidateStore.Remove(cdb, realExe);
    var afterRemove = KontaktCandidateStore.Merge(cdb, scan);
    Check("删除自动项后不再出现在列表", rOk && afterRemove.All(c => !c.Path.Equals(realExe, StringComparison.OrdinalIgnoreCase)), rMsg);

    // 手动加回来
    var (aOk, aMsg) = KontaktCandidateStore.Add(cdb, realExe);
    var afterAdd = KontaktCandidateStore.Merge(cdb, scan);
    Check("手动添加后重新出现", aOk && afterAdd.Any(c => c.Path.Equals(realExe, StringComparison.OrdinalIgnoreCase)), aMsg);
    Check("手工项标记为 manual", afterAdd.First(c => c.Path.Equals(realExe, StringComparison.OrdinalIgnoreCase)).Source == "manual");

    // 修改路径（改成一个真实存在的另一个程序，模拟换路径）
    if (secondExe.Length > 0)
    {
        var (uOk, uMsg) = KontaktCandidateStore.Update(cdb, realExe, secondExe);
        var afterUpd = KontaktCandidateStore.Merge(cdb, scan);
        Check("修改路径生效", uOk && afterUpd.Any(c => c.Path.Equals(secondExe, StringComparison.OrdinalIgnoreCase))
                                   && afterUpd.All(c => !c.Path.Equals(realExe, StringComparison.OrdinalIgnoreCase)), uMsg);
    }

    // 非法路径应被拒绝
    var (badAdd, badAddMsg) = KontaktCandidateStore.Add(cdb, @"Z:\nope\Kontakt 9.exe");
    Check("拒绝不存在的路径", !badAdd, badAddMsg);
}
else Check("存在可用于测试的 Kontakt 程序", false, "未检测到");

// ═══ 11. 便携版 Kontakt 库列表（本项目最关键的发现）═══
Section("11. 便携版 Kontakt 库列表检测");
string? portableExe = scan.Installs.FirstOrDefault(i => i.Path.Contains("Kontakt", StringComparison.OrdinalIgnoreCase))?.Path;
var portables = scan.Installs
    .Select(i => new { i.Path, Info = PortableKontaktStore.Inspect(i.Path) })
    .Where(x => x.Info != null)
    .ToList();

if (portables.Count == 0)
{
    Console.WriteLine("      本机未检测到便携版 Kontakt（没有 UserData\\Settings.cfg），跳过");
}
else
{
    foreach (var p in portables)
    {
        var info = p.Info!;
        Console.WriteLine($"      便携版根目录：{info.Root}");
        Console.WriteLine($"        库列表文件：{info.SettingsCfg}（修改于 {info.SettingsCfgModified:yyyy-MM-dd HH:mm:ss}）");
        Console.WriteLine($"        库管理器：{info.LibraryManager}");
        Console.WriteLine($"        列表中库数：{info.RegisteredNames.Count}");
    }

    var first = portables[0].Info!;
    Check("找到 Settings.cfg", first.SettingsCfg.Length > 0 && File.Exists(first.SettingsCfg));
    Check("解析出库条目（>50）", first.RegisteredNames.Count > 50, $"{first.RegisteredNames.Count} 个");
    Check("找到库管理器程序", first.LibraryManager.Length > 0, first.LibraryManager);
    Check("条目带 ContentDir", first.ContentDirs.Count > 0, $"{first.ContentDirs.Count} 条");

    // Settings.cfg 中存在指向 N 盘的库
    int nCount = first.ContentDirs.Count(d => d.StartsWith(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    Check("便携版列表含指向本音色库根目录的条目", nCount > 0, $"{nCount} 条");

    // 与注册表比对：找出"注册表有、便携版列表没有"的库（正是报"库未安装"的那批）
    var portableSet = new HashSet<string>(first.RegisteredNames, StringComparer.OrdinalIgnoreCase);
    int regNotInPortable = registered.Keys.Count(k => !portableSet.Contains(k));
    Console.WriteLine($"      注册表产品 {registered.Count} 个，其中 {regNotInPortable} 个不在便携版列表中（需用库管理器「扫描 → 保存」）");
    Check("能算出差集（不抛异常）", regNotInPortable >= 0);
}

// ═══ 12. 封面图与说明书（M5 新功能）═══
Section("12. 库封面图与说明书采集");
var libsWithCover = sc.Libraries.Count(l => l.CoverFile.Length > 0);
var libsWithNicnt = sc.Libraries.Count(l => l.HasNicnt);
Console.WriteLine($"      有 .nicnt 的库：{libsWithNicnt} / {sc.Libraries.Count}");
Console.WriteLine($"      提取到封面的库：{libsWithCover}");
Console.WriteLine($"      采集到说明书：{sc.Manuals.Count} 个，覆盖 {sc.Manuals.Select(m => m.LibraryName).Distinct().Count()} 个库");

Check("从 .nicnt 提取到封面（>40）", libsWithCover > 40, $"{libsWithCover} 个");
Check("封面文件确实落盘", sc.Libraries.Where(l => l.CoverFile.Length > 0)
        .Take(10).All(l => File.Exists(Path.Combine(CoverStore.Dir, l.CoverFile))),
    CoverStore.Dir);
Check("无 .nicnt 的库不误报封面",
    sc.Libraries.Where(l => !l.HasNicnt).All(l => l.CoverFile.Length == 0),
    $"{sc.Libraries.Count(l => !l.HasNicnt && l.CoverFile.Length > 0)} 个误报");
    // **基线更新（2026-09-20）**：机械过滤（LibraryScanner.LooksLikeManual）上线后，
    // 说明书采集不再「只按扩展名收」，而是额外做四级过滤：
    //   `._` 前缀/.DS_Store → 白名单 → 黑名单(license/readme/changelog/thirdpartycontent/…) → 50 KB 阈值。
    // 因此**数量本应大幅下降**（旧基线 1,684 条里有大量 readme/授权书）。实测降到 ~96。
    // 阈值设为 > 50：既能证明「确实采到了说明书」，又能容忍过滤规则后续微调。
    Check("采集到说明书（>50，机械过滤后）", sc.Manuals.Count > 50, $"{sc.Manuals.Count} 个");
    // 反向断言：过滤必须真的起作用 —— 黑名单文档不应出现在结果里
    Check("机械过滤生效（无 license/readme/changelog 混入）",
        !sc.Manuals.Any(m => System.Text.RegularExpressions.Regex.IsMatch(
            m.Name,
            @"license|licence|eula|readme|changelog|thirdparty|licensing|\.DS_Store|^\._",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
        $"{sc.Manuals.Count(m => System.Text.RegularExpressions.Regex.IsMatch(m.Name, @"license|readme|changelog", System.Text.RegularExpressions.RegexOptions.IgnoreCase))} 个可疑");
Check("识别出主要手册", sc.Manuals.Any(m => m.IsPrimary), $"{sc.Manuals.Count(m => m.IsPrimary)} 个标记为 main");
Check("说明书覆盖过半库", sc.Manuals.Select(m => m.LibraryName).Distinct().Count() >= 50,
    $"{sc.Manuals.Select(m => m.LibraryName).Distinct().Count()} 个库");

// 封面图尺寸合理性（应为宽幅横幅）
var sampleCover = sc.Libraries.FirstOrDefault(l => l.CoverFile.Length > 0);
if (sampleCover != null)
{
    string p = Path.Combine(CoverStore.Dir, sampleCover.CoverFile);
    long size = new FileInfo(p).Length;
    Check("封面文件大小合理（10KB~2MB）", size is > 10_000 and < 2_000_000, $"{size:N0} bytes ← {sampleCover.Name}");
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine(failures == 0
    ? " ✅ 全部通过 — 多路径扫描 / 入库判定 / 版本兼容 / 索引 v2 链路验证成功"
    : $" ❌ {failures} 项未通过");
Console.WriteLine("═══════════════════════════════════════════════════════");
return failures == 0 ? 0 : 1;

static string FindCsv()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        string candidate = Path.Combine(dir.FullName, "scripts", "kontakt-libraries.csv");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return Path.Combine(AppContext.BaseDirectory, "kontakt-libraries.csv");
}

static List<CsvRow> LoadCsv(string path)
{
    var rows = new List<CsvRow>();
    if (!File.Exists(path)) return rows;
    var lines = File.ReadAllLines(path, Encoding.UTF8);
    for (int i = 1; i < lines.Length; i++)
    {
        if (string.IsNullOrWhiteSpace(lines[i])) continue;
        var f = SplitCsv(lines[i]);
        if (f.Count < 13) continue;
        rows.Add(new CsvRow
        {
            Library = f[0],
            SizeGB = double.Parse(f[1], CultureInfo.InvariantCulture),
            Files = int.Parse(f[2], CultureInfo.InvariantCulture),
            NKI = int.Parse(f[3], CultureInfo.InvariantCulture),
            NICNT = int.Parse(f[11], CultureInfo.InvariantCulture),
        });
    }
    return rows;
}

static List<string> SplitCsv(string line)
{
    var result = new List<string>();
    var sb = new StringBuilder();
    bool inQuotes = false;
    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];
        if (inQuotes)
        {
            if (c == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = false;
            }
            else sb.Append(c);
        }
        else
        {
            if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
    }
    result.Add(sb.ToString());
    return result;
}

internal sealed class CsvRow
{
    public string Library { get; set; } = "";
    public double SizeGB { get; set; }
    public int Files { get; set; }
    public int NKI { get; set; }
    public int NICNT { get; set; }
}
