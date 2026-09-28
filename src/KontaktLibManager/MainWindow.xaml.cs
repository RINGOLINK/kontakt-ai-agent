using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using KontaktLibManager.Core;
using Microsoft.Web.WebView2.Core;
using System.Collections.Concurrent;

namespace KontaktLibManager;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Database? _db;
    private readonly LibraryScanner _scanner = new();
    private CancellationTokenSource? _scanCts;
    private volatile bool _scanning;
    private ScanProgress? _lastProgress;
    private Dictionary<string, RegisteredProduct> _registered = new(StringComparer.OrdinalIgnoreCase);
    private PortableKontaktStore.PortableInfo? _portable;
    private bool _defaultRootEnsured;

    public MainWindow()
    {
        InitializeComponent();
        // 🔴 **把 Windows 原生标题栏切成暗色**（2026-09-28）——
        //   实测问题：程序整体是深色，但顶部那条【原生标题栏】是白的（截图上很突兀）。
        //   ⚠️ 那不是我们的 HTML `.topbar`（它本来就是深色），而是 Windows 的【非客户区】
        //   ⇒ 改 CSS 没用，必须调 DWM：DWMWA_USE_IMMERSIVE_DARK_MODE = 20（旧版是 19）。
        try { EnableDarkTitleBar(); } catch { }
        try { ScriptTools.CleanupAll(); } catch { }
        Loaded += OnLoaded;
        Closed += (_, _) => _scanCts?.Cancel();
    }

    private Database Db => _db ??= CreateDb();


    /// <summary>
    /// **把窗口的原生标题栏切成暗色**（Windows 10 1809+ / Windows 11）。
    ///
    /// 为什么要它：程序整体深色，但【原生标题栏】默认跟随系统亮色 ⇒ 顶上一条白杠很突兀。
    /// ⚠️ 这是 Windows 的【非客户区】，CSS 管不到，只能调 DWM。
    /// ⚠️ 属性号有版本差异：20 = Win10 1809+ 的新值，19 = 早期预览版；都试一遍。
    /// ⚠️ 失败时【静默忽略】—— 老系统没有这个属性，不能让程序因此起不来。
    /// </summary>
    private void EnableDarkTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（1809+）；19 = 早期版本
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private Database CreateDb()
    {
        var db = new Database(AppPaths.DbPath);
        db.EnsureCreated();
        return db;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string wwwroot = AppPaths.WwwRoot;
            if (!Directory.Exists(wwwroot))
            {
                MessageBox.Show($"缺少前端资源目录：{wwwroot}", "Kontakt 音色库管理器",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // **注册表读取必须放后台线程** ——
            // `RefreshRegistry()` 会遍历 `HKLM\SOFTWARE\Native Instruments` 下的**全部子键**
            // （实测 HKLM64 有 3,411 个），逐个打开读值，还会读便携版 Settings.cfg。
            // 原来在 UI 线程同步调用 → 启动时整个窗口卡住（用户报告「打开程序造成系统卡顿」）。
            await Task.Run(() => RefreshRegistry());

            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDir);
            await Web.EnsureCoreWebView2Async(env);

            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "kontakt.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
            // 封面图缓存目录（从 .nicnt 提取的官方横幅），供界面 <img> 直接引用
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "kontakt-covers", CoverStore.Dir, CoreWebView2HostResourceAccessKind.Allow);
            // 知识库目录（说明书页图），供「问问AI」展示例图。
            // 注意：SetVirtualHostNameToFolderMapping 要求目录必须已存在，否则抛
            // DirectoryNotFoundException(0x80070003) 导致整个 WebView2 初始化失败。
            string kbDir = Path.Combine(AppPaths.DataDir, "kb");
            Directory.CreateDirectory(kbDir);
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "kontakt-kb", kbDir, CoreWebView2HostResourceAccessKind.Allow);

            // 试听音频缓存目录：本地目录 + 导航前映射 —— 这条路径已被验证可用。
            // （直接把音色库根目录映射成虚拟主机在运行时注册不生效，故改为缓存后从本地服务；
            //   试听片段都很小（4 KB ~ 1.2 MB），拷贝开销可忽略。）
            Directory.CreateDirectory(AudioCacheDir);
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "kontakt-audio", AudioCacheDir, CoreWebView2HostResourceAccessKind.Allow);
            Task.Run(CleanAudioCache);
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // 测试接缝（仅本地、由环境变量显式开启）：
            //   KLM_START_VIEW=settings  启动即进入设置页
            //   KLM_TEST_SCRIPT=<js>     加载完成后执行一段 JS（自动化验证用）
            string startView = Environment.GetEnvironmentVariable("KLM_START_VIEW") ?? "";
            string testScript = Environment.GetEnvironmentVariable("KLM_TEST_SCRIPT") ?? "";
            if (startView.Length > 0 || testScript.Length > 0)
            {
                Web.CoreWebView2.NavigationCompleted += async (_, _) =>
                {
                    try
                    {
                        if (startView.Length > 0)
                        {
                            await Web.CoreWebView2.ExecuteScriptAsync($"showView('{startView}')");
                            if (startView == "settings")
                                await Web.CoreWebView2.ExecuteScriptAsync("loadSettings()");
                        }
                        if (testScript.Length > 0)
                        {
                            Log($"执行测试脚本：{testScript}");
                            await Web.CoreWebView2.ExecuteScriptAsync(testScript);
                        }
                    }
                    catch (Exception ex) { Log($"测试脚本失败：{ex.Message}"); }
                };
            }

            Web.CoreWebView2.Navigate("https://kontakt.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show("WebView2 初始化失败：\n" + ex, "Kontakt 音色库管理器",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── 数据装配 ───────────────────────────────────────────
    private void RefreshRegistry()
    {
        try { _registered = LibraryRegistrar.ReadRegistered(); }
        catch { _registered = new Dictionary<string, RegisteredProduct>(StringComparer.OrdinalIgnoreCase); }

        // 便携版 Kontakt 的库列表在自己的 UserData\Settings.cfg，需一并读取才能得到真实入库状态
        try
        {
            string exe = Db.GetMeta("kontakt_exe", "");
            _portable = exe.Length > 0 ? PortableKontaktStore.Inspect(exe) : null;
        }
        catch { _portable = null; }
    }

    private HashSet<string>? PortableNames =>
        _portable?.RegisteredNames is { Count: > 0 } names
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : null;

    private string KontaktVersion => Db.GetMeta("kontakt_version", "");

    private List<LibraryRecord> LoadLibraries()
    {
        var list = Db.GetLibraries();
        LibraryRegistrar.Enrich(list, _registered, KontaktVersion, PortableNames);
        return list;
    }

    /// <summary>首次运行时，若未配置任何根目录，则尝试加入默认路径。</summary>
    private void EnsureDefaultRoot()
    {
        if (_defaultRootEnsured) return;
        _defaultRootEnsured = true;
        try
        {
            if (Db.GetRoots().Count == 0 && Directory.Exists(AppPaths.DefaultRoot))
                Db.AddRoot(AppPaths.DefaultRoot, "默认路径");
        }
        catch (Exception ex) { Log($"初始化默认路径失败：{ex.Message}"); }
    }

    // ── JS → C# 桥 ─────────────────────────────────────────
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        long id = 0;
        try
        {
            // JS 侧 postMessage(字符串) 时，WebMessageAsJson 给出的是「JSON 字符串字面量」，
            // 直接 Parse 会得到 String 而非 Object。故优先 TryGetWebMessageAsString。
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch (InvalidOperationException) { raw = e.WebMessageAsJson; }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"桥接消息不是 JSON 对象：{root.ValueKind}");

            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long parsed))
                id = parsed;
            string method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
            JsonElement args = root.TryGetProperty("args", out var aEl) ? aEl.Clone() : default;
            _ = DispatchAsync(id, method, args);
        }
        catch (Exception ex)
        {
            Log($"桥接解析失败（id={id}）：{ex.Message}");
            Reply(id, false, null, ex.Message);
        }
    }

    private static readonly object LogLock = new();
    internal static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(AppPaths.DataDir, "bridge.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private async Task DispatchAsync(long id, string method, JsonElement args)
    {
        try
        {
            EnsureDefaultRoot();
            object? result = method switch
            {
                "ping" => new { pong = true, version = "0.2.0-m2" },
                "config" => GetConfig(),
                "dashboard" => BuildDashboard(),
                "libraries" => LoadLibraries(),

                // 根目录管理
                "roots" => Db.GetRoots(),
                "addRoot" => AddRoot(args),
                "removeRoot" => RemoveRoot(args),
                "setRootEnabled" => SetRootEnabled(args),
                "pickFolder" => PickFolder(args),

                // Kontakt 本体
                "detectKontakt" => DetectKontakt(),
                "kontaktCandidates" => GetKontaktCandidates(),
                "addKontaktCandidate" => AddKontaktCandidate(args),
                "updateKontaktCandidate" => UpdateKontaktCandidate(args),
                "removeKontaktCandidate" => RemoveKontaktCandidate(args),
                "pickKontaktExe" => PickKontaktExe(args),
                "setKontaktExe" => SetKontaktExe(args),
                "clearKontaktExe" => ClearKontaktExe(),
                "launchKontakt" => LaunchKontakt(args),
                "testLibrary" => TestLibrary(args),

                // 扫码与入库
                "status" => new { scanning = _scanning, progress = _lastProgress },
                "scan" => await StartScanAsync(args),
                "cancelScan" => CancelScan(),
                "registration" => BuildRegistrationReport(),
                "registerLibraries" => RegisterLibraries(args),
                "registrationViewIssues" => RegistrationViewIssues(),                 "registrationPromote" => RegistrationPromote(args),                 "registerWithoutNicnt" => RegisterWithoutNicnt(args),
                "generateNicnt" => GenerateNicnt(args),
            // **Nicnt Maker 兼容**：为无 .nicnt 的第三方库生成 .nicnt（SNPID 自动挑，不与已注册/官方表冲突）
            "nicntGenStatus" => NicntGenStatus(),
            "nicntGenMake" => NicntGenMake(args),
            // **孤儿注册表**：ContentDir 指向不存在目录的注册项（预览 / 备份后清理 / 一键还原）
            "orphanPreview" => OrphanPreview(),
            "orphanClean" => OrphanClean(args),
            "orphanRestore" => OrphanRestore(),
                "unregister" => Unregister(args),
                "closeKontakt" => CloseKontakt(),
                "openLibraryManager" => OpenLibraryManager(),
                "writePortableLibraries" => WritePortableLibraries(args),
                "portableInfo" => PortableInfoResult(),
                "compat" => BuildCompatReport(),
                "manuals" => GetManuals(args),
            "manualPreview" => ManualPreview(args),
            "manualPageImage" => ManualPageImage(args),
                "openFile" => OpenFile(args),

                // AI 助手
                "aiSettings" => GetAiSettings(),
                "pathCandidates" => PathCandidates(args),
                "aiSetPermissionTier" => SetPermissionTier(args),
                "setAiSettings" => SetAiSettings(args),
                "testAi" => await TestAi(),
                "kbStatus" => KbStatus(args),
                "kbBuild" => KbBuild(args),
            "kbBuildManual" => KbBuildManual(args),
                "kbDelete" => KbDelete(args),
                "kbSearch" => KbSearch(args),
                "ask" => await AskAsync(args),
                "pageImage" => PageImage(args),
                "autoKb" => AutoKb(args),

                // 移动音色库
                "movePlan" => MovePlanResult(args),
                "moveExecute" => MoveExecute(args),
                "moveDeleteSource" => MoveDeleteSource(args),
                "repairPath" => RepairPath(args),

                // 乐器中心
                "instrumentTree" => Db.GetInstrumentTree().Select(t => new
                {
                    category = t.Category, library = t.Library, libraryId = t.LibraryId, count = t.Count,
                }),
                "instruments" => Instruments(args),
                "openInstrument" => OpenInstrument(args),
                "revealInstrument" => RevealInstrument(args),
                "copyText" => CopyText(args),

                // 试听
                "audioClips" => AudioClips(args),

                // 健康检查 / 杂质清理 / 导出
                "healthIssues" => Db.GetHealthIssues().Select(h => new
                {
                    h.LibraryId, h.LibraryName, h.Kind, h.Severity, h.Detail, h.SizeBytes,
                }),
                "favorites" => GetFavorites(),
                "toggleFavorite" => ToggleFavorite(args),
                "tags" => GetTags(),
                "tagsOf" => GetTagsOf(args),
                "tagsSet" => SetTags(args),
                "tagDelete" => DeleteTag(args),
                "tagRename" => RenameTag(args),
                "healthFixCovers" => HealthFixCovers(),
                "healthFixJunk" => HealthFixJunk(args),
                "coverWriteBack" => CoverWriteBack(args),
                "quickLoadTargets" => QuickLoadTargets(),
                "clientLog" => ClientLog(args),
            "agentTraces" => AgentTraces(args),
                "agentTraceStats" => AgentTraceStats(),
                "agentTraceClear" => new { cleared = Db.ClearTraces() },
                "junkList" => JunkList(args),
                "junkDelete" => JunkDelete(args),
                "exportLibraries" => ExportLibraries(args),
                "snapshotList" => RegistrationSnapshot.List().Select(s => new
                {
                    s.Name, s.CreatedAt, s.Reason, s.ProductCount, s.HasPortable, s.SizeBytes, s.Path,
                }),
                "snapshotCapture" => SnapshotCapture(args),
                "snapshotRestore" => SnapshotRestore(args),
                "snapshotDelete" => SnapshotDelete(args),

                // 问问AI：会话 / 分支 / 工作区
                "chatSessions" => ChatSessions(),
                "chatSessionCreate" => ChatSessionCreate(args),
                "rescanManuals" => RescanManuals(args),
            "chatStats" => ChatStats(args),
            "suggestions" => Suggestions(args),
            "extractAudioFeatures" => ExtractAudioFeatures(args),
            "audioFeatureStatus" => AudioFeatureStatus(),
            "timbreFilter" => TimbreFilterRpc(args),
            "similarToClip" => SimilarToClipRpc(args),
            "semanticFilter" => SemanticFilterRpc(args),
            "timbreDims" => TimbreDims(),
            "timbreValues" => TimbreValues(),
            "cpuInfo" => CpuInfo(),
            "audioMapStatus" => AudioMapStatus(),
            "audioMapPause" => AudioMapPause(),
            "audioMapStart" => AudioMapStart(args),
            "audioMapRebuild" => AudioMapRebuild(args),
            "audioMapBuildInstruments" => AudioMapBuildInstruments(),
            "audioMapDetail" => AudioMapDetail(args),
            "exportMapSelection" => ExportMapSelection(args),
            "audioFeatureStats" => AudioFeatureStats(),
            "chatStop" => ChatStop(args),
            "chatSteer" => ChatSteer(args),
            "chatSessionOpen" => ChatSessionOpen(args),
                "chatSessionDelete" => ChatSessionDelete(args),
                "chatSessionRename" => ChatSessionRename(args),
                "chatBranchCreate" => ChatBranchCreate(args),
                "chatBranchSwitch" => ChatBranchSwitch(args),
                "chatBranchDelete" => ChatBranchDelete(args),
                // 🔴 **把整轮 Agent 工作挪到【后台线程】（2026-09-25 修「工具执行时界面无响应」）** ——
                //   原来直接 `await ChatAsk(args)` ⇒ await 之后的所有续体都在【UI 线程】执行，
                //   而工具执行、JSON 解析、字符串拼接等 CPU 活都会压住 UI ⇒ 界面卡住（用户实测反馈：
                //   「Agent 在调用工具、运行脚本、读取文件时会导致暂时无响应」）。
                //   ✅ `Task.Run` 把整轮丢到线程池；`Push` 走的是 `Dispatcher.BeginInvoke`，
                //      本来就线程安全，所以事件推送不受影响。
                "chatAsk" => await Task.Run(() => ChatAsk(args)),
                "manualsAll" => ManualsAll(),
                "kbAll" => KbAll(),
                "kbView" => KbView(args),
                "kbBuildAsync" => KbBuildAsync(args),
        "kbBuildAll" => KbBuildAll(args),
        "kbBatchCancel" => KbBatchCancel(),
                "shellDecision" => ShellDecision(args),
                "captureScreen" => CaptureScreen(),
                "captureWindow" => CaptureWindow(),
                "memList" => MemList(),
                "memAdd" => MemAdd(args),
                "memUpdate" => MemUpdate(args),
                "memDelete" => MemDelete(args),

                // 重复内容检测
                "dupScan" => DupScan(args),
                "dupSimilar" => DupSimilar(),
                "dupTransferReg" => DupTransferReg(args),
                "dupDeleteLibrary" => DupDeleteLibrary(args),
                "dupDiff" => DupDiff(args),
                "dupRegisterLibrary" => DupRegisterLibrary(args),
                "addToQuickLoad" => AddToQuickLoad(args),
                "removeFromQuickLoad" => RemoveFromQuickLoad(args),

                // 封面编辑器
                "pickImage" => PickImage(),
                "readImage" => ReadImage(args),
                "saveCover" => SaveCover(args),

                "openPath" => OpenPath(args),

                _ => throw new InvalidOperationException($"未知方法：{method}"),
            };
            Reply(id, true, result);
        }
        catch (Exception ex)
        {
            Log($"方法 {method} 执行失败：{ex.Message}");
            Reply(id, false, null, ex.Message);
        }
    }

    // ── 设置 / 概览 ────────────────────────────────────────
    private object GetConfig()
    {
        RefreshRegistry();
        return new
        {
            dbPath = AppPaths.DbPath,
            defaultRoot = AppPaths.DefaultRoot,
            roots = Db.GetRoots(),
            kontaktExe = Db.GetMeta("kontakt_exe", ""),
            kontaktVersion = KontaktVersion,
            isAdmin = KontaktInfo.IsAdmin(),
            kontaktRunning = KontaktInfo.IsRunning(),
        };
    }

    private object BuildDashboard()
    {
        EnsureDefaultRoot();
        var data = Db.GetDashboard();
        LibraryRegistrar.Enrich(data.TopLibraries, _registered, KontaktVersion);
        return new
        {
            data.HasData,
            data.Root,
            data.RootCount,
            data.LastScannedAt,
            data.LibraryCount,
            data.TotalBytes,
            data.TotalFiles,
            data.InstrumentCount,
            data.InstrumentNamedFromHeader,
            data.LibrariesWithoutNicnt,
            data.JunkCount,
            data.JunkBytes,
            data.LegacyInstrumentCount,
            data.LastScanSeconds,
            data.Categories,
            data.TopLibraries,
            kontaktExe = Db.GetMeta("kontakt_exe", ""),
            kontaktVersion = KontaktVersion,
            isAdmin = KontaktInfo.IsAdmin(),
            compat = BuildCompatSummary(),
        };
    }

    // ── 根目录管理 ─────────────────────────────────────────
    private object AddRoot(JsonElement args)
    {
        string path = GetString(args, "path");
        string label = GetString(args, "label");
        var (ok, msg, _) = Db.AddRoot(path, label);
        return new { ok, message = msg, roots = Db.GetRoots() };
    }

    private object RemoveRoot(JsonElement args)
    {
        long id = GetLong(args, "id");
        var (ok, msg) = Db.RemoveRoot(id);
        return new { ok, message = msg, roots = Db.GetRoots() };
    }

    private object SetRootEnabled(JsonElement args)
    {
        long id = GetLong(args, "id");
        bool enabled = GetBool(args, "enabled");
        var (ok, msg) = Db.SetRootEnabled(id, enabled);
        return new { ok, message = msg, roots = Db.GetRoots() };
    }

    private object PickFolder(JsonElement args)
    {
        string initial = GetString(args, "initial");
        string? picked = null;
        Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 Kontakt 音色库根目录",
                Multiselect = false,
            };
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                dlg.InitialDirectory = initial;
            if (dlg.ShowDialog(this) == true) picked = dlg.FolderName;
        });
        return new { picked = picked != null, path = picked ?? "" };
    }

    // ── Kontakt 本体 ───────────────────────────────────────
    // ══════════════════ 收藏与标签 RPC ══════════════════

    private object GetFavorites() => new { ids = Db.GetFavoriteIds().ToArray() };

    private object ToggleFavorite(JsonElement args)
    {
        long id = GetLong(args, "instrumentId");
        if (id <= 0) return new { ok = false, message = "缺少 instrumentId" };
        bool now = Db.ToggleFavorite(id);
        return new { ok = true, instrumentId = id, favorite = now };
    }

    private object GetTags() => new
    {
        tags = Db.GetAllTags().Select(t => new { t.Name, t.Count }),
        // 一次性把「音色库」的标签全带上，前端渲染列表时不必逐行查询
        libraryTags = Db.GetTagsForAll("library"),
    };

    private object GetTagsOf(JsonElement args)
    {
        string type = GetString(args, "entityType");
        if (type.Length == 0) type = "library";
        long id = GetLong(args, "entityId");
        return new { entityType = type, entityId = id, tags = Db.GetEntityTags(type, id) };
    }

    private object SetTags(JsonElement args)
    {
        string type = GetString(args, "entityType");
        if (type.Length == 0) type = "library";
        long id = GetLong(args, "entityId");
        if (id <= 0) return new { ok = false, message = "缺少 entityId" };

        var names = new List<string>();
        if (args.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
                if (it.ValueKind == JsonValueKind.String) names.Add(it.GetString() ?? "");

        int n = Db.SetEntityTags(type, id, names);
        Log($"[标签] {type}#{id} → {string.Join("、", names)}（{n} 条关联）");
        return new { ok = true, entityType = type, entityId = id, tags = Db.GetEntityTags(type, id) };
    }

    private object DeleteTag(JsonElement args)
    {
        string name = GetString(args, "name");
        if (name.Length == 0) return new { ok = false, message = "缺少标签名" };
        int n = Db.DeleteTag(name);
        return new { ok = n > 0, message = n > 0 ? $"已删除标签「{name}」" : "未找到该标签" };
    }

    
private object RenameTag(JsonElement args)
    
{
    
    string from = GetString(args, "from");
    
    string to = GetString(args, "to");
    
    var (ok, msg) = Db.RenameTag(from, to);
    
    if (ok) Log($"[标签] 重命名 {from} → {to}：{msg}");
    
    return new { ok, message = msg };
    
}

    // ══════════════════ 健康检查：一键修复 ══════════════════

    /// <summary>
    /// **补全缺失的封面图**。
    ///
    /// 健康检查里 `no-cover` 一类是可修的：扫描时只看了库根目录的 .nicnt，
    /// 有些库的 .nicnt 在子目录里（或封面图直接以图片文件形式放在目录中），
    /// 所以这里做两轮补救：
    ///   ① 递归（深 4 层）找 .nicnt → ExtractArtwork → 存进 covers/
    ///   ② 找目录里的常见封面图文件（folder.jpg / cover.* / artwork.* / front.* 等）
    ///
    /// **只读库目录、只写应用的 covers/ 与数据库** —— 绝不修改音色库里的任何文件。
    /// </summary>
    private object HealthFixCovers()
    {
        var targets = Db.GetLibraries().Where(l => l.CoverFile.Length == 0).ToList();
        var fixedList = new List<object>();
        int ok = 0, fail = 0;

        string[] coverNames = { "folder", "cover", "artwork", "front", "thumb", "poster", "box" };
        string[] coverExts = { ".jpg", ".jpeg", ".png", ".webp" };

        foreach (var lib in targets)
        {
            if (string.IsNullOrEmpty(lib.Path) || !Directory.Exists(lib.Path)) { fail++; continue; }
            byte[]? art = null;
            string source = "";

            // ① 递归找 .nicnt 取内嵌横幅
            try
            {
                foreach (var n in NicntReader.FindInLibrary(lib.Path, 4))
                {
                    var a = NicntReader.ExtractArtwork(n.FilePath);
                    if (a != null && a.Length > 0) { art = a; source = ".nicnt"; break; }
                }
            }
            catch { }

            // ② 目录里的图片文件
            if (art == null)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(lib.Path, "*", SearchOption.AllDirectories))
                    {
                        string nm = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                        string ex = Path.GetExtension(f).ToLowerInvariant();
                        if (!coverExts.Contains(ex)) continue;
                        if (!coverNames.Contains(nm)) continue;
                        var b = File.ReadAllBytes(f);
                        if (b.Length is > 0 and < 8_000_000) { art = b; source = Path.GetFileName(f); break; }
                    }
                }
                catch { }
            }

            if (art == null) { fail++; continue; }
            try
            {
                string fn = CoverStore.Save(lib.Path, art);
                if (fn.Length == 0) { fail++; continue; }
                Db.SetLibraryCover(lib.Id, fn);
                ok++;
                fixedList.Add(new { id = lib.Id, name = lib.Name, source });
            }
            catch { fail++; }
        }

        Log($"[健康修复] 封面补全：成功 {ok} 个，未找到 {fail} 个");
        return new
        {
            ok = true,
            total = targets.Count,
            fixedCount = ok,
            notFound = fail,
            items = fixedList,
            message = $"补全封面：成功 {ok} 个（共检查 {targets.Count} 个缺封面的库），{fail} 个未找到可用图源。",
        };
    }

    /// <summary>
    /// **一键清理全部杂质文件**（健康检查里 `junk` 一类的修复动作）。
    /// 复用 junkDelete 的删除逻辑，默认**删到回收站**（可恢复）。
    /// </summary>
    private object HealthFixJunk(JsonElement args)
    {
        bool toRecycle = !GetBool(args, "permanent");   // 默认回收站
        var all = Db.GetJunkFiles();
        if (all.Count == 0) return new { ok = true, deleted = 0, message = "没有杂质文件需要清理" };

        long bytes = all.Sum(j => j.SizeBytes);
        var ids = all.Select(j => j.Id).ToList();
        var payload = System.Text.Json.JsonSerializer.Serialize(new { ids, recycle = toRecycle });
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var r = JunkDelete(doc.RootElement);
        Log($"[健康修复] 一键清理杂质：{all.Count} 个文件 / {bytes / 1048576.0:F1} MB（回收站={toRecycle}）");
        return new
        {
            ok = true,
            count = all.Count,
            bytes,
            detail = r,
            message = $"已清理 {all.Count} 个杂质文件（约 {bytes / 1048576.0:F1} MB）" + (toRecycle ? "，文件已放入回收站可恢复。" : "。"),
        };
    }

    // ══════════════════ 封面写回 .nicnt ══════════════════

    /// <summary>
    /// **把应用里保存的封面写回 `.nicnt` 的内嵌横幅**（让 Kontakt 的 Libraries 页也显示自定义封面）。
    ///
    /// ⚠ 这是本项目里**唯一会修改音色库自身文件**的操作，所以安全措施是最高一级：
    ///   ① **尺寸必须完全一致** —— `.nicnt` 里的横幅是固定尺寸（通常 905×99），
    ///      Kontakt 会校验结构；所以先把新封面**缩放到与原横幅完全相同的像素尺寸**；
    ///   ② **写前强制备份** 原 `.nicnt` 为 `<名>.nicnt.klm-backup-<时间戳>`；
    ///   ③ **写后校验** —— 重新按 `.nicnt` 解析链路读一遍，确认 PNG 区间、ProductHints 都还在；
    ///   ④ **失败自动回滚** —— 任何一步出错立即从备份还原，并如实报告；
    ///   ⑤ **只动 PNG 那一段字节** —— 其余内容（授权字段 HU/JDX、ProductHints XML）原样保留，
    ///      **不伪造、不修改任何授权字段**。
    /// </summary>
    private object CoverWriteBack(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };
        if (lib.CoverFile.Length == 0) return new { ok = false, message = "该库还没有封面图，请先添加封面" };

        string coverPath = Path.Combine(CoverStore.Dir, lib.CoverFile);
        if (!File.Exists(coverPath)) return new { ok = false, message = $"封面文件不存在：{coverPath}" };

        // ① **主机制：在库目录写 `Wallpaper.png`（905×99，RGB 无 Alpha）** ——
        //    这是 Kontakt 8 库列表横幅的**真正来源**（2026-09-20 实测确认）。
        //    其余方案（.nicnt 内嵌 PNG / Kontakt 8\PAResources / pal.db.rsrc_cache /
        //    注册表 / NI Resources / 文件夹名大小写 / 手工造 LibrariesCache）**均已验证无效或非必需**。
        string wpPath = Path.Combine(lib.Path, "Wallpaper.png");
        try
        {
            byte[] wpPng = FlattenToRgb905x99(File.ReadAllBytes(coverPath));
            if (File.Exists(wpPath))
                File.Copy(wpPath, wpPath + ".klm-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            File.WriteAllBytes(wpPath, wpPng);
            Log($"[封面] {lib.Name} → Wallpaper.png（{wpPng.Length} 字节，905×99 RGB）");
        }
        catch (Exception ex)
        {
            return new { ok = false, message = "写入 Wallpaper.png 失败：" + ex.Message };
        }
        string wpNote = "已写入库目录的 Wallpaper.png（905×99 RGB）";

        var nicnts = NicntReader.FindInLibrary(lib.Path, 3);
        if (nicnts.Count == 0)
            return new
            {
                ok = true,
                written = 1,
                failed = 0,
                items = Array.Empty<object>(),
                message = wpNote + "。该库没有 .nicnt，未做 .nicnt 写回。重启 Kontakt 后可见。",
            };

        byte[]? newCover = File.ReadAllBytes(coverPath);
        var results = new List<object>();
        int okCount = 0, failCount = 0;

        foreach (var n in nicnts)
        {
            string path = n.FilePath;
            string backup = path + ".klm-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                byte[] original = File.ReadAllBytes(path);

                // 定位原有 PNG 区间
                ReadOnlySpan<byte> pngSig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                ReadOnlySpan<byte> iendSig = stackalloc byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
                int start = FindSeq(original, pngSig);
                if (start < 0) { failCount++; results.Add(new { file = Path.GetFileName(path), error = "原 .nicnt 里没有 PNG 横幅" }); continue; }
                int end = FindSeq(original, iendSig, start);
                if (end < 0) { failCount++; results.Add(new { file = Path.GetFileName(path), error = "PNG 区间不完整" }); continue; }
                end += iendSig.Length;

                int oldW = PngWidth(original, start), oldH = PngHeight(original, start);
                int newW = PngWidth(newCover, 0), newH = PngHeight(newCover, 0);
                if (oldW <= 0 || oldH <= 0 || newW <= 0 || newH <= 0)
                {
                    failCount++; results.Add(new { file = Path.GetFileName(path), error = "无法读取尺寸" }); continue;
                }

                // 尺寸必须一致：不一致就先缩放
                byte[] useCover = newCover;
                if (oldW != newW || oldH != newH)
                    useCover = ResizePng(newCover, oldW, oldH);

                // 备份 → 替换 → 校验
                File.Copy(path, backup, true);
                var outp = new byte[original.Length - (end - start) + useCover.Length];
                Array.Copy(original, 0, outp, 0, start);
                Array.Copy(useCover, 0, outp, start, useCover.Length);
                Array.Copy(original, end, outp, start + useCover.Length, original.Length - end);
                File.WriteAllBytes(path, outp);

                // 校验：PNG 还在 + .nicnt 仍能解析
                var verifyPng = NicntReader.ExtractArtwork(path);
                var verifyInfo = NicntReader.Read(path);
                if (verifyPng == null || verifyInfo == null)
                {
                    File.Copy(backup, path, true);
                    failCount++;
                    results.Add(new { file = Path.GetFileName(path), error = "校验失败，已自动回滚" });
                    continue;
                }

                okCount++;
                results.Add(new
                {
                    file = Path.GetFileName(path),
                    size = $"{oldW}x{oldH}",
                    resized = oldW != newW || oldH != newH,
                    backup = Path.GetFileName(backup),
                });
                Log($"[封面写回] {lib.Name} → {Path.GetFileName(path)}（{oldW}x{oldH}，原文件已备份）");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(backup)) File.Copy(backup, path, true); } catch { }
                failCount++;
                results.Add(new { file = Path.GetFileName(path), error = ex.Message + "（已尝试回滚）" });
            }
        }

        return new
        {
            ok = okCount > 0,
            written = okCount,
            failed = failCount,
            items = results,
            message = okCount > 0
                ? wpNote + $"，并额外写回 {okCount} 个 .nicnt（原文件已备份为 .klm-backup-*，失败会自动回滚）。重启 Kontakt 后可见。"
                : wpNote + "（.nicnt 写回失败，未做任何修改）。重启 Kontakt 后可见。",
        };
    }

    /// <summary>
    /// 把任意 PNG 拉伸/压扁为 **905×99 的 24bpp RGB（PNG 颜色类型 2，无 Alpha）**。
    /// Kontakt 的库列表横幅要求这个尺寸与格式（实测：带 Alpha 的 RGBA 不被接受）。
    /// </summary>
    private static byte[] FlattenToRgb905x99(byte[] png)
    {
        const int W = 905, H = 99;
        using var ms = new MemoryStream(png);
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
            ms,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 先铺黑底，压掉透明通道（避免透明区域在 RGB 下变白/变乱）
            dc.DrawRectangle(System.Windows.Media.Brushes.Black, null,
                new System.Windows.Rect(0, 0, W, H));
            dc.DrawImage(frame, new System.Windows.Rect(0, 0, W, H));
        }

        // RenderTargetBitmap 只支持 Pbgra32/Bgra32 等，**不支持 Bgr24** ——
        // 故先按 Pbgra32 渲染，再用 FormatConvertedBitmap 转成 Bgr24（PNG 颜色类型 2）。
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);

        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            rtb, System.Windows.Media.PixelFormats.Bgr24, null, 0);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(converted));
        using var outMs = new MemoryStream();
        enc.Save(outMs);
        return outMs.ToArray();
    }

    private static int FindSeq(byte[] hay, ReadOnlySpan<byte> needle, int from = 0)
    {
        for (int i = from; i <= hay.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>从 PNG 的 IHDR 块读宽度（PNG 头：8 字节签名 + 4 长度 + "IHDR" + 宽 4 字节大端）。</summary>
    private static int PngWidth(byte[] b, int off) => ReadBeInt(b, off + 16);
    private static int PngHeight(byte[] b, int off) => ReadBeInt(b, off + 20);

    private static int ReadBeInt(byte[] b, int i)
    {
        if (i < 0 || i + 3 >= b.Length) return 0;
        return (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    }

    /// <summary>把 PNG 缩放到指定像素尺寸（保持拉伸填充，因为横幅比例是固定的）。</summary>
    private static byte[] ResizePng(byte[] src, int w, int h)
    {
        using var ms = new MemoryStream(src);
        using var img = System.Drawing.Image.FromStream(ms);
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, 0, 0, w, h);
        }
        using var outMs = new MemoryStream();
        bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
        return outMs.ToArray();
    }

    private object DetectKontakt()
    {
        var scan = KontaktInfo.Detect();
        var candidates = KontaktCandidateStore.Merge(Db, scan);
        return new
        {
            candidates,
            ignored = scan.Ignored,
            currentExe = Db.GetMeta("kontakt_exe", ""),
            currentVersion = KontaktVersion,
            isAdmin = scan.IsAdmin,
            kontaktRunning = scan.KontaktRunning,
        };
    }

    private object GetKontaktCandidates()
    {
        return new
        {
            candidates = KontaktCandidateStore.Merge(Db, KontaktInfo.Detect()),
            currentExe = Db.GetMeta("kontakt_exe", ""),
            currentVersion = KontaktVersion,
        };
    }

    private object AddKontaktCandidate(JsonElement args)
    {
        var (ok, msg) = KontaktCandidateStore.Add(Db, GetString(args, "path"));
        return new { ok, message = msg, candidates = KontaktCandidateStore.Merge(Db, KontaktInfo.Detect()) };
    }

    private object UpdateKontaktCandidate(JsonElement args)
    {
        var (ok, msg) = KontaktCandidateStore.Update(Db, GetString(args, "path"), GetString(args, "newPath"));
        return new
        {
            ok,
            message = msg,
            candidates = KontaktCandidateStore.Merge(Db, KontaktInfo.Detect()),
            currentExe = Db.GetMeta("kontakt_exe", ""),
            currentVersion = KontaktVersion,
        };
    }

    private object RemoveKontaktCandidate(JsonElement args)
    {
        var (ok, msg) = KontaktCandidateStore.Remove(Db, GetString(args, "path"));
        return new
        {
            ok,
            message = msg,
            candidates = KontaktCandidateStore.Merge(Db, KontaktInfo.Detect()),
            currentExe = Db.GetMeta("kontakt_exe", ""),
            currentVersion = KontaktVersion,
        };
    }

    private object PickKontaktExe(JsonElement args)
    {
        string? picked = null;
        Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Kontakt 主程序（Kontakt N.exe，不要选安装包）",
                Filter = "Kontakt 主程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            string current = Db.GetMeta("kontakt_exe", "");
            if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
                dlg.InitialDirectory = Path.GetDirectoryName(current) ?? "";
            if (dlg.ShowDialog(this) == true) picked = dlg.FileName;
        });
        return new { picked = picked != null, path = picked ?? "" };
    }

    private object SetKontaktExe(JsonElement args)
    {
        string path = GetString(args, "path").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) return new { ok = false, message = "路径为空" };
        if (!File.Exists(path)) return new { ok = false, message = $"文件不存在：{path}" };

        // 使用某程序时一并纳入候选列表，便于后续编辑/切换
        KontaktCandidateStore.Add(Db, path);

        string version = KontaktInfo.ReadVersion(path);
        Db.SetMeta("kontakt_exe", path);
        Db.SetMeta("kontakt_version", version);
        return new
        {
            ok = true,
            message = string.IsNullOrEmpty(version) ? "已设置路径（未能识别版本号）" : $"已设置：Kontakt {version}",
            exe = path,
            version,
        };
    }

    private object ClearKontaktExe()
    {
        Db.SetMeta("kontakt_exe", "");
        Db.SetMeta("kontakt_version", "");
        return new { ok = true, message = "已清除 Kontakt 路径" };
    }

    private object LaunchKontakt(JsonElement args)
    {
        string exe = Db.GetMeta("kontakt_exe", "");
        string file = GetString(args, "file");
        var (ok, msg) = KontaktInfo.Launch(exe, string.IsNullOrWhiteSpace(file) ? null : file);
        return new { ok, message = msg };
    }

    private object TestLibrary(JsonElement args)
    {
        long id = GetLong(args, "id");
        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == id);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };

        string exe = Db.GetMeta("kontakt_exe", "");
        if (string.IsNullOrWhiteSpace(exe))
            return new { ok = false, message = "请先在设置中指定 Kontakt 主程序路径" };

        // 优先用库内第一个 NKI 做加载测试
        string? target = Db.GetFirstInstrumentPath(id);
        var (ok, msg) = KontaktInfo.Launch(exe, target);
        return new
        {
            ok,
            message = ok
                ? (target != null
                    ? $"已调用 Kontakt 打开测试乐器：{Path.GetFileName(target)}"
                    : $"该库内未找到 .nki，仅启动了 Kontakt")
                : msg,
        };
    }

    // ── 扫描 ───────────────────────────────────────────────
    private object CancelScan()
    {
        _scanCts?.Cancel();
        return new { cancelled = true };
    }

    private Task<object> StartScanAsync(JsonElement args)
    {
        if (_scanning)
            return Task.FromResult<object>(new { started = false, reason = "已有扫描正在进行中" });

        var allRoots = Db.GetRoots();
        var roots = allRoots.Where(r => r.Enabled && Directory.Exists(r.Path)).ToList();

        if (roots.Count == 0)
        {
            string reason = allRoots.Count == 0
                ? "尚未添加任何音色库路径，请先在设置中添加"
                : "已启用的路径都不存在（可能是硬盘未连接）";
            return Task.FromResult<object>(new { started = false, reason });
        }

        // 扫描中若写注册表可能被 Kontakt 退出时覆盖，故提示但不阻止
        _scanning = true;
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _lastProgress = null;

        var progress = new Progress<ScanProgress>(p =>
        {
            _lastProgress = p;
            Push(new { type = "scanProgress", progress = p });
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _scanner.ScanAsync(roots, progress, cts.Token);
                Db.SaveScanResult(result);
                RefreshRegistry();
                Push(new
                {
                    type = "scanDone",
                    summary = new
                    {
                        root = result.Root,
                        roots = result.Roots.Count,
                        libraries = result.Libraries.Count,
                        instruments = result.TotalInstruments,
                        files = result.TotalFiles,
                        bytes = result.TotalBytes,
                        seconds = Math.Round(result.ElapsedSeconds, 2),
                        errors = result.Errors,
                    },
                });
            }
            catch (OperationCanceledException)
            {
                Push(new { type = "scanError", error = "扫描已取消" });
            }
            catch (Exception ex)
            {
                Log($"扫描失败：{ex}");
                Push(new { type = "scanError", error = ex.Message });
            }
            finally
            {
                _scanning = false;
                _scanCts = null;
            }
        });

        return Task.FromResult<object>(new { started = true, roots = roots.Count, reason = "" });
    }

    // ── 入库管理 ───────────────────────────────────────────
    private RegistrationReport BuildRegistrationReport()
    {
        RefreshRegistry();
        var libs = LoadLibraries();
        var report = new RegistrationReport
        {
            Total = libs.Count,
            Registered = libs.Count(l => l.RegStatus == "registered"),
            Incomplete = libs.Count(l => l.RegStatus == "incomplete"),
            PendingManager = libs.Count(l => l.RegStatus == "pending-manager"),
            PathMismatch = libs.Count(l => l.RegStatus is "path-mismatch" or "partial"),
            Missing = libs.Count(l => l.RegStatus == "missing"),
            NonStandard = libs.Count(l => l.RegStatus == "non-standard"),
            IsAdmin = KontaktInfo.IsAdmin(),
            KontaktRunning = KontaktInfo.IsRunning(),
            ServiceCenterDir = LibraryRegistrar.ServiceCenterDir(),
            PortableRoot = _portable?.Root ?? "",
            PortableSettingsCfg = _portable?.SettingsCfg ?? "",
            PortableSettingsCfgModified = _portable?.SettingsCfgModified.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            LibraryManagerPath = _portable?.LibraryManager ?? "",
            PortableLibraryCount = _portable?.RegisteredNames.Count ?? 0,
            RegistryNote = KontaktInfo.IsAdmin()
                ? "已具备管理员权限，可写入注册表与 Service Center 记录。"
                : "当前非管理员权限，入库操作需要以管理员身份重启本工具。",
            Items = libs,
        };
        return report;
    }

        /// <summary>
        /// **扫描「注册表视图错位」的库**：注册项存在但不在 HKLM 64 位视图里。
        /// Kontakt 8 是 64 位程序，只读 64 位视图 → 这些库在界面上会报
        /// 「Library not found. Click 'Manage Libraries'…」。
        /// </summary>
        private object RegistrationViewIssues()
        {
            var list = LibraryRegistrar.FindNotInHklm64();
            var items = new List<object>();
            foreach (var (key, cd, in32, inCu) in list)
            {
                bool dirOk = !string.IsNullOrEmpty(cd) && Directory.Exists(cd);
                items.Add(new
                {
                    regKey = key,
                    contentDir = cd,
                    inHklm32 = in32,
                    inHkcu = inCu,
                    dirExists = dirOk,
                });
            }
            return new
            {
                ok = true,
                count = items.Count,
                // 只把「目录真实存在」的算作可修复 —— 目录都没了就没意义
                fixable = items.Cast<dynamic>().Count(x => (bool)x.dirExists),
                items,
                hint = "这些库的注册项只在 32 位视图/HKCU 里，Kontakt 8（64 位）读不到。用 registrationPromote 一键提升到 64 位视图。",
            };
        }

        /// <summary>把「视图错位」的库提升到 HKLM 64 位视图（可只提升指定键）。</summary>
        private object RegistrationPromote(JsonElement args)
        {
            if (!KontaktInfo.IsAdmin())
                return new { ok = false, message = "需要管理员权限：请右键以管理员身份运行本工具" };

            bool overwrite = GetBool(args, "overwrite");

            // 指定键名则只处理这些，否则处理全部可修复项
            var only = new List<string>();
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("keys", out var ke) && ke.ValueKind == JsonValueKind.Array)
                foreach (var el in ke.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String) only.Add(el.GetString() ?? "");

            var targets = LibraryRegistrar.FindNotInHklm64();
            int done = 0, skipped = 0, failed = 0;
            var msgs = new List<string>();

            foreach (var (key, cd, _, _) in targets)
            {
                if (only.Count > 0 && !only.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(cd) || !Directory.Exists(cd)) { skipped++; continue; }
                var (ok, msg) = LibraryRegistrar.PromoteToHklm64(key, overwrite);
                if (ok) { done++; if (msgs.Count < 8) msgs.Add(msg); }
                else { failed++; if (msgs.Count < 8) msgs.Add(msg); }
            }

            RefreshRegistry();
            return new
            {
                ok = failed == 0,
                promoted = done,
                skipped,
                failed,
                messages = msgs,
                message = $"已提升 {done} 个到 64 位视图" + (skipped > 0 ? $"，跳过 {skipped} 个（目录不存在）" : "") + (failed > 0 ? $"，失败 {failed} 个" : ""),
            };
        }

        /// <summary>
        /// **为没有 .nicnt 的库生成注册文件**（.nicnt + Service Center XML，含 UPID 与分配的 SNPID）。
        /// 用于第三方/自制库 —— 它们缺少 Kontakt 认的注册元数据。
        /// </summary>
        private object GenerateNicnt(JsonElement args)
        {
            var ids = new List<long>();
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ids", out var ie) && ie.ValueKind == JsonValueKind.Array)
                foreach (var el in ie.EnumerateArray())
                    if (el.TryGetInt64(out long v)) ids.Add(v);

            bool overwrite = GetBool(args, "overwrite");
            var libs = Db.GetLibraries();
            var targets = ids.Count > 0
                ? libs.Where(l => ids.Contains(l.Id)).ToList()
                : libs.Where(l => !l.HasNicnt).Take(20).ToList();   // 不传 ids 时只做前 20 个，避免误伤

            var results = new List<object>();
            int ok = 0, fail = 0;
            foreach (var lib in targets)
            {
                var (o, msg, snp, upid, nicntPath, scPath) =
                    NicntWriter.GenerateFor(lib.Name, lib.Path, null, overwrite);
                if (o) ok++; else fail++;
                results.Add(new { library = lib.Name, ok = o, message = msg, snpId = snp, upid, nicntPath, scPath });
            }

            RefreshRegistry();
            return new
            {
                ok = fail == 0,
                generated = ok,
                failed = fail,
                results,
                message = $"已生成 {ok} 个注册文件组" + (fail > 0 ? $"，失败 {fail} 个" : "") +
                          "。生成后请重新入库（写注册表+NA记录），再启动 Kontakt 验证。",
            };
        }

        /// <summary>
        /// **SNPID 占用情况**：读 Service Center 已注册的 SNPID + 官方 SNPID 表，并给出一个可用的候选。
        /// 供界面在生成 `.nicnt` 前展示，用户无需自己一个个试。
        /// </summary>
        private object NicntGenStatus()
        {
            var (used, sc, off, ck, fn, note) = NicntGenerator.CollectUsedSnpIds(Db.GetRoots().Select(r => r.Path));
            var (free, msg) = NicntGenerator.PickFreeSnpId(used);
            return new
            {
                ok = free.Length > 0,
                totalUsed = used.Count,
                fromServiceCenter = sc,
                fromOfficialList = off,
                fromContentKey = ck,
                fromNicntFiles = fn,
                suggested = free,
                officialListPath = NicntGenerator.OfficialSnpIdListPath,
                officialListExists = File.Exists(NicntGenerator.OfficialSnpIdListPath),
                officialListIsBuiltin = NicntGenerator.OfficialListIsBuiltin,
                serviceCenterDir = NicntGenerator.ServiceCenterDir,
                serviceCenterXmlCount = Directory.Exists(NicntGenerator.ServiceCenterDir)
                    ? Directory.GetFiles(NicntGenerator.ServiceCenterDir, "*.xml").Length : 0,
                tailTemplateExists = File.Exists(NicntGenerator.TailTemplatePath),
                message = msg + (note.Length > 0 ? "（" + note + "）" : ""),
            };
        }

        /// <summary>
        /// **为无 `.nicnt` 的第三方库生成 `.nicnt`（Nicnt Maker 兼容格式）**。
        /// 参数：`libraryId` 或（`libraryPath` + `name`）、`company`、`snpid`（可空 = 自动挑）。
        /// </summary>
        private object NicntGenMake(JsonElement args)
        {
            string path = GetString(args, "libraryPath");
            string name = GetString(args, "name");
            string company = GetString(args, "company");
            string snpid = GetString(args, "snpid");

            long libId = GetLong(args, "libraryId");
            if (libId > 0)
            {
                var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
                if (lib == null) return new { ok = false, message = "未找到该音色库" };
                if (path.Length == 0) path = lib.Path;
                if (name.Length == 0) name = lib.Name;
            }
            if (path.Length == 0 || name.Length == 0)
                return new { ok = false, message = "需要提供库目录与库名" };
            if (company.Length == 0) company = "3rd Party";

            var (ok, snp, file, msg) = NicntGenerator.GenerateFor(path, company, name, snpid.Length > 0 ? snpid : null);
            Log($"[生成 .nicnt] {(ok ? "成功" : "失败")} {name} → {msg}");

            // 生成成功后直接入库。
            // **必须用「刚生成的 .nicnt」走完整 Register** —— 早前误用 RegisterWithoutNicnt，
            // 它不读 .nicnt（自己从注册表/LibraryHints 拼元数据），导致：
            //   ① SC XML 里的 SNPID 与 .nicnt 里的不一致（实测 .nicnt=Z00、SC XML=D00）；
            //   ② **根本不写 `Content\k2lib0<SNPID>` 键** → Kontakt 认不出库；
            //   ③ 于是下次查重仍认为该 SNPID 空闲，重复分配。
            string regMsg = "";
            if (ok)
            {
                try
                {
                    string nicntPath = Path.Combine(path, file);
                    var info = NicntReader.Read(nicntPath);
                    (bool rok, string rmsg) = info != null
                        ? LibraryRegistrar.Register(info)
                        : LibraryRegistrar.RegisterWithoutNicnt(name, path);
                    regMsg = rok
                        ? $"，并已完成入库（SNPID {snp}，Content 键 k2lib0{snp}）"
                        : "，但入库失败：" + rmsg;
                }
                catch (Exception ex) { regMsg = "，但入库异常：" + ex.Message; }
                RefreshRegistry();
            }

            return new
            {
                ok,
                snpid = snp,
                file,
                message = msg + regMsg,
            };
        }

        // ═══════════════ 孤儿注册表：预览 / 清理 / 还原 ═══════════════

        /// <summary>备份目录（与 docs 同级，按时间戳分目录）。</summary>
        private static string OrphanBackupDir => Path.Combine(AppPaths.DataDir, "orphan-backups");

        /// <summary>
        /// 扫描三视图，找出 **Kontakt 音色库** 里 ContentDir 非空但目录不存在的孤儿项。
        /// **只读，不做任何修改。**
        ///
        /// **为什么要「先证明是 Kontakt 库」**：`HKLM\SOFTWARE\Native Instruments` 下并不只有 Kontakt 音色库 ——
        /// 实测 HKLM64 有 3,411 个键，其中混着 NKS Store / Maschine / 以及 **Arturia 等第三方的 NKS 预设注册**
        /// （例如 `Arturia-Analog Lab V` 的 ContentDir 是 `C:\ProgramData\Arturia\...\Third Party\Native Instruments\presets`，
        /// 根本不是 Kontakt 库）。只按「ContentDir 失效」判定会误报一大批。
        ///
        /// **判定为 Kontakt 库注册的依据（满足其一）**：
        /// ① `Content\k2lib*` 里存在「值 == 该键名」的条目（Kontakt 自己登记的内容索引）；或
        /// ② `Service Center\&lt;键名&gt;.xml` 里 **`&lt;Type&gt;Content&lt;/Type&gt;`**（Kontakt 音色库）
        ///    或含 `<PoweredBy>Kontakt</PoweredBy>` / `<Icon>kontakt</Icon>`。
        ///
        /// **实测反例（必须排除）**：`Arturia-Analog Lab V` 等第三方的 NKS 注册，其 SC XML 里是
        /// **`&lt;Type&gt;Plugin&lt;/Type&gt;`**、`&lt;AuthSystem&gt;None&lt;/AuthSystem&gt;`，
        /// 还带 `&lt;BinName&gt;`/`&lt;AuthAppID&gt;`/`&lt;PluginID&gt;`/`&lt;FactoryLibrary&gt;` 这些插件专有字段，
        /// 且 `<ProductHints>` **没有 `spec` 属性** —— 这类不是 Kontakt 音色库，不应作为孤儿处理。
        /// </summary>
        private object OrphanPreview()
        {
            // Kontakt 的内容索引：k2lib* → 库名
            var k2Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var lm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var ck = lm32.OpenSubKey(@"SOFTWARE\Native Instruments\Content");
                if (ck != null)
                    foreach (var vn in ck.GetValueNames())
                        if (ck.GetValue(vn) is string s && s.Length > 0) k2Names.Add(s);
            }
            catch { }

            var items = new List<object>();
            foreach (var (view, hive, rv) in new[]
            {
                ("HKLM64", RegistryHive.LocalMachine, RegistryView.Registry64),
                ("HKLM32", RegistryHive.LocalMachine, RegistryView.Registry32),
                ("HKCU",   RegistryHive.CurrentUser,  RegistryView.Default),
            })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, rv);
                    using var root = baseKey.OpenSubKey(@"SOFTWARE\Native Instruments");
                    if (root == null) continue;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        if (name.Equals("Content", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            using var sub = root.OpenSubKey(name);
                            if (sub == null) continue;
                            string cd = sub.GetValue("ContentDir") as string ?? "";
                            if (cd.Length == 0) continue;                 // 空 ContentDir = 非库项，不动
                            string p = cd.TrimEnd('\\');
                            if (Directory.Exists(p)) continue;            // 目录存在 = 正常

                            // ── 必须是 Kontakt 音色库才继续 ──
                            bool isKontakt = k2Names.Contains(name);
                            string reason = isKontakt ? "Content 键登记" : "";
                            if (!isKontakt)
                            {
                                string sc = Path.Combine(NicntGenerator.ServiceCenterDir, name + ".xml");
                                if (File.Exists(sc) && new FileInfo(sc).Length > 0)
                                {
                                    string body = File.ReadAllText(sc);
                                    bool typeContent = body.Contains("<Type>Content</Type>");
                                    bool poweredByKontakt = body.Contains("<PoweredBy>Kontakt</PoweredBy>")
                                                         || body.Contains("<Icon>kontakt</Icon>");
                                    if (typeContent || poweredByKontakt)
                                    {
                                        isKontakt = true;
                                        reason = typeContent ? "SC 记录 Type=Content" : "SC 记录 PoweredBy=Kontakt";
                                    }
                                }
                            }
                            if (!isKontakt) continue;                     // 非 Kontakt 库（如 Arturia NKS 插件）→ 跳过

                            items.Add(new { view, key = name, contentDir = p, evidence = reason });
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return new
            {
                ok = true,
                count = items.Count,
                items,
                message = items.Count == 0 ? "未发现孤儿注册表项" : $"发现 {items.Count} 个孤儿注册表项",
            };
        }

        /// <summary>
        /// **清理孤儿注册表**：删除前把三视图的值 + `Content\k2lib*` 条目 + SC XML + NA 记录
        /// 全部备份到 `data\orphan-backups\<时间戳>\`，然后删除。
        /// </summary>
        private object OrphanClean(JsonElement args)
        {
            var pv = OrphanPreview();
            var list = (IEnumerable<object>)pv.GetType().GetProperty("items")!.GetValue(pv)!;
            var all = list.Cast<dynamic>().ToList();

            // 传了 view + key 就只删这一项（维护页的「单个删除」）
            string onlyView = GetString(args, "view");
            string onlyKey = GetString(args, "key");
            List<dynamic> targets = all;
            if (onlyView.Length > 0 && onlyKey.Length > 0)
            {
                targets = all.Where(t => (string)t.view == onlyView && (string)t.key == onlyKey).ToList();
                if (targets.Count == 0)
                    return new { ok = false, removed = 0, message = $"未找到该孤儿项：{onlyView}\\{onlyKey}（可能已被清理）" };
            }
            if (targets.Count == 0) return new { ok = true, removed = 0, message = "没有需要清理的孤儿项" };

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = Path.Combine(OrphanBackupDir, stamp);
            Directory.CreateDirectory(dir);

            var backup = new List<object>();
            var contentKey = new List<object>();
            int removed = 0;

            foreach (var t in targets)
            {
                string view = (string)t.view, key = (string)t.key;
                var (hive, rv) = view switch
                {
                    "HKLM64" => (RegistryHive.LocalMachine, RegistryView.Registry64),
                    "HKLM32" => (RegistryHive.LocalMachine, RegistryView.Registry32),
                    _ => (RegistryHive.CurrentUser, RegistryView.Default),
                };
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, rv);
                    using var root = baseKey.OpenSubKey(@"SOFTWARE\Native Instruments", writable: true);
                    if (root == null) continue;
                    using (var sub = root.OpenSubKey(key))
                    {
                        if (sub != null)
                        {
                            var vals = new Dictionary<string, object?>();
                            foreach (var vn in sub.GetValueNames())
                                vals[vn] = sub.GetValue(vn, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            backup.Add(new { view, key, values = vals });
                        }
                    }
                    root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                    removed++;
                }
                catch (Exception ex) { Log($"[孤儿清理] {view}\\{key} 删除失败：{ex.Message}"); }
            }

            // Content\k2lib* 中指向这些库名的条目
            var names = targets.Select(t => (string)t.key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var lm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var ck = lm32.OpenSubKey(@"SOFTWARE\Native Instruments\Content", writable: true);
                if (ck != null)
                {
                    foreach (var vn in ck.GetValueNames())
                    {
                        string? v = ck.GetValue(vn) as string;
                        if (v != null && names.Contains(v))
                        {
                            contentKey.Add(new { name = vn, value = v });
                            ck.DeleteValue(vn, throwOnMissingValue: false);
                        }
                    }
                }
            }
            catch { }

            // SC XML 与 NA 记录
            var scRemoved = new List<string>();
            var naRemoved = new List<string>();
            foreach (var n in names)
            {
                try
                {
                    string sc = Path.Combine(NicntGenerator.ServiceCenterDir, n + ".xml");
                    if (File.Exists(sc)) { File.Copy(sc, Path.Combine(dir, "SC_" + n + ".xml"), true); File.Delete(sc); scRemoved.Add(n); }
                }
                catch { }
                try
                {
                    string na = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                        "Native Instruments", "installed_products", n + ".json");
                    if (File.Exists(na)) { File.Copy(na, Path.Combine(dir, "NA_" + n + ".json"), true); File.Delete(na); naRemoved.Add(n); }
                }
                catch { }
            }

            File.WriteAllText(Path.Combine(dir, "registry-backup.json"),
                JsonSerializer.Serialize(new { stamp, removed, registry = backup, content = contentKey, sc = scRemoved, na = naRemoved },
                    new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));

            RefreshRegistry();
            Log($"[孤儿清理] 删除 {removed} 项，备份于 {dir}");
            return new
            {
                ok = true,
                removed,
                contentEntries = contentKey.Count,
                scFiles = scRemoved.Count,
                naFiles = naRemoved.Count,
                backupDir = dir,
                message = $"已清理 {removed} 个孤儿注册项（Content 条目 {contentKey.Count}、SC XML {scRemoved.Count}、NA 记录 {naRemoved.Count}）。" +
                          $"备份在 {dir}，可用「还原孤儿注册表」一键恢复。",
            };
        }

        /// <summary>**一键还原**：把最近一次（或指定时间戳的）孤儿清理备份写回注册表。</summary>
        private object OrphanRestore()
        {
            if (!Directory.Exists(OrphanBackupDir))
                return new { ok = false, message = "没有可还原的备份" };
            var dirs = Directory.GetDirectories(OrphanBackupDir).OrderByDescending(d => d).ToList();
            if (dirs.Count == 0) return new { ok = false, message = "没有可还原的备份" };

            string dir = dirs[0];
            string jsonPath = Path.Combine(dir, "registry-backup.json");
            if (!File.Exists(jsonPath)) return new { ok = false, message = $"备份文件缺失：{jsonPath}" };

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            int restored = 0;
            if (doc.RootElement.TryGetProperty("registry", out var reg))
            {
                foreach (var e in reg.EnumerateArray())
                {
                    string view = e.GetProperty("view").GetString() ?? "";
                    string key = e.GetProperty("key").GetString() ?? "";
                    var (hive, rv) = view switch
                    {
                        "HKLM64" => (RegistryHive.LocalMachine, RegistryView.Registry64),
                        "HKLM32" => (RegistryHive.LocalMachine, RegistryView.Registry32),
                        _ => (RegistryHive.CurrentUser, RegistryView.Default),
                    };
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, rv);
                        using var root = baseKey.CreateSubKey(@"SOFTWARE\Native Instruments", writable: true);
                        if (root == null) continue;
                        using var sub = root.CreateSubKey(key, writable: true);
                        if (sub == null) continue;
                        if (e.TryGetProperty("values", out var vals))
                            foreach (var v in vals.EnumerateObject())
                                sub.SetValue(v.Name, v.Value.ValueKind == JsonValueKind.Number
                                    ? (object)v.Value.GetInt32() : v.Value.GetString() ?? "");
                        restored++;
                    }
                    catch (Exception ex) { Log($"[孤儿还原] {view}\\{key} 失败：{ex.Message}"); }
                }
            }
            // Content 条目
            int ck = 0;
            if (doc.RootElement.TryGetProperty("content", out var cks))
            {
                try
                {
                    using var lm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                    using var c = lm32.CreateSubKey(@"SOFTWARE\Native Instruments\Content", writable: true);
                    foreach (var e in cks.EnumerateArray())
                    {
                        string nm = e.GetProperty("name").GetString() ?? "";
                        string vl = e.GetProperty("value").GetString() ?? "";
                        if (nm.Length > 0) { c?.SetValue(nm, vl); ck++; }
                    }
                }
                catch { }
            }
            // SC XML / NA 记录
            int files = 0;
            foreach (var f in Directory.GetFiles(dir, "SC_*.xml"))
            {
                try { File.Copy(f, Path.Combine(NicntGenerator.ServiceCenterDir, Path.GetFileName(f).Substring(3)), true); files++; } catch { }
            }
            foreach (var f in Directory.GetFiles(dir, "NA_*.json"))
            {
                try
                {
                    string na = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                        "Native Instruments", "installed_products", Path.GetFileName(f).Substring(3));
                    File.Copy(f, na, true); files++;
                }
                catch { }
            }

            RefreshRegistry();
            return new
            {
                ok = true,
                restored,
                contentEntries = ck,
                files,
                from = dir,
                message = $"已从 {Path.GetFileName(dir)} 还原 {restored} 个注册项（Content {ck}、文件 {files}）。请重启 Kontakt 查看。",
            };
        }
        /// **无 .nicnt 的库入库**：不依赖 .nicnt 元数据，直接用「库名 + 目录」写三处注册表 + NA 记录。
        /// 用于第三方/非标准库（实测很多库 has_nicnt=0，原入库路径对它们永远失败）。
        /// </summary>
        private object RegisterWithoutNicnt(JsonElement args)
        {
            if (!KontaktInfo.IsAdmin())
                return new { ok = false, message = "需要管理员权限：请右键以管理员身份运行本工具" };
            if (KontaktInfo.IsRunning())
                return new { ok = false, kontaktRunning = true, message = "检测到 Kontakt 正在运行。入库结果不会被已运行的 Kontakt 读取，且它退出时可能覆盖注册表。请先完全关闭 Kontakt。" };

            var ids = new List<long>();
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ids", out var ie) && ie.ValueKind == JsonValueKind.Array)
                foreach (var el in ie.EnumerateArray())
                    if (el.TryGetInt64(out long v)) ids.Add(v);

            var libs = Db.GetLibraries();
            var targets = ids.Count > 0 ? libs.Where(l => ids.Contains(l.Id)).ToList()
                                        : libs.Where(l => !l.HasNicnt).ToList();
            int done = 0, failed = 0;
            var msgs = new List<string>();

            foreach (var lib in targets)
            {
                if (string.IsNullOrEmpty(lib.Path) || !Directory.Exists(lib.Path)) { failed++; continue; }
                // **一个库可能有多个产品键**：`.nicnt` 的 ProductHints 里 Company/Name/RegKey
                // 都可能被当作键（实测 `Native Instruments BASiS` 与 `BASiS` 都要写一份 SC 记录）。
                // 所以键列表按「ScMissingRecords（最准）→ ProductKey → 库名」优先级合并去重。
                var keys = new List<string>();
                if (lib.ScMissingRecords != null)
                    foreach (var k0 in lib.ScMissingRecords)
                        if (!string.IsNullOrWhiteSpace(k0)) keys.Add(k0.Trim());
                if (!string.IsNullOrWhiteSpace(lib.ProductKey))
                    foreach (var k1 in lib.ProductKey.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!keys.Contains(k1, StringComparer.OrdinalIgnoreCase)) keys.Add(k1);
                if (keys.Count == 0) keys.Add(lib.Name);
                bool anyOk = false; string lastMsg = "";
                foreach (var k2 in keys)
                {
                    var (ok2, msg2) = LibraryRegistrar.RegisterWithoutNicnt(k2, lib.Path);
                    if (ok2) anyOk = true;
                    lastMsg = msg2;
                }
                if (anyOk) done++; else failed++;
                if (msgs.Count < 10) msgs.Add(lib.Name + " → " + lastMsg);
            }
            RefreshRegistry();
            return new
            {
                ok = failed == 0,
                registered = done,
                failed,
                messages = msgs,
                message = $"已入库 {done} 个（无 .nicnt 模式）" + (failed > 0 ? $"，失败 {failed} 个" : ""),
            };
        }

    private object RegisterLibraries(JsonElement args)
    {
        if (!KontaktInfo.IsAdmin())
            return new { ok = false, message = "需要管理员权限：请右键以管理员身份运行本工具" };

        bool force = GetBool(args, "force");
        // Kontakt 在启动时读取库列表；运行中写注册表不会被它看到，且退出时可能覆写。
        // 因此默认拒绝，由界面提示用户关闭 Kontakt（或确认强行继续）。
        if (!force && KontaktInfo.IsRunning())
        {
            var names = string.Join(", ", KontaktInfo.KontaktProcesses().Select(p => p.ProcessName));
            return new
            {
                ok = false,
                kontaktRunning = true,
                message = $"检测到 Kontakt 正在运行（{names}）。\n" +
                          "入库结果不会被已运行的 Kontakt 读取，且它退出时可能覆盖注册表。\n" +
                          "请先完全关闭 Kontakt 再入库。",
            };
        }

        var ids = new List<long>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ids", out var idsEl) &&
            idsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in idsEl.EnumerateArray())
                if (el.TryGetInt64(out long v)) ids.Add(v);
        }

        var libs = Db.GetLibraries().Where(l => ids.Contains(l.Id)).ToList();
        var results = new List<object>();
        var portableEntries = new List<PortableKontaktStore.LibraryEntry>();
        int okCount = 0, skipCount = 0, failCount = 0, scCount = 0;

            foreach (var lib in libs)
            {
                // **先判断目录是否存在** —— 换包 / 目录改名后索引里的旧路径会失效，
                // 此时若直接报「非标准库：目录内没有 .nicnt」会严重误导用户
                // （实测踩到：新包目录名带 ` Library` 后缀，旧索引指向已不存在的目录）。
                if (string.IsNullOrEmpty(lib.Path) || !Directory.Exists(lib.Path))
                {
                    skipCount++;
                    results.Add(new
                    {
                        library = lib.Name,
                        ok = false,
                        message = $"目录不存在：{lib.Path} —— 可能是换包或目录改名后索引未更新，请先「重新扫描」再入库。",
                    });
                    continue;
                }

                var nicnts = NicntReader.FindInLibrary(lib.Path, maxDepth: 3);
                if (nicnts.Count == 0)
            if (nicnts.Count == 0)
            {
                // 无 .nicnt：无法写注册表，但如果注册表里已有该库（手工入库/旧式库），
                // 仍可把它补进便携版库列表——这正是「待库管理器保存」的场景。
                portableEntries.AddRange(BuildPortableEntries(lib));
                skipCount++;
                results.Add(new
                {
                    library = lib.Name,
                    ok = false,
                    message = "非标准库：目录内没有 .nicnt 元数据，无法写入注册表；已尝试补写便携版库列表（可在 Files 浏览器中加载乐器）",
                });
                continue;
            }

            foreach (var n in nicnts)
            {
                var (ok, msg) = LibraryRegistrar.Register(n);
                if (ok)
                {
                    okCount++;
                    if (msg.Contains("已写入 Service Center 记录")) scCount++;
                }
                else failCount++;
                results.Add(new { library = lib.Name, product = n.Name, ok, message = msg });
            }
            portableEntries.AddRange(BuildPortableEntries(lib));
        }

        // 便携版 Kontakt：把库写进它自己的列表（等价于库管理器的「扫描 → 保存」），
        // 否则它读不到、乐器会报 "belongs to a library that is not installed currently"。
        object? portableResult = null;
        if (_portable != null && portableEntries.Count > 0 && !GetBool(args, "skipPortable"))
        {
            var wr = PortableKontaktStore.AddLibraries(Db.GetMeta("kontakt_exe", ""), portableEntries);
            portableResult = new
            {
                ok = wr.Ok,
                message = wr.Message,
                added = wr.Added,
                skipped = wr.Skipped,
                backupSettingsCfg = wr.BackupSettingsCfg,
                backupLibraryHints = wr.BackupLibraryHints,
            };
        }

        // **登记断言**：把本次真正入库成功的库记下来 —— Agent 收尾前会复查注册表三视图 + Content\k2lib0<SNPID>，
        // 防止「谎称已入库」（本项目踩过：说已入库、实际注册表里查不到）。
        foreach (var rr in results)
        {
            var t2 = rr.GetType();
            string? nm = (t2.GetProperty("library") ?? t2.GetProperty("name"))?.GetValue(rr) as string;
            bool okOne = t2.GetProperty("ok")?.GetValue(rr) is bool b && b;
            if (okOne && !string.IsNullOrEmpty(nm))
                _agentClaims.Add(new AssertionVerifier.Claim
                { Kind = AssertionVerifier.Registered, Subject = nm!, Source = "registerLibraries" });
        }

        RefreshRegistry();
        string summary = $"注册成功 {okCount} 个产品（其中 {scCount} 个写入了 Service Center 记录）" +
                         (skipCount > 0 ? $"，跳过 {skipCount} 个非标准库" : "") +
                         (failCount > 0 ? $"，失败 {failCount} 个" : "");
        if (portableResult != null)
            summary += "\n便携版：请见下方结果。";
        return new
        {
            ok = failCount == 0,
            message = summary,
            details = results,
            portable = portableResult,
            report = BuildRegistrationReport(),
        };
    }

    /// <summary>
    /// 为一个音色库组装便携版写入条目。三级数据源回退：
    ///   ① 库目录里的 .nicnt（字段最全：Name/SNPID/Company/HU/JDX）
    ///   ② 系统注册表里已有条目（RegKey/ContentDir/HU/JDX/ContentVersion）
    ///   ③ 便携版 LibraryHints.xml（补 Name/SNPID——无 .nicnt 的旧式库主要靠这里）
    /// </summary>
    private List<PortableKontaktStore.LibraryEntry> BuildPortableEntries(LibraryRecord lib)
        => PortableKontaktStore.BuildEntries(
            lib.Path, lib.Name, _registered, PortableKontaktStore.ReadLibraryHints(_portable));

    private object WritePortableLibraries(JsonElement args)
    {
        if (_portable == null)
            return new { ok = false, message = "当前设置的 Kontakt 不是便携版" };
        if (KontaktInfo.IsRunning())
            return new { ok = false, kontaktRunning = true, message = "Kontakt 正在运行，请先完全关闭（它退出时会覆写 Settings.cfg）" };

        var ids = new List<long>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ids", out var idsEl) &&
            idsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in idsEl.EnumerateArray())
                if (el.TryGetInt64(out long v)) ids.Add(v);
        }

        RefreshRegistry();
        var libs = Db.GetLibraries().Where(l => ids.Contains(l.Id)).ToList();
        var all = new List<PortableKontaktStore.LibraryEntry>();
        foreach (var lib in libs)
            all.AddRange(BuildPortableEntries(lib));

        if (all.Count == 0)
            return new { ok = false, message = "没有可写入的库（既无 .nicnt，注册表里也没有对应条目）", report = BuildRegistrationReport() };

        var wr = PortableKontaktStore.AddLibraries(Db.GetMeta("kontakt_exe", ""), all);
        RefreshRegistry();
        return new
        {
            ok = wr.Ok,
            message = wr.Message,
            added = wr.Added,
            skipped = wr.Skipped,
            backupSettingsCfg = wr.BackupSettingsCfg,
            report = BuildRegistrationReport(),
        };
    }

    private object CloseKontakt()
    {
        var (ok, msg) = KontaktInfo.CloseKontakt();
        return new { ok, message = msg, running = KontaktInfo.IsRunning() };
    }

    private object PortableInfoResult()
    {
        RefreshRegistry();
        if (_portable == null)
            return new { isPortable = false, message = "当前设置的 Kontakt 不是便携版（未找到 UserData\\Settings.cfg）" };

        return new
        {
            isPortable = true,
            root = _portable.Root,
            settingsCfg = _portable.SettingsCfg,
            settingsCfgModified = _portable.SettingsCfgModified.ToString("yyyy-MM-dd HH:mm:ss"),
            libraryManager = _portable.LibraryManager,
            libraryCount = _portable.RegisteredNames.Count,
        };
    }

    private object OpenLibraryManager()
    {
        string exe = Db.GetMeta("kontakt_exe", "");
        var (ok, msg) = PortableKontaktStore.LaunchLibraryManager(exe);
        return new { ok, message = msg };
    }

    private object Unregister(JsonElement args)
    {
        string key = GetString(args, "regKey");
        var (ok, msg) = LibraryRegistrar.Unregister(key);
        RefreshRegistry();
        return new { ok, message = msg, report = BuildRegistrationReport() };
    }

    // ── 版本兼容 ───────────────────────────────────────────
    private object BuildCompatSummary()
    {
        string kv = KontaktVersion;
        var libs = Db.GetLibraries();
        int tooOld = libs.Count(l => VersionUtil.IsValid(l.RequiredKontakt) && VersionUtil.IsValid(kv) &&
                                     VersionUtil.Compare(l.RequiredKontakt, kv) > 0);
        return new
        {
            kontaktVersion = kv,
            kontaktExe = Db.GetMeta("kontakt_exe", ""),
            tooOld,
            total = libs.Count,
        };
    }

    private CompatReport BuildCompatReport()
    {
        string kv = KontaktVersion;
        string exe = Db.GetMeta("kontakt_exe", "");
        var libs = Db.GetLibraries();
        LibraryRegistrar.Enrich(libs, _registered, kv);

        var report = new CompatReport
        {
            KontaktVersion = kv,
            KontaktPath = exe,
            Ok = libs.Count(l => l.CompatStatus == "ok"),
            TooOld = libs.Count(l => l.CompatStatus == "too-old"),
            Unknown = libs.Count(l => l.CompatStatus == "unknown"),
            Incompatible = libs.Where(l => l.CompatStatus == "too-old")
                               .OrderByDescending(l => VersionUtil.ParseParts(l.RequiredKontakt).FirstOrDefault())
                               .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                               .ToList(),
        };
        return report;
    }

    // ── 通用 ───────────────────────────────────────────────
    private object OpenPath(JsonElement args)
    {
        string path = GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("缺少 path 参数");

        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else
            throw new DirectoryNotFoundException($"路径不存在：{path}");
        return new { opened = path };
    }

    // ══════════════════ 移动音色库 ══════════════════

    private volatile bool _moving;

    private object MovePlanResult(JsonElement args)
    {
        long id = GetLong(args, "libraryId");
        string dest = GetString(args, "destRoot");
        var plan = MoveService.Plan(Db, id, dest);
        return new
        {
            plan.LibraryId,
            plan.LibraryName,
            plan.SourcePath,
            plan.DestRoot,
            plan.DestPath,
            plan.SizeBytes,
            plan.FileCount,
            plan.NkiCount,
            plan.DestRootExists,
            plan.DestRootKnown,
            plan.FreeSpaceBytes,
            plan.KontaktRunning,
            plan.IsAdmin,
            plan.ProductKeys,
            plan.Errors,
            plan.Warnings,
            plan.CanExecute,
        };
    }

    private object MoveExecute(JsonElement args)
    {
        if (_moving) return new { started = false, reason = "已有移动任务在进行中" };

        long id = GetLong(args, "libraryId");
        string dest = GetString(args, "destRoot");
        var plan = MoveService.Plan(Db, id, dest);
        if (!plan.CanExecute)
            return new { started = false, reason = string.Join("；", plan.Errors) };

        _moving = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var prog = new Progress<MoveProgress>(p => Push(new
                {
                    type = "moveProgress",
                    libraryId = id,
                    phase = p.Phase,
                    percent = p.Percent,
                    filesDone = p.FilesDone,
                    filesTotal = p.FilesTotal,
                    bytesDone = p.BytesDone,
                    bytesTotal = p.BytesTotal,
                    speedMBps = Math.Round(p.SpeedMBps, 1),
                    message = p.Message,
                }));

                var res = await MoveService.ExecuteAsync(plan, Db, prog, CancellationToken.None);
                Push(new
                {
                    type = "moveDone",
                    libraryId = id,
                    ok = res.Ok,
                    message = res.Message,
                    newPath = res.NewPath,
                    details = res.Details,
                    reregistered = res.Reregistered,
                    portableUpdated = res.PortableUpdated,
                    sourcePath = plan.SourcePath,
                    destPath = plan.DestPath,
                });
            }
            catch (Exception ex)
            {
                Log($"移动失败：{ex}");
                Push(new { type = "moveError", libraryId = id, error = ex.Message });
            }
            finally { _moving = false; }
        });

        return new { started = true, destPath = plan.DestPath };
    }

    private object MoveDeleteSource(JsonElement args)
    {
        long id = GetLong(args, "libraryId");
        string dest = GetString(args, "destRoot");
        var plan = MoveService.Plan(Db, id, dest);
        // 源目录此时可能已被标记为"不存在"（已删），Plan 会报错，这里直接构造
        plan.SourcePath = GetString(args, "sourcePath");
        plan.DestPath = GetString(args, "destPath");

        var (ok, msg) = MoveService.DeleteSource(plan);
        return new { ok, message = msg };
    }

    /// <summary>
    /// 修复入库路径：把注册表 / 便携版库列表里指向该库的 ContentDir 改成它当前的真实路径。
    /// 用于「库被移动过，Kontakt 里路径失效」的场景（不需要重新拷贝文件）。
    /// </summary>
    private object RepairPath(JsonElement args)
    {
        long id = GetLong(args, "libraryId");
        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == id);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };
        if (!Directory.Exists(lib.Path)) return new { ok = false, message = $"库路径不存在：{lib.Path}" };
        if (!KontaktInfo.IsAdmin()) return new { ok = false, message = "需要管理员权限才能改写注册表" };

        RefreshRegistry();
        string norm = LibraryRegistrar.NormalizePath(lib.Path);
        var keys = new List<string>();

        // 1) 注册表里指向「旧路径」或「本库产品键」的条目
        foreach (var kv in _registered)
        {
            bool pointsInto = kv.Value.ContentDir.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                              kv.Value.ContentDir.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase);
            bool productMatch = lib.ProductKey.Length > 0 &&
                                lib.ProductKey.Split(';').Contains(kv.Key, StringComparer.OrdinalIgnoreCase);
            if (pointsInto || productMatch) keys.Add(kv.Key);
        }
        if (lib.ProductKey.Length > 0)
            foreach (var k in lib.ProductKey.Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (!keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);

        // 2) 库内 .nicnt 的产品键（无注册表条目时也能补上）
        foreach (var n in NicntReader.FindInLibrary(lib.Path, 3))
        {
            string k = string.IsNullOrWhiteSpace(n.RegKey) ? n.Name : n.RegKey;
            if (k.Length > 0 && !keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);
        }

        if (keys.Count == 0)
            return new { ok = false, message = "无法确定该库的注册表键名，请用「入库」重新注册" };

        int fixedCount = 0;
        var details = new List<string>();
        foreach (var key in keys)
        {
            var (ok, msg) = LibraryRegistrar.UpdateContentDir(key, lib.Path);
            if (ok) fixedCount++;
            details.Add(ok ? $"✓ {msg}" : $"✗ {msg}");
        }

        bool portableOk = false;
        string kontaktExe = Db.GetMeta("kontakt_exe", "");
        if (kontaktExe.Length > 0)
        {
            var map = keys.ToDictionary(k => k, _ => lib.Path, StringComparer.OrdinalIgnoreCase);
            var (ok, msg, updated) = PortableKontaktStore.UpdateLibraryPaths(kontaktExe, map);
            portableOk = ok && updated > 0;
            details.Add(ok ? $"便携版：{msg}" : $"便携版：{msg}");
        }

        RefreshRegistry();
        return new
        {
            ok = fixedCount > 0,
            message = $"已修复 {fixedCount} 个产品的入库路径" + (portableOk ? "，并更新了便携版库列表" : ""),
            details,
            report = BuildRegistrationReport(),
        };
    }

    // ══════════════════ 乐器中心 ══════════════════

    private object Instruments(JsonElement args)
    {
        string query = GetString(args, "query");
        long libId = GetLong(args, "libraryId");
        string category = GetString(args, "category");
        string kind = GetString(args, "kind");
        int limit = (int)Math.Clamp(GetLong(args, "limit") is > 0 and <= 2000 ? GetLong(args, "limit") : 200, 1, 2000);
        int offset = (int)Math.Max(0, GetLong(args, "offset"));
        string sort = GetString(args, "sort");
        string articulation = GetString(args, "articulation");
        bool favOnly = GetBool(args, "favoriteOnly");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (items, total) = Db.SearchInstruments(query, libId, category, kind, limit, offset, sort, articulation, favOnly);
        sw.Stop();

        return new
        {
            total,
            offset,
            limit,
            ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            items = items.Select(i => new
            {
                i.Id,
                i.LibraryId,
                i.LibraryName,
                i.Name,
                i.RelPath,
                i.Kind,
                i.SizeBytes,
                i.EngineVersion,
                i.Articulation,
                i.Source,
                i.Format,
                fullPath = Path.Combine(i.LibraryPath, i.RelPath),
            }),
        };
    }

    private object OpenInstrument(JsonElement args)
    {
        long id = GetLong(args, "id");
        string? full = ResolveInstrumentPath(id);
        if (full == null || !File.Exists(full))
            return new { ok = false, message = "文件不存在（可能已被移动）" };

        try
        {
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            return new { ok = true, message = $"已用默认程序打开：{Path.GetFileName(full)}" };
        }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }

    private object RevealInstrument(JsonElement args)
    {
        long id = GetLong(args, "id");
        string? full = ResolveInstrumentPath(id);
        if (full == null || !File.Exists(full))
            return new { ok = false, message = "文件不存在" };
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
        return new { ok = true, message = "已在资源管理器中定位" };
    }

    /// <summary>把乐器相对路径还原成完整路径（用库路径拼接）。</summary>
    private string? ResolveInstrumentPath(long instrumentId)
    {
        var (items, _) = Db.SearchInstruments(limit: 1, offset: 0);
        // SearchInstruments 不支持按 id 查，这里直接查库
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = AppPaths.DbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.rel_path, l.path FROM instruments i JOIN libraries l ON l.id = i.library_id
            WHERE i.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", instrumentId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        return Path.Combine(rd.GetString(1), rd.GetString(0));
    }

    private object CopyText(JsonElement args)
    {
        string text = GetString(args, "text");
        if (text.Length == 0) return new { ok = false, message = "内容为空" };
        Dispatcher.Invoke(() =>
        {
            try { System.Windows.Clipboard.SetText(text); } catch { }
        });
        return new { ok = true, message = "已复制到剪贴板" };
    }

    // ══════════════════ 封面编辑器 ══════════════════

    /// <summary>Kontakt 官方封面横幅尺寸（.nicnt 内嵌 PNG 实测为 905×99）。</summary>
    private const int CoverWidth = 905;
    private const int CoverHeight = 99;

    private object PickImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件 (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog() != true) return new { ok = false, message = "已取消" };

        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 40L * 1024 * 1024) return new { ok = false, message = "图片过大（>40 MB）" };
            string ext = info.Extension.TrimStart('.').ToLowerInvariant();
            string mime = ext switch
            {
                "png" => "image/png", "jpg" or "jpeg" => "image/jpeg", "bmp" => "image/bmp",
                "gif" => "image/gif", "webp" => "image/webp", _ => "application/octet-stream",
            };
            return new
            {
                ok = true,
                path = dlg.FileName,
                name = info.Name,
                sizeBytes = info.Length,
                dataUrl = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(dlg.FileName))}",
                coverWidth = CoverWidth,
                coverHeight = CoverHeight,
            };
        }
        catch (Exception ex) { return new { ok = false, message = $"读取图片失败：{ex.Message}" }; }
    }

    private object ReadImage(JsonElement args)
    {
        string path = GetString(args, "path");
        if (!File.Exists(path)) return new { ok = false, message = "文件不存在" };
        try
        {
            var info = new FileInfo(path);
            string ext = info.Extension.TrimStart('.').ToLowerInvariant();
            string mime = ext switch
            {
                "png" => "image/png", "jpg" or "jpeg" => "image/jpeg", "bmp" => "image/bmp",
                "gif" => "image/gif", "webp" => "image/webp", _ => "application/octet-stream",
            };
            return new
            {
                ok = true,
                dataUrl = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}",
                coverWidth = CoverWidth,
                coverHeight = CoverHeight,
            };
        }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }

    /// <summary>保存编辑好的封面（前端 canvas 导出的 PNG dataURL）。</summary>
    private object SaveCover(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        string dataUrl = GetString(args, "png");
        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };

        int comma = dataUrl.IndexOf(',');
        if (comma <= 0 || !dataUrl.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase))
            return new { ok = false, message = "封面数据格式不正确" };

        try
        {
            byte[] png = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            string file = CoverStore.SaveCustom(lib.Path, png);
            if (file.Length == 0) return new { ok = false, message = "写入封面失败" };

            Db.SetLibraryCover(libId, file);

            // **自动写入库目录的 `Wallpaper.png`（905×99 RGB）** ——
            //   这是 Kontakt 8 库列表横幅的真正来源，保存封面后即刻生效（重启 Kontakt 可见）。
            string wpNote = "";
            try
            {
                string wpPath = Path.Combine(lib.Path, "Wallpaper.png");
                byte[] wpPng = FlattenToRgb905x99(png);
                if (File.Exists(wpPath))
                    File.Copy(wpPath, wpPath + ".klm-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                File.WriteAllBytes(wpPath, wpPng);
                wpNote = "，已写入库目录的 Wallpaper.png（905×99）";
                Log($"[封面] {lib.Name} → Wallpaper.png（{wpPng.Length} 字节，905×99 RGB）");
            }
            catch (Exception wex)
            {
                wpNote = "，但写入 Wallpaper.png 失败：" + wex.Message;
            }

            return new { ok = true, message = $"已保存封面（{png.Length / 1024.0:F1} KB）{wpNote}，重启 Kontakt 后可见", coverFile = file };
        }
        catch (Exception ex) { return new { ok = false, message = $"保存失败：{ex.Message}" }; }
    }

    /// <summary>把多个库的知识库合并成一个，块文本前缀【库名】以便 AI 引用来源。</summary>
    private static LibraryKb MergeKbs(List<LibraryKb> kbs)
    {
        var m = new LibraryKb
        {
            LibraryId = 0,
            LibraryName = $"跨库（{kbs.Count} 个音色库）",
            ManualName = $"合并了 {kbs.Count} 个库的知识库",
            PageCount = kbs.Sum(k => k.PageCount),
            TotalChars = kbs.Sum(k => k.TotalChars),
            VisionPages = kbs.Sum(k => k.VisionPages),
            BuiltAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };
        int id = 1;
        foreach (var k in kbs)
        {
            foreach (var c in k.Chunks)
                m.Chunks.Add(new KbChunk { Id = id++, Page = c.Page, Text = $"【{k.LibraryName}】{c.Text}" });
        }
        m.Overview = string.Join("\n\n", kbs.Where(k => k.Overview.Length > 0)
            .Select(k => $"【{k.LibraryName}】{k.Overview}"));
        return m;
    }

    /// <summary>跨库模式下的库上下文：概览全部库，让 AI 知道"我们有哪些库"。</summary>
    private LibraryContext BuildCrossContext()
    {
        var libs = LoadLibraries();
        return new LibraryContext
        {
            Name = $"跨库问答模式（共 {libs.Count} 个音色库）",
            Category = "全部",
            SizeBytes = libs.Sum(l => l.SizeBytes),
            NkiCount = libs.Sum(l => l.NkiCount),
            NkmCount = libs.Sum(l => l.NkmCount),
            FileCount = libs.Sum(l => l.FileCount),
            KontaktVersion = KontaktVersion,
            InstrumentGroups = libs.GroupBy(l => l.Category)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}（{g.Count()} 个库：" + string.Join("、", g.Take(6).Select(x => x.Name)) + "）")
                .ToList(),
        };
    }

    /// <summary>
    /// 构造 Agent 的只读工具上下文。
    /// **权限模型**：这里只放「只读白名单」——音色库根目录。写/删/改类能力不在此上下文内，
    /// 必须另走「预览改动 → 用户确认 → 执行 → 可回滚」的验权+提权流程。
    /// </summary>
    /// <summary>
    /// **结果断言登记表**（断言式 verifier）—— 工具执行后往里登记它「声称了什么」，
    /// Agent 在给出最终回答前会复查，不一致就把差异回灌让模型自纠。每轮对话开始时清空。
    /// 放在 MainWindow 上是因为部分工具（如 RegisterLibraries）是 MainWindow 方法、拿不到 toolCtx。
    /// </summary>
    private readonly List<AssertionVerifier.Claim> _agentClaims = new();

        // 🔴 **加 libraryId 参数（2026-09-27）** —— 让 ShellWorkDir 能指向【当前会话绑定的库】，
        //   而不是 AllowedRoots.FirstOrDefault()（那是字母序排序结果、无语义）。
        private AgentToolContext BuildToolContext(long libraryId = 0)
    {
        var aiSet = AiSettings.Load(Db);
        var ctx = new AgentToolContext
        {
            Db = Db,
            AllowNetwork = aiSet.AllowNetwork,
            AllowShell = aiSet.AllowShell,
            ShellAutoApproveAll = aiSet.PermissionTier == "full",
            ConfirmShell = ConfirmShellAsync,
            ConfirmScript = ConfirmScriptAsync,
            AllowUiControl = aiSet.AllowUiControl,
            ConfirmUi = ConfirmUiAsync,
                // 试听：Core 层通过回调拿可播放 URL，并把清单推给界面渲染内嵌播放器
                ResolveAudioUrl = (full, ext, libPath) => CacheAudio(full, ext, libPath),
                OnAudition = items =>
                {
                    // 🔴 诊断日志（2026-09-26）：确认后端是否真的推了 chatAudition、带了多少条
                    try { Log($"[audition] 推送 chatAudition，items={items?.Count ?? 0}"); } catch { }
                    Push(new { type = "chatAudition", items });
                },
                // Agent 触发的建库：进度推给界面（知识库面板下方日志窗口实时显示）
                OnKbProgress = (kbLibId, kbLibName, p) => Push(new
                {
                    type = "kbProgress",
                    libraryId = kbLibId,
                    libraryName = kbLibName,
                    phase = p.Phase,
                    current = p.Current,
                    total = p.Total,
                    message = p.Message,
                }),
                // **插话引导**：Agent 循环每步都会来取一次；取走即清空该分支队列
                TakeSteering = () =>
                {
                    // 🔴 **修「插话从来没被 Agent 看到」（2026-09-25）** ——
                    //   原来只用 `_activeChatBranchId`，而**这个字段【从来没有被赋值过】**
                    //   ⇒ 恒为 0 ⇒ `bid > 0` 恒为假 ⇒ **永远返回空列表**
                    //   ⇒ 用户插的话进了队列却【永远没人取】⇒ 表现为「插话没生效」。
                    //   ✅ 回退：用【当前正在运行的分支】—— 正在跑的回合会把自己登记进 `_chatCts`。
                    long bid = _activeChatBranchId;
                    if (bid <= 0)
                    {
                        try { if (_chatCts.Count == 1) bid = _chatCts.Keys.First(); } catch { }
                    }
                    var list = new List<string>();
                    if (bid > 0 && _chatSteer.TryGetValue(bid, out var q))
                        while (q.TryDequeue(out var s)) list.Add(s);
                    return list;
                },
                OnKbDone = (kbLibId, kbLibName, kbManual, s) => Push(new
                {
                    type = "kbDone",
                    libraryId = kbLibId,
                    libraryName = kbLibName,
                    summary = new
                    {
                        pages = s.Pages, chunks = s.Chunks, chars = s.Chars,
                        visionPages = s.VisionPages, manual = kbManual,
                    },
                }),
            Memory = MakeMemory(),
            Plans = _plans,
            OnPlanChanged = plan => Push(new
                {
                    type = "chatPlan",
                    goal = plan.Goal,
                    done = plan.DoneCount,
                    total = plan.Steps.Count,
                    steps = plan.Steps.Select(s => new { status = s.Status, title = s.Title }),
                }),
        };
        try
        {
            foreach (var l in LoadLibraries())
                if (!string.IsNullOrEmpty(l.Path)) ctx.AllowedRoots.Add(l.Path);
            foreach (var r in Db.GetRoots())
                if (!string.IsNullOrEmpty(r.Path)) ctx.AllowedRoots.Add(r.Path);
        }
        catch { }
        // 🔴 **ShellWorkDir 的语义（2026-09-27 修）** ——
        //   旧实现 = `AllowedRoots.FirstOrDefault()`，那是【按字母序的第一个库】，
        //   跟用户当前在聊哪个库毫无关系 ⇒ shell 的默认目录会莫名其妙。
        //   现在：**优先用当前会话绑定的库路径**，拿不到才回退到第一个库。
        try
        {
            string? bound = null;
            if (libraryId > 0)
            {
                var cur = LoadLibraries().FirstOrDefault(x => x.Id == libraryId);
                if (cur != null && !string.IsNullOrEmpty(cur.Path) && Directory.Exists(cur.Path))
                    bound = cur.Path;
            }
            ctx.ShellWorkDir = bound ?? ctx.AllowedRoots.FirstOrDefault() ?? "";
        }
        catch { ctx.ShellWorkDir = ctx.AllowedRoots.FirstOrDefault() ?? ""; }
        return ctx;
    }



    /// <summary>
    /// 代码执行的用户确认：推送 scriptConfirm（带**完整脚本正文**，即「预览改动」），
    /// 阻塞等待用户决定；拒绝或超时（5 分钟）返回 false，脚本不会被执行也不会残留。
    /// </summary>
    private async Task<bool> ConfirmScriptAsync(string language, string code)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shellPending = tcs;
        Push(new { type = "scriptConfirm", language, code });
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using (cts.Token.Register(() => tcs.TrySetResult(false)))
                return await tcs.Task;
        }
        finally { _shellPending = null; }
    }

    // ══════════════════ 长期记忆 ══════════════════

    private readonly PlanStore _plans = new();

    private AgentMemory? _memory;

    private AgentMemory MakeMemory()
    {
        _memory ??= new AgentMemory(Db.DbPath);
        _memory.EnsureCreated();
        return _memory;
    }

    private object MemList()
    {
        var m = MakeMemory();
        var libs = Db.GetLibraries().ToDictionary(l => l.Id, l => l.Name);
        return new
        {
            total = m.List().Count,
            items = m.List().Select(e => new
            {
                e.Id, e.Scope, e.LibraryId, e.Key, e.Content, e.Importance, e.CreatedAt, e.UpdatedAt,
                libraryName = e.LibraryId.HasValue && libs.TryGetValue(e.LibraryId.Value, out var n) ? n : "",
            }),
        };
    }

    private object MemAdd(JsonElement args)
    {
        var m = MakeMemory();
        long libId = GetLong(args, "libraryId");
        var e = m.Remember(
            GetString(args, "scope") is { Length: > 0 } sc ? sc : "global",
            libId > 0 ? libId : null,
            GetString(args, "key"),
            GetString(args, "content"),
            (int)(GetLong(args, "importance") is > 0 and <= 5 ? GetLong(args, "importance") : 3));
        return new { ok = true, id = e.Id };
    }

    private object MemUpdate(JsonElement args)
    {
        var m = MakeMemory();
        long id = GetLong(args, "id");
        long imp = GetLong(args, "importance");
        m.Update(id, GetString(args, "content"), imp is >= 1 and <= 5 ? (int)imp : null);
        return new { ok = true };
    }

    private object MemDelete(JsonElement args)
    {
        MakeMemory().Forget(GetLong(args, "id"));
        return new { ok = true };
    }



    /// <summary>
    /// 界面写操作的确认：推送 uiConfirm（含动作描述，如「点击 &lt;RadioButton&gt; 'Tools' 坐标 260,195」），
    /// 阻塞等待用户决定；拒绝或超时（2 分钟）返回 false，界面不会被操作。
    /// </summary>
    private async Task<bool> ConfirmUiAsync(string description)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shellPending = tcs;
        Push(new { type = "uiConfirm", description });
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using (cts.Token.Register(() => tcs.TrySetResult(false)))
                return await tcs.Task;
        }
        finally { _shellPending = null; }
    }

    // ══════════════════ 多模态：截图 ══════════════════

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>截取本应用窗口（用于「这个界面怎么用」这类提问）。</summary>
    private object CaptureWindow()
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) return new { ok = false, message = "窗口尚未就绪" };
            if (!GetWindowRect(helper.Handle, out RECT r)) return new { ok = false, message = "取窗口尺寸失败" };

            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return new { ok = false, message = "窗口尺寸异常" };
            return CaptureRegion(r.Left, r.Top, w, h);
        }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }

    /// <summary>截取整个屏幕（用于让 AI 看外部程序，如 Kontakt 界面）。</summary>
    private object CaptureScreen()
    {
        try
        {
            var wa = System.Windows.SystemParameters.WorkArea;
            return CaptureRegion((int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height);
        }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }

    private object CaptureRegion(int left, int top, int width, int height)
    {
        // 限制最长边，避免 base64 过大
        // 截图要经 WebView2 桥接传给前端，base64 后过大会静默失败；
        // 故限制最长边并把 PNG 换成 JPEG（截图内容用 JPEG 体积小一个数量级）。
        int maxSide = 1100;
        double scale = Math.Min(1.0, maxSide / (double)Math.Max(width, height));
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));

        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
        }
        using var ms = new MemoryStream();
        var jpgCodec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using (var ep = new System.Drawing.Imaging.EncoderParameters(1))
        {
            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 78L);
            bmp.Save(ms, jpgCodec, ep);
        }
        byte[] imgBytes = ms.ToArray();
        return new
        {
            ok = true,
            base64 = Convert.ToBase64String(imgBytes),
            width = w,
            height = h,
            bytes = imgBytes.Length,
            multimodal = AiSettings.Load(Db).Multimodal,
        };
    }

    // ══════════════════ Shell 验权（风险命令的用户确认）══════════════════

    private TaskCompletionSource<bool>? _shellPending;

    /// <summary>
    /// 风险命令的用户确认：推送 shellConfirm 事件让界面弹窗，然后**阻塞等待**用户决定。
    /// 用户拒绝或超时（5 分钟）都返回 false，命令不会被执行。
    /// </summary>
    private async Task<bool> ConfirmShellAsync(string command, string reason)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shellPending = tcs;
        Push(new { type = "shellConfirm", command, reason });
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using (cts.Token.Register(() => tcs.TrySetResult(false)))
                return await tcs.Task;
        }
        finally { _shellPending = null; }
    }

    /// <summary>界面回传用户决定。</summary>
    private object ShellDecision(JsonElement args)
    {
        bool approve = GetBool(args, "approve");
        _shellPending?.TrySetResult(approve);
        return new { ok = true, approved = approve };
    }

    // ══════════════════ 问问AI：会话 / 分支 / 工作区 ══════════════════

    private ChatStore? _chat;
    private ChatStore Chat => _chat ??= MakeChatStore();

    private ChatStore MakeChatStore()
    {
        var s = new ChatStore(Db.DbPath);
        s.EnsureCreated();
        return s;
    }

    /// <summary>会话列表（按分类分组，供左侧文件夹收纳）。</summary>
    private object ChatSessions()
    {
        var list = Chat.ListSessions();
        var groups = list
            .GroupBy(s => string.IsNullOrEmpty(s.Category) ? "未分类" : s.Category)
            .Select(g => new
            {
                category = g.Key,
                count = g.Count(),
                sessions = g.Select(s => new
                {
                    s.Id, s.LibraryId, s.LibraryName, s.Category, s.Title,
                    s.CreatedAt, s.UpdatedAt, s.ActiveBranchId, s.BranchCount, s.MessageCount,
                }),
            })
            .OrderByDescending(g => g.sessions.Max(x => x.UpdatedAt));
        return new { total = list.Count, groups };
    }

    /// <summary>新建会话。mode=single 时主分支为单库模式；mode=cross 时主分支即跨库模式。</summary>
    private object ChatSessionCreate(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        string mode = GetString(args, "mode");
        if (mode.Length == 0) mode = libId > 0 ? "single" : "cross";

        string libName = "", category = "";
        if (libId > 0)
        {
            var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
            if (lib != null) { libName = lib.Name; category = lib.Category; }
        }
        string title = GetString(args, "title");
        if (title.Length == 0)
            title = libId > 0 ? (libName.Length > 0 ? libName : "单库问答") : "跨库问答";

        var s = Chat.CreateSession(libId > 0 ? libId : null, libName, category, title, mode);
        return OpenSession(s.Id);
    }

    /// <summary>打开会话：返回会话信息 + 全部分支 + 激活分支的消息。</summary>
    private object ChatSessionOpen(JsonElement args) => OpenSession(GetLong(args, "sessionId"));

    private object OpenSession(long sid)
    {
        var s = Chat.GetSession(sid);
        if (s == null) return new { ok = false, message = "会话不存在" };

        var branches = Chat.ListBranches(sid);
        long active = s.ActiveBranchId ?? branches.FirstOrDefault()?.Id ?? 0;
        var msgs = active > 0 ? Chat.GetMessages(active) : new List<ChatEntry>();

        return new
        {
            ok = true,
            session = new
            {
                s.Id, s.LibraryId, s.LibraryName, s.Category, s.Title,
                s.CreatedAt, s.UpdatedAt, s.ActiveBranchId, s.BranchCount, s.MessageCount,
            },
            branches = branches.Select(b => new
            {
                b.Id, b.SessionId, b.Mode, b.ParentId, b.Title, b.CreatedAt, b.MessageCount,
            }),
            activeBranchId = active,
            messages = msgs.Select(m => new { m.Id, m.BranchId, m.Role, m.Content, m.Reasoning, m.CreatedAt }),
        };
    }

    private object ChatSessionDelete(JsonElement args)
    {
        Chat.DeleteSession(GetLong(args, "sessionId"));
        return new { ok = true };
    }

    private object ChatSessionRename(JsonElement args)
    {
        Chat.RenameSession(GetLong(args, "sessionId"), GetString(args, "title"));
        return new { ok = true };
    }

    private object ChatBranchCreate(JsonElement args)
    {
        long sid = GetLong(args, "sessionId");
        long parent = GetLong(args, "parentId");
        string mode = GetString(args, "mode");
        if (mode.Length == 0) mode = "cross";
        var b = Chat.CreateBranch(sid, mode, parent > 0 ? parent : null, GetString(args, "title"));
        return new { ok = true, branch = new { b.Id, b.SessionId, b.Mode, b.ParentId, b.Title, b.CreatedAt, b.MessageCount } };
    }

    private object ChatBranchSwitch(JsonElement args)
    {
        long sid = GetLong(args, "sessionId");
        long bid = GetLong(args, "branchId");
        Chat.SetActiveBranch(sid, bid);
        return OpenSession(sid);
    }

    private object ChatBranchDelete(JsonElement args)
    {
        long sid = GetLong(args, "sessionId");
        long bid = GetLong(args, "branchId");
        var main = Chat.MainBranch(sid);
        if (main != null && main.Id == bid) return new { ok = false, message = "主分支不能删除" };
        Chat.DeleteBranch(bid);
        if (Chat.GetSession(sid)?.ActiveBranchId == bid && main != null)
            Chat.SetActiveBranch(sid, main.Id);
        return OpenSession(sid);
    }

    /// <summary>全部说明书 + 所属库分类 + 是否已知识库化。</summary>
    private object ManualsAll()
    {
        var manuals = Db.GetManuals(null);
        var libs = Db.GetLibraries().ToDictionary(l => l.Id);
        var items = manuals.Select(m =>
        {
            libs.TryGetValue(m.LibraryId, out var lib);
            var kb = ManualKb.Load(m.LibraryId);
            bool kbBuilt = kb != null && kb.Pages.Count > 0;
            bool isKbSource = kb != null && string.Equals(kb.ManualPath, m.FullPath, StringComparison.OrdinalIgnoreCase);
            return new
            {
                m.Id, m.LibraryId, m.LibraryName, m.Name, m.RelPath, m.Ext, m.SizeBytes, m.IsPrimary,
                m.FullPath,
                category = lib?.Category ?? "未分类",
                kbBuilt,
                kbIsSource = isKbSource,
                kbPages = isKbSource ? kb!.PageCount : 0,
                kbBuiltAt = isKbSource ? kb!.BuiltAt : "",
            };
        }).ToList();

        var groups = items
            .GroupBy(m => m.category)
            .Select(g => new
            {
                category = g.Key,
                libraries = g.GroupBy(m => m.LibraryName).Select(lg => new
                {
                    libraryId = lg.First().LibraryId,
                    libraryName = lg.Key,
                    count = lg.Count(),
                    kbBuilt = lg.Any(x => x.kbBuilt),
                    items = lg.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Name),
                }).OrderBy(l => l.libraryName),
            })
            .OrderBy(g => g.category);

        return new
        {
            total = items.Count,
            kbCount = items.Count(m => m.kbBuilt),
            libraryCount = items.Select(m => m.LibraryId).Distinct().Count(),
            groups,
        };
    }

    /// <summary>已知识库化的知识库清单（复用，避免重复消耗 token）。</summary>
    private object KbAll()
    {
        var libs = Db.GetLibraries();
        var list = new List<object>();
        foreach (var lib in libs)
        {
            var kb = ManualKb.Load(lib.Id);
            if (kb == null || kb.Pages.Count == 0) continue;
            list.Add(new
            {
                libraryId = lib.Id,
                libraryName = lib.Name,
                category = lib.Category,
                manualName = kb.ManualName,
                manualPath = kb.ManualPath,
                manualSize = kb.ManualSize,
                pageCount = kb.PageCount,
                totalChars = kb.TotalChars,
                visionPages = kb.VisionPages,
                builtAt = kb.BuiltAt,
                overview = kb.Overview.Length > 240 ? kb.Overview[..240] + "…" : kb.Overview,
                chunkCount = kb.Chunks.Count,
            });
        }
        return new
        {
            total = list.Count,
            items = list.OrderByDescending(x => ((dynamic)x).builtAt),
        };
    }

    /// <summary>查看某个知识库的内容（概览 + 分页文本，供「查看已存在的知识库」）。</summary>
    private object KbView(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        var kb = ManualKb.Load(libId);
        if (kb == null) return new { ok = false, message = "该库尚未建立知识库" };
        int page = (int)GetLong(args, "page");
        int size = (int)(GetLong(args, "size") is > 0 and <= 200 ? GetLong(args, "size") : 60);
        if (size <= 0) size = 60;

        var pages = kb.Pages;
        int totalPages = (int)Math.Ceiling(pages.Count / (double)size);
        if (page < 0) page = 0;
        if (totalPages > 0 && page >= totalPages) page = totalPages - 1;
        var slice = pages.Skip(page * size).Take(size);

        return new
        {
            ok = true,
            libraryId = kb.LibraryId,
            libraryName = kb.LibraryName,
            manualName = kb.ManualName,
            manualPath = kb.ManualPath,
            manualSize = kb.ManualSize,
            pageCount = kb.PageCount,
            totalChars = kb.TotalChars,
            visionPages = kb.VisionPages,
            builtAt = kb.BuiltAt,
            overview = kb.Overview,
            page = page,
            pageSize = size,
            totalPages,
            items = slice.Select(p => new
            {
                n = p.N,
                source = p.Source,
                chars = p.Chars,
                preview = p.Text.Length > 1200 ? p.Text[..1200] + "…" : p.Text,
            }),
        };
    }

    /// <summary>后台构建知识库（独立于旧设置页的 kbBuild，供新页面调用）。</summary>
    private object KbBuildAsync(JsonElement args) => KbBuild(args);

    /// <summary>
    /// 在指定分支里提问。流式回传 chatDelta / chatReasoning / chatTool / chatDone。
    /// 单库分支只装载该库知识库；跨库分支装载全部知识库（由 AiAssistant 决定检索范围）。
    /// </summary>
    private async Task<object> ChatAsk(JsonElement args)
    {
        long sid = GetLong(args, "sessionId");
        long bid = GetLong(args, "branchId");
        string question = GetString(args, "question");

        // 可选附图（多模态）：前端传 data URL 或纯 base64
        byte[]? imagePng = null;
        string thinkEffort = GetString(args, "thinkingEffort");
        try
        {
            string imgB64 = GetString(args, "image");
            if (imgB64.Length > 0)
            {
                int comma = imgB64.IndexOf(",");
                if (imgB64.StartsWith("data:") && comma > 0) imgB64 = imgB64[(comma + 1)..];
                imagePng = Convert.FromBase64String(imgB64);
                if (imagePng.Length > 6 * 1024 * 1024) imagePng = null;   // 上限 6MB
            }
        }
        catch { imagePng = null; }
        if (question.Length == 0) return new { ok = false, message = "问题为空" };

        var session = Chat.GetSession(sid);
        if (session == null) return new { ok = false, message = "会话不存在" };
        var branches = Chat.ListBranches(sid);
        var branch = branches.FirstOrDefault(b => b.Id == bid) ?? branches.FirstOrDefault();
        if (branch == null) return new { ok = false, message = "分支不存在" };

        bool cross = branch.Mode == "cross";

        // 历史（当前分支内，互相隔离）
        var history = Chat.GetMessages(branch.Id)
            .Select(m => new ChatMessage { Role = m.Role, Content = m.Content }).ToList();

        Chat.AppendMessage(branch.Id, "user", question);
        Push(new { type = "chatUser", sessionId = sid, branchId = branch.Id, content = question });

        var ai = AiSettings.Load(Db);
        if (!ai.Configured)
        {
            string msg = "尚未配置 AI（请在 设置 → AI 助手里填写接口与密钥）";
            Chat.AppendMessage(branch.Id, "assistant", msg);
            Push(new { type = "chatDone", sessionId = sid, branchId = branch.Id, content = msg, error = true });
            return new { ok = false, message = msg };
        }

        // 组装知识库：单库分支只装该库；跨库分支合并全部已建库的知识库
        List<LibraryKb> kbs = new();
        var missingKb = new List<string>();   // 没有知识库的库名（必须如实告知模型）
        if (cross)
        {
            foreach (var lib in Db.GetLibraries())
            {
                var k = ManualKb.Load(lib.Id);
                if (k != null && k.Chunks.Count > 0)
                {
                    // **必须给块文本加库名前缀** —— 旧代码注释写了「前缀库名以便引用」但**实际没做**。
                    // 后果：跨库检索命中后模型分不清文字来自哪个库，
                    // 实测「问 Damage、却拿 8Dio Solo Frame Drums 的手册作答」且毫无察觉。
                    foreach (var c in k.Chunks)
                        if (!c.Text.StartsWith("【" + lib.Name, StringComparison.Ordinal))
                            c.Text = $"【{lib.Name}】{c.Text}";
                    kbs.Add(k);
                }
                else missingKb.Add(lib.Name);
            }
        }
        else if (session.LibraryId is long lid)
        {
            var k = ManualKb.Load(lid);
            if (k != null && k.Chunks.Count > 0) kbs.Add(k);
            else missingKb.Add(session.LibraryName.Length > 0 ? session.LibraryName : $"库 {lid}");
        }

            if (kbs.Count == 0)
            {
                // 没有知识库不再直接退出：结构化工具/联网/截图问答都不依赖知识库，
                // 只有 search_manual 会检索不到。用占位 KB 让 Agent 正常走完流程。
                kbs.Add(new LibraryKb
                {
                    LibraryId = 0,
                    LibraryName = cross ? "（未建知识库）" : (session.LibraryName.Length > 0 ? session.LibraryName : "（未建知识库）"),
                    ManualName = "无",
                    Overview = "当前没有可用知识库。你仍可使用结构化查询工具（query_libraries / query_instruments）、" +
                               "联网搜索、以及用户提供的截图来回答；但无法检索说明书原文。若问题必须查手册，请提示用户先建知识库。",
                    Chunks = new List<KbChunk> { new KbChunk { Id = 1, Page = 0, Text = "（无知识库内容）" } },
                });
            }

        var kbMerged = kbs.Count == 1 ? kbs[0] : MergeKbs(kbs);
        // **把「哪些库没有知识库」明确写进 Overview** ——
        // 否则模型在跨库会话里检索到别的库的手册后会直接作答，
        // 既不说明「你问的这个库没建库」，也不标注内容出处（实测踩过）。
        if (missingKb.Count > 0)
        {
            var note = new System.Text.StringBuilder();
            note.AppendLine();
            note.AppendLine();
            note.AppendLine("【重要 · 知识库覆盖情况】");
            if (cross)
                note.AppendLine($"· 以下 {missingKb.Count} 个库**尚未建立知识库**，检索不到它们的说明书：" +
                                string.Join("、", missingKb.Take(40)) + (missingKb.Count > 40 ? " …" : ""));
            else
                note.AppendLine($"· **当前库「{missingKb[0]}」尚未建立知识库**，检索不到它的说明书。");
            note.AppendLine("· 因此 `search_manual` 若返回内容，**可能来自其它库**——引用前必须核对命中的库名是否与用户所问一致。");
            note.AppendLine("· 若用户问的正是上面这些库：**必须如实说明「该库还没建知识库」**，不要拿别的库的手册冒充，");
            note.AppendLine("  并建议用户在右侧「说明书」列表里选中文档后点「建库」。");
            kbMerged.Overview = (kbMerged.Overview ?? "") + note.ToString();
        }
        var libCtx = cross ? BuildCrossContext() : BuildLibraryContext(session.LibraryId ?? 0);

        var sb = new System.Text.StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reasoningSb = new System.Text.StringBuilder();
           // ══════════ 流式事件节流（性能优化）══════════
           // 背景：旧实现**每个 token** 都 Push 一条 WebView2 消息，前端每次都整段重渲染
           // Markdown —— 两侧都是 O(n²)，且 UI 线程的消息队列会被瞬间打满，
           // 表现为「提权弹窗出现时界面短暂无响应」（用户实测报告）。
           // 做法：把 delta / reasoning 累积到缓冲区，每 ~60ms 或攒够 3000 字符才成批推送；
           // 工具/步骤等**结构化事件**推送前先强制冲刷，保证事件顺序不乱。
           var streamLock = new object();
           var deltaBuf = new System.Text.StringBuilder();
           var reasonBuf = new System.Text.StringBuilder();
           long lastFlushTicks = DateTime.UtcNow.Ticks;
           const int FlushIntervalMs = 60;
           const int FlushMaxChars = 3000;

           void FlushStream(bool force)
           {
               string? d = null, r = null;
               lock (streamLock)
               {
                   long now = DateTime.UtcNow.Ticks;
                   long elapsedMs = (now - lastFlushTicks) / TimeSpan.TicksPerMillisecond;
                   bool due = elapsedMs >= FlushIntervalMs
                              || deltaBuf.Length >= FlushMaxChars
                              || reasonBuf.Length >= FlushMaxChars;
                   if (!force && !due) return;
                   if (deltaBuf.Length > 0) { d = deltaBuf.ToString(); deltaBuf.Clear(); }
                   if (reasonBuf.Length > 0) { r = reasonBuf.ToString(); reasonBuf.Clear(); }
                   lastFlushTicks = now;
               }
               // 🔴 **流式正文不能用低优先级（2026-09-24 修）** ——
                //   原来传 `lowPriority: true` ⇒ `DispatcherPriority.Background`（**最低优先级**）
                //   ⇒ UI 线程一忙（生成中就是忙的）这些消息就被【饿死】
                //   ⇒ **正文板块空白、直到全部输出完才一次性显示**（用户实测反馈）。
                //   ⇒ 改用默认优先级（Normal），让每个 delta 都能及时渲染。
                if (d != null) Push(new { type = "chatDelta", sessionId = sid, branchId = branch.Id, text = d });
               if (r != null) Push(new { type = "chatReasoning", sessionId = sid, branchId = branch.Id, text = r }, lowPriority: true);
           }

           AgentEventHandler ev = e =>
           {
               switch (e.Kind)
               {
                   case "delta":
                       lock (streamLock) deltaBuf.Append(e.Text);
                       FlushStream(false);
                       break;
                   case "reasoning":
                       reasoningSb.Append(e.Text);
                       lock (streamLock) reasonBuf.Append(e.Text);
                       FlushStream(false);
                       break;
                    case "tool":
                        FlushStream(true);   // 结构化事件前先冲刷，保证顺序
                        Push(new { type = "chatTool", sessionId = sid, branchId = branch.Id, name = e.ToolName, args = e.ToolArgs });
                        break;
                    case "toolresult":
                        FlushStream(true);
                        Push(new { type = "chatToolResult", sessionId = sid, branchId = branch.Id, name = e.ToolName, pages = e.Pages });
                        break;
                    case "steer":
                        // 插话引导已被 Agent 取走 → 通知界面（可在对话流里显示「已插入引导」）
                        Push(new { type = "chatSteer", sessionId = sid, branchId = branch.Id, text = e.Text });
                        break;
                    case "usage":
                        // **用量事件**：token 统计推给界面（问问AI 状态行用）
                        Push(new
                        {
                            type = "chatUsage",
                            sessionId = sid,
                            branchId = branch.Id,
                            step = e.Step,
                            pt = e.PromptTokens,
                            ct = e.CompletionTokens,
                            rt = e.ReasoningTokens,
                            ms = e.ElapsedMs,
                        });
                        break;
                   case "step":
                       FlushStream(true);
                       Push(new { type = "chatStep", sessionId = sid, branchId = branch.Id, text = e.Text });
                       break;
               }
           };

        // **建立本回合的取消源** —— 用户点「停止」时 Cancel 它，
        // 让流式生成与 Agent 工具循环立刻中止（旧实现传的是 CancellationToken.None，无法打断）。
        using var cts = new CancellationTokenSource();
        _chatCts[branch.Id] = cts;
        var ct = cts.Token;
        try
        {
            _agentClaims.Clear();   // 每轮对话开始时清空断言表（断言式 verifier）
            var toolCtx = BuildToolContext(session.LibraryId ?? 0);
            toolCtx.CurrentLibraryId = session.LibraryId;
            toolCtx.CurrentBranchId = branch.Id;
            var res = await AiAssistant.AskAsync(ai, kbMerged, question, history, libCtx, ev, ct, 5, !cross, toolCtx, imagePng, thinkEffort);
            FlushStream(true);   // 收尾：把缓冲区里最后一批 token 推出去，避免丢字
            sw.Stop();
            string answer = res.Answer ?? "";
            Chat.AppendMessage(branch.Id, "assistant", answer, reasoningSb.ToString());
            Push(new
            {
                type = "chatDone", sessionId = sid, branchId = branch.Id,
                content = answer, reasoning = reasoningSb.ToString(),
                seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                cross,
                crossSuggest = res.CrossSuggest,
                crossReason = res.CrossReason,
            });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            sw.Stop();
                // **带错误码的失败**：让界面能显示「错误码 + 解读 + 建议」，而不是一句无从下手的消息。
                string code = ex is AiException ae ? ae.Code : AiErrors.Unknown;
                string detail = ex is AiException ae2 ? ae2.Detail : ex.Message;
                string msg = AiErrors.Describe(code, detail);
                Log($"[AI失败] {code} {detail}");
                Chat.AppendMessage(branch.Id, "assistant", msg);
                Push(new
                {
                    type = "chatDone",
                    sessionId = sid,
                    branchId = branch.Id,
                    content = msg,
                    error = true,
                    errorCode = code,
                    errorHint = AiErrors.Advice(code),
                });
            return new { ok = false, message = ex.Message };
        }
            finally { _chatCts.Remove(branch.Id); }
    }

    // ══════════════════ 重复内容检测 ══════════════════

    private volatile bool _dupScanning;

    /// <summary>启动跨库重复大文件扫描（后台，带进度）。</summary>
    private object DupScan(JsonElement args)
    {
        if (_dupScanning) return new { started = false, reason = "已有扫描在进行中" };
        long minMb = GetLong(args, "minMb");
        long minBytes = (minMb is > 0 and <= 1024 ? minMb : 5) * 1024 * 1024;

        _dupScanning = true;
        _ = Task.Run(() =>
        {
            try
            {
                var prog = new Progress<DupProgress>(p => Push(new
                {
                    type = "dupProgress",
                    phase = p.Phase,
                    current = p.Current,
                    total = p.Total,
                    message = p.Message,
                }));
                var groups = DuplicateFinder.FindDuplicateFiles(Db, minBytes, prog, CancellationToken.None);
                long reclaim = groups.Sum(g => g.ReclaimableBytes);
                Push(new
                {
                    type = "dupDone",
                    groupCount = groups.Count,
                    reclaimBytes = reclaim,
                    groups = groups.Take(200).Select(g => new
                    {
                        sizeBytes = g.SizeBytes,
                        reclaimBytes = g.ReclaimableBytes,
                        files = g.Files.Select(f => new { library = f.LibraryName, path = f.FullPath }),
                    }),
                });
            }
            catch (Exception ex)
            {
                Log($"重复扫描失败：{ex}");
                Push(new { type = "dupError", error = ex.Message });
            }
            finally { _dupScanning = false; }
        });

        return new { started = true, minMb = minBytes / 1024 / 1024 };
    }

    // ══════════════════ 重复库清理：转移注册 / 删除库 ══════════════════

    // ══════════════════ 非标准库：加入 Quick-Load ══════════════════

    /// <summary>
    /// **把音色库目录以 Windows 快捷方式加入 Kontakt 的 Quick-Load**。
    ///
    /// 为什么这是「非标准库」（无 .nicnt）的正当出路：
    ///   · Kontakt 的 **Libraries 标签页**要求 `.nicnt` 里有 NI 签发的许可字段（HU/JDX），
    ///     没有 .nicnt 的库**无法**出现在那里 —— 伪造 .nicnt 属于绕过授权，本工具不做；
    ///   · 但 Kontakt 的 **Quick-Load 浏览器**会显示
    ///     `UserData\Kontakt 8\QuickLoad\{Bank,Instr,Multi}` 三个目录的内容，
    ///     并且**支持 Windows 快捷方式指向文件夹**；
    ///   · 于是在 `Instr\` 下建一个指向库目录的 .lnk，该库就会出现在 Quick-Load 里，
    ///     点开即可浏览并加载 .nki —— 与「入库」的使用体验基本等价，且完全正当。
    ///
    /// 用 .lnk 而不是复制文件：库动辄几十 GB，复制不可行；
    /// 快捷方式还能跟随库移动（重指向即可）。
    /// </summary>
    private object AddToQuickLoad(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        string category = GetString(args, "category");   // instr（默认）/ bank / multi
        if (category.Length == 0) category = "instr";
        category = category.ToLowerInvariant() switch
        {
            "bank" => "Bank",
            "multi" => "Multi",
            _ => "Instr",
        };

        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };
        if (string.IsNullOrEmpty(lib.Path) || !Directory.Exists(lib.Path))
            return new { ok = false, message = $"库目录不存在：{lib.Path}" };

            // 定位 Quick-Load 根：**便携版与正式版路径不同**（用户实测报告）
            //   · 便携版：<便携根>\UserData\Kontakt 8\QuickLoad
            //   · 正式版：%LOCALAPPDATA%\Native Instruments\Kontakt 8\QuickLoad
            // 旧实现只看便携根 → 换正式版后必然报「找不到 QuickLoad 目录」。
            string kexe = Db.GetMeta("kontakt_exe", "");
            string? qlRoot = PortableKontaktStore.ResolveQuickLoadRoot(kexe, createIfMissing: true);
            if (qlRoot == null)
                return new { ok = false, message = "找不到 Kontakt 的 QuickLoad 目录。请先设置 Kontakt 主程序路径，并至少运行过一次 Kontakt（让它创建用户数据目录）。" };

        string dir = Path.Combine(qlRoot, category);
        try { Directory.CreateDirectory(dir); } catch { }
        if (!Directory.Exists(dir)) return new { ok = false, message = $"QuickLoad\\{category} 目录不可用" };

        // 文件名要合法：去掉路径非法字符
        string safe = new string(lib.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (safe.Length == 0) safe = "library-" + libId;
        string lnkPath = Path.Combine(dir, safe + ".lnk");

            try
            {
            // 用 IShellLink COM 创建 .lnk（Unicode 原生）
            // **不用 WScript.Shell** —— 它的 TargetPath 会把路径转 ANSI，
            // 中文代码页下遇到 ø 这类字符会抛 E_INVALIDARG（实测踩到）。
            bool made = ShellLink.Create(
                lnkPath,
                lib.Path,
                "Kontakt 音色库：" + lib.Name + "（由 Kontakt 音色库管理器加入 Quick-Load）",
                lib.Path);
            if (!made)
            {
                // 极少数情况下仍可能失败（如目录权限），回退到 ASCII 安全文件名再试一次
                string ascii = new string(safe.Select(c => c <= 127 ? c : '_').ToArray()).Trim();
                if (ascii.Length == 0) ascii = "library-" + libId;
                string lnk2 = Path.Combine(dir, ascii + ".lnk");
                made = ShellLink.Create(lnk2, lib.Path, "Kontakt library: " + lib.Name, lib.Path);
                if (made)
                {
                    Log($"[QuickLoad] 已加入（ASCII 名回退）：{lib.Name} → {lnk2}");
                    return new
                    {
                        ok = true,
                        message = $"已把「{lib.Name}」加入 Quick-Load\\{category}（快捷方式用了安全文件名「{ascii}」）。重启 Kontakt 后可见。",
                        shortcut = lnk2,
                        target = lib.Path,
                        category,
                    };
                }
                return new { ok = false, message = $"创建快捷方式失败：无法在 {dir} 下写入 .lnk" };
            }

            Log($"[QuickLoad] 已加入：{lib.Name} → {lnkPath}");
            return new
            {
                ok = true,
                message = $"已把「{lib.Name}」加入 Quick-Load\\{category}。重启 Kontakt 后可在 Quick-Load 浏览器里看到它，点开即可加载乐器。",
                shortcut = lnkPath,
                target = lib.Path,
                category,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = "创建快捷方式失败：" + ex.Message };
        }

    }

        /// <summary>
        /// **为指定说明书建知识库**（预览弹窗底部「建立知识库」按钮调用）。
        /// 与 `kbBuild` 的区别：可指定具体哪一本（一个库常有多本说明书）。
        /// </summary>
        private object KbBuildManual(JsonElement args)
        {
            long libId = GetLong(args, "libraryId");
            string manualName = GetString(args, "path");   // 传文件名，按名匹配更稳
            var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
            if (lib == null) return new { ok = false, message = "未找到该音色库" };
            var manuals = Db.GetManuals(libId);
            var target = manuals.FirstOrDefault(m => string.Equals(m.Name, manualName, StringComparison.OrdinalIgnoreCase))
                         ?? AiAssistant.PickBestManual(manuals);
            if (target == null) return new { ok = false, message = "该库没有可用的说明书文档" };
            _ = Task.Run(async () =>
            {
                try
                {
                    var prog = new Progress<KbProgress>(p => Push(new
                    {
                        type = "kbProgress", libraryId = libId, phase = p.Phase,
                        total = p.Total, current = p.Current, message = p.Message,
                    }));
                    var ai = AiSettings.Load(Db);
                    await ManualKb.BuildAsync(libId, lib.Name, target, ai.Configured ? ai : null, prog);
                    // **带 summary 的 kbDone** —— 前端据此刷新「知识库」与「说明书」两个列表；
                    // 不带 summary 时列表不会刷新，用户会看到「已建库但列表仍显示未建库」（实测反馈）。
                    var doneKb = ManualKb.Load(libId);
                    Push(new
                    {
                        type = "kbDone",
                        libraryId = libId,
                        libraryName = lib.Name,
                        summary = new
                        {
                            pages = doneKb?.PageCount ?? 0,
                            chunks = doneKb?.Chunks.Count ?? 0,
                            chars = doneKb?.TotalChars ?? 0,
                            visionPages = doneKb?.VisionPages ?? 0,
                            manual = target.Name,
                        },
                    });
                }
                catch (Exception ex)
                {
                    Log($"[建库失败] {lib.Name} / {target.Name}：{ex.Message}");
                    Push(new { type = "kbError", libraryId = libId, error = ex.Message });
                }
            });
            return new { ok = true, libraryId = libId, manual = target.Name, ext = target.Ext };
        }
    /// <summary>把 Quick-Load 里指向某个库的快捷方式删掉。</summary>
    /// <summary>列出 Quick-Load 里已有的目标路径（前端据此把非标准库标为「已处理」）。</summary>
    /// <summary>Agent 工具调用轨迹（调研报告第二优先：让失败归因与成本可见）。</summary>
        /// <summary>
        /// **按新规则重扫全部库的说明书清单**（机械过滤生效后清理旧数据）。
        /// 只找文档扩展名再过滤，不重扫全部文件，秒级~分钟级完成。
        /// </summary>
        /// <summary>
        /// **说明书预览**（用户要求：点说明书先看内容、确认是说明书再决定建库）。
        /// 返回 kind(pdf|text) / pages / text（**建库用的就是这份文本，所见即所得**）/ imageUrl / isManualHint。
        /// </summary>
        private object ManualPreview(JsonElement args)
        {
            long libId = GetLong(args, "libraryId");
            string path = GetString(args, "path");
            int page = (int)Math.Max(1, GetLong(args, "page"));
            bool wantImage = args.ValueKind == JsonValueKind.Object
                             && args.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new { ok = false, message = "文件不存在：" + path };
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            long size = 0; try { size = new FileInfo(path).Length; } catch { }
            string hint = LibraryScanner.LooksLikeManual(Path.GetFileName(path), size)
                ? "机械规则判定：**像说明书**（文件名命中手册关键词，或体积够大）"
                : "机械规则判定：**可能不是说明书**（文件名像 readme/license 且体积偏小）";
            try
            {
                if (ext == "pdf")
                {
                    int pageCount = PdfText.GetPageCount(path);
                    if (page < 1 || page > pageCount)
                        return new { ok = false, message = $"页号超出范围（共 {pageCount} 页）" };
                    var texts = PdfText.ExtractAllText(path);
                    string body = page - 1 < texts.Count ? texts[page - 1] : "";
                    string? imgUrl = null;
                    bool imgBlank = false;
                    if (wantImage)
                    {
                        try
                        {
                            var png = PdfText.RenderPagePng(path, page - 1, 1.4);
                            if (png != null)
                            {
                                string dir = Path.Combine(AppPaths.DataDir, "kb", libId.ToString(), "preview");
                                Directory.CreateDirectory(dir);
                                File.WriteAllBytes(Path.Combine(dir, $"p{page}.png"), png);
                                imgUrl = $"https://kontakt-kb/{libId}/preview/p{page}.png";
                            }
                        }
                        catch { }
                    }
                    return new
                    {
                        ok = true, kind = "pdf", pages = pageCount, page, text = body, chars = body.Length,
                        imageUrl = imgUrl, imageBlank = imgBlank, name = Path.GetFileName(path), sizeBytes = size, ext, isManualHint = hint,
                    };
                }
                string text = ManualKb.ReadPlainManualPublic(path, ext);
                return new
                {
                    ok = true, kind = "text", pages = 1, page = 1, text, chars = text.Length,
                    imageUrl = (string?)null, name = Path.GetFileName(path), sizeBytes = size, ext, isManualHint = hint,
                };
            }
            catch (Exception ex) { return new { ok = false, message = "预览失败：" + ex.Message }; }
        }
        /// <summary>渲染 PDF 指定页为 PNG 并返回可访问 URL（预览用，按需调用）。</summary>
        private object ManualPageImage(JsonElement args)
        {
            long libId = GetLong(args, "libraryId");
            string path = GetString(args, "path");
            int page = (int)Math.Max(1, GetLong(args, "page"));
            try
            {
                var png = PdfText.RenderPagePng(path, page - 1, 1.4);
                if (png == null) return new { ok = false, message = "该页渲染失败" };
                string dir = Path.Combine(AppPaths.DataDir, "kb", libId.ToString(), "preview");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"p{page}.png"), png);
                return new { ok = true, url = $"https://kontakt-kb/{libId}/preview/p{page}.png" };
            }
            catch (Exception ex) { return new { ok = false, message = ex.Message }; }
        }
        private object RescanManuals(JsonElement args)
        {
            int removedTotal = 0, addedTotal = 0, libs = 0;
            var details = new List<object>();
            foreach (var lib in Db.GetLibraries())
            {
                try
                {
                    var recs = LibraryScanner.RescanManuals(lib.Path, lib.Name);
                    var (rm, ad) = Db.ReplaceManuals(lib.Id, recs);
                    removedTotal += rm; addedTotal += ad; libs++;
                    if (rm != ad) details.Add(new { name = lib.Name, before = rm, after = ad });
                    if (libs % 20 == 0)
                        Push(new { type = "scanProgress", progress = new { phase = "重扫说明书", librariesTotal = 0, librariesDone = libs, filesIndexed = addedTotal, currentLibrary = lib.Name, elapsed = "" } });
                }
                catch { }
            }
            Log($"[重扫说明书] {libs} 个库，删除 {removedTotal} 条，写入 {addedTotal} 条");
            return new
            {
                ok = true, libs, removed = removedTotal, added = addedTotal,
                changed = details.Take(60),
                message = $"重扫完成：{libs} 个库，删除 {removedTotal} 条、写入 {addedTotal} 条（净减 {removedTotal - addedTotal} 条非说明书文档）",
            };
        }
        /// <summary>
        /// **插话引导**：Agent 正在工作时用户补一句引导。
        /// 不新建回合、不打断，而是放进该分支队列；Agent 循环在下一个步骤边界取走，
        /// 作为一条 user 消息追加进对话，模型据此调整方向（对标 DSH 的 steering）。
        /// </summary>
        private object ChatSteer(JsonElement args)
        {
            long branchId = GetLong(args, "branchId");
            string text = GetString(args, "text");
            if (string.IsNullOrWhiteSpace(text)) return new { ok = false, message = "内容为空" };
            if (branchId <= 0) return new { ok = false, message = "缺少 branchId" };
            if (!_chatSteer.TryGetValue(branchId, out var q))
            {
                q = new ConcurrentQueue<string>();
                _chatSteer[branchId] = q;
            }
            q.Enqueue(text.Trim());
            try { Chat.AppendMessage(branchId, "user", text.Trim()); } catch { }
            Log($"[插话引导] branch={branchId}：{text.Trim()}");
            return new { ok = true, queued = q.Count, message = "已插入引导，Agent 会在下一步看到" };
        }
        /// <summary>
        /// **停止当前 AI 回合**（用户点「停止」按钮）。
        /// Cancel 该分支的 CancellationTokenSource → 流式生成与 Agent 工具循环都会中止。
        /// 未指定 branchId 时，停掉该会话下所有分支（用户不一定知道分支 id）。
        /// </summary>
        private object ChatStop(JsonElement args)
        {
            long branchId = GetLong(args, "branchId");
            long sessionId = GetLong(args, "sessionId");
            int stopped = 0;

            if (branchId > 0)
            {
                if (_chatCts.TryGetValue(branchId, out var c1)) { try { c1.Cancel(); stopped++; } catch { } }
            }
            else if (sessionId > 0)
            {
                var ids = Chat.ListBranches(sessionId).Select(b => b.Id).ToList();
                foreach (var id in ids)
                    if (_chatCts.TryGetValue(id, out var c2)) { try { c2.Cancel(); stopped++; } catch { } }
            }
            else
            {
                foreach (var kv in _chatCts.ToList())
                    { try { kv.Value.Cancel(); stopped++; } catch { } }
            }

            Log($"[停止] branchId={branchId} sessionId={sessionId} 已取消 {stopped} 个回合");
            return new { ok = true, stopped, message = stopped > 0 ? "已请求停止" : "当前没有正在进行的回答" };
        }
        /// <summary>
        /// 音色地图三期：**构建乐器级地图**（聚合已有采样特征，**不重新跑模型**）。
        /// 后台执行，进度靠轮询。
        /// </summary>
        private object AudioMapBuildInstruments()
        {
            if (AudioMapTask.IsRunning)
                return new { ok = true, running = true, message = "已有任务在跑。" };
            if (Db.CountMertFeatures() < 3)
                return new { ok = false, message = "还没有采样级特征 —— 请先点「开始分析」完成特征提取。" };

            AudioMapTask.StartInstrumentMap(Db, (d, t2, cur, fin) => { });
            return new
            {
                ok = true, running = true,
                message = "正在构建乐器级地图（聚合已有特征 + 降维）—— 不重新跑模型，应该很快。",
            };
        }

        /// <summary>读乐器级地图点（含库名）。</summary>
        private object[] LoadInstrumentPoints()
        {
            var rows = Db.LoadInstrumentMap();
            if (rows.Count == 0) return Array.Empty<object>();
            var libNames = new Dictionary<long, string>();
            foreach (var l in Db.GetLibraries()) libNames[l.Id] = l.Name;
            return rows.Select(r => (object)new
            {
                x = r.X, y = r.Y, clipId = 0, cluster = r.Cluster,
                lib = libNames.TryGetValue(r.LibId, out var ln) ? ln : "?",
                file = r.Name + (r.Count > 1 ? $"（{r.Count} 个采样）" : ""),
            }).ToArray();
        }

        /// <summary>
        /// **算每个簇的元信息**（音色地图任务 3）：规模、占比最高的库、代表样本。
        ///
        /// **为什么要它**：地图上只有 `cluster 0/1/2` 这种编号，**用户看不懂**。
        /// 给出「这个簇主要是什么库、代表样本叫什么」，地图才有可读性。
        /// </summary>
        private object[] LoadClusterMeta()
        {
            var pts = Db.LoadUmapCoords();
            if (pts.Count == 0) return Array.Empty<object>();

            var libNames = new Dictionary<long, string>();
            foreach (var l in Db.GetLibraries()) libNames[l.Id] = l.Name;
            var clipNames = new Dictionary<long, string>();
            foreach (var l in Db.GetLibraries())
            {
                try { foreach (var c in Db.GetAnalyzableClips(l.Id)) clipNames[c.Id] = c.Name; } catch { }
            }

            var groups = pts.Where(p => p.Cluster >= 0).GroupBy(p => p.Cluster)
                             .OrderByDescending(g => g.Count());
            var outp = new List<object>();
            foreach (var g in groups)
            {
                var list = g.ToList();
                // 占比最高的库（「这个簇主要来自哪个库」）
                var topLib = list.GroupBy(p => p.LibraryId)
                                 .OrderByDescending(x => x.Count()).First().Key;
                // 代表样本：**离簇质心最近的点**（比随便取第一个更有代表性）
                double cx = list.Average(p => p.X), cy = list.Average(p => p.Y);
                var rep = list.OrderBy(p =>
                {
                    double dx = p.X - cx, dy = p.Y - cy;
                    return dx * dx + dy * dy;
                }).First();

                outp.Add(new
                {
                    cluster = g.Key,
                    count = list.Count,
                    topLibrary = libNames.TryGetValue(topLib, out var ln) ? ln : "?",
                    repFile = clipNames.TryGetValue(rep.ClipId, out var cn) ? cn : "?",
                    repClipId = rep.ClipId,
                    cx, cy,
                });
            }
            return outp.ToArray();
        }

        /// <summary>读地图点（含库名，供界面着色与详情用）。</summary>
        private object[] LoadMapPoints()
        {
            var pts = Db.LoadUmapCoords();
            if (pts.Count == 0) return Array.Empty<object>();
            var libNames = new Dictionary<long, string>();
            foreach (var l in Db.GetLibraries()) libNames[l.Id] = l.Name;
            var clipNames = new Dictionary<long, string>();
            foreach (var l in Db.GetLibraries())
            {
                try { foreach (var c in Db.GetAnalyzableClips(l.Id)) clipNames[c.Id] = c.Name; } catch { }
            }
            return pts.Select(p => (object)new
            {
                x = p.X, y = p.Y, clipId = p.ClipId, cluster = p.Cluster,
                lib = libNames.TryGetValue(p.LibraryId, out var ln) ? ln : "?",
                file = clipNames.TryGetValue(p.ClipId, out var cn) ? cn : "?",
            }).ToArray();
        }

        /// <summary>音色地图：读当前分析状态（含可分析范围与覆盖率）。</summary>
        private (int Total, int Decodable, int Extracted, string Text) _coverageCache;
        private DateTime _coverageAt = DateTime.MinValue;

        /// <summary>
        /// 覆盖率统计（**带 30 秒缓存**）—— 修「数字每次都在变」的 bug。
        /// 旧实现每次 status 轮询都重新遍历全部库（每库取前 400 个片段），
        /// 结果会随扫描状态漂移（实测同机出现过 4934 → 4917 的跳动），
        /// 导致进度分母变化、看起来像「跑到 99.7% 就不动了」。
        /// </summary>
        private (int Total, int Decodable, int Extracted, string Text) GetCoverageCached()
        {
            if ((DateTime.Now - _coverageAt).TotalSeconds < 30 && _coverageCache.Total > 0) return _coverageCache;
            _coverageCache = AudioMapTask.Survey(Db);
            _coverageAt = DateTime.Now;
            return _coverageCache;
        }

        private object AudioMapStatus()
        {
            var st = AudioMapTask.Snapshot();
            int mertDone = Db.CountMertFeatures();
            var cov = GetCoverageCached();

            // **按阶段给出各自的进度语义** —— 同时修「重算地图没有进度显示」与「跑完 99.7%」。
            // 旧实现一律按「提取阶段」算 done/total（已完成轮数 ÷ 可解码片段数），
            // 在降维阶段会算出荒谬百分比（如 5/4934 = 0.1%），看起来就像「没进度」。
            string phase = st.Running
                ? (st.Stage == AudioMapTask.Stage.Reducing ? "reduce"
                   : st.Stage == AudioMapTask.Stage.Clustering ? "cluster" : "extract")
                : "idle";

            int done, total;
            if (phase == "extract" || phase == "reduce") { done = st.Done; total = st.Total; }
            else { done = mertDone; total = mertDone; }

            double percent = (phase == "extract" || phase == "reduce")
                ? (st.Total > 0 ? st.Done * 100.0 / st.Total : 0)
                : (mertDone > 0 ? 100 : 0);
            return new
            {
                ok = true,
                running = st.Running,
                stage = st.Stage.ToString(),
                phase,
                done,
                total,
                percent = Math.Round(percent, 1),
                failed = st.Failed,
                mertDone,
                elapsedText = st.ElapsedText,
                etaText = phase == "reduce" ? "" : st.EtaText,
                message = st.Message,
                error = st.Error,
                extractState = st.ExtractState,
                umapState = st.UmapState,
                clusterState = st.ClusterState,
                coverageText = cov.Text,
                gpuUsed = MertFeatures.LastGpuUsed,
                gpuError = MertFeatures.LastGpuError,
                mapPoints = LoadMapPoints(),
                instrumentPoints = LoadInstrumentPoints(),
                clusterMeta = LoadClusterMeta(),
            };
        }

        /// <summary>
        /// 音色地图：**启动/继续后台提取 MERT 嵌入**。
        /// **重活在后台线程**（全量约 86 分钟），本方法立即返回；进度由前端轮询 audioMapStatus 获取。
        /// </summary>
        private object AudioMapStart(JsonElement args)
        {
            bool force = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("force", out var frEl)
                         && frEl.ValueKind == JsonValueKind.True;   // **重新提取（覆盖）**
            bool useGpu = false;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("useGpu", out var gEl))
                useGpu = gEl.ValueKind == JsonValueKind.True;
            int mthreads = (int)GetLong(args, "threads");

            if (AudioMapTask.IsRunning)
                return new { ok = true, running = true, message = "分析已在运行中。" };

            var mp = MertFeatures.DefaultModelPath;
            if (!File.Exists(mp))
                return new
                {
                    ok = false,
                    message = "找不到 MERT 模型文件。",
                    expected = mp,
                    hint = "需要把 mert_uint8.onnx 放到上述路径（约 117 MB）。",
                };

            MertFeatures.ResetSession();   // **换模式必须重建会话**（否则会出现「勾了 GPU 但还在用 CPU」）
            AudioMapTask.StartExtraction(Db, mp, (done, total, cur, finished) => { /* 前端轮询取进度 */ }, useGpu, mthreads, force);
            return new
            {
                ok = true,
                running = true,
                message = "已在后台开始提取 MERT 音色嵌入 —— 可以离开这个标签页，任务会继续；" +
                          "**全量约 86 分钟**，随时可暂停（已完成的都已保存，继续时不重复跑）。",
            };
        }

        /// <summary>音色地图：暂停当前分析（已完成的都已落盘）。</summary>
        private object AudioMapPause()
        {
            AudioMapTask.RequestCancel();
            return new { ok = true, message = "已请求暂停 —— 当前片段跑完就停。" };
        }

        /// <summary>
        /// 音色地图：**重算 2D 地图与聚类**（UMAP + 网格聚类）。
        /// **UMAP 是全局优化，几千个点要跑很多轮 —— 必须在后台**（本方法立即返回，进度靠轮询）。
        /// </summary>
        private object AudioMapRebuild(JsonElement args)
        {
            if (AudioMapTask.IsRunning)
                return new { ok = true, running = true, message = "已有任务在跑，请等它结束。" };

            int have = Db.CountMertFeatures();
            if (have < 3)
                return new
                {
                    ok = false,
                    message = $"还没有足够的 MERT 特征（当前 {have} 个）—— 请先点「开始分析」完成特征提取。",
                };

            int neighbors = (int)GetLong(args, "neighbors");
            if (neighbors <= 0) neighbors = 15;
            if (neighbors > 100) neighbors = 100;
            string metric = "cosine";
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("metric", out var mEl)
                && mEl.ValueKind == JsonValueKind.String)
                metric = mEl.GetString() ?? "cosine";

            AudioMapTask.StartReduce(Db, (done, total, cur, finished) => { /* 前端轮询取进度 */ }, neighbors, metric);
            return new
            {
                ok = true,
                running = true,
                message = $"已开始降维（{have} 个点，n_neighbors={neighbors}，度量={metric}）—— 进度会实时刷新。",
            };
        }

        /// <summary>
        /// **导出地图选区为 CSV**（框选批量操作之一）。
        /// 写到 `data\exports\map-selection-<时间戳>.csv`，**返回路径供界面提示**。
        /// </summary>
        private object ExportMapSelection(JsonElement args)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("rows", out var rowsEl)
                    || rowsEl.ValueKind != JsonValueKind.Array)
                    return new { ok = false, message = "没有可导出的行" };

                var dir = System.IO.Path.Combine(AppPaths.DataDir, "exports");
                Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, $"map-selection-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("音色库,文件名,聚类,X,Y,clipId");
                int cnt = 0;
                foreach (var row in rowsEl.EnumerateArray())
                {
                    string S(string k) => row.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    string N(string k) => row.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.ToString() : "";
                    sb.AppendLine($"{Csv(S("lib"))},{Csv(S("file"))},{N("cluster")},{N("x")},{N("y")},{N("clipId")}");
                    cnt++;
                }
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));   // 带 BOM，Excel 打开中文不乱码
                return new { ok = true, path, rows = cnt, message = $"已导出 {cnt} 行" };
            }
            catch (Exception ex) { return new { ok = false, message = ex.Message }; }
        }

        /// <summary>CSV 字段转义（含逗号/引号/换行时用引号包起来）。</summary>
        private static string Csv(string s)
        {
            s ??= "";
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>音色地图：某个片段的详情 + 最相似的若干项（纯查表，毫秒级）。</summary>
        private object AudioMapDetail(JsonElement args)
        {
            long clipId = GetLong(args, "clipId");
            // 🔴 **必须查 mert_features，不是 audio_features** —— 地图是档 2（768 维）建的，
            // 而 audio_features 是档 1（32 维手工声学特征）、实测是空表。
            // 用错表的后果：详情永远返回「没有特征」⇒ 所有簇都无法试听。
            var vec = Db.GetMertFeature(clipId);
            if (vec == null)
                return new { ok = false, error = "这个片段还没有 MERT 特征（可能未提取过，或格式不支持）。" };

            var nn = Db.FindSimilarMert(vec, 12, clipId);

            // 建 clip → (库名, 相对路径, 文件名, 绝对路径, 扩展名, 库路径) 映射（供详情 + 试听 URL）
            var clipMap = new Dictionary<long, (string Lib, string RelPath, string Name, string Full, string Ext, string LibPath)>();
            foreach (var lib in Db.GetLibraries())
            {
                try
                {
                    foreach (var c in Db.GetAnalyzableClips(lib.Id))
                        clipMap[c.Id] = (lib.Name, c.RelPath, c.Name,
                                         System.IO.Path.Combine(lib.Path, c.RelPath), c.Ext, lib.Path);
                }
                catch { }
            }
            clipMap.TryGetValue(clipId, out var me);

            // **可播放 URL**：复用已有的 CacheAudio（与「问问AI」的试听同一套）
            string? myUrl = me.Full != null ? CacheAudio(me.Full, me.Ext, me.LibPath) : null;

            return new
            {
                ok = true,
                clipId,
                file = me.Name ?? "",
                path = me.RelPath ?? "",
                library = me.Lib ?? "",
                cluster = "—",
                audioUrl = myUrl,
                similar = nn.Select(x =>
                {
                    clipMap.TryGetValue(x.ClipId, out var m);
                    return new
                    {
                        clipId = x.ClipId,
                        score = Math.Round(x.Score, 4),
                        file = m.Name ?? "?",
                        library = m.Lib ?? "?",
                        audioUrl = m.Full != null ? CacheAudio(m.Full, m.Ext, m.LibPath) : null,
                    };
                }).ToList(),
            };
        }

        /// <summary>
        /// **批量提取音色声学特征**（调研报告「远期」第 7 项 · 档 1）—— 界面入口。
        /// 只处理可解码格式（wav/ogg/aif）；Kontakt 专有的 .ncw/.nkx 会被跳过并计数（硬边界）。
        /// </summary>
        /// <summary>
        /// **启动/继续后台提取档 1 声学特征**（32 维可解释特征）。
        ///
        /// **🔴 为什么必须改后台**：原实现**在 RPC handler 里直接跑双重循环**，
        /// **阻塞 UI 线程** —— 实测表现为「点击按钮后界面无响应一段时间」（用户反馈）。
        /// 现在改成后台任务，**进度靠轮询 audioFeatureStatus**，与音色地图同一套机制。
        ///
        /// **⚠️ 同时修了两个 bug**（原实现有、但只在 AgentActions 那份修过）：
        ///   · 用了 GetAudioClips（**随机 + 每库 400 上限**）⇒ 改 GetAnalyzableClips；
        ///   · 用了 CanDecode（**不含 .ncw**）⇒ 改 CanAnalyze + ExtractAny。
        /// </summary>
        private object ExtractAudioFeatures(JsonElement args)
        {
            long libId = GetLong(args, "libraryId");
            int maxClips = (int)GetLong(args, "maxClips");
            int threads = (int)GetLong(args, "threads");   // 0 = 自动（留 2 核给系统）
            bool force = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("force", out var fEl)
                         && fEl.ValueKind == JsonValueKind.True;   // **重新提取（覆盖）**

            if (AudioFeatureTask.IsRunning)
                return new { ok = true, running = true, message = "特征提取已在运行中。" };
            // 🔴 **任务互斥**：MERT 任务正在跑时不允许启动
            if (AudioMapTask.IsRunning)
                return new { ok = false, message = "MERT 任务（音色地图）正在运行 —— 两个任务不能同时跑，请先暂停它。" };

            int have = Db.CountAudioFeatures();
            AudioFeatureTask.Start(Db, libId, maxClips, threads, (done, total, cur, fin) => { }, force);
            return new
            {
                ok = true,
                running = true,
                message = force
                    ? $"已在后台【重新提取全部】—— 会覆盖已有的 {have} 个特征（用于特征逻辑改版后重提）。"
                    : (have > 0
                    ? $"已在后台继续提取（已提取 {have} 个）—— 进度见日志。"
                    : "已在后台开始提取声学特征（32 维、不用模型，比音色地图快）—— 进度见日志。"),
            };
        }

        /// <summary>
        /// **关键词语义筛选**（方案 A）—— 用【已有的命名语义】筛，不依赖跨模态模型。
        ///
        /// **为什么这样做**：用户真实需求是「**打击感强 + 尖锐的 + 军鼓**」——
        /// 前面那个是「感觉」（靠 TimbreFilter），后面那个是「**类别**」（靠命名）。
        /// **而命名语义到处都是、且是人工标注的（比模型猜的准）**：
        ///   · **采样文件名**：`SD_rim_01.wav`、`snare_ghost.wav`
        ///   · **采样相对路径**：`Samples\Snare\...` ← **路径里的目录名往往就是乐器名**
        ///   · **音色库名**：`Studio Drummer`、`Abbey Road Drums`
        ///   · **用户标签**：自己打的「打击乐」
        ///
        /// **⚠️ 局限（如实说明）**：**前提是「命名规范」** —— 若某个库的采样叫 `Sample_001.wav`，
        /// 关键词就失效；那时只能靠库名 / 乐器名 / MERT 相似度。
        ///
        /// **只读表、毫秒级**。
        /// </summary>
        private object SemanticFilterRpc(JsonElement args)
        {
            string kw = "";
            bool mFile = true, mPath = true, mLib = false, mTag = false, relaxInst = false;
            if (args.ValueKind == JsonValueKind.Object)
            {
                if (args.TryGetProperty("keywords", out var kEl) && kEl.ValueKind == JsonValueKind.String)
                    kw = (kEl.GetString() ?? "").Trim();
                bool Flag(string name, bool def) =>
                    args.TryGetProperty(name, out var e2) && e2.ValueKind == JsonValueKind.True ? true
                    : args.TryGetProperty(name, out var e3) && e3.ValueKind == JsonValueKind.False ? false : def;
                mFile = Flag("matchFile", true);
                mPath = Flag("matchPath", true);
                mLib = Flag("matchLibrary", false);
                mTag = Flag("matchTag", false);
                // **库级放宽**：某库若有【乐器名】命中关键词，则该库的采样全部纳入
                // （**精度下降但能覆盖缩写命名的库**，如 HAR_/SMP_/DIWET_ —— 见探测结论）
                relaxInst = Flag("relaxByInstrument", false);
            }

            if (string.IsNullOrWhiteSpace(kw))
                return new { ok = true, ready = true, matched = 0, total = 0, keywords = "", message = "（没填关键词）" };

            // **空格分隔多词**（全部要命中 = AND）
            var words = kw.Split(new[] { ' ', '\t', ',', '、' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return new { ok = true, ready = true, matched = 0, total = 0, keywords = "", message = "（没填关键词）" };

            // 预取库名（可选参与匹配）
            var libNames = new Dictionary<long, string>();
            if (mLib)
                foreach (var l in Db.GetLibraries()) libNames[l.Id] = l.Name;

            // 预取标签（可选参与匹配）：entityType=library
            var libTags = new Dictionary<long, string>();
            if (mTag)
            {
                foreach (var l in Db.GetLibraries())
                {
                    try
                    {
                        var tags = Db.GetEntityTags("library", l.Id);   // 第二参是 long，不是 string
                        if (tags != null && tags.Count > 0) libTags[l.Id] = string.Join(" ", tags);
                    }
                    catch { }
                }
            }

            // **库级放宽**：先算出「乐器名命中全部关键词」的库集合
            var relaxLibs = new HashSet<long>();
            if (relaxInst)
            {
                foreach (var lib in Db.GetLibraries())
                {
                    List<InstrumentRecord> insts;
                    try { insts = Db.GetInstruments(lib.Id); } catch { continue; }
                    foreach (var it in insts)
                    {
                        bool allI = true;
                        foreach (var w in words)
                            if ((it.Name ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { allI = false; break; }
                        if (allI) { relaxLibs.Add(lib.Id); break; }   // 该库有一个乐器命中 → 整库纳入
                    }
                }
            }

            var hits = new List<long>();
            int total = 0;
            foreach (var lib in Db.GetLibraries())
            {
                List<AudioClip> clips;
                try { clips = Db.GetAnalyzableClips(lib.Id); } catch { continue; }
                foreach (var c in clips)
                {
                    total++;
                    // 组装这个片段可供匹配的文本
                    var hay = new System.Text.StringBuilder();
                    if (mFile) hay.Append(c.Name).Append(' ');
                    if (mPath) hay.Append(c.RelPath).Append(' ');
                    if (mLib && libNames.TryGetValue(lib.Id, out var ln)) hay.Append(ln).Append(' ');
                    if (mTag && libTags.TryGetValue(lib.Id, out var tg)) hay.Append(tg).Append(' ');
                    var text = hay.ToString();
                    if (text.Length == 0) continue;

                    // **该库被放宽** → 直接纳入（不再看采样名）
                    if (relaxLibs.Contains(lib.Id)) { hits.Add(c.Id); continue; }

                    bool all = true;
                    foreach (var w in words)
                        if (text.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
                    if (all) hits.Add(c.Id);
                }
            }

            return new
            {
                ok = true, ready = true,
                keywords = string.Join(" ", words),
                matched = hits.Count,
                total,
                clipIds = hits.ToArray(),
                relaxedLibraries = relaxLibs.Count,
                note = "**空格分隔多词 = 全部命中（AND）**。匹配范围见选项；中文/英文都支持。"
                       + (relaxInst ? $"（已启用库级放宽：{relaxLibs.Count} 个库因乐器名命中而整库纳入）" : ""),
            };
        }

        /// <summary>
        /// **以某个片段为基准找相似**（方案 B：以音色搜音色）—— 供前端「🔍 找相似的」按钮用。
        ///
        /// **为什么需要它**：关键词语义筛（方案 A）对【缩写命名】的库天然失效
        /// （实测 `HAR_HDF_` / `SMP_MTDGTR_` / `DIWET_` 这类命名里没有可读词）。
        /// **而本功能完全不依赖名字** —— 用 MERT 768 维语义嵌入做余弦 KNN
        /// （**实测：同目录 0.9420 vs 异类 0.7933**）。
        ///
        /// **只读表 + 向量点积、毫秒级**。
        /// </summary>
        private object SimilarToClipRpc(JsonElement args)
        {
            long clipId = GetLong(args, "clipId");
            int topK = (int)Math.Clamp(GetLong(args, "topK"), 1, 200);
            if (topK <= 0) topK = 60;
            if (clipId <= 0) return new { ok = false, ready = false, message = "需要传 clipId。" };

            var all = Db.LoadAllMertFeatures();
            if (all.Count == 0)
                return new { ok = true, ready = false, message = "还没有 MERT 嵌入 —— 请先跑音色分析。" };

            var q = all.FirstOrDefault(x => x.ClipId == clipId);
            if (q.Vec == null) return new { ok = false, ready = false, message = $"片段 {clipId} 没有 MERT 嵌入。" };

            // 余弦排序（向量已归一化，点积即可）
            var scored = new List<(long Id, double Score)>();
            foreach (var x in all)
            {
                if (x.ClipId == clipId || x.Vec == null) continue;
                scored.Add((x.ClipId, MertFeatures.Cosine(q.Vec, x.Vec)));
            }
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            var top = scored.Take(topK).ToList();

            return new
            {
                ok = true, ready = true,
                clipId,
                count = top.Count,
                clipIds = top.Select(x => x.Id).ToArray(),
                scores = top.Select(x => Math.Round(x.Score, 4)).ToArray(),
                note = "按 MERT 语义嵌入余弦排序（**不依赖文件名**，能触及缩写命名的库）。",
            };
        }

        /// <summary>可解释维度的定义（供前端生成滑杆）。</summary>
        private object TimbreDims()
        {
            return new
            {
                ok = true,
                stored = Db.CountAudioFeatures(),
                dims = TimbreFilter.Dims.Select(d => new { d.Key, d.Label, d.Meaning }),
            };
        }

        /// <summary>
        /// **按可解释特征筛选**（前端滑杆用）—— 返回命中的 clipId 集合。
        /// **只读表、毫秒级**，不触发任何重算。
        /// </summary>
        private object TimbreFilterRpc(JsonElement args)
        {
            var raw = Db.LoadAllAudioFeatures();
            if (raw.Count == 0)
                return new
                {
                    ok = true, ready = false, matched = 0, total = 0,
                    message = "还没有提取声学特征 —— 请先点「声学特征」按钮（32 维、不用模型，很快）。",
                };

            var f = new TimbreFilter.Filter();
            foreach (var d in TimbreFilter.Dims)
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(d.Key, out var el)) continue;
                if (el.ValueKind != JsonValueKind.Number) continue;
                float v = (float)el.GetDouble();
                if (v <= 0f) continue;                 // 0 = 不限
                f.Set(d.Key, v, null);                 // 只做「>=」阈值
            }

            var rows = TimbreFilter.Normalize(raw);
            var hit = f.IsEmpty ? rows : TimbreFilter.Apply(rows, f);

            return new
            {
                ok = true, ready = true,
                condition = TimbreFilter.Describe(f),
                total = rows.Count,
                matched = hit.Count,
                // **只回 clipId 集合** —— 前端拿它去点亮地图上的点
                clipIds = hit.Select(r => r.ClipId).ToArray(),
            };
        }

        /// <summary>
        /// **返回【地图上那些点】的 6 维可解释百分位**（供「按特征着色」用）。
        ///
        /// **为什么只回地图上的点**：全库有近 3 万个片段，但地图只显示 1.5 万个 ——
        /// 回没在地图上的点是浪费带宽（实测 6 维 × 3 万 ≈ 1.4 MB JSON，只回地图点约 0.7 MB）。
        /// **只读表、毫秒级**。
        /// </summary>
        private object TimbreValues()
        {
            var mapPts = Db.LoadUmapCoords();
            if (mapPts.Count == 0)
                return new { ok = true, ready = false, count = 0, values = Array.Empty<object>() };

            var raw = Db.LoadAllAudioFeatures();
            if (raw.Count == 0)
                return new
                {
                    ok = true, ready = false, count = 0, values = Array.Empty<object>(),
                    message = "还没有提取声学特征 —— 请先点「声学特征」按钮。",
                };

            // 只保留「地图上有」的片段（用集合过滤，避免 O(n²)）
            var onMap = new HashSet<long>(mapPts.Select(p => p.ClipId));
            var subset = raw.Where(r => onMap.Contains(r.ClipId)).ToList();
            if (subset.Count == 0)
                return new { ok = true, ready = false, count = 0, values = Array.Empty<object>() };

            var rows = TimbreFilter.Normalize(subset);
            return new
            {
                ok = true, ready = true,
                count = rows.Count,
                dims = TimbreFilter.Dims.Select(d => d.Key).ToArray(),
                // **紧凑形式**：每条 [clipId, p0, p1, p2, p3, p4, p5]
                values = rows.Select(r => new object[]
                {
                    r.ClipId,
                    Math.Round(r.Pct[0], 3), Math.Round(r.Pct[1], 3), Math.Round(r.Pct[2], 3),
                    Math.Round(r.Pct[3], 3), Math.Round(r.Pct[4], 3), Math.Round(r.Pct[5], 3),
                }).ToArray(),
                note = "顺序与 dims 一致。数值是 0~1 的百分位（0.7 = 比 70% 的样本更强）。",
            };
        }

        /// <summary>档 1 特征提取的状态（供前端轮询）。</summary>
        /// <summary>供弹窗用：本机 CPU 核数与建议线程数。</summary>
        private object CpuInfo()
        {
            int cores = Environment.ProcessorCount;
            int suggest = Math.Max(1, cores - 2);   // **默认留 2 个核给系统**
            return new
            {
                ok = true,
                cores,
                suggest,
                note = cores <= 2
                    ? $"本机只有 {cores} 个逻辑核 —— 建议就用 {cores}（再留就没得跑了）。"
                    : $"本机有 {cores} 个逻辑核。**建议填 {suggest}**（留 2 个给系统，避免界面卡顿）；也可以填 {cores} 跑满，但界面可能会卡。",
            };
        }

        private object AudioFeatureStatus()
        {
            var st = AudioFeatureTask.Snapshot();
            int done = Db.CountAudioFeatures();
            return new
            {
                ok = true,
                running = st.Running,
                done = st.Running ? st.Done : done,
                total = st.Total,
                percent = st.Total > 0 ? Math.Round(st.Done * 100.0 / st.Total, 1) : 0,
                elapsedText = st.ElapsedText,
                etaText = st.EtaText,
                message = st.Message,
                failed = st.Failed,
                skipped = st.Skipped,
                stored = done,
            };
        }

        /// <summary>声学特征覆盖率统计（界面显示用）。</summary>
        private object AudioFeatureStats()
        {
            Db.EnsureAudioFeaturesTable();
            int have = Db.CountAudioFeatures();
            int decodable = 0, total = 0;
            foreach (var lib in Db.GetLibraries().ToList())
            {
                try
                {
                    foreach (var c in Db.GetAnalyzableClips(lib.Id))
                    { total++; if (AudioFeatures.CanDecode(c.Ext)) decodable++; }
                }
                catch { }
            }
            return new
            {
                ok = true, extracted = have, decodableSampled = decodable, sampledTotal = total,
                note = "跳过的都是 Kontakt 专有格式（.ncw/.nkx），开源库解不了 —— 硬边界。",
            };
        }
        /// <summary>
        /// **主动建议**（调研报告「远期」第 12 项）—— 跑一遍体检，返回按影响量排序的建议列表。
        /// `includeSlow=true` 时额外跑重复大文件扫描（较慢，默认关）。
        /// </summary>
        private object Suggestions(JsonElement args)
        {
            bool slow = args.ValueKind == JsonValueKind.Object
                        && args.TryGetProperty("includeSlow", out var s) && s.ValueKind == JsonValueKind.True;
            var list = SuggestionEngine.Analyze(Db, slow);
            return new
            {
                ok = true,
                count = list.Count,
                items = list.Select(x => new
                {
                    level = x.Level, title = x.Title, detail = x.Detail,
                    action = x.Action, actionArgs = x.ActionArgs, actionLabel = x.ActionLabel,
                    samples = x.Samples, weight = x.Weight,
                }),
            };
        }
        /// <summary>会话级统计（轮次 / 步数）—— 从库里数，重启不归零。</summary>
        private object ChatStats(JsonElement args)
        {
            long sid = GetLong(args, "sessionId");
            if (sid <= 0) return new { ok = false, turns = 0, steps = 0 };
            var (turns, steps) = Chat.GetSessionStats(sid);
            return new { ok = true, sessionId = sid, turns, steps };
        }
        /// <summary>前端错误上报：把浏览器侧的 JS 异常写进后端日志，便于排查「界面无反应」。</summary>
        private object ClientLog(JsonElement args)
        {
            string kind = GetString(args, "kind");
            string msg = GetString(args, "message");
            string src = GetString(args, "source");
            string stack = GetString(args, "stack");
            Log($"[前端{kind}] {msg}  @{src}  {stack}");
            return new { ok = true };
        }
    private object AgentTraces(JsonElement args)
    {
        int limit = (int)Math.Clamp(GetLong(args, "limit") is > 0 and <= 2000 ? GetLong(args, "limit") : 200, 1, 2000);
        var rows = Db.GetTraces(limit);
        return new
        {
            total = rows.Count,
            items = rows.Select(t => new
            {
                t.Id, t.SessionId, t.Step, tool = t.Tool, args = t.ArgsPreview,
                ok = t.Ok, ms = t.ElapsedMs, chars = t.ResultChars, t.Error, t.CreatedAt,
                pt = t.PromptTokens, ct = t.CompletionTokens, rt = t.ReasoningTokens,
            }),
        };
    }

    private object AgentTraceStats() => new
    {
        tools = Db.GetTraceStats().Select(s => new
        {
            tool = s.Tool, calls = s.Calls, fails = s.Fails,
            avgMs = Math.Round(s.AvgMs, 1), avgChars = Math.Round(s.AvgChars, 0),
        }),
    };
    private object QuickLoadTargets()
    {
        string kexe = Db.GetMeta("kontakt_exe", "");
        var set = PortableKontaktStore.ListQuickLoadTargets(kexe);
        return new { paths = set.ToArray(), count = set.Count };
    }
    private object RemoveFromQuickLoad(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };

        string kexe = Db.GetMeta("kontakt_exe", "");
            // 与 AddToQuickLoad 一致：遍历便携版 + 正式版两种 Quick-Load 根
            var qlRoots = PortableKontaktStore.QuickLoadRoots(kexe);
            if (qlRoots.Count == 0) return new { ok = false, message = "找不到 Kontakt 的 QuickLoad 目录" };

            int removed = 0;
            foreach (var cat in new[] { "Instr", "Bank", "Multi" })
            foreach (var qlRoot in qlRoots)
            {
                string dir = Path.Combine(qlRoot, cat);
            if (!Directory.Exists(dir)) continue;
            try
            {
                Type? t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) break;
                dynamic sh = Activator.CreateInstance(t)!;
                foreach (var f in Directory.GetFiles(dir, "*.lnk"))
                {
                    try
                    {
                        dynamic sc = sh.CreateShortcut(f);
                        string tp = (string)sc.TargetPath;
                        if (tp.Equals(lib.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(f);
                            removed++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return new { ok = true, removed, message = removed > 0 ? $"已从 Quick-Load 移除 {removed} 个快捷方式" : "Quick-Load 里没有该库的快捷方式" };
    }

    /// <summary>
    /// **查看两个库的文件差异** —— 列出「只在 A 有」「只在 B 有」的文件。
    ///
    /// 为什么需要：检测判定为「完全重复」时，体积可能仍差几 MB（如 3MB / 4MB），
    /// 用户需要知道**这点差异到底是什么文件**才能决定删哪个（用户明确要求）。
    ///
    /// 做法：递归枚举两个库目录下的相对路径 + 大小，做集合差。
    /// 限制：最多各扫 20000 个文件、跳过 .nkx/.ncw 等大容器之外的常规文件也照列（只列路径与大小，不读内容）。
    /// </summary>
    private object DupDiff(JsonElement args)
    {
        long idA = GetLong(args, "libraryIdA");
        long idB = GetLong(args, "libraryIdB");
        var libs = Db.GetLibraries();
        var a = libs.FirstOrDefault(l => l.Id == idA);
        var b = libs.FirstOrDefault(l => l.Id == idB);
        if (a == null || b == null) return new { ok = false, message = "未找到指定的音色库" };

        Dictionary<string, long> Scan(string root)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return d;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (d.Count >= 20000) break;
                    try
                    {
                        string rel = Path.GetRelativePath(root, f);
                        d[rel] = new FileInfo(f).Length;
                    }
                    catch { }
                }
            }
            catch { }
            return d;
        }

        var fa = Scan(a.Path);
        var fb = Scan(b.Path);
        if (fa.Count == 0 || fb.Count == 0)
            return new { ok = false, message = $"扫描失败或目录为空（A {fa.Count} 个文件 / B {fb.Count} 个文件）" };

        var onlyA = fa.Where(kv => !fb.ContainsKey(kv.Key))
                      .OrderByDescending(kv => kv.Value)
                      .Select(kv => new { path = kv.Key, bytes = kv.Value }).ToList();
        var onlyB = fb.Where(kv => !fa.ContainsKey(kv.Key))
                      .OrderByDescending(kv => kv.Value)
                      .Select(kv => new { path = kv.Key, bytes = kv.Value }).ToList();

        long sumA = onlyA.Sum(x => x.bytes);
        long sumB = onlyB.Sum(x => x.bytes);

        return new
        {
            ok = true,
            nameA = a.Name, nameB = b.Name,
            pathA = a.Path, pathB = b.Path,
            totalA = fa.Count, totalB = fb.Count,
            sizeA = a.SizeBytes, sizeB = b.SizeBytes,
            onlyACount = onlyA.Count, onlyABytes = sumA,
            onlyBCount = onlyB.Count, onlyBBytes = sumB,
            onlyA = onlyA.Take(200).ToList(),
            onlyB = onlyB.Take(200).ToList(),
            truncated = onlyA.Count > 200 || onlyB.Count > 200,
        };
    }

    /// <summary>把某个库注册到 Kontakt（用库内的 .nicnt 产品键）。</summary>
    private object DupRegisterLibrary(JsonElement args)
    {
        long id = GetLong(args, "libraryId");
        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == id);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };
        if (lib.ProductKey.Length == 0)
            return new { ok = false, message = $"「{lib.Name}」没有产品键（缺少 .nicnt），无法自动注册 —— 请在 Kontakt 的 Library Manager 里手动入库。" };

        // 直接改注册表里的 ContentDir 指向本库；若注册项不存在则提示手动入库
        var (ok, msg) = LibraryRegistrar.UpdateContentDir(lib.ProductKey, lib.Path);
        if (!ok)
            return new { ok = false, message = $"注册失败：{msg}（若该产品从未注册过，请在 Kontakt 里手动入库）" };

        Log($"[重复清理] 已注册「{lib.Name}」→ {lib.Path}（RegKey={lib.ProductKey}）");
        return new { ok = true, message = $"已把「{lib.Name}」注册到 {lib.Path}。建议重新扫描刷新状态。" };
    }

    /// <summary>
    /// **把某个音色库的注册信息转移到另一个路径**。
    ///
    /// 用途（用户设计的清理流程）：当「要删的那个库」正好是注册表当前指向的库时，
    /// 必须先把它在注册表里的 ContentDir 改到保留的那个库，再删除 ——
    /// 否则 Kontakt 会找不到库。
    ///
    /// 做法：拿注册方的 ProductKey（RegKey），调 LibraryRegistrar.UpdateContentDir 改路径。
    /// </summary>
    private object DupTransferReg(JsonElement args)
    {
        long fromId = GetLong(args, "fromLibraryId");   // 当前注册的库（注册要被转移走的）
        long toId = GetLong(args, "toLibraryId");       // 要接收注册的库

        var libs = Db.GetLibraries();
        var from = libs.FirstOrDefault(l => l.Id == fromId);
        var to = libs.FirstOrDefault(l => l.Id == toId);
        if (from == null || to == null) return new { ok = false, message = "未找到指定的音色库" };
        if (to.Path.Length == 0) return new { ok = false, message = "目标库没有有效路径" };

        string regKey = from.ProductKey;
        if (regKey.Length == 0)
            return new { ok = false, message = $"「{from.Name}」没有 .nicnt / 产品键，无法转移注册信息（这类库需要手动在 Kontakt 里重新定位）" };

        var (ok, msg) = LibraryRegistrar.UpdateContentDir(regKey, to.Path);
        if (!ok) return new { ok = false, message = "转移失败：" + msg };

        Log($"[重复清理] 注册信息 {from.Name}({from.Path}) → {to.Name}({to.Path})，RegKey={regKey}");
        Push(new { type = "toast", text = $"注册信息已转移到「{to.Name}」", kind = "ok" });

        return new
        {
            ok = true,
            message = $"已把注册信息从「{from.Name}」转移到「{to.Name}」（注册表路径现为 {to.Path}）。建议重新扫描以刷新界面状态。",
            regKey,
            toPath = to.Path,
        };
    }

    /// <summary>
    /// **删除一个音色库（整个文件夹）**。
    ///
    /// 安全设计（对应既定的权限模型）：
    ///   · **删到回收站**（不是永久删除）—— 这是「可回滚」的落点，误删可从回收站恢复；
    ///   · **路径校验** —— 必须位于已知的库根目录之下，且不能是盘符根/库根本身，防止误删整盘；
    ///   · **删除前检查注册状态** —— 若该库正是注册表指向的，**拒绝直接删除**（除非 force），
    ///     提示用户先转移注册信息；
    ///   · 删除后从索引里移除该库记录（Database.DeleteLibrary 只删索引，不动磁盘）。
    /// </summary>
    private object DupDeleteLibrary(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        bool force = GetBool(args, "force");   // 用户已确认风险后传 true

        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };
        if (string.IsNullOrEmpty(lib.Path)) return new { ok = false, message = "该库没有路径，无法删除" };

        string path = lib.Path.TrimEnd('\\', '/');
        if (!Directory.Exists(path)) return new { ok = false, message = $"路径不存在：{path}" };

        // ── 安全检查 1：必须在已知库根之下，且不是库根本身 ──
        var roots = Db.GetRoots().Select(r => (r.Path ?? "").TrimEnd('\\', '/'))
                                 .Where(r => r.Length > 0).ToList();
        bool underRoot = roots.Any(r => path.Length > r.Length &&
            path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        if (!underRoot)
            return new { ok = false, message = $"拒绝删除：{path} 不在任何已知的库根目录之下（防止误删）。可先到「设置」里添加该根目录。" };
        if (path.Length <= 3) return new { ok = false, message = "拒绝删除盘符根目录" };

        // ── 安全检查 2：注册表指向它时不许直接删 ──
        if (lib.RegStatus == "registered" && !force)
            return new
            {
                ok = false,
                needTransfer = true,
                message = $"「{lib.Name}」当前是注册表指向的库，直接删除会导致 Kontakt 找不到库。请先点「转为注册」把注册信息转到要保留的库，再删除。",
            };

            // ── 先解绑 Kontakt 注册（用户要求：删已注册的库要自动清理注册表与注册路径）──
            // 一个库目录下可能注册了多个产品（多层 .nicnt），所以要把**所有指向该路径的注册项**都解绑。
            var unregKeys = new List<string>();
            try
            {
                var registered = LibraryRegistrar.ReadRegistered();
                string norm = LibraryRegistrar.NormalizePath(path);
                foreach (var kv in registered)
                {
                    string cd = kv.Value.ContentDir ?? "";
                    if (cd.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                        cd.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        var (uok, _) = LibraryRegistrar.Unregister(kv.Key);
                        if (uok) unregKeys.Add(kv.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[重复清理] 解绑注册失败：{ex.Message}");
                return new { ok = false, message = "解绑 Kontakt 注册失败（已中止删除，未动磁盘文件）：" + ex.Message };
            }

                
            // ── 同时清理**便携版**自己的库列表（用户实测发现：只清系统注册表不够）──
            // 便携版把库列表存在自身目录的 UserData\Settings.cfg 里，
            // 不清理的话 Kontakt 里仍会显示这些库并报 "Content not found"。
            int portableRemoved = 0;
            string portableMsg = "";
            try
            {
                // Kontakt 主程序路径存在 app_meta 的 kontakt_exe 里
                string kexe = Db.GetMeta("kontakt_exe", "");
                if (kexe.Length > 0)
                {
                    var (pok, pmsg, prem) = PortableKontaktStore.RemoveLibraries(kexe, new[] { path }, new[] { lib.Name });
                    portableRemoved = prem;
                    portableMsg = pmsg;
                    if (prem > 0) Log($"[重复清理] 便携版库列表已移除 {prem} 条：{lib.Name}");
                }
            }
            catch (Exception ex) { portableMsg = "便携版清理异常：" + ex.Message; Log("[重复清理] " + portableMsg); }

        // ── 执行：删到回收站（可回滚）──
        try
        {
            long size = lib.SizeBytes;
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            Db.DeleteLibrary(libId);
            Log($"[重复清理] 已删除库「{lib.Name}」→ {path}（回收站，约 {size / 1073741824.0:F1} GB）");
            Push(new { type = "toast", text = $"已删除「{lib.Name}」（已放入回收站，可恢复）", kind = "ok" });

            return new
            {
                ok = true,
                message = $"已删除「{lib.Name}」（{path}），文件已放入**回收站**可恢复；索引记录已移除。" +
                          (unregKeys.Count > 0 ? $" 同时已解绑 Kontakt 注册 {unregKeys.Count} 项（{string.Join("、", unregKeys)}）。" : " 该库原本未注册，无需解绑。")
                          + (portableRemoved > 0 ? $" 便携版库列表已移除 {portableRemoved} 条。" : " 便携版库列表无匹配条目。"),
                portableRemoved,
                unregisteredKeys = unregKeys,
                path,
                recycledBytes = size,
            };
        }
        catch (Exception ex)
        {
            Log($"[重复清理] 删除失败：{ex}");
            return new { ok = false, message = "删除失败：" + ex.Message };
        }
    }

    private object DupSimilar() => DuplicateFinder.FindSimilarLibraries(Db).Select(s => new
    {
        s.LibraryA, s.LibraryB, s.NameA, s.NameB,
        s.SharedInstruments, s.TotalA, s.TotalB, s.Jaccard,
        coverA = s.CoverA, coverB = s.CoverB,
        sizeA = s.SizeA, sizeB = s.SizeB,
        versionA = s.VersionA, versionB = s.VersionB,
        relation = s.Relation.ToString(),
        relationLabel = s.RelationLabel,
        recommendation = s.Recommendation,
        keepName = s.KeepName,
        removeName = s.RemoveName,
        reclaimBytes = s.ReclaimableBytes,
        // 路径与注册状态（重复清理界面必需）
        pathA = s.PathA, pathB = s.PathB,
        regA = s.RegA, regB = s.RegB, regPath = s.RegPath, regKey = s.RegKey,
        keepId = s.KeepId, keepPath = s.KeepPath,
        deleteId = s.DeleteId, deletePath = s.DeletePath,
        needTransferFirst = s.NeedTransferFirst,
    });
    // ══════════════════ 入库快照与回滚 ══════════════════

    private object SnapshotCapture(JsonElement args)
    {
        string reason = GetString(args, "reason");
        var (ok, msg, path) = RegistrationSnapshot.Capture(reason.Length > 0 ? reason : "手动快照");
        return new { ok, message = msg, path };
    }

    private object SnapshotRestore(JsonElement args)
    {
        string path = GetString(args, "path");
        var (ok, msg, details) = RegistrationSnapshot.Restore(path);
        return new { ok, message = msg, details, report = ok ? BuildRegistrationReport() : null };
    }

    private object SnapshotDelete(JsonElement args)
    {
        string path = GetString(args, "path");
        var (ok, msg) = RegistrationSnapshot.Delete(path);
        return new { ok, message = msg };
    }
    // ══════════════════ 健康检查 / 杂质清理 / 导出 ══════════════════

    private object JunkList(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        var items = Db.GetJunkFiles(libId);
        return new
        {
            total = items.Count,
            totalBytes = items.Sum(i => i.SizeBytes),
            items = items.Select(i => new
            {
                i.Id, i.LibraryId, i.LibraryName, i.RelPath, i.Kind, i.SizeBytes, i.SuggestedKeep, i.FullPath,
            }),
        };
    }

    /// <summary>删除杂质文件（默认送回收站，可撤销）。</summary>
    private object JunkDelete(JsonElement args)
    {
        var ids = new List<long>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ids", out var idsEl) &&
            idsEl.ValueKind == JsonValueKind.Array)
            foreach (var el in idsEl.EnumerateArray())
                if (el.TryGetInt64(out long v)) ids.Add(v);

        if (ids.Count == 0) return new { ok = false, message = "未选择任何文件" };
        bool toRecycle = GetBool(args, "recycle");

        var all = Db.GetJunkFiles();
        var targets = all.Where(j => ids.Contains(j.Id)).ToList();
        int deleted = 0, failed = 0, missing = 0;
        long freed = 0;
        var errors = new List<string>();

        foreach (var j in targets)
        {
            try
            {
                if (!File.Exists(j.FullPath)) { missing++; continue; }
                long size = new FileInfo(j.FullPath).Length;

                if (toRecycle) RecycleFile(j.FullPath);
                else File.Delete(j.FullPath);

                deleted++;
                freed += size;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < 5) errors.Add($"{Path.GetFileName(j.FullPath)}：{ex.Message}");
            }
        }

        // 已删除/不存在的记录从索引里移除
        var doneIds = targets.Where(t => !File.Exists(t.FullPath)).Select(t => t.Id).ToList();
        Db.RemoveJunkRecords(doneIds);

        return new
        {
            ok = deleted > 0 || missing > 0,
            message = $"已删除 {deleted} 个文件，释放 {freed / 1024.0 / 1024:F1} MB" +
                      (missing > 0 ? $"，{missing} 个已不存在（记录已清理）" : "") +
                      (failed > 0 ? $"，{failed} 个失败" : "") +
                      (toRecycle ? "（已送回收站，可撤销）" : ""),
            deleted, failed, missing, freedBytes = freed, errors,
        };
    }

    /// <summary>把文件送入回收站（失败则退回直接删除）。</summary>
    private static void RecycleFile(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch { File.Delete(path); }
    }

    /// <summary>导出库清单（CSV / Markdown）。</summary>
    private object ExportLibraries(JsonElement args)
    {
        string format = GetString(args, "format").ToLowerInvariant();
        if (format.Length == 0) format = "csv";

        var libs = LoadLibraries();
        var manuals = Db.GetManuals();
        var junk = Db.GetJunkFiles();

        string ext = format == "md" ? "md" : "csv";
        string defaultName = $"Kontakt音色库清单-{DateTime.Now:yyyyMMdd-HHmm}.{ext}";

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出音色库清单",
            FileName = defaultName,
            Filter = format == "md" ? "Markdown (*.md)|*.md|所有文件 (*.*)|*.*" : "CSV (*.csv)|*.csv|所有文件 (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog() != true) return new { ok = false, message = "已取消" };

        string path = dlg.FileName;
        var sb = new System.Text.StringBuilder();

        if (format == "md")
        {
            sb.AppendLine("# Kontakt 音色库清单");
            sb.AppendLine();
            sb.AppendLine($"> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}　·　共 {libs.Count} 个库　·　" +
                          $"合计 {libs.Sum(l => l.SizeBytes) / 1024.0 / 1024 / 1024:F2} GB");
            sb.AppendLine();
            sb.AppendLine("| 音色库 | 分类 | 占用 | NKI | 版本要求 | 入库状态 | 说明书 | 杂质 | 路径 |");
            sb.AppendLine("|---|---|---:|---:|---|---|---:|---:|---|");
            foreach (var l in libs)
            {
                int mc = manuals.Count(m => m.LibraryId == l.Id);
                int jc = junk.Count(j => j.LibraryId == l.Id);
                string reg = l.RegStatus switch
                {
                    "registered" => "已入库", "incomplete" => "记录不完整", "pending-manager" => "待库管理器",
                    "missing" => "未入库", "path-mismatch" => "路径失效", "partial" => "部分入库",
                    "non-standard" => "非标准库", _ => l.RegStatus,
                };
                sb.AppendLine($"| {l.Name} | {l.Category} | {l.SizeBytes / 1024.0 / 1024 / 1024:F2} GB | {l.NkiCount} | " +
                              $"{l.RequiredKontakt} | {reg} | {mc} | {jc} | `{l.Path}` |");
            }
        }
        else
        {
            sb.AppendLine("音色库,分类,占用GB,文件数,NKI,NKM,版本要求,入库状态,说明书数,杂质数,路径");
            foreach (var l in libs)
            {
                int mc = manuals.Count(m => m.LibraryId == l.Id);
                int jc = junk.Count(j => j.LibraryId == l.Id);
                string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
                sb.AppendLine(string.Join(",",
                    Q(l.Name), Q(l.Category),
                    (l.SizeBytes / 1024.0 / 1024 / 1024).ToString("F2"),
                    l.FileCount.ToString(), l.NkiCount.ToString(), l.NkmCount.ToString(),
                    Q(l.RequiredKontakt), Q(l.RegStatus),
                    mc.ToString(), jc.ToString(), Q(l.Path)));
            }
        }

        try
        {
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));   // 带 BOM，Excel 直接识别中文
            return new { ok = true, message = $"已导出 {libs.Count} 个库 → {path}", path };
        }
        catch (Exception ex) { return new { ok = false, message = $"写入失败：{ex.Message}" }; }
    }
    /// <summary>按库路径反查注册表键名（.nkx 解密需要它来取 JDX/HU）。</summary>
    private string? FindRegKeyForLibrary(string libraryPath)
    {
        try
        {
            string norm = LibraryRegistrar.NormalizePath(libraryPath);
            RefreshRegistry();
            foreach (var kv in _registered)
            {
                string cd = kv.Value.ContentDir;
                if (cd.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                    cd.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            }
            // 兜底：按库名匹配（含去厂商前缀的强相似）
            var lib = LoadLibraries().FirstOrDefault(l =>
                LibraryRegistrar.NormalizePath(l.Path).Equals(norm, StringComparison.OrdinalIgnoreCase));
            if (lib != null && lib.ProductKey.Length > 0)
                return lib.ProductKey.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch { }
        return null;
    }
    // ══════════════════ 试听（音频缓存 + 本地服务）══════════════════

    /// <summary>试听缓存目录（本地，启动时已映射为 kontakt-audio 虚拟主机）。</summary>
    private static string AudioCacheDir => Path.Combine(AppPaths.DataDir, "audio");

    /// <summary>超过此大小不缓存（试听片段通常 4 KB ~ 2 MB）。</summary>
    private const long MaxAuditionBytes = 32L * 1024 * 1024;

    /// <summary>把文件复制进缓存目录并返回可访问 URL；已缓存则直接复用。</summary>
    private string? CacheAudio(string fullPath, string ext, string libraryPath = "")
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length == 0) return null;
            // .nkx/.nkr 是容器（单文件可达 2 GB），大小上限只对「单个音频文件」生效
            bool isContainer = ext.Equals("nkx", StringComparison.OrdinalIgnoreCase) ||
                               ext.Equals("nkr", StringComparison.OrdinalIgnoreCase);
            if (!isContainer && info.Length > MaxAuditionBytes) return null;

            // 以「路径 + 大小 + 修改时间」做键，内容变了自动换缓存文件
            string key = $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|norm1";
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
            string stem = Convert.ToHexString(hash)[..20].ToLowerInvariant();

            Directory.CreateDirectory(AudioCacheDir);

            // .ncw 是 NI 专有压缩格式：本地解码成 WAV 后缓存（6 秒足够试听，约 10~40 ms）
            if (ext.Equals("ncw", StringComparison.OrdinalIgnoreCase))
            {
                string wavName = stem + ".wav";
                string wavPath = Path.Combine(AudioCacheDir, wavName);
                bool fresh = false;
                if (!File.Exists(wavPath))
                {
                    if (NcwDecoder.DecodeToWav(fullPath, wavPath, maxSeconds: 6.0) <= 0) return null;
                    fresh = true;
                }
                // 🔴 响度归一化（2026-09-27）—— 远距离麦位电平天然低 15~25 dB，不归一化用户听不清。
                //   ⚠️ **只在【刚解码】时做一次**：已有缓存说明已经归一化过，重复做会二次提增益。
                //   ⚠️ 之前这里因误插导致 `return null` 变成无条件执行 ⇒ **所有 .ncw 都返回 null**、
                //      audition 永远 items=0（实测：76 个 WAV 都生成了却全被丢弃）。
                if (fresh) Loudness.NormalizeWavInPlace(wavPath);
                return $"https://kontakt-audio/{wavName}";
            }

            // .nkx 是加密单块容器：解包出随机一个样本 → 解密成 NCW → 解码成 WAV
            if (ext.Equals("nkx", StringComparison.OrdinalIgnoreCase))
            {
                // 先取随机样本，再把它纳入缓存键 —— 保证每次点击都能听到不同的采样
                // （若只用容器名做键，第二次点击会命中缓存、永远听到同一个样本）
                string? regKey = FindRegKeyForLibrary(libraryPath.Length > 0 ? libraryPath : (Path.GetDirectoryName(fullPath) ?? ""));
                if (regKey == null) return null;
                var keyInfo = NkxCrypto.LoadKey(regKey);
                if (keyInfo == null) return null;
                var mask = NkxCrypto.BuildXorMask(keyInfo.Value.key, keyInfo.Value.iv);

                var tree = NkxReader.ReadTree(fullPath);
                if (tree == null) return null;
                var files = tree.Flatten().Where(f => f.Size > 0).ToList();
                if (files.Count == 0) { Log($"[nkx] 容器内无文件项：{fullPath}"); return null; }

                // 随机取一个样本（同一容器内多次点击可听到不同音）
                var pick = files[Random.Shared.Next(files.Count)];

                // 缓存键 = 容器哈希 + 样本名哈希 → 不同样本各自缓存，互不覆盖
                byte[] pickHash = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(pick.Name));
                string pickStem = stem[..12] + Convert.ToHexString(pickHash)[..8].ToLowerInvariant();
                string wavName = pickStem + ".wav";
                string wavPath = Path.Combine(AudioCacheDir, wavName);
                if (File.Exists(wavPath)) return $"https://kontakt-audio/{wavName}";

                string tmpNcw = Path.Combine(AudioCacheDir, pickStem + ".ncw");
                using (var of = File.Create(tmpNcw))
                {
                    if (NkxReader.ExtractFile(fullPath, pick, mask, of) <= 0) { Log($"[nkx] 解包失败：{pick.Name}"); try { File.Delete(tmpNcw); } catch { } return null; }
                }
                long n = NcwDecoder.DecodeToWav(tmpNcw, wavPath, maxSeconds: 6.0);
                if (n > 0) Loudness.NormalizeWavInPlace(wavPath);   // 🔴 响度归一化（2026-09-26）
                try { File.Delete(tmpNcw); } catch { }
                if (n <= 0) return null;
                return $"https://kontakt-audio/{wavName}";
            }

            // 🔴 **`.aif` / `.aiff` 必须【转码成 WAV】再给播放器**（2026-09-27）——
            //   实测问题：Chromium/WebView2 的 `<audio>` **不支持 AIFF** ⇒ 原样返回 `.aif` 会「点开没声」。
            //   而分析链路一直支持 `.aif`（AudioDecoder 含 aif、自研 AiffReader 兜底）⇒ 只是预览没做。
            //   这里补上：解码 → 写 16-bit 单声道 WAV → 归一化 → 再返回。
            if (ext.Equals("aif", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals("aiff", StringComparison.OrdinalIgnoreCase))
            {
                string aifWavName = stem + ".wav";
                string aifWavPath = Path.Combine(AudioCacheDir, aifWavName);
                if (!File.Exists(aifWavPath))
                {
                    var dec = AudioDecoder.DecodeMono(fullPath, maxSeconds: 6.0);
                    if (dec == null) return null;
                    var (samples, rate) = dec.Value;
                    if (samples.Length == 0 || rate <= 0) return null;
                    try
                    {
                        using (var w = new NAudio.Wave.WaveFileWriter(aifWavPath,
                                   new NAudio.Wave.WaveFormat(rate, 16, 1)))
                        {
                            w.WriteSamples(samples, 0, samples.Length);
                        }
                    }
                    catch { try { File.Delete(aifWavPath); } catch { } return null; }
                    Loudness.NormalizeWavInPlace(aifWavPath);
                }
                return $"https://kontakt-audio/{aifWavName}";
            }

            string name = stem + "." + ext;
            string target = Path.Combine(AudioCacheDir, name);
            if (!File.Exists(target)) File.Copy(fullPath, target, overwrite: true);

            return $"https://kontakt-audio/{name}";
        }
        catch { return null; }
    }

    /// <summary>清理过期的试听缓存（保留最近 3 天，且总量不超过 300 MB）。</summary>
    private static void CleanAudioCache()
    {
        try
        {
            var dir = new DirectoryInfo(AudioCacheDir);
            if (!dir.Exists) return;
            var cutoff = DateTime.UtcNow.AddDays(-3);
            foreach (var f in dir.EnumerateFiles().Where(f => f.LastWriteTimeUtc < cutoff))
                try { f.Delete(); } catch { }

            var rest = dir.EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in rest)
            {
                total += f.Length;
                if (total > 300L * 1024 * 1024) try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    private object AudioClips(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        long rawLimit = GetLong(args, "limit");
        int limit = (int)Math.Clamp(rawLimit is > 0 and <= 50 ? rawLimit : 6, 1, 50);
        string matchName = GetString(args, "matchName");

        var lib = LoadLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };

        var (demoCount, sampleCount, ncwCount, nkxCount) = Db.CountAudioClips(libId);
        // 多取一些候选，因为部分文件可能缓存失败（过大/不可读），需保证最终仍有可播放项
        var clips = Db.GetAudioClips(libId, Math.Min(50, limit * 4), matchName);

        var items = new List<object>();
        foreach (var c in clips)
        {
            if (items.Count >= limit) break;
            string full = Path.Combine(lib.Path, c.RelPath);
            string? url = CacheAudio(full, c.Ext, lib.Path);
            if (url == null) continue;
            items.Add(new
            {
                c.Id,
                c.Name,
                c.RelPath,
                c.Ext,
                c.SizeBytes,
                c.Kind,
                url,
                fullPath = full,
            });
        }

        return new
        {
            ok = true,
            libraryId = libId,
            libraryName = lib.Name,
            total = demoCount + sampleCount + ncwCount + nkxCount,
            demoCount,
            sampleCount,
            ncwCount,
            nkxCount,
            playable = items.Count,
            matched = matchName,
            items,
            note = (demoCount + sampleCount + ncwCount + nkxCount) == 0
                ? "该库没有可直接试听的音频片段。"
                : "",
        };
    }
    /// <summary>用系统默认程序打开文件（如 PDF 说明书）。</summary>
    private object OpenFile(JsonElement args)
    {
        string path = GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("缺少 path 参数");
        if (!File.Exists(path)) throw new FileNotFoundException($"文件不存在：{path}");

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return new { opened = path };
    }

    /// <summary>取说明书清单（可按库过滤）。</summary>
    private object GetManuals(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        var manuals = Db.GetManuals(libId > 0 ? libId : null);
        return new
        {
            total = manuals.Count,
            primary = manuals.Count(m => m.IsPrimary),
            librariesWithManuals = manuals.Select(m => m.LibraryName).Distinct().Count(),
            items = manuals,
        };
    }

    // ══════════════════ AI 助手 ══════════════════

    private readonly HashSet<long> _kbBuilding = new();

    /// <summary>
    /// **当前 AI 回合的取消源** —— 用户在界面上点「停止」时 Cancel 它，
    /// 让正在流式生成的回答（以及 Agent 工具循环）立刻停下来。
    /// 每个分支一个，避免并发会话互相打断。
    /// </summary>
    private readonly Dictionary<long, CancellationTokenSource> _chatCts = new();

    /// <summary>
    /// **插话引导队列**（每分支一个）—— 用户在 Agent 工作时补的话先放这里，
    /// Agent 循环在每个步骤边界取走并作为 user 消息追加进对话（steering）。
    /// </summary>
    private readonly Dictionary<long, ConcurrentQueue<string>> _chatSteer = new();

    /// <summary>当前正在跑的 AI 回合所属分支（插话引导据此取队列）。</summary>
    private volatile int _activeChatBranchIdInt;
    private long _activeChatBranchId { get => _activeChatBranchIdInt; set => _activeChatBranchIdInt = (int)value; }

            /// <summary>
        /// 🔴 设置 Agent 权限档位（2026-09-26，用户要求放右上角）。
        ///   readonly / readwrite / full —— 存入 DB meta，立即生效（下次请求读取）。
        /// </summary>
        private object SetPermissionTier(System.Text.Json.JsonElement args)
        {
            string tier = GetString(args, "tier").Trim().ToLowerInvariant();
            if (tier != "readonly" && tier != "readwrite" && tier != "full")
                return new { ok = false, message = "档位只能是 readonly / readwrite / full" };
            Db.SetMeta("ai_permission_tier", tier);
            Log($"[权限] 档位已切到 {tier}");
            return new { ok = true, tier };
        }

/// <summary>
/// 🔴 **`@` 文件路径补全**（2026-09-27，用户要求的第 2 项）。
/// 在聊天输入框打 `@` 时，前端列出可引用的路径（库根 + 其子目录）供选择。
/// ⚠️ **只列【白名单内的目录】**（音色库根与其子目录），不列任意磁盘路径 ——
///   与 Agent 的 AllowedRoots 权限模型保持一致。
/// </summary>
private object PathCandidates(System.Text.Json.JsonElement args)
{
    string prefix = GetString(args, "prefix").Trim();
    const int limit = 300;
    var roots = new List<string>();
    try
    {
        foreach (var l in LoadLibraries())
            if (!string.IsNullOrEmpty(l.Path)) roots.Add(l.Path);
        foreach (var r in Db.GetRoots())
            if (!string.IsNullOrEmpty(r.Path)) roots.Add(r.Path);
    }
    catch { }
    var uniq = roots.Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    string NameOf(string path)
    {
        try { return Path.GetFileName(path.TrimEnd('\\')) ?? path; } catch { return path; }
    }

    // 🔴 **按【库名】模糊匹配，而不是比完整路径**（2026-09-27 修）——
    //   旧实现用 `r.StartsWith("8d")` 比完整路径 `D:\Kontakt Libraries\8dio\...` ⇒ 永远不命中。
    //   评分：0 = 名字以它开头（最相关）、1 = 名字含它、2 = 完整路径以它开头、3 = 路径含它。
    int Score(string path, string pf)
    {
        if (pf.Length == 0) return 9;
        string nm = NameOf(path);
        if (nm.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return 0;
        if (nm.Contains(pf, StringComparison.OrdinalIgnoreCase)) return 1;
        if (path.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return 2;
        if (path.Contains(pf, StringComparison.OrdinalIgnoreCase)) return 3;
        return 99;
    }

    var libs = uniq.Select(x => new { path = x, name = NameOf(x), score = Score(x, prefix) })
                   .Where(x => x.score < 99)
                   .OrderBy(x => x.score).ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                   .ToList();

    var hits = new List<object>();
    // 🔴 **空前缀 = 列出全部库根**（2026-09-27 修）——
    //   旧实现为防「一个目录霸屏」加了「每父目录最多 6 个」的配额，
    //   副作用是【隐藏大量库】（实测 D:\Kontakt Libraries\Orange Tree 有 27 个、只显示 5 个），
    //   而带前缀搜索时又不做配额 ⇒ 前后不一致、还会误导用户。
    //   ⇒ 现在统一：不设配额、只受 limit 约束（菜单可滚动）。
    foreach (var l in libs.Take(limit))
        hits.Add(new { path = l.path, kind = "library", name = l.name });
    
    // 子目录：只有当输入串本身像一个已存在的路径时才展开（否则会淹没库名匹配）
    if (prefix.Length > 0 && hits.Count < limit)
    {
        try
        {
            string? dir = Directory.Exists(prefix) ? prefix : null;
            if (dir != null)
            {
                bool inside = uniq.Any(r => dir.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                if (inside)
                {
                    string leaf = Path.GetFileName(dir) ?? "";
                    foreach (var dd in Directory.EnumerateDirectories(dir)
                                 .OrderBy(x => x).Take(limit - hits.Count))
                        hits.Add(new { path = dd, kind = "dir", name = Path.GetFileName(dd) });
                }
            }
        }
        catch { }
    }
    return new { ok = true, prefix, count = hits.Count, items = hits };
}
private object GetAiSettings()
    {
        var s = AiSettings.Load(Db);
        return new
        {
            baseUrl = s.BaseUrl,
            model = s.Model,
            hasKey = s.ApiKey.Length > 0,
            keyMasked = s.ApiKey.Length > 8 ? s.ApiKey[..8] + "…" + s.ApiKey[^4..] : "",
            temperature = s.Temperature,
            maxTokens = s.MaxTokens,
            contextLength = s.ContextLength,
            compressPercent = s.CompressPercent,
            multimodal = s.Multimodal,
            allowNetwork = s.AllowNetwork,
            // 🔴 权限档位（2026-09-26）：右上角下拉据此显示当前档位
            permissionTier = s.PermissionTier,
            allowShell = s.AllowShell,
            thinkingEffort = s.ThinkingEffort,
            uiLanguage = s.UiLanguage,
            thinkLanguage = s.ThinkLanguage,
            replyLanguage = s.ReplyLanguage,
            allowUiControl = s.AllowUiControl,
            configured = s.Configured,
            kbRoot = Path.Combine(AppPaths.DataDir, "kb"),
        };
    }

    private object SetAiSettings(JsonElement args)
    {
        var s = AiSettings.Load(Db);
        string url = GetString(args, "baseUrl");
        string model = GetString(args, "model");
        string key = GetString(args, "apiKey");

        if (url.Length > 0) s.BaseUrl = url.Trim().TrimEnd('/');
        if (model.Length > 0) s.Model = model.Trim();
        if (key.Length > 0) s.ApiKey = key.Trim();          // 空字符串表示"不修改 Key"
        if (double.TryParse(GetString(args, "temperature"), out double t) && t >= 0 && t <= 2) s.Temperature = t;
        // maxTokens 留空 = 0 = 不限制（请求里不发送 max_tokens）
        // maxTokens 留空 = 0 = 不限制；显式写了数字才设上限
        string mtRaw = GetString(args, "maxTokens").Trim();
        if (mtRaw.Length == 0) s.MaxTokens = 0;
        else if (int.TryParse(mtRaw, out int mt) && mt is >= 0 and <= 200000) s.MaxTokens = mt;
        if (int.TryParse(GetString(args, "contextLength"), out int cl) && cl > 0) s.ContextLength = cl;
        if (int.TryParse(GetString(args, "compressPercent"), out int cp) && cp is >= 10 and <= 95) s.CompressPercent = cp;
        if (args.TryGetProperty("multimodal", out var mmEl) && (mmEl.ValueKind == JsonValueKind.True || mmEl.ValueKind == JsonValueKind.False)) s.Multimodal = mmEl.GetBoolean();
        if (args.TryGetProperty("allowNetwork", out var anEl) && (anEl.ValueKind == JsonValueKind.True || anEl.ValueKind == JsonValueKind.False)) s.AllowNetwork = anEl.GetBoolean();
        if (args.TryGetProperty("allowShell", out var shEl) && (shEl.ValueKind == JsonValueKind.True || shEl.ValueKind == JsonValueKind.False)) s.AllowShell = shEl.GetBoolean();
            string te = GetString(args, "thinkingEffort");
            if (te is "auto" or "low" or "medium" or "high") s.ThinkingEffort = te;
            // 语言设置（三项独立）
            string ul = GetString(args, "uiLanguage");
            if (ul is "zh" or "en") s.UiLanguage = ul;
            string tl = GetString(args, "thinkLanguage");
            if (tl is "zh" or "en" or "auto") s.ThinkLanguage = tl;
            string rl = GetString(args, "replyLanguage");
            if (rl is "zh" or "en" or "auto") s.ReplyLanguage = rl;
            if (args.TryGetProperty("allowUiControl", out var uiEl) && (uiEl.ValueKind == JsonValueKind.True || uiEl.ValueKind == JsonValueKind.False)) s.AllowUiControl = uiEl.GetBoolean();

        s.Save(Db);
        return new { ok = true, message = "AI 设置已保存", settings = GetAiSettings() };
    }

    private async Task<object> TestAi()
    {
        var s = AiSettings.Load(Db);
        var (ok, msg, _) = await AiClient.TestAsync(s);
        return new { ok, message = msg };
    }

    private object KbStatus(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };

        var manuals = Db.GetManuals(libId);
        var best = AiAssistant.PickBestManual(manuals);
        var kb = ManualKb.Load(libId);
        bool fresh = best != null && ManualKb.IsFresh(kb, best.FullPath);

        return new
        {
            ok = true,
            libraryId = libId,
            libraryName = lib.Name,
            manualCount = manuals.Count,
            manualName = best?.Name ?? "",
            manualPath = best?.FullPath ?? "",
            manualSize = best?.SizeBytes ?? 0,
            built = kb != null,
            fresh,
            building = _kbBuilding.Contains(libId),
            pages = kb?.PageCount ?? 0,
            chunks = kb?.Chunks.Count ?? 0,
            chars = kb?.TotalChars ?? 0,
            visionPages = kb?.VisionPages ?? 0,
            builtAt = kb?.BuiltAt ?? "",
            kbSize = KbDirSize(libId),
        };
    }

    private static long KbDirSize(long libId)
    {
        try
        {
            var d = new DirectoryInfo(ManualKb.DirFor(libId));
            return d.Exists ? d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch { return 0; }
    }


    // ══════════════════ 知识库：批量建立 ══════════════════

    private volatile bool _kbBatchRunning;
    private volatile bool _kbBatchCancel;

    /// <summary>
    /// **批量建立知识库** —— 扫描所有有 PDF 说明书的音色库，把还没建/已过期的逐个建起来。
    ///
    /// 为什么要批量：本项目有 300+ 份说明书，但知识库长期是 0 个 ——
    /// 意味着 Agent 的「问说明书」能力**从未真正启用**（问任何库的手册都会失败）。
    /// 逐个手点不现实，必须能一键批量。
    ///
    /// 设计取舍：
    ///   · **顺序执行**（不并发）—— 并发会同时打爆模型 API 和磁盘 IO，且进度难读；
    ///   · **只建缺失/已过期的**（用 ManualKb.IsFresh 判断），默认不重建已有的；
    ///   · **失败不中断** —— 单个库失败记下来继续下一个，最后汇总；
    ///   · **可取消** —— 通过 kbBatchCancel 设置标志，当前库建完后停止；
    ///   · **视觉页数受限** —— 批量时默认只对每个手册解析少量图片页（见 visionLimit），
    ///     否则 300+ 手册 × 80 页 × ~18s 完全跑不完。
    /// </summary>
    private object KbBuildAll(JsonElement args)
    {
        if (_kbBatchRunning)
            return new { ok = false, message = "批量建库正在进行中（可先点取消）" };

        bool useVision = !GetBool(args, "skipVision");
        bool rebuild = GetBool(args, "rebuild");          // true = 连已有的也重建
        long vLimitRaw = GetLong(args, "visionLimit");
        int visionLimit = vLimitRaw is > 0 and <= 80 ? (int)vLimitRaw : 12;

        var ai = AiSettings.Load(Db);
        if (!ai.Configured) useVision = false;

        // 收集候选：有 PDF 说明书、且（没建过 或 已过期）
        var todo = new List<(long Id, string Name, string ManualPath, string ManualName)>();
        foreach (var lib in Db.GetLibraries())
        {
            if (string.IsNullOrEmpty(lib.Path)) continue;
            ManualRecord? m;
            try { m = AiAssistant.PickBestManual(Db.GetManuals(lib.Id)); } catch { continue; }
            if (m == null) continue;
            if (!m.Ext.Equals("pdf", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(m.FullPath)) continue;
            if (!rebuild)
            {
                var existing = ManualKb.Load(lib.Id);
                if (ManualKb.IsFresh(existing, m.FullPath)) continue;   // 已是最新，跳过
            }
            todo.Add((lib.Id, lib.Name, m.FullPath, m.Name));
        }

        if (todo.Count == 0)
            return new { ok = true, total = 0, message = "所有有说明书的音色库都已建立知识库（可勾选「重建」强制重来）" };

        _kbBatchRunning = true;
        _kbBatchCancel = false;
        int total = todo.Count;

        _ = Task.Run(async () =>
        {
            int done = 0, okCount = 0, failCount = 0;
            var failures = new List<object>();
            var swAll = System.Diagnostics.Stopwatch.StartNew();

            Push(new { type = "kbBatch", state = "start", total, useVision, visionLimit });

            foreach (var item in todo)
            {
                if (_kbBatchCancel)
                {
                    Push(new { type = "kbBatch", state = "cancelled", done, total, ok = okCount, fail = failCount });
                    break;
                }

                Push(new
                {
                    type = "kbBatch", state = "item", done, total,
                    libraryId = item.Id, libraryName = item.Name, manualName = item.ManualName,
                });

                try
                {
                    var manuals = Db.GetManuals(item.Id);
                    var manual = manuals.FirstOrDefault(x => x.FullPath.Equals(item.ManualPath, StringComparison.OrdinalIgnoreCase));
                    if (manual == null) { failCount++; failures.Add(new { name = item.Name, error = "说明书记录已失效" }); done++; continue; }

                    // 复用单库建库的进度事件，前端可显示当前库的细进度
                    var prog = new Progress<KbProgress>(pp => Push(new
                    {
                        type = "kbProgress", libraryId = item.Id,
                        phase = pp.Phase, current = pp.Current, total = pp.Total, message = pp.Message,
                    }));

                    int savedLimit = ManualKb.VisionPageLimit;
                    ManualKb.VisionPageLimit = useVision ? visionLimit : 0;
                    LibraryKb kb;
                    try
                    {
                        kb = await ManualKb.BuildAsync(item.Id, item.Name, manual, useVision ? ai : null, prog);
                    }
                    finally { ManualKb.VisionPageLimit = savedLimit; }

                    okCount++;
                    Push(new
                    {
                        type = "kbBatch", state = "itemDone", done = done + 1, total, libraryId = item.Id,
                        pages = kb.PageCount, chunks = kb.Chunks.Count, chars = kb.TotalChars,
                    });
                }
                catch (Exception ex)
                {
                    failCount++;
                    failures.Add(new { name = item.Name, error = ex.Message });
                    Log($"批量建库：库「{item.Name}」失败：{ex.Message}");
                    Push(new { type = "kbBatch", state = "itemFail", done = done + 1, total, libraryId = item.Id, error = ex.Message });
                }
                done++;
            }

            swAll.Stop();
            _kbBatchRunning = false;
            Push(new
            {
                type = "kbBatch", state = "done", done, total, ok = okCount, fail = failCount,
                seconds = Math.Round(swAll.Elapsed.TotalSeconds, 1), failures,
            });
        });

        return new { ok = true, total, message = $"开始批量建立 {total} 个知识库（顺序执行，可取消）", vision = useVision, visionLimit };
    }

    private object KbBatchCancel()
    {
        if (!_kbBatchRunning) return new { ok = false, message = "当前没有正在进行的批量建库" };
        _kbBatchCancel = true;
        return new { ok = true, message = "已请求取消，当前这个库建完后停止" };
    }

    private object KbBuild(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        bool useVision = !GetBool(args, "skipVision");
        if (_kbBuilding.Contains(libId))
            return new { ok = false, message = "该库的知识库正在构建中" };

        var lib = Db.GetLibraries().FirstOrDefault(l => l.Id == libId);
        if (lib == null) return new { ok = false, message = "未找到该音色库" };

        var manuals = Db.GetManuals(libId);
        string wanted = GetString(args, "manualPath");
        ManualRecord? manual = wanted.Length > 0
            ? manuals.FirstOrDefault(m => m.FullPath.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            : AiAssistant.PickBestManual(manuals);

        if (manual == null)
            return new { ok = false, message = "该库没有可用于建库的说明书（需 PDF 等文档）" };
        if (!manual.Ext.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            return new { ok = false, message = $"暂只支持 PDF 说明书，该文档为 .{manual.Ext}" };

        var ai = AiSettings.Load(Db);
        if (useVision && !ai.Configured)
            useVision = false;   // 未配置 AI 时退化为纯文字抽取

        _kbBuilding.Add(libId);
        string libName = lib.Name;
        _ = Task.Run(async () =>
        {
            try
            {
                var prog = new Progress<KbProgress>(p => Push(new
                {
                    type = "kbProgress",
                    libraryId = libId,
                    phase = p.Phase,
                    current = p.Current,
                    total = p.Total,
                    message = p.Message,
                }));

                var kb = await ManualKb.BuildAsync(libId, libName, manual, useVision ? ai : null, prog);
                Push(new
                {
                    type = "kbDone",
                    libraryId = libId,
                    summary = new
                    {
                        pages = kb.PageCount,
                        chunks = kb.Chunks.Count,
                        chars = kb.TotalChars,
                        visionPages = kb.VisionPages,
                        manual = kb.ManualName,
                    },
                });
            }
            catch (Exception ex)
            {
                Log($"知识库构建失败（库 {libId}）：{ex}");
                Push(new { type = "kbError", libraryId = libId, error = ex.Message });
            }
            finally { _kbBuilding.Remove(libId); }
        });

        return new { ok = true, message = $"开始构建「{libName}」的知识库（{manual.Name}）", vision = useVision };
    }

    private object KbDelete(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        bool ok = ManualKb.Delete(libId);
        return new { ok, message = ok ? "已删除该库的知识库" : "删除失败（可能文件被占用）" };
    }

    private object KbSearch(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        var kb = ManualKb.Load(libId);
        if (kb == null) return new { ok = false, message = "该库尚未建立知识库" };
        string q = GetString(args, "query");
        int topK = (int)Math.Clamp(GetLong(args, "topK") is > 0 and <= 20 ? GetLong(args, "topK") : 6, 1, 20);
        var hits = ManualKb.HybridSearch(kb, q, topK);
        return new
        {
            ok = true,
            tokens = ManualKb.Tokenize(q, true).Distinct().ToList(),
            count = hits.Count,
            items = hits.Select(h => new { page = h.Page, preview = h.Text.Length > 300 ? h.Text[..300] : h.Text }),
        };
    }

    private async Task<object> AskAsync(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        string question = GetString(args, "question");
        if (question.Trim().Length == 0) return new { ok = false, message = "问题为空" };

        var kb = ManualKb.Load(libId);
        if (kb == null) return new { ok = false, message = "该库尚未建立知识库，请先点「建立知识库」" };

        var ai = AiSettings.Load(Db);
        if (!ai.Configured) return new { ok = false, message = "尚未配置 AI 端点，请到「设置 → AI 助手」填写 Base URL / API Key / 模型" };

        // 前端传来的历史（只取 role/content）
        var history = new List<ChatMessage>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("history", out var hEl) &&
            hEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in hEl.EnumerateArray())
            {
                string role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                string content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (content.Length > 0) history.Add(new ChatMessage { Role = role, Content = content });
            }
        }

        var res = await AiAssistant.AskAsync(ai, kb, question, history, BuildLibraryContext(libId),
            ev => Push(new
            {
                type = "aiEvent",
                libraryId = libId,
                kind = ev.Kind,
                text = ev.Text,
                toolName = ev.ToolName,
                toolArgs = ev.ToolArgs,
                pages = ev.Pages,
                step = ev.Step,
                pt = ev.PromptTokens,
                ct = ev.CompletionTokens,
                rt = ev.ReasoningTokens,
                ms = ev.ElapsedMs,
            }));

        return new
        {
            ok = res.Error.Length == 0,
            answer = res.Answer,
            pages = res.Pages,
            chunks = res.ChunkCount,
            compressed = res.UsedCompression,
            noManualMatch = res.NoManualMatch,
            error = res.Error,
            seconds = Math.Round(res.ElapsedSeconds, 1),
            manualName = kb.ManualName,
        };
    }

    /// <summary>组装 Agent 的库上下文（让它知道自己在为哪个库服务、库的状态如何）。</summary>
    private LibraryContext? BuildLibraryContext(long libraryId)
    {
        var libs = LoadLibraries();
        var lib = libs.FirstOrDefault(l => l.Id == libraryId);
        if (lib == null) return null;

        var manuals = Db.GetManuals(libraryId);
        var best = AiAssistant.PickBestManual(manuals);
        var kb = ManualKb.Load(libraryId);

        return new LibraryContext
        {
            Name = lib.Name,
            Category = lib.Category,
            SizeBytes = lib.SizeBytes,
            NkiCount = lib.NkiCount,
            NkmCount = lib.NkmCount,
            FileCount = lib.FileCount,
            RegStatus = lib.RegStatus,
            RequiredKontakt = lib.RequiredKontakt,
            KontaktVersion = KontaktVersion,
            CompatStatus = lib.CompatStatus,
            ManualName = best?.Name ?? "",
            ManualPageCount = kb?.PageCount ?? 0,
            InstrumentGroups = Db.GetInstrumentGroups(libraryId, 25),
        };
    }

    /// <summary>按需渲染/取回说明书某页图片，返回可被 WebView2 加载的 URL。</summary>
    private object PageImage(JsonElement args)
    {
        long libId = GetLong(args, "libraryId");
        int page = (int)GetLong(args, "page");
        var kb = ManualKb.Load(libId);
        if (kb == null) return new { ok = false, message = "该库尚未建立知识库" };

        var png = ManualKb.GetPageImage(libId, kb.ManualPath, page);
        if (png == null) return new { ok = false, message = $"第 {page} 页渲染失败" };

        return new
        {
            ok = true,
            page,
            url = $"https://kontakt-kb/{libId}/pages/p{page}.png",
            sizeKb = png.Length / 1024,
        };
    }

    /// <summary>入库后自动建库（功能 4）：为刚入库的库在后台生成知识库。</summary>
    private object AutoKb(JsonElement args)
    {
        var ids = new List<long>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ids", out var idsEl) &&
            idsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in idsEl.EnumerateArray())
                if (el.TryGetInt64(out long v)) ids.Add(v);
        }

        var started = new List<object>();
        var skipped = new List<object>();
        foreach (var id in ids)
        {
            var manuals = Db.GetManuals(id);
            var best = AiAssistant.PickBestManual(manuals);
            if (best == null) { skipped.Add(new { libraryId = id, reason = "该库没有说明书文档" }); continue; }
            // **不再限定 .pdf** —— 库里 .txt 798 + .rtf 434 = 1,232 本，旧逻辑直接跳过它们，
            // 于是绝大多数库「入库后自动建库」从未发生，用户看到「知识库还是空的」却不知原因。
            var kb0 = ManualKb.Load(id);
            if (ManualKb.IsFresh(kb0, best.FullPath)) { skipped.Add(new { libraryId = id, reason = "知识库已是最新" }); continue; }
            // 复用 KbBuild 的启动逻辑
            var fake = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(
                $"{{\"libraryId\":{id}}}");
            var r = KbBuild(fake);
            started.Add(new { libraryId = id, manual = best.Name, ext = best.Ext, result = r });
        }

        return new { ok = true, started = started.Count, details = started, skippedCount = skipped.Count, skipped = skipped.Take(20) };
    }

    private static string GetString(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? "" : "";

    private static long GetLong(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var el) && el.TryGetInt64(out long v) ? v : 0;

    private static bool GetBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.GetDouble() != 0,
            JsonValueKind.String => bool.TryParse(el.GetString(), out bool b) && b,
            _ => false,
        };
    }

    // ── C# → JS ───────────────────────────────────────────
    private void Reply(long id, bool ok, object? result, string? error = null)
    {
        var payload = JsonSerializer.Serialize(new { id, ok, result, error }, Json);
        Dispatcher.Invoke(() =>
        {
            try { Web.CoreWebView2?.PostWebMessageAsJson(payload); }
            catch { }
        });
    }

        // 🔴 **攒批投递（2026-09-25 性能优化最后一层）** ——
        //   背景：每次 Push 都要在 WPF **UI 线程**上执行一次 `PostWebMessageAsJson`；
        //   流式输出时每秒几十次 ⇒ **UI 线程频繁被唤醒、WebView2 反复跨进程通信**
        //   ⇒ 表现为「Agent 输出时打字轻微卡」（用户实测反馈）。
        //   ✅ 做法：把 payload 先攒进 `_pushQueue`，由一个 ~40ms 的节拍统一 flush，
        //     一次投递一个 **JSON 数组**。前端已兼容数组（逐条处理）。
        //   ⚠️ 必须保留【立即 flush 的通道】：`PushNow` 用于「用户主动操作后的即时反馈」
        //     （如 toast / 按钮状态），否则会感觉迟钝。
        private void Push(object message, bool lowPriority = false)
        {
            string payload;
            try { payload = JsonSerializer.Serialize(message, Json); }
            catch (Exception ex) { Log($"[push] 序列化失败：{ex.Message}"); return; }

            lock (_pushLock)
            {
                _pushQueue.Add(payload);
                if (_pushTimerArmed) return;
                _pushTimerArmed = true;
            }
            // 用 BeginInvoke 而非 Invoke（非阻塞）—— 见下方原注释
            try
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(FlushPushQueue));
            }
            catch (Exception ex) { Log($"[push] BeginInvoke 失败：{ex.Message}"); }
        }

        /// <summary>把攒下的 payload 一次性投递给前端（JSON 数组）。</summary>
        private void FlushPushQueue()
        {
            List<string>? batch = null;
            lock (_pushLock)
            {
                if (_pushQueue.Count > 0) { batch = new List<string>(_pushQueue); _pushQueue.Clear(); }
                _pushTimerArmed = false;
            }
            if (batch == null) return;
            try
            {
                // 单条也发数组，前端逻辑统一（避免两条分支）
                string json = "[" + string.Join(",", batch) + "]";
                Web.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch (Exception ex) { Log($"[push] 批量投递失败：{ex.Message}"); }
        }

        private readonly List<string> _pushQueue = new();
        private readonly object _pushLock = new();
        private bool _pushTimerArmed;
}
