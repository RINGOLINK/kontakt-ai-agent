using Microsoft.Win32;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>已注册到 Kontakt 的产品（一个产品 = 一个注册表键）。</summary>
public sealed class RegisteredProduct
{
    public string RegKey { get; set; } = "";
    public string ContentDir { get; set; } = "";
    public string Hu { get; set; } = "";
    public string Jdx { get; set; } = "";
    public int Visibility { get; set; }
    public string ContentVersion { get; set; } = "";
    public bool InHklm64 { get; set; }
    public bool InHklm32 { get; set; }
    public bool InHkcu { get; set; }
}

/// <summary>
/// Kontakt 音色库「入库」管理器。
///
/// 实测的注册表布局（Kontakt 库浏览器据此显示库）：
///   HKLM(64) SOFTWARE\Native Instruments\&lt;RegKey&gt;
///        ContentDir(REG_SZ, 无尾斜杠) / Visibility(REG_DWORD=3) / HU / JDX / ContentVersion
///   HKLM(32) 同上（RegistryView.Registry32 自动映射 WOW6432Node）
///        ContentDir(REG_SZ, 带尾斜杠) / Visibility / HU / JDX
///   HKCU     SOFTWARE\Native Instruments\&lt;RegKey&gt;
///        ContentDir(带尾斜杠) / UserRemoved(REG_DWORD=0) / UserListIndex(REG_DWORD=0)
///
/// 边界声明：本类只把 .nicnt 里**已有**的元数据写入注册表，等价于 Kontakt 自带的
/// 「Add Library」按钮流程（批量/修复路径场景的自动化）。不修改 .nicnt、不伪造 HU/JDX、
/// 不绕过 Native Access / RAS 的任何授权校验；需要授权的库仍由 Kontakt 自身校验。
/// </summary>
public static class LibraryRegistrar
{
    private const string SubKeyPath = @"SOFTWARE\Native Instruments";
    private const int DefaultVisibility = 3;

    /// <summary>
    /// Service Center 记录目录。NI 在这里保存每个库的 ProductHints XML 副本
    /// （文件名 = RegKey.xml，内容与 .nicnt 中的 XML 块一致）。
    /// 缺少该记录时：Kontakt 能列出库，但加载乐器会报
    /// "This instrument belongs to a library that is not installed currently!"。
    /// </summary>
    public static string ServiceCenterDir()
    {
        var candidates = new List<string>();
        string common = Environment.GetEnvironmentVariable("CommonProgramFiles")
                        ?? @"C:\Program Files\Common Files";
        candidates.Add(Path.Combine(common, "Native Instruments", "Service Center"));

        string? commonX86 = Environment.GetEnvironmentVariable("CommonProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(commonX86))
            candidates.Add(Path.Combine(commonX86!, "Native Instruments", "Service Center"));

        candidates.Add(@"C:\Program Files (x86)\Common Files\Native Instruments\Service Center");

        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return candidates[0];
    }

    public static string ServiceCenterRecordPath(string regKey)
        => Path.Combine(ServiceCenterDir(), $"{regKey}.xml");

    public static bool HasServiceCenterRecord(string regKey)
    {
        if (string.IsNullOrWhiteSpace(regKey)) return false;
        try { return File.Exists(ServiceCenterRecordPath(regKey)); }
        catch { return false; }
    }

    public static string NormalizePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        return p.Trim().Trim('"').TrimEnd('\\', '/');
    }

    /// <summary>读取本机已注册的全部产品，键为 RegKey（不区分大小写）。</summary>
    public static Dictionary<string, RegisteredProduct> ReadRegistered()
    {
        var map = new Dictionary<string, RegisteredProduct>(StringComparer.OrdinalIgnoreCase);

        void Merge(RegistryHive hive, RegistryView view, Action<RegisteredProduct> mark)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(SubKeyPath);
                if (root == null) return;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(name);
                    if (sub == null) continue;

                    string contentDir = sub.GetValue("ContentDir") as string ?? "";
                    // 只认真正指向库的条目（过滤 Kontakt 自身的应用键等）
                    if (string.IsNullOrWhiteSpace(contentDir)) continue;

                    if (!map.TryGetValue(name, out var prod))
                    {
                        prod = new RegisteredProduct { RegKey = name };
                        map[name] = prod;
                    }
                    mark(prod);

                    if (string.IsNullOrEmpty(prod.ContentDir)) prod.ContentDir = NormalizePath(contentDir);
                    prod.Hu = prod.Hu.Length > 0 ? prod.Hu : (sub.GetValue("HU") as string ?? "");
                    prod.Jdx = prod.Jdx.Length > 0 ? prod.Jdx : (sub.GetValue("JDX") as string ?? "");
                    prod.ContentVersion = prod.ContentVersion.Length > 0 ? prod.ContentVersion : (sub.GetValue("ContentVersion") as string ?? "");
                    if (prod.Visibility == 0 && sub.GetValue("Visibility") is int v) prod.Visibility = v;
                }
            }
            catch { }
        }

        Merge(RegistryHive.LocalMachine, RegistryView.Registry64, p => p.InHklm64 = true);
        Merge(RegistryHive.LocalMachine, RegistryView.Registry32, p => p.InHklm32 = true);
        Merge(RegistryHive.CurrentUser, RegistryView.Default, p => p.InHkcu = true);
        return map;
    }

    /// <summary>按 .nicnt 元数据注册一个产品（等价于 Kontakt 的 Add Library）。</summary>
    public static (bool ok, string message) Register(NicntInfo nicnt)
    {
        string contentDir = NormalizePath(Path.GetDirectoryName(nicnt.FilePath) ?? "");
        if (string.IsNullOrWhiteSpace(contentDir))
            return (false, "无法确定库目录");

        string regKey = string.IsNullOrWhiteSpace(nicnt.RegKey) ? nicnt.Name : nicnt.RegKey;
        if (string.IsNullOrWhiteSpace(regKey))
            return (false, "缺少 RegKey（.nicnt 未提供产品名）");

        if (!KontaktInfo.IsAdmin())
            return (false, "需要管理员权限才能写入 HKLM，请以管理员身份重启本工具");

        try
        {
            // HKLM 64 位视图：ContentDir 不带尾斜杠
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.CreateSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                if (key == null) return (false, "无法创建 HKLM(64) 注册表项");
                key.SetValue("ContentDir", contentDir, RegistryValueKind.String);
                key.SetValue("Visibility", DefaultVisibility, RegistryValueKind.DWord);
                if (nicnt.HasAuthPair)
                {
                    key.SetValue("HU", nicnt.Hu, RegistryValueKind.String);
                    key.SetValue("JDX", nicnt.Jdx, RegistryValueKind.String);
                }
                if (!string.IsNullOrEmpty(nicnt.ContentVersion))
                    key.SetValue("ContentVersion", nicnt.ContentVersion, RegistryValueKind.String);
            }

            // HKLM 32 位视图（自动映射 WOW6432Node）：ContentDir 带尾斜杠
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = baseKey.CreateSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                if (key != null)
                {
                    key.SetValue("ContentDir", contentDir + "\\", RegistryValueKind.String);
                    key.SetValue("Visibility", DefaultVisibility, RegistryValueKind.DWord);
                    if (nicnt.HasAuthPair)
                    {
                        key.SetValue("HU", nicnt.Hu, RegistryValueKind.String);
                        key.SetValue("JDX", nicnt.Jdx, RegistryValueKind.String);
                    }
                }
            }

            // HKCU：用户级列表状态
            using (var key = Registry.CurrentUser.CreateSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                if (key != null)
                {
                    key.SetValue("ContentDir", contentDir + "\\", RegistryValueKind.String);
                    key.SetValue("UserRemoved", 0, RegistryValueKind.DWord);
                    key.SetValue("UserListIndex", 0, RegistryValueKind.DWord);
                }
            }

            // ④ Service Center 记录：写入 .nicnt 中 ProductHints XML 的原文副本。
            //    这一步缺失会导致「库能列出但乐器无法加载」。
            string scNote = "";
            string scDir = ServiceCenterDir();
            if (!string.IsNullOrWhiteSpace(nicnt.RawXml) && Directory.Exists(scDir))
            {
                try
                {
                    Directory.CreateDirectory(scDir);
                    File.WriteAllText(ServiceCenterRecordPath(regKey), nicnt.RawXml, new UTF8Encoding(false));
                    scNote = "，已写入 Service Center 记录";
                }
                catch (Exception ex)
                {
                    scNote = $"，但 Service Center 记录写入失败：{ex.Message}";
                }
            }
            else if (!Directory.Exists(scDir))
            {
                scNote = $"，但未找到 Service Center 目录（{scDir}），乐器可能仍无法加载";
            }


            
// ⑤ **Native Access 记录**（`installed_products\<产品名>.json`）——
            
//    正式版 Kontakt 8.13.1 判断「库可用」时会看这里，光写注册表不够：
            
//    记录缺失或 ContentDir 为空就会报「Library not found. Click 'Manage Libraries'…」。
            
//    （便携版看自己的 Settings.cfg，与这套是**两套独立注册**。）
            
string naNote = "";
            
if (NativeAccessStore.Available)
            
{
            
    var (naOk, naMsg) = NativeAccessStore.Register(nicnt.Name, contentDir,
            
        string.IsNullOrEmpty(nicnt.ContentVersion) ? "1.0.0" : nicnt.ContentVersion);
            
    naNote = naOk ? "，已写入 Native Access 记录" : $"，但 Native Access 记录写入失败：{naMsg}";
            
}
            
else
            
{
            
    naNote = "，未找到 Native Access 记录目录（正式版 Kontakt 可能仍提示找不到库）";
            
}

            // ⑥ **Kontakt Content 键**（`HKLM\SOFTWARE\WOW6432Node\Native Instruments\Content\k2lib0<SNPID>`）——
            //    **这是正式版 Kontakt 判断「库可用与否」的真正关键**（2026-09-20 用 ProcMon 全量抓取第三方工具
            //    `Kontakt 音色库添加工具 v3.0.exe` 的行为后定位）：工具入库时会写
            //    `Content\k2lib0<SNPID> = <库名>`，并删掉指向本库的旧 `k2lib<旧编号>` 条目（换号）。
            //    **只写 32 位视图**（`WOW6432Node`）—— 64 位视图与库可用性无关。
            string k2Note = "";
            try
            {
                string snpId = (nicnt.SnpId ?? "").Trim();
                if (snpId.Length == 0)
                {
                    k2Note = "，但 .nicnt 缺 SNPID，未能写入 Kontakt Content 键（正式版可能仍提示找不到库）";
                }
                else
                {
                    string k2Key = "k2lib0" + snpId;
                    using var lm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                    using var contentKey = lm32.CreateSubKey(
                        @"SOFTWARE\Native Instruments\Content", writable: true);
                    if (contentKey == null)
                    {
                        k2Note = "，但无法创建 Kontakt Content 键";
                    }
                    else
                    {
                        // 先删掉指向本库的旧条目（工具入库时会「换号」）
                        foreach (var vn in contentKey.GetValueNames())
                        {
                            if (string.Equals(contentKey.GetValue(vn) as string, nicnt.Name,
                                    StringComparison.OrdinalIgnoreCase))
                                contentKey.DeleteValue(vn, throwOnMissingValue: false);
                        }
                        contentKey.SetValue(k2Key, nicnt.Name, RegistryValueKind.String);
                        k2Note = $"，已写入 Kontakt Content 键（{k2Key}）";
                    }
                }
            }
            catch (Exception ex) { k2Note = "，但 Kontakt Content 键写入失败：" + ex.Message; }
            return (true, $"{nicnt.Name} → 已注册（键名 {regKey}）{scNote}{naNote}{k2Note}");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "写入注册表被拒绝：需要管理员权限");
        }
        catch (Exception ex)
        {
            return (false, $"注册失败：{ex.Message}");
        }
    }

    /// <summary>取消注册（移除三个视图下的同名键 + Service Center 记录）。</summary>
    public static (bool ok, string message) Unregister(string regKey)
    {
        if (string.IsNullOrWhiteSpace(regKey)) return (false, "缺少注册表键名");
        if (!KontaktInfo.IsAdmin()) return (false, "需要管理员权限才能修改 HKLM");

        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                baseKey.DeleteSubKeyTree(Path.Combine(SubKeyPath, regKey), throwOnMissingSubKey: false);
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                baseKey.DeleteSubKeyTree(Path.Combine(SubKeyPath, regKey), throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(Path.Combine(SubKeyPath, regKey), throwOnMissingSubKey: false);

            string scFile = ServiceCenterRecordPath(regKey);
            if (File.Exists(scFile)) File.Delete(scFile);

            return (true, $"已取消注册：{regKey}（含 Service Center 记录）");
        }
        catch (Exception ex)
        {
            return (false, $"取消注册失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析一个音色库对应的注册表产品键。三级策略：
    ///   ① 路径匹配：注册表 ContentDir 指向该库目录（或子目录）——最可靠；
    ///   ② 名称匹配：RegKey 与库名强相似（去掉厂商前缀后相等，或库名以 RegKey 结尾）——
    ///      用于「注册表路径已失效」的场景（如库被移动过、硬盘未接）；
    ///   ③ .nicnt 里的 RegKey。
    /// </summary>
    public static List<string> ResolveProductKeys(
        string libraryName, string libraryPath, Dictionary<string, RegisteredProduct> registered)
    {
        var keys = new List<string>();
        string normPath = NormalizePath(libraryPath);

        // ① 路径匹配
        foreach (var kv in registered)
        {
            string cd = kv.Value.ContentDir;
            if (cd.Equals(normPath, StringComparison.OrdinalIgnoreCase) ||
                cd.StartsWith(normPath + "\\", StringComparison.OrdinalIgnoreCase))
                if (!keys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) keys.Add(kv.Key);
        }

        // ② 名称匹配（兜底）
        if (keys.Count == 0)
        {
            string normName = NormalizeName(libraryName);
            foreach (var kv in registered)
            {
                string nk = NormalizeName(kv.Key);
                // 下限从 5 提到 8：太短的归一化 token（如 "brass"、"drums"）极易误匹配无关库
                if (nk.Length < 8) continue;
                bool strong = normName == nk
                              || normName.EndsWith(nk, StringComparison.Ordinal)
                              || nk.EndsWith(normName, StringComparison.Ordinal);
                if (strong && !keys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) keys.Add(kv.Key);
            }
        }

        // ③ .nicnt
        try
        {
            foreach (var n in NicntReader.FindInLibrary(libraryPath, 2))
            {
                string k = string.IsNullOrWhiteSpace(n.RegKey) ? n.Name : n.RegKey;
                if (k.Length > 0 && !keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);
            }
        }
        catch { }

        return keys;
    }

    /// <summary>库名归一化：去掉常见厂商前缀与符号，便于与 RegKey 比对。</summary>
    private static string NormalizeName(string s)
    {
        string n = (s ?? "").ToLowerInvariant();
        foreach (var prefix in new[]
        {
            "native instruments - ", "native instruments ", "best service ", "projectsam ",
            "heavyocity ", "musical sampling - ", "musical sampling ", "session guitarist - ",
            "cinematic studio ", "orchestral tools ", "spitfire audio ",
        })
        {
            if (!n.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rest = n[prefix.Length..].Trim();
            // **关键防护**：若剥离厂商前缀后只剩一个很短的通用词（如 "brass"、"strings"、
            // "drums"），这个 token 会与大量**无关**库名「后缀匹配」，导致张冠李戴。
            // 实测：注册键 "Cinematic Studio Brass" 被剥成 "brass" 后，
            // 把 "Kirk_Hunter_Studios_Kinetic_Brass" 也匹配上了（两者是完全不同的库）。
            // 所以剩余长度不足时**不剥离**，保留完整名字。
            if (rest.Length >= 8) n = rest;
            break;
        }
        return new string(n.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// 修改已注册产品的 ContentDir（用于音色库移动后修复入库路径）。
    /// 三视图同时更新：HKLM64 不带尾斜杠；HKLM32/HKCU 带尾斜杠。
    /// </summary>
    public static (bool ok, string message) UpdateContentDir(string regKey, string newContentDir)
    {
        if (string.IsNullOrWhiteSpace(regKey)) return (false, "缺少注册表键名");
        if (!KontaktInfo.IsAdmin()) return (false, "需要管理员权限才能修改 HKLM");

        string dir = NormalizePath(newContentDir);
        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                key?.SetValue("ContentDir", dir, RegistryValueKind.String);
            }
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = baseKey.OpenSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                key?.SetValue("ContentDir", dir + "\\", RegistryValueKind.String);
            }
            using (var key = Registry.CurrentUser.OpenSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                key?.SetValue("ContentDir", dir + "\\", RegistryValueKind.String);
            }
            return (true, $"{regKey} → {dir}");
        }
        catch (Exception ex)
        {
            return (false, $"更新 ContentDir 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 检查一批产品键在 Service Center 的记录完整度。
    /// 返回 (已存在记录数, 缺失的键列表)。
    /// </summary>
    public static (int present, List<string> missing) CheckServiceCenter(IEnumerable<string> regKeys)
    {
        int present = 0;
        var missing = new List<string>();
        foreach (var k in regKeys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            if (HasServiceCenterRecord(k)) present++;
            else missing.Add(k);
        }
        return (present, missing);
    }

    /// <summary>
    /// **不依赖 `.nicnt` 的注册** —— 用于没有 `.nicnt` 的库（第三方/非标准库）。
    ///
    /// **为什么需要**（用户实测报告）：很多库目录里没有 `.nicnt`（实测 `Audio Imperia
    /// Dark Dimensions Vol. 1` 等 `has_nicnt=0`），而原 <see cref="Register(NicntInfo)"/>
    /// 依赖 `.nicnt` 里的 RegKey/HU/JDX，导致这类库**永远无法入库**。
    ///
    /// 做法：用「库名 + 目录」直接写三处注册表 + Native Access 记录。
    /// **注意**：Kontakt 8 是 64 位程序，**只读 HKLM 64 位视图**，所以 64 位那一份必须写成功。
    /// </summary>
    public static (bool ok, string message) RegisterWithoutNicnt(string libraryName, string libraryPath)
    {
        // **防护：键名不能含 `;`** —— 早前那版代码误把整个 `ProductKey`（多键拼接）当键名，         // 在注册表里留下了 89 个形如 `A;B` 的非法键，导致 SC 记录检查永远失败（实测踩到）。         if (libraryName.Contains(';'))             return (false, $"键名非法（含 ; ）：{libraryName}"); 
        if (string.IsNullOrWhiteSpace(libraryName)) return (false, "库名为空");
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return (false, $"库目录不存在：{libraryPath}");

            // **ContentDir 必须指向「该库自己的 .nicnt 所在目录」。**
            //
            // 两个必须同时满足的要点（都是实测踩过的坑）：
            //   ① 不能写库根目录 —— 官方库常把 .nicnt 放在子目录里
            //      （Abbey Road 50s → \Samples\、Studio Drummer → \Studio Drummer Library\）；
            //   ② 不能只取「递归找到的第一个 .nicnt」—— 一个目录下可能装着多个子库
            //      （如 `Chris Hein Winds Complete 2` 下有 Vol 1~4），
            //      只取第一个会让 Vol 2/3/4 的 ContentDir 全指向 Vol 1 的目录 → 报 Library not found。
            // 所以这里**优先按 RegKey 精确匹配**，匹配不到才退回第一个。
            string contentDir = NormalizePath(libraryPath);
            try
            {
                var found = NicntReader.FindInLibrary(libraryPath, maxDepth: 4);
                if (found.Count > 0)
                {
                    var exact = found.FirstOrDefault(x =>
                        string.Equals(x.RegKey, libraryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name, libraryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(x.FilePath), libraryName, StringComparison.OrdinalIgnoreCase));
                    var pick = exact.FilePath != null ? exact : found[0];
                    contentDir = NormalizePath(Path.GetDirectoryName(pick.FilePath) ?? libraryPath);
                }
            }
            catch { }
        string regKey = libraryName.Trim();
        if (!KontaktInfo.IsAdmin())
            return (false, "需要管理员权限才能写入 HKLM，请以管理员身份重启本工具");

        var notes = new List<string>();
        bool hklm64Ok = false;

        try
        {
            // ① HKLM 64 位视图 —— **Kontakt 8（64 位）唯一会读的那一份，必须成功**
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.CreateSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                if (key == null) return (false, "无法创建 HKLM(64) 注册表项");
                key.SetValue("ContentDir", contentDir, RegistryValueKind.String);
                key.SetValue("Visibility", DefaultVisibility, RegistryValueKind.DWord);
                hklm64Ok = true;
                notes.Add("已写 HKLM(64)");
            }

            // ② HKLM 32 位视图（兼容旧工具/便携版）
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = baseKey.CreateSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                if (key != null)
                {
                    key.SetValue("ContentDir", contentDir + "\\", RegistryValueKind.String);
                    key.SetValue("Visibility", DefaultVisibility, RegistryValueKind.DWord);
                    notes.Add("已写 HKLM(32)");
                }
            }

            // ③ HKCU：用户级列表状态（Kontakt 也会读，用于显示在库里）
            using (var key = Registry.CurrentUser.CreateSubKey(Path.Combine(SubKeyPath, regKey), writable: true))
            {
                if (key != null)
                {
                    key.SetValue("ContentDir", contentDir + "\\", RegistryValueKind.String);
                    key.SetValue("UserRemoved", 0, RegistryValueKind.DWord);
                    key.SetValue("UserListIndex", 0, RegistryValueKind.DWord);
                    notes.Add("已写 HKCU");
                }
            }

            // ④ Service Center XML —— **必须写**，否则界面显示「缺 Service Center 记录」，
            //    且 Kontakt 的库列表可能不认（实测：漏写会导致大量库报缺记录）。
            try
            {
                string scDir2 = ServiceCenterDir();
                Directory.CreateDirectory(scDir2);
                string scPath2 = Path.Combine(scDir2, regKey + ".xml");
                if (!File.Exists(scPath2))
                {
                    string upid2 = Guid.NewGuid().ToString("D");
                    string snp2 = NicntWriter.AllocateSnpId(NicntWriter.LoadUsedSnpIds());
                    File.WriteAllText(scPath2,
                        NicntWriter.BuildServiceCenterXml(regKey, regKey, snp2, upid2),
                        new UTF8Encoding(false));
                    notes.Add("已写 Service Center 记录");
                }
                else notes.Add("Service Center 记录已存在");
            }
            catch (Exception ex2) { notes.Add("Service Center 记录写入失败：" + ex2.Message); }
            // ④ Native Access 记录（正式版 Kontakt 判断「库可用」时会看）
            if (NativeAccessStore.Available)
            {
                var (naOk, _) = NativeAccessStore.Register(regKey, contentDir);
                notes.Add(naOk ? "已写 Native Access 记录" : "Native Access 记录写入失败");
            }

            return (hklm64Ok, $"{regKey} → 已注册（无 .nicnt 模式）：{string.Join("，", notes)}");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "写入注册表被拒绝：需要管理员权限");
        }
        catch (Exception ex)
        {
            return (false, $"注册失败：{ex.Message}");
        }
    }

    /// <summary>
    /// **把注册项「提升」到 HKLM 64 位视图**。
    ///
    /// **为什么需要**（用户实测报告）：Kontakt 8 是 64 位程序，**只读 HKLM 64 位视图**；
    /// 但很多库的注册项只在 `HKLM\SOFTWARE\WOW6432Node\Native Instruments`（32 位视图）
    /// 或 `HKCU` 里（历史遗留）→ Kontakt 读不到 → 界面报
    /// 「Library not found. Click 'Manage Libraries' to set the content via Native Access.」。
    ///
    /// 做法：从 32 位视图 / HKCU 里取已有的 ContentDir（及 HU/JDX/ContentVersion/Visibility），
    /// 补写到 64 位视图。已有 64 位项时默认不覆盖（overwrite=false）。
    /// </summary>
    public static (bool ok, string message) PromoteToHklm64(string regKey, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(regKey)) return (false, "键名为空");
        if (!KontaktInfo.IsAdmin()) return (false, "需要管理员权限才能写入 HKLM，请以管理员身份重启本工具");

        // 收集已有信息（优先 32 位，其次 HKCU）
        string contentDir = "", hu = "", jdx = "", ver = "";
        int vis = DefaultVisibility;
        bool found = false;

        void Read(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(SubKeyPath);
                using var sub = root?.OpenSubKey(regKey);
                if (sub == null) return;
                string cd = sub.GetValue("ContentDir") as string ?? "";
                if (string.IsNullOrWhiteSpace(cd)) return;
                if (!found)
                {
                    contentDir = NormalizePath(cd);
                    hu = sub.GetValue("HU") as string ?? "";
                    jdx = sub.GetValue("JDX") as string ?? "";
                    ver = sub.GetValue("ContentVersion") as string ?? "";
                    if (sub.GetValue("Visibility") is int v) vis = v;
                    found = true;
                }
            }
            catch { }
        }

        Read(RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(RegistryHive.CurrentUser, RegistryView.Default);
        if (!found) return (false, "32 位视图与 HKCU 里都没有该键的有效 ContentDir，无法提升");

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(Path.Combine(SubKeyPath, regKey), writable: true);
            if (key == null) return (false, "无法创建 HKLM(64) 注册表项");

            string existing = key.GetValue("ContentDir") as string ?? "";
            if (!overwrite && !string.IsNullOrWhiteSpace(existing))
                return (true, $"{regKey}：64 位视图已有 ContentDir，跳过");

            key.SetValue("ContentDir", contentDir, RegistryValueKind.String);
            key.SetValue("Visibility", vis, RegistryValueKind.DWord);
            if (hu.Length > 0) key.SetValue("HU", hu, RegistryValueKind.String);
            if (jdx.Length > 0) key.SetValue("JDX", jdx, RegistryValueKind.String);
            if (ver.Length > 0) key.SetValue("ContentVersion", ver, RegistryValueKind.String);

            return (true, $"{regKey} → 已提升到 HKLM(64)：{contentDir}");
        }
        catch (UnauthorizedAccessException) { return (false, "写入注册表被拒绝：需要管理员权限"); }
        catch (Exception ex) { return (false, $"提升失败：{ex.Message}"); }
    }

    /// <summary>
    /// **扫描「视图错位」的库**：注册项存在，但**不在 HKLM 64 位视图**里
    /// （Kontakt 8 读不到，界面会报 Library not found）。
    /// </summary>
    public static List<(string RegKey, string ContentDir, bool In32, bool InHkcu)> FindNotInHklm64()
    {
        var res = new List<(string, string, bool, bool)>();
        var all = ReadRegistered();
        foreach (var kv in all)
        {
            if (kv.Value.InHklm64) continue;                       // 已在 64 位，OK
            if (!kv.Value.InHklm32 && !kv.Value.InHkcu) continue;  // 三处都没有，不是这个问题
            res.Add((kv.Key, kv.Value.ContentDir, kv.Value.InHklm32, kv.Value.InHkcu));
        }
        return res;
    }

    /// <summary>判断某库的入库状态。</summary>
    public static (string status, string regKey, string regContentDir) EvaluateStatus(
        string libraryPath, Dictionary<string, RegisteredProduct> registered)
    {
        string norm = NormalizePath(libraryPath);

        // 1) 注册表里是否有指向本库（或本库子目录）的条目
        foreach (var kv in registered)
        {
            string cd = kv.Value.ContentDir;
            if (cd.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                cd.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return ("registered", kv.Key, cd);
            }
        }

        // 2) 本库内有 .nicnt 但注册表没指向它 → 未入库（或路径已失效）
        var nicnts = NicntReader.FindInLibrary(libraryPath, maxDepth: 2);
        if (nicnts.Count == 0)
            return ("non-standard", "", "");

        foreach (var n in nicnts)
        {
            string key = string.IsNullOrWhiteSpace(n.RegKey) ? n.Name : n.RegKey;
            if (registered.TryGetValue(key, out var prod))
                return ("path-mismatch", key, prod.ContentDir);   // 已注册但路径不是当前库
        }
        return ("missing", "", "");
    }

    /// <summary>
    /// 为库记录补齐「入库状态」与「版本兼容状态」。
    /// 入库状态基于扫描时采集的 product_key（避免每次刷新都遍历文件系统）；
    /// 兼容状态基于库内 NKI 的最高引擎版本与当前选定的 Kontakt 版本比较。
    /// </summary>
    public static void Enrich(IEnumerable<LibraryRecord> libraries, Dictionary<string, RegisteredProduct> registered,
                              string kontaktVersion, HashSet<string>? portableNames = null)
    {
        foreach (var lib in libraries)
        {
            // ── 入库状态 ──
            // 判定顺序很重要：先看「注册表是否已指向本库」——这是"已入库"的事实依据；
            // 无 .nicnt 的库同样可能已被注册（用户手工入库/历史遗留），不能仅凭缺 .nicnt 就判为未入库。
            // 另外必须检查 Service Center 记录：注册表齐全但缺该记录时，Kontakt 会列出库却加载不了乐器。
            string norm = NormalizePath(lib.Path);
            var hereKeys = new List<string>();
            string regDirHere = "";
            foreach (var kv in registered)
            {
                string cd = kv.Value.ContentDir;
                if (cd.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                    cd.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    hereKeys.Add(kv.Key);
                    if (regDirHere.Length == 0) regDirHere = cd;
                }
            }

            // **ProductKey 有两种来源，第二段含义不同**：
            //   · 来自注册表 → `<键1>;<键2>`，每个都是真键，都要查 SC 记录；
            //   · 来自 .nicnt 扫描 → `<RegKey>;<SNPID>`，**第二段是 SNPID、不是键**，
            //     拿它去查 SC 记录必然缺失（实测：一直报「缺 BASiS.xml / Kawa.xml / RA.xml」）。
            // 这里按「形如 SNPID（1~3 位字母数字）就剔除」来区分。
            var keys = (lib.ProductKey ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !System.Text.RegularExpressions.Regex.IsMatch(x, @"^[A-Za-z0-9]{1,3}$"))
                .ToList();
            if (keys.Count == 0 && !string.IsNullOrWhiteSpace(lib.ProductKey))
                keys.Add(lib.ProductKey.Split(';')[0].Trim());
            List<string> keysToCheck;
            if (hereKeys.Count > 0)
            {
                lib.RegStatus = "registered";
                lib.RegContentDir = regDirHere;
                if (lib.ProductKey.Length == 0) lib.ProductKey = string.Join(";", hereKeys);
                keysToCheck = hereKeys;
            }
            else if (keys.Count == 0)
            {
                // 无 .nicnt 且注册表未指向本库 → 无法自动注册（缺元数据）
                lib.RegStatus = "non-standard";
                keysToCheck = new List<string>();
            }
            else
            {
                int matched = 0, pathMismatch = 0;
                string firstKey = "", firstDir = "";
                var matchedKeys = new List<string>();
                foreach (var key in keys)
                {
                    if (!registered.TryGetValue(key, out var prod)) continue;
                    bool pointsHere = prod.ContentDir.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                                      prod.ContentDir.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase);
                    if (pointsHere) { matched++; matchedKeys.Add(key); }
                    else { pathMismatch++; if (firstKey.Length == 0) { firstKey = key; firstDir = prod.ContentDir; } }
                }

                if (matched == keys.Count && keys.Count > 0) { lib.RegStatus = "registered"; keysToCheck = matchedKeys; }
                else if (matched > 0) { lib.RegStatus = "partial"; keysToCheck = matchedKeys; }
                else if (pathMismatch > 0)
                {
                    lib.RegStatus = "path-mismatch";
                    lib.ProductKey = firstKey;
                    lib.RegContentDir = firstDir;
                    keysToCheck = new List<string> { firstKey };
                }
                else { lib.RegStatus = "missing"; keysToCheck = keys; }
            }

            // 注册表齐全但缺 Service Center 记录 → 记录不完整（乐器仍无法加载）。
            // 仅对「有 .nicnt 的库」判定：无 .nicnt 的旧式库没有 XML 可写，本就不适用这一步。
            if (lib.HasNicnt && lib.RegStatus is "registered" or "partial")
            {
                var (present, missingSc) = CheckServiceCenter(keysToCheck);
                lib.ScMissingRecords = missingSc;
                if (missingSc.Count > 0 && present < keysToCheck.Count)
                    lib.RegStatus = "incomplete";
            }

            // 便携版 Kontakt：系统注册表齐全，但库未写入它自己的 UserData\Settings.cfg。
            // 这种状态下 Kontakt 会把库当作"未安装"，必须用库管理器「扫描 → 保存」。
            if (portableNames != null && lib.RegStatus is "registered" or "incomplete")
            {
                var verifyKeys = keysToCheck.Count > 0
                    ? keysToCheck
                    : lib.ProductKey.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (verifyKeys.Count > 0 && !verifyKeys.Any(k => portableNames.Contains(k)))
                {
                    lib.RegStatus = "pending-manager";
                    lib.ScMissingRecords = verifyKeys;
                }
            }

            // ── 版本兼容 ──
            if (!VersionUtil.IsValid(lib.RequiredKontakt) || !VersionUtil.IsValid(kontaktVersion))
                lib.CompatStatus = "unknown";
            else
                lib.CompatStatus = VersionUtil.Compare(lib.RequiredKontakt, kontaktVersion) > 0 ? "too-old" : "ok";
        }
    }
}
