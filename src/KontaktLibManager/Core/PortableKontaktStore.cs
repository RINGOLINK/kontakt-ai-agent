using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// 便携版 Kontakt 的库列表读取。
///
/// 关键事实（实测）：便携版 Kontakt 不使用系统注册表作为最终库列表，
/// 而是把库列表保存在自身目录的 ``UserData\Settings.cfg`` 中，条目形如：
///   [Chris Hein - Solo ContraBass]
///   Name=sz:...
///   ContentDir=sz:D:\Kontakt Libraries\...
///   JDX=sz:...  HU=sz:...  Visibility=dw:3
/// 该文件由 Kontakt 自带的「库管理器」(Helper\Library Manager) 扫描系统注册表后写入，
/// 用户必须点 SAVE 并重启 Kontakt 才生效。
///
/// 因此：仅写系统注册表不足——必须在库管理器里「扫描 → 保存」。
/// 本类负责定位该文件、读出真实库列表、并给出与注册表的差异。
/// </summary>
public static class PortableKontaktStore
{
    public sealed class PortableInfo
    {
        public string Root { get; set; } = "";
        public string SettingsCfg { get; set; } = "";
        public string LibraryManager { get; set; } = "";
        public List<string> RegisteredNames { get; set; } = new();
        public List<string> ContentDirs { get; set; } = new();
        public DateTime SettingsCfgModified { get; set; }
        public bool Exists => SettingsCfg.Length > 0 && File.Exists(SettingsCfg);
    }

    /// <summary>从 Kontakt 主程序路径向上寻找便携版根目录（含 UserData\Settings.cfg 或 Kontakt*Portable.txt）。</summary>
    public static string? FindPortableRoot(string kontaktExePath)
    {
        if (string.IsNullOrWhiteSpace(kontaktExePath)) return null;
        string? dir = Path.GetDirectoryName(kontaktExePath);
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
        {
            try
            {
                string cfg = Path.Combine(dir, "UserData", "Settings.cfg");
                if (File.Exists(cfg)) return dir;

                if (Directory.EnumerateFiles(dir, "Kontakt*Portable.txt", SearchOption.TopDirectoryOnly).Any())
                    return dir;
            }
            catch { }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static PortableInfo? Inspect(string kontaktExePath)
    {
        string? root = FindPortableRoot(kontaktExePath);
        if (root == null) return null;

        var info = new PortableInfo { Root = root };

        string cfg = Path.Combine(root, "UserData", "Settings.cfg");
        if (File.Exists(cfg))
        {
            info.SettingsCfg = cfg;
            info.SettingsCfgModified = File.GetLastWriteTime(cfg);
            (info.RegisteredNames, info.ContentDirs) = ParseSettingsCfg(cfg);
        }

        string lmDir = Path.Combine(root, "Helper", "Library Manager");
        if (Directory.Exists(lmDir))
        {
            var exe = Directory.EnumerateFiles(lmDir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exe != null) info.LibraryManager = exe;
        }

        return info;
    }

    /// <summary>
    /// 解析 Settings.cfg：收集所有「库条目」的节名（= 注册表 RegKey）与 ContentDir。
    /// 只把同时含 ContentDir 的节算作库条目，避免把设置节混进来。
    /// </summary>
    public static (List<string> names, List<string> contentDirs) ParseSettingsCfg(string path)
    {
        var names = new List<string>();
        var dirs = new List<string>();

        string? currentSection = null;
        string? currentDir = null;
        bool currentIsLibrary = false;

        void Flush()
        {
            if (currentIsLibrary && !string.IsNullOrWhiteSpace(currentSection))
            {
                names.Add(currentSection!);
                if (!string.IsNullOrWhiteSpace(currentDir)) dirs.Add(currentDir!);
            }
            currentSection = null; currentDir = null; currentIsLibrary = false;
        }

        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                currentSection = line[1..^1].Trim();
                continue;
            }

            if (currentSection == null) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();

            // 值形如 sz:内容 或 dw:数字
            if (val.StartsWith("sz:", StringComparison.OrdinalIgnoreCase)) val = val[3..];

            if (key.Equals("ContentDir", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
            {
                currentDir = LibraryRegistrar.NormalizePath(val);
                currentIsLibrary = true;
            }
        }
        Flush();

        return (names, dirs);
    }

    /// <summary>
    /// 改写便携版 Settings.cfg 中**已有库节**的 ContentDir（音色库移动后修复路径用）。
    /// 行级改写、保留其余内容与 CRLF 换行；写前备份、写后校验、失败自动还原。
    /// </summary>
    public static (bool ok, string message, int updated) UpdateLibraryPaths(
        string kontaktExePath, Dictionary<string, string> regKeyToNewPath)
    {
        var info = Inspect(kontaktExePath);
        if (info == null || !info.Exists) return (false, "未检测到便携版 Kontakt", 0);
        if (KontaktInfo.IsRunning()) return (false, "Kontakt 正在运行，请先完全关闭（它退出时会覆写 Settings.cfg）", 0);
        if (regKeyToNewPath.Count == 0) return (true, "无需更新", 0);

        string cfg = info.SettingsCfg;
        string backup = Backup(cfg, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        int updated = 0;

        try
        {
            string text = File.ReadAllText(cfg, Encoding.UTF8);
            var lines = text.Split("\r\n");
            string? section = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }
                if (section == null || !line.StartsWith("ContentDir=sz:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!regKeyToNewPath.TryGetValue(section, out var newPath)) continue;

                string norm = LibraryRegistrar.NormalizePath(newPath);
                lines[i] = "ContentDir=sz:" + norm;
                updated++;
            }

            if (updated > 0)
                File.WriteAllText(cfg, string.Join("\r\n", lines), new UTF8Encoding(false));

            // 校验：重新解析，确认这些节的路径已指向新位置
            var after = ParseSettingsCfg(cfg);
            int verified = 0;
            for (int i = 0; i < after.names.Count; i++)
            {
                if (!regKeyToNewPath.TryGetValue(after.names[i], out var want)) continue;
                if (i < after.contentDirs.Count &&
                    after.contentDirs[i].Equals(LibraryRegistrar.NormalizePath(want), StringComparison.OrdinalIgnoreCase))
                    verified++;
            }

            if (verified < updated)
            {
                Restore(cfg, backup);
                return (false, $"校验失败（{verified}/{updated}），已自动还原", 0);
            }

            return (true, $"已更新 {updated} 个库的便携版路径" + (backup.Length > 0 ? $"（备份：{Path.GetFileName(backup)}）" : ""), updated);
        }
        catch (Exception ex)
        {
            Restore(cfg, backup);
            return (false, $"改写失败（已尝试还原）：{ex.Message}", 0);
        }
    }

    /// <summary>启动库管理器（用户在界面里点「扫描 → 保存」即可把注册表里的库写入便携版列表）。</summary>
    public static (bool ok, string message) LaunchLibraryManager(string kontaktExePath)
    {
        var info = Inspect(kontaktExePath);
        if (info == null) return (false, "未检测到便携版 Kontakt（没有 UserData\\Settings.cfg）");
        if (info.LibraryManager.Length == 0 || !File.Exists(info.LibraryManager))
            return (false, $"未找到库管理器：{info.LibraryManager}");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = info.LibraryManager,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(info.LibraryManager) ?? "",
            });
            return (true, "已启动 Kontakt 库管理器：请在其中执行「扫描」(Alt+Insert)，然后点「保存/SAVE」，再重启 Kontakt 生效。");
        }
        catch (Exception ex)
        {
            return (false, $"启动库管理器失败：{ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  自动写入便携版库列表（等价于库管理器的「扫描 → 保存」）
    //
    //  写入两处（均为字节级追加，不做整体重写，最大限度降低风险）：
    //    ① UserData\Settings.cfg            —— 库列表（[RegKey] 段，含 ContentDir/HU/JDX）
    //    ② UserData\Service Center\LibraryHints.xml —— 库提示元数据（Name/Company/SNPID/RegKey）
    //
    //  安全保障：写入前备份 → 追加 → 重新解析校验 → 校验失败自动回滚。
    //  写入内容全部来自各库自带的 .nicnt，不伪造任何字段。
    // ══════════════════════════════════════════════════════════

    public sealed class WriteResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public List<string> Added { get; set; } = new();
        public List<string> Skipped { get; set; } = new();
        public string BackupSettingsCfg { get; set; } = "";
        public string BackupLibraryHints { get; set; } = "";
    }

    /// <summary>
    /// 待写入便携版的库条目。
    /// 元数据来源有三级：.nicnt（首选，字段最全）→ 系统注册表 → LibraryHints.xml。
    /// 无 .nicnt 的旧式库没有 HU/JDX，此时只写能拿到的字段（Kontakt 对此类库不校验授权）。
    /// </summary>
    public sealed class LibraryEntry
    {
        public string RegKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string Company { get; set; } = "";
        public string SnpId { get; set; } = "";
        public string ContentDir { get; set; } = "";
        public string Visibility { get; set; } = "3";
        public string Hu { get; set; } = "";
        public string Jdx { get; set; } = "";
        public string ContentVersion { get; set; } = "";
        public string Source { get; set; } = "";   // nicnt | registry
        public bool HasAuth => Hu.Length > 0 && Jdx.Length > 0;
    }

    /// <summary>LibraryHints.xml 中的一条记录（便携版自带的库提示元数据）。</summary>
    public sealed class HintEntry
    {
        public string Name { get; set; } = "";
        public string Company { get; set; } = "";
        public string SnpId { get; set; } = "";
        public string RegKey { get; set; } = "";
    }

    /// <summary>读取便携版 LibraryHints.xml（无 .nicnt 的库可从这里补 Name/SNPID）。</summary>
    public static List<HintEntry> ReadLibraryHints(PortableInfo? info)
    {
        var list = new List<HintEntry>();
        if (info == null || info.Root.Length == 0) return list;

        string path = Path.Combine(info.Root, "UserData", "Service Center", "LibraryHints.xml");
        if (!File.Exists(path)) return list;

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(path);
            foreach (var lib in doc.Descendants("Library"))
            {
                list.Add(new HintEntry
                {
                    Name = lib.Element("Name")?.Value?.Trim() ?? "",
                    Company = lib.Element("Company")?.Value?.Trim() ?? "",
                    SnpId = lib.Element("SNPID")?.Value?.Trim() ?? "",
                    RegKey = lib.Element("RegKey")?.Value?.Trim() ?? "",
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>按库名/SNPID 在 LibraryHints 中查找（库目录名与产品名常不一致，故做模糊匹配）。</summary>
    public static HintEntry? FindHint(List<HintEntry> hints, string libraryName, string regKey)
    {
        string norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        string nLib = norm(libraryName), nKey = norm(regKey);

        foreach (var h in hints)
        {
            string nName = norm(h.Name);
            if (nName.Length == 0) continue;
            if (nName == nKey || nName == nLib) return h;
        }
        foreach (var h in hints)
        {
            string nName = norm(h.Name);
            if (nName.Length < 4) continue;
            if (nKey.Length >= 4 && (nName.Contains(nKey) || nKey.Contains(nName))) return h;
            if (nLib.Length >= 4 && (nName.Contains(nLib) || nLib.Contains(nName))) return h;
        }
        return null;
    }

    public static LibraryEntry FromNicnt(NicntInfo n) => new()
    {
        RegKey = n.RegKey,
        Name = n.Name,
        Company = n.Company,
        SnpId = n.SnpId,
        ContentDir = LibraryRegistrar.NormalizePath(Path.GetDirectoryName(n.FilePath) ?? ""),
        Visibility = n.Visibility.Length > 0 ? n.Visibility : "3",
        Hu = n.Hu,
        Jdx = n.Jdx,
        ContentVersion = n.ContentVersion,
        Source = "nicnt",
    };

    /// <summary>
    /// 为一个音色库组装写入条目。三级数据源回退：
    ///   ① 库目录里的 .nicnt（字段最全）
    ///   ② 系统注册表中已有条目（RegKey/ContentDir/HU/JDX/ContentVersion）
    ///   ③ 便携版 LibraryHints.xml（补 Name/SNPID——无 .nicnt 的旧式库主要靠这里）
    /// </summary>
    public static List<LibraryEntry> BuildEntries(
        string libraryPath, string libraryName,
        Dictionary<string, RegisteredProduct> registered,
        List<HintEntry> hints)
    {
        var entries = new List<LibraryEntry>();

        var nicnts = NicntReader.FindInLibrary(libraryPath, maxDepth: 3);
        if (nicnts.Count > 0)
        {
            entries.AddRange(nicnts.Select(FromNicnt));
            return entries;
        }

        string norm = LibraryRegistrar.NormalizePath(libraryPath);
        var prod = registered.Values.FirstOrDefault(p =>
            p.ContentDir.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
            p.ContentDir.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase));

        string regKey = prod?.RegKey ?? libraryName;
        var hint = FindHint(hints, libraryName, regKey);

        entries.Add(new LibraryEntry
        {
            RegKey = regKey,
            Name = hint?.Name is { Length: > 0 } hn ? hn : regKey,
            Company = hint?.Company ?? "",
            SnpId = hint?.SnpId ?? "",
            ContentDir = prod?.ContentDir is { Length: > 0 } cd ? cd : norm,
            Visibility = "3",
            Hu = prod?.Hu ?? "",
            Jdx = prod?.Jdx ?? "",
            ContentVersion = prod?.ContentVersion ?? "",
            Source = prod != null ? "registry" : "fallback",
        });
        return entries;
    }

    public static WriteResult AddLibraries(string kontaktExePath, IEnumerable<NicntInfo> nicnts)
        => AddLibraries(kontaktExePath, nicnts.Select(FromNicnt));

    public static WriteResult AddLibraries(string kontaktExePath, IEnumerable<LibraryEntry> entries)
    {
        var result = new WriteResult();
        var info = Inspect(kontaktExePath);
        if (info == null || !info.Exists)
        {
            result.Message = "未检测到便携版 Kontakt（没有 UserData\\Settings.cfg）";
            return result;
        }

        if (KontaktInfo.IsRunning())
        {
            result.Message = "Kontakt 正在运行：它退出时会覆写 Settings.cfg，请先完全关闭 Kontakt";
            return result;
        }

        var wanted = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.RegKey) && !string.IsNullOrWhiteSpace(e.ContentDir))
            .GroupBy(e => e.RegKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (wanted.Count == 0)
        {
            result.Message = "没有可写入的库（缺少注册表键名或库路径）";
            return result;
        }

        string cfg = info.SettingsCfg;
        string hintsPath = Path.Combine(info.Root, "UserData", "Service Center", "LibraryHints.xml");

        // ── 备份 ──
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        result.BackupSettingsCfg = Backup(cfg, stamp);
        if (File.Exists(hintsPath)) result.BackupLibraryHints = Backup(hintsPath, stamp);

        try
        {
            var existing = new HashSet<string>(
                ParseSettingsCfg(cfg).names, StringComparer.OrdinalIgnoreCase);

            // 现有最大 UserListIndex
            int maxIndex = 0;
            foreach (var line in File.ReadLines(cfg, Encoding.UTF8))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^UserListIndex=dw:(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int v) && v > maxIndex) maxIndex = v;
            }

            // ── ① 追加 Settings.cfg 节 ──
            var sb = new StringBuilder();
            foreach (var e in wanted)
            {
                if (existing.Contains(e.RegKey))
                {
                    result.Skipped.Add(e.RegKey);
                    continue;
                }
                maxIndex++;
                sb.Append('[').Append(e.RegKey).Append("]\r\n");
                if (e.Name.Length > 0) sb.Append("Name=sz:").Append(e.Name).Append("\r\n");
                if (e.SnpId.Length > 0) sb.Append("SNPID=sz:").Append(e.SnpId).Append("\r\n");
                if (e.Company.Length > 0) sb.Append("Company=sz:").Append(e.Company).Append("\r\n");
                sb.Append("ContentDir=sz:").Append(e.ContentDir).Append("\r\n");
                sb.Append("Visibility=dw:").Append(e.Visibility.Length > 0 ? e.Visibility : "3").Append("\r\n");
                if (e.HasAuth)
                {
                    sb.Append("JDX=sz:").Append(e.Jdx).Append("\r\n");
                    sb.Append("HU=sz:").Append(e.Hu).Append("\r\n");
                }
                sb.Append("ContentVersion=sz:").Append(e.ContentVersion.Length > 0 ? e.ContentVersion : "1.0.0").Append("\r\n");
                sb.Append("UserListIndex=dw:").Append(maxIndex).Append("\r\n");
                sb.Append("UserRemoved=dw:0\r\n");
                sb.Append("Expansion=dw:0\r\n");
                sb.Append("DBRemoved=dw:0\r\n");
                result.Added.Add(e.RegKey);
                existing.Add(e.RegKey);
            }

            if (result.Added.Count == 0)
            {
                result.Ok = true;
                result.Message = "便携版库列表已包含全部目标库，无需写入";
                return result;
            }

            // 字节级追加（保持原编码/CRLF 换行，绝不整体重写）
            var append = new UTF8Encoding(false).GetBytes(sb.ToString());
            using (var fs = new FileStream(cfg, FileMode.Append, FileAccess.Write, FileShare.None))
                fs.Write(append, 0, append.Length);

            // ── ② 追加 LibraryHints.xml 提示项 ──
            if (File.Exists(hintsPath))
            {
                var hintText = File.ReadAllText(hintsPath, Encoding.UTF8);
                var blocks = new StringBuilder();
                foreach (var e in wanted.Where(x => result.Added.Contains(x.RegKey, StringComparer.OrdinalIgnoreCase)))
                {
                    // 按 RegKey 或 Name 去重（部分库的 hint 用 GUID 作 RegKey，故 Name 也要比）
                    if (hintText.Contains($"<RegKey>{e.RegKey}</RegKey>", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.Name.Length > 0 &&
                        hintText.Contains($"<Name>{XmlEscape(e.Name)}</Name>", StringComparison.OrdinalIgnoreCase)) continue;

                    blocks.Append("  <Library>\r\n");
                    blocks.Append("    <Type>Content</Type>\r\n");
                    if (e.Name.Length > 0) blocks.Append("    <Name>").Append(XmlEscape(e.Name)).Append("</Name>\r\n");
                    if (e.Company.Length > 0) blocks.Append("    <Company>").Append(XmlEscape(e.Company)).Append("</Company>\r\n");
                    if (e.SnpId.Length > 0) blocks.Append("    <SNPID>").Append(XmlEscape(e.SnpId)).Append("</SNPID>\r\n");
                    blocks.Append("    <Relevance>\r\n      <Application nativeContent=\"true\">Kontakt</Application>\r\n    </Relevance>\r\n");
                    blocks.Append("    <RegKey>").Append(XmlEscape(e.RegKey)).Append("</RegKey>\r\n");
                    blocks.Append("  </Library>\r\n");
                }
                if (blocks.Length > 0)
                {
                    int close = hintText.LastIndexOf("</LibraryHints>", StringComparison.OrdinalIgnoreCase);
                    if (close > 0)
                    {
                        string merged = hintText[..close] + blocks + hintText[close..];
                        File.WriteAllText(hintsPath, merged, new UTF8Encoding(false));
                    }
                }
            }

            // ── 校验 ──
            var after = new HashSet<string>(ParseSettingsCfg(cfg).names, StringComparer.OrdinalIgnoreCase);
            var missing = result.Added.Where(k => !after.Contains(k)).ToList();
            if (missing.Count > 0)
            {
                Restore(cfg, result.BackupSettingsCfg);
                if (result.BackupLibraryHints.Length > 0) Restore(hintsPath, result.BackupLibraryHints);
                result.Ok = false;
                result.Message = $"写入校验失败，已自动还原：{string.Join(", ", missing)}";
                return result;
            }

            result.Ok = true;
            result.Message = $"已自动写入便携版库列表 {result.Added.Count} 个" +
                             (result.Skipped.Count > 0 ? $"，跳过已存在 {result.Skipped.Count} 个" : "") +
                             $"（Settings.cfg + LibraryHints.xml），重启 Kontakt 后生效";
            return result;
        }
        catch (Exception ex)
        {
            Restore(cfg, result.BackupSettingsCfg);
            result.Ok = false;
            result.Message = $"写入便携版库列表失败（已尝试还原）：{ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// **从便携版库列表里移除若干条目**（按 ContentDir 或库名匹配）。
    ///
    /// 为什么需要（用户实测发现）：便携版 Kontakt 的库列表存在自身目录的
    /// `UserData\Settings.cfg` 里，**删除音色库时若只清系统注册表，
    /// 便携版的 Settings.cfg 仍留着条目** → Kontakt 里会继续显示这些库
    /// 并报 "Content not found"。本方法负责把对应小节从该文件里删掉。
    ///
    /// 安全：写入前自动备份为 `Settings.cfg.klm-backup-<时间戳>`；
    /// 采用「先写临时文件再原子替换」，失败不影响原文件。
    /// </summary>
    /// <param name="kontaktExePath">Kontakt 主程序路径（用于定位便携版根目录）。</param>
    /// <param name="contentDirs">要移除的库路径（匹配 ContentDir，忽略大小写与尾斜杠）。</param>
    /// <param name="names">要移除的库名（匹配小节名，可选）。</param>
    public static (bool ok, string message, int removed) RemoveLibraries(
        string kontaktExePath, IEnumerable<string> contentDirs, IEnumerable<string>? names = null)
    {
        var root = FindPortableRoot(kontaktExePath);
        if (root == null) return (false, "未找到便携版根目录", 0);
        string cfg = Path.Combine(root, "UserData", "Settings.cfg");
        if (!File.Exists(cfg)) return (false, $"未找到 {cfg}", 0);

        var wantDirs = new HashSet<string>(
            (contentDirs ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => LibraryRegistrar.NormalizePath(d)),
            StringComparer.OrdinalIgnoreCase);
        var wantNames = new HashSet<string>(
            (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);
        if (wantDirs.Count == 0 && wantNames.Count == 0) return (false, "没有要移除的目标", 0);

        List<string> lines;
        try { lines = File.ReadAllLines(cfg, Encoding.UTF8).ToList(); }
        catch (Exception ex) { return (false, "读取失败：" + ex.Message, 0); }

        // Settings.cfg 是 INI 风格：以 [库名] 开头的小节，节内是 Key=value 行。
        // 做法：按小节切分，逐节判断是否命中，命中的整节丢弃。
        var kept = new List<string>();
        var cur = new List<string>();
        string curName = "";
        bool curHit = false;
        int removed = 0;

        bool SectionHit(string name, List<string> body)
        {
            if (name.Length > 0 && wantNames.Contains(name)) return true;
            foreach (var ln in body)
            {
                int eq = ln.IndexOf('=');
                if (eq <= 0) continue;
                string k = ln[..eq].Trim();
                if (!k.Equals("ContentDir", StringComparison.OrdinalIgnoreCase)) continue;
                string v = ln[(eq + 1)..].Trim();
                // 值可能带 sz: 前缀
                if (v.StartsWith("sz:", StringComparison.OrdinalIgnoreCase)) v = v[3..];
                v = v.Trim().Trim('"');
                if (wantDirs.Contains(LibraryRegistrar.NormalizePath(v))) return true;
            }
            return false;
        }

        void Flush()
        {
            if (curName.Length == 0 && cur.Count == 0) return;
            if (curHit) { removed++; return; }
            kept.AddRange(cur);
        }

        foreach (var ln in lines)
        {
            string t = ln.Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                Flush();
                cur = new List<string> { ln };
                curName = t[1..^1].Trim();
                curHit = false;
                // 先按名字判断（节内 ContentDir 稍后统一判）
                curHit = wantNames.Contains(curName);
                continue;
            }
            cur.Add(ln);
        }
        // 最后一节：需要连同前面收集的 body 一起判 ContentDir
        if (cur.Count > 0)
        {
            if (!curHit) curHit = SectionHit(curName, cur);
            Flush();
        }

        if (removed == 0) return (true, "便携版库列表里没有匹配的条目（无需清理）", 0);

        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(cfg, cfg + ".klm-backup-" + stamp, true);
            string tmp = cfg + ".klm-tmp";
            File.WriteAllLines(tmp, kept, new UTF8Encoding(false));
            File.Move(tmp, cfg, true);
            CleanupBackups(root, 5);   // 顺手清理累积的旧备份
        }
        catch (Exception ex) { return (false, "写入失败（已备份原文件）：" + ex.Message, 0); }

        return (true, $"已从便携版库列表移除 {removed} 个条目（原文件已备份）", removed);
    }

    /// <summary>
    /// 定位 Kontakt 的 **Quick-Load 根目录**（可能多个，按优先级返回）。
    ///
    /// **为什么需要两种路径**（用户实测报告）：
    ///   · **便携版**：Quick-Load 在 `<便携根>\UserData\Kontakt 8\QuickLoad`
    ///   · **正式版**：Quick-Load 在 `%LOCALAPPDATA%\Native Instruments\Kontakt 8\QuickLoad`
    ///     例如 `C:\Users\<用户>\AppData\Local\Native Instruments\Kontakt 8\QuickLoad`
    ///
    /// 旧实现只看便携根，所以用户从便携版换成正式版后，加入 Quick-Load 会报
    /// 「找不到 Kontakt 的 QuickLoad 目录（需要先设置 Kontakt 程序路径，且为便携版）」。
    ///
    /// 返回的每一项都已确保**父目录存在**（正式版路径在首次使用时可能还没被 Kontakt 创建，
    /// 但父目录 `AppData\Local\Native Instruments\Kontakt 8` 通常已存在，可直接建 QuickLoad）。
    /// </summary>
    public static List<string> QuickLoadRoots(string kontaktExePath)
    {
        var list = new List<string>();

        // ① 便携版：<便携根>\UserData\Kontakt 8\QuickLoad
        try
        {
            string? root = FindPortableRoot(kontaktExePath);
            if (!string.IsNullOrEmpty(root))
            {
                string p = Path.Combine(root, "UserData", "Kontakt 8", "QuickLoad");
                if (Directory.Exists(p)) list.Add(p);
            }
        }
        catch { }

        // ② 正式版：%LOCALAPPDATA%\Native Instruments\Kontakt 8\QuickLoad
        //    同时兼容 Kontakt 7（老版本用户数据目录）
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                foreach (var ver in new[] { "Kontakt 8", "Kontakt 7", "Kontakt" })
                {
                    string baseDir = Path.Combine(local, "Native Instruments", ver);
                    string p = Path.Combine(baseDir, "QuickLoad");
                    if (Directory.Exists(p)) { list.Add(p); continue; }
                    // 父目录存在就允许「将来创建」—— 加入 Quick-Load 时会自动建
                    if (Directory.Exists(baseDir)) list.Add(p);
                }
            }
        }
        catch { }

        // ③ 兜底：文档目录下的 Native Instruments\Kontakt 8（部分安装方式用这里）
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs))
            {
                string baseDir = Path.Combine(docs, "Native Instruments", "Kontakt 8");
                string p = Path.Combine(baseDir, "QuickLoad");
                if (Directory.Exists(p)) list.Add(p);
            }
        }
        catch { }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 取**首个可写**的 Quick-Load 根；没有现成的就按优先级挑一个父目录存在的来创建。
    /// 返回 null 表示连父目录都找不到（例如 Kontakt 从未运行过）。
    /// </summary>
    public static string? ResolveQuickLoadRoot(string kontaktExePath, bool createIfMissing = true)
    {
        var roots = QuickLoadRoots(kontaktExePath);
        foreach (var r in roots) if (Directory.Exists(r)) return r;

        if (!createIfMissing) return roots.FirstOrDefault();

        // 没有现成的 → 在候选里找一个父目录存在的创建
        foreach (var r in roots)
        {
            try
            {
                string? parent = Path.GetDirectoryName(r);
                if (parent != null && Directory.Exists(parent))
                {
                    Directory.CreateDirectory(r);
                    return r;
                }
            }
            catch { }
        }
        return null;
    }
    /// <summary>
    /// **列出 Quick-Load 里已有的快捷方式指向的目标路径**。
    ///
    /// 用途：无 .nicnt 的非标准库没法真正「入库」，但加入 Quick-Load 后
    /// 在 Kontakt 里已经一点即达 —— 所以界面应把它当成「已处理」，不再列在待办里。
    /// </summary>
    public static HashSet<string> ListQuickLoadTargets(string kontaktExePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // **遍历所有 Quick-Load 根**（便携版 + 正式版），不再只看便携根 ——
        // 旧实现只看便携根，用户换正式版后会报「找不到 QuickLoad 目录」。
        var roots = QuickLoadRoots(kontaktExePath);
        if (roots.Count == 0) return set;

        foreach (var cat in new[] { "Instr", "Bank", "Multi" })
        foreach (var qlRoot in roots)
        {
            string dir = Path.Combine(qlRoot, cat);
            if (!Directory.Exists(dir)) continue;

            // ① .lnk 快捷方式：用 ShellLink.ReadTarget（IShellLinkW.GetPath，Unicode 原生）读目标。
            //    **不要自己解析 .lnk 字节** —— 它的 LinkInfo 里路径是 ANSI 存的，
            //    按 UTF-16 解码会得到「两字节打包」的乱码，匹配不到（实测踩到）。
            foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
            {
                try
                {
                    string t = ShellLink.ReadTarget(lnk);
                    if (t.Length > 0)
                    {
                        t = t.Trim().TrimEnd('\\', '/');
                        if (Directory.Exists(t)) set.Add(t);
                    }
                }
                catch { }
            }

            // ② 真实子目录（用户也可能直接把文件夹放进来）
            try
            {
                foreach (var d in Directory.GetDirectories(dir)) set.Add(d.TrimEnd('\\', '/'));
            }
            catch { }
        }
        return set;
    }
    /// <summary>
    /// **清理累积的 Settings.cfg 备份**，只保留最近的若干份。
    ///
    /// 背景（实测发现）：本项目历次写 `Settings.cfg`（加库 / 移库 / 删库 / 修路径 / 快照回滚）
    /// 都会先备份成 `Settings.cfg.klm-backup-<时间戳>`，但**从来没有清理** ——
    /// 用户机器上已累积 20+ 份、每份 130 KB 以上，既占空间也让 UserData 目录很乱。
    ///
    /// 策略：按文件名里的时间戳倒序，保留最近 <paramref name="keep"/> 份，其余删除。
    /// 删除失败静默忽略（备份文件不该阻塞主流程）。
    /// </summary>
    public static int CleanupBackups(string portableRoot, int keep = 5)
    {
        if (string.IsNullOrWhiteSpace(portableRoot) || keep < 0) return 0;
        string ud = Path.Combine(portableRoot, "UserData");
        if (!Directory.Exists(ud)) return 0;

        int removed = 0;
        try
        {
            var files = Directory.GetFiles(ud, "Settings.cfg.klm-backup-*")
                                 .Select(f => new FileInfo(f))
                                 .OrderByDescending(f => f.Name, StringComparer.Ordinal)   // 文件名带时间戳，字典序即时间序
                                 .ToList();
            foreach (var f in files.Skip(keep))
            {
                try { f.Delete(); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>按 Kontakt 主程序路径清理备份（便利重载）。</summary>
    public static int CleanupBackupsByExe(string kontaktExePath, int keep = 5)
    {
        var root = FindPortableRoot(kontaktExePath);
        return root == null ? 0 : CleanupBackups(root, keep);
    }
    /// <summary>把备份还原到便携版配置（供用户手动撤销时使用）。</summary>
    public static (bool ok, string message) RestoreFromBackup(string backupPath, string kontaktExePath)
    {
        var info = Inspect(kontaktExePath);
        if (info == null) return (false, "未检测到便携版 Kontakt");
        if (!File.Exists(backupPath)) return (false, $"备份文件不存在：{backupPath}");
        if (KontaktInfo.IsRunning()) return (false, "请先完全关闭 Kontakt 再还原");

        try
        {
            string target = backupPath.Contains("LibraryHints", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(info.Root, "UserData", "Service Center", "LibraryHints.xml")
                : info.SettingsCfg;
            File.Copy(target, target + ".before-restore", overwrite: true);
            File.Copy(backupPath, target, overwrite: true);
            return (true, $"已还原：{target}（还原前状态另存为 .before-restore）");
        }
        catch (Exception ex)
        {
            return (false, $"还原失败：{ex.Message}");
        }
    }

    private static void Restore(string path, string backup)
    {
        try { if (backup.Length > 0 && File.Exists(backup)) File.Copy(backup, path, overwrite: true); }
        catch { }
    }

    private static string Backup(string path, string stamp)
    {
        try
        {
            string b = $"{path}.klm-backup-{stamp}";
            File.Copy(path, b, overwrite: true);
            return b;
        }
        catch { return ""; }
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");
}
