using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace KontaktLibManager.Core;

/// <summary>Kontakt 本体探测：注册表卸载项 + 常见安装路径 + 手动指定。</summary>
public static class KontaktInfo
{
    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>读取 exe 版本：文件版本 → 产品版本 → 文件名启发式。</summary>
    public static string ReadVersion(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return "";
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            if (VersionUtil.IsValid(vi.FileVersion)) return Normalize(vi.FileVersion!);
            if (VersionUtil.IsValid(vi.ProductVersion)) return Normalize(vi.ProductVersion!);
        }
        catch { }

        // 便携版常被剥离版本资源，退回文件名启发式（如 KontaktPortable_v871.exe → 8.7.1）
        string guessed = VersionUtil.GuessFromFileName(Path.GetFileNameWithoutExtension(exePath));
        if (VersionUtil.IsValid(guessed)) return guessed;

        // 最后尝试从卸载项里按目录名匹配
        return MatchFromUninstall(exePath);
    }

    private static string Normalize(string v)
    {
        var parts = VersionUtil.ParseParts(v);
        return parts.Length == 0 ? "" : string.Join('.', parts);
    }

    private static string MatchFromUninstall(string exePath)
    {
        string dir = Path.GetDirectoryName(exePath) ?? "";
        foreach (var entry in ReadUninstallEntries())
        {
            if (!string.IsNullOrEmpty(entry.InstallLocation) &&
                dir.StartsWith(entry.InstallLocation.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                VersionUtil.IsValid(entry.DisplayVersion))
                return Normalize(entry.DisplayVersion);
        }
        return "";
    }

    private readonly record struct UninstallEntry(string DisplayName, string DisplayVersion, string InstallLocation, string Icon);

    private static List<UninstallEntry> ReadUninstallEntries()
    {
        var list = new List<UninstallEntry>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(name);
                    if (sub == null) continue;
                    string display = sub.GetValue("DisplayName") as string ?? "";
                    if (!display.Contains("Kontakt", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new UninstallEntry(
                        display,
                        sub.GetValue("DisplayVersion") as string ?? "",
                        sub.GetValue("InstallLocation") as string ?? "",
                        sub.GetValue("DisplayIcon") as string ?? ""));
                }
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// 自动探测本机所有 Kontakt 主程序。
    /// 精度要点（实测教训）：
    ///   1. 必须排除安装包 —— 便携版安装程序（如 KontaktPortable_v871.exe，1GB、无版本信息）
    ///      曾被误当成主程序；真主程序（Kontakt 8.exe，160MB）带 Native Instruments 版本信息。
    ///   2. 必须深挖目录 —— 真主程序常在 ``Kontakt 8\x64\`` 这种深层子目录。
    ///   3. 命名匹配 ``Kontakt N.exe`` 优先级最高，其次 ``Kontakt.exe``。
    /// </summary>
    public static KontaktScanResult Detect()
    {
        var installs = new Dictionary<string, KontaktInstall>(StringComparer.OrdinalIgnoreCase);
        var ignored = new Dictionary<string, KontaktInstall>(StringComparer.OrdinalIgnoreCase);

        void Consider(string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Trim().Trim('"');
            if (path.EndsWith(",")) path = path[..^1];
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(path)) return;
            if (installs.ContainsKey(path) || ignored.ContainsKey(path)) return;

            var info = Describe(path, source);
            if (info == null) return;
            if (info.Note.StartsWith("IGNORE", StringComparison.Ordinal)) ignored[path] = info;
            else installs[path] = info;
        }

        // 1) 注册表卸载项：DisplayIcon 与 InstallLocation
        foreach (var e in ReadUninstallEntries())
        {
            if (!string.IsNullOrEmpty(e.Icon)) Consider(e.Icon, "registry");
            if (!string.IsNullOrEmpty(e.InstallLocation))
                foreach (var exe in SafeExeIn(e.InstallLocation, "Kontakt"))
                    Consider(exe, "registry");
        }

        // 2) 文件系统：候选根目录下剪枝递归查找（只深入名字像 Kontakt/NI 的目录）
        foreach (var root in CandidateRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var exe in FindKontaktExes(root, maxDepth: 3))
                Consider(exe, "common-path");
        }

        var list = installs.Values
            .OrderByDescending(Score)
            .ThenBy(k => k.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new KontaktScanResult
        {
            Installs = list,
            Ignored = ignored.Values.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            IsAdmin = IsAdmin(),
            KontaktRunning = IsRunning(),
        };
    }

    /// <summary>兼容旧调用：仅返回可用程序。</summary>
    public static List<KontaktInstall> DetectInstalls() => Detect().Installs;

    /// <summary>读取单个程序的展示信息（用户手工添加时使用；不过滤，返回原始判定）。</summary>
    public static KontaktInstall DescribeForUser(string path)
    {
        var info = Describe(path, "manual");
        if (info == null)
        {
            return new KontaktInstall
            {
                Path = path,
                Source = "manual",
                Note = File.Exists(path) ? "无法读取文件信息" : "文件不存在",
                Exists = File.Exists(path),
            };
        }
        if (info.Note.StartsWith("IGNORE", StringComparison.Ordinal))
            info.Note = info.Note.Replace("IGNORE: ", "注意：");
        return info;
    }

    private static int Score(KontaktInstall i)
    {
        int s = 0;
        string name = Path.GetFileName(i.Path);
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^Kontakt( \d+)?\.exe$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) s += 100;
        if (VersionUtil.IsValid(i.Version)) s += 50;
        var parts = VersionUtil.ParseParts(i.Version);
        if (parts.Length > 0) s += parts[0] * 2;   // 版本越高越优先
        if (i.Source == "registry") s += 5;
        return s;
    }

    /// <summary>读取 exe 元数据并判定它是主程序还是安装包。</summary>
    private static KontaktInstall? Describe(string path, string source)
    {
        FileVersionInfo vi;
        long size;
        try
        {
            vi = FileVersionInfo.GetVersionInfo(path);
            size = new FileInfo(path).Length;
        }
        catch { return null; }

        string fileName = Path.GetFileName(path);
        string company = vi.CompanyName ?? "";
        string product = vi.ProductName ?? "";
        string desc = vi.FileDescription ?? "";
        string version = VersionUtil.IsValid(vi.FileVersion) ? Normalize(vi.FileVersion!)
                       : (VersionUtil.IsValid(vi.ProductVersion) ? Normalize(vi.ProductVersion!) : "");

        double sizeMb = Math.Round(size / 1024.0 / 1024.0, 1);
        bool noMetadata = string.IsNullOrWhiteSpace(company) && string.IsNullOrWhiteSpace(product)
                          && string.IsNullOrWhiteSpace(vi.FileVersion);

        var install = new KontaktInstall
        {
            Path = path,
            Version = version,
            ProductName = product.Length > 0 ? product : fileName,
            Company = company,
            Description = desc,
            SizeMB = sizeMb,
            Source = source,
            Exists = true,
        };

        // ── 判定 1：安装包/卸载器/更新包 —— 直接忽略 ──
        if (System.Text.RegularExpressions.Regex.IsMatch(fileName,
                @"^(unins|setup|install)|(Setup|Installer|Portable_v|Portable_?\d|Patcher|Updater|Update|Button|Helper)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            install.Note = "IGNORE: 命名像安装程序/更新包";
            return install;
        }
        if (sizeMb > 500)
        {
            install.Note = $"IGNORE: 体积 {sizeMb} MB，远超主程序（通常 100–250 MB），应为安装包";
            return install;
        }

        // ── 判定 2：必须是 Kontakt 主程序 ──
        bool niBranded = company.Contains("Native Instruments", StringComparison.OrdinalIgnoreCase)
                      || product.Contains("Native Instruments", StringComparison.OrdinalIgnoreCase);
        bool isKontakt = product.Contains("Kontakt", StringComparison.OrdinalIgnoreCase)
                      || desc.Contains("Kontakt", StringComparison.OrdinalIgnoreCase)
                      || System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^Kontakt( \d+)?\.exe$",
                             System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (noMetadata)
        {
            // 无任何版本信息：只有在命名严格匹配且体积合理时才接受
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^Kontakt( \d+)?\.exe$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase) && sizeMb is > 30 and < 500)
            {
                install.Note = "无版本信息，但命名与体积符合主程序特征";
                return install;
            }
            install.Note = "IGNORE: 无任何版本信息，判定为安装包/非程序文件";
            return install;
        }

        if (!isKontakt)
        {
            install.Note = "IGNORE: 元数据中不含 Kontakt";
            return install;
        }
        if (!niBranded)
        {
            install.Note = "IGNORE: 非 Native Instruments 出品";
            return install;
        }

        install.Note = version.Length > 0 ? $"Kontakt 主程序 v{version}" : "Kontakt 主程序（版本未知）";
        return install;
    }

    /// <summary>剪枝递归查找：只深入到名字像 Kontakt / Native Instruments 的目录，避免全盘慢扫。</summary>
    private static List<string> FindKontaktExes(string root, int maxDepth)
    {
        var found = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;

            // 收集当前目录下的 Kontakt*.exe
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "Kontakt*.exe", SearchOption.TopDirectoryOnly))
                    found.Add(f);
            }
            catch { }

            // 仅当目录名像 Kontakt/Native Instruments，或还在浅层时，才继续下探
            bool interesting = System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileName(dir.TrimEnd('\\')),
                @"Kontakt|Native Instruments",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (depth >= maxDepth) continue;
            if (depth >= 1 && !interesting) continue;   // 第 1 层之后只深入「像 Kontakt」的目录

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    queue.Enqueue((sub, depth + 1));
            }
            catch { }
        }
        return found;
    }

    private static List<string> SafeExeIn(string dir, string prefix)
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string n = Path.GetFileName(f);
                if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !n.StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                    !n.Contains("Button", StringComparison.OrdinalIgnoreCase) &&
                    !n.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    list.Add(f);
            }
        }
        catch { }
        return list;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
            string r = drive.RootDirectory.FullName;
            yield return r;                                                  // D:\Kontakt8\
            yield return Path.Combine(r, "Program Files");                    // D:\Program Files\Kontakt 7\
            yield return Path.Combine(r, "Program Files (x86)");
            yield return Path.Combine(r, "Program Files", "Native Instruments");
            yield return Path.Combine(r, "Program Files (x86)", "Native Instruments");
            yield return Path.Combine(r, "Native Instruments");
            yield return Path.Combine(r, "Program Files", "Common Files", "VST3");
        }
    }

    /// <summary>启动 Kontakt；可附带要打开的乐器文件。</summary>
    public static (bool ok, string message) Launch(string exePath, string? fileToOpen = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return (false, "未设置 Kontakt 程序路径");
        if (!File.Exists(exePath)) return (false, $"Kontakt 程序不存在：{exePath}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            };
            if (!string.IsNullOrWhiteSpace(fileToOpen))
                psi.Arguments = $"\"{fileToOpen}\"";
            Process.Start(psi);
            return (true, string.IsNullOrWhiteSpace(fileToOpen) ? "已启动 Kontakt" : $"已启动 Kontakt 并请求打开：{fileToOpen}");
        }
        catch (Exception ex)
        {
            return (false, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 真正在运行的 Kontakt 本体进程。
    /// 注意：不能用「进程名包含 Kontakt」判断——本工具自身叫 KontaktLibManager 也会命中，
    /// 之前正是这里误报「Kontakt 运行中」。只认 Kontakt / Kontakt 7 / Kontakt 8 这类名字。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex KontaktProcPattern =
        new(@"^Kontakt( \d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static List<Process> KontaktProcesses()
    {
        var list = new List<Process>();
        try
        {
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self) continue;
                    if (!KontaktProcPattern.IsMatch(p.ProcessName)) continue;

                    // 🔴 **排除僵尸进程（重要）** ——
                    //   实测：某些 Kontakt 7 进程会进入「已终止但父进程未回收」的僵尸态：
                    //     · 内存 0.0 MB、Kill() 报 Access denied、无法手动关闭
                    //     · 却仍然出现在进程列表里
                    //   若不排除，IsRunning() 会【永远返回 true】⇒ 便携版写入永远被拒绝
                    //   （用户报：「一直提示有 2 个 Kontakt 7 僵尸进程，怎么也无法关闭」）。
                    //   ⇒ 判据：HasExited 为 true，或 内存为 0（僵尸态的典型特征）。
                    if (IsZombie(p)) continue;

                    list.Add(p);
                }
                catch { /* 进程可能已退出 */ }
            }
        }
        catch { }
        return list;
    }

    public static bool IsRunning() => KontaktProcesses().Count > 0;

    /// <summary>关闭正在运行的 Kontakt（先优雅关闭，超时后强制结束）。</summary>
    public static (bool ok, string message) CloseKontakt(int waitMs = 10000)
    {
        var procs = KontaktProcesses();
        if (procs.Count == 0) return (true, "Kontakt 当前未在运行");

        var names = new List<string>();
        foreach (var p in procs)
        {
            try { names.Add($"{p.ProcessName}(PID {p.Id})"); } catch { }
            try { p.CloseMainWindow(); } catch { }
        }

        var deadline = DateTime.Now.AddMilliseconds(waitMs);
        while (DateTime.Now < deadline && procs.Any(p => !SafeHasExited(p)))
            Thread.Sleep(300);

        var remaining = procs.Where(p => !SafeHasExited(p)).ToList();
        foreach (var p in remaining)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        if (remaining.Count > 0) Thread.Sleep(800);

        int left = procs.Count(p => !SafeHasExited(p));
        return left == 0
            ? (true, $"已关闭 {names.Count} 个 Kontakt 进程：{string.Join(", ", names)}")
            : (false, $"仍有 {left} 个 Kontakt 进程未能关闭，请手动结束后再入库");
    }

    /// <summary>
    /// **判断是否为僵尸进程** —— 已终止但父进程尚未回收。
    /// 特征：<c>HasExited</c> 为 true，或 工作集为 0（内存 0.0 MB）。
    /// **为什么重要**：僵尸进程无法 Kill（Access denied），但会出现在进程列表里；
    /// 若不排除，任何「是否在运行」的检测都会永远为真。
    /// </summary>
    private static bool IsZombie(Process p)
    {
        try { if (p.HasExited) return true; } catch { return true; }   // 读不到 ⇒ 当作已退出
        try
        {
            // 内存 0 是僵尸态的典型特征（正常进程至少几百 KB）
            if (p.WorkingSet64 == 0 && p.PrivateMemorySize64 == 0) return true;
        }
        catch { return true; }
        return false;
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }
}
