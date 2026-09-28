using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// **为没有 `.nicnt` 的第三方库生成 `.nicnt`**（并据此完成入库）。
///
/// 机制来源：逆向并实测 `Nicnt Maker.exe`（NI-NICNT GENERATOR Mini v1，2026-09-20）。
/// 用户输入「厂商名 + 库名 + SNPID」后，该工具：
///   ① 到 `C:\Program Files\Common Files\Native Instruments\Service Center\` 查已注册库的 SNPID，重复则驳回；
///   ② 还要避开官方 SNPID 表 `Native_Access_SNPID_List.txt`；
///   ③ 通过后生成 `.nicnt`（**放在工具自己所在目录**，故正确用法是把工具丢进目标库目录）；
///   ④ 并顺带把该目录写进注册表（等于连入库一起做了）。
///
/// **本实现的不同点**：SNPID **自动挑选**（用户不必一个个试），
/// 且 `.nicnt` 直接写进目标库目录，无需把工具搬来搬去。
///
/// **`.nicnt` 的字节布局（实测 `Adagietto.nicnt`，514,344 B）**：
/// <code>
/// 0..15            头部 `/\ NI FC MTD  /\`
/// 16..255          零填充（240 B）
/// 256..(256+N-1)   主 XML（ProductHints）
/// 之后 1 字节       0x0A
/// 零填充到 512058
/// 512058..514343   尾部模板（第二 MTD 头 / TOC / 第二 soundinfos XML）—— 固定偏移，与主 XML 长度无关
/// </code>
/// 尾部模板存为 `data\nicnt-tail.bin`（2,286 B）。
/// </summary>
public static class NicntGenerator
{
    /// <summary>第二个 MTD / TOC / 第二 XML 的固定起始偏移（实测值）。</summary>
    public const int TailOffset = 512058;

    /// <summary>主 XML 的起始偏移（头部 16 + 零填充 240）。</summary>
    public const int XmlOffset = 256;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("/\\ NI FC MTD  /\\");

    /// <summary>
    /// 官方 SNPID 表。**优先读项目内置的静态副本**（随应用分发，保证版本稳定、不依赖用户是否导出过），
    /// 内置副本不存在时才回退到 Native Access 的导出目录。
    /// </summary>
    public static string OfficialSnpIdListPath
    {
        get
        {
            string builtin = Path.Combine(AppPaths.DataDir, "Native_Access_SNPID_List.txt");
            if (File.Exists(builtin)) return builtin;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "Kontakt 8", "Native Access Products Report", "Native_Access_SNPID_List.txt");
        }
    }

    /// <summary>是否为内置副本（用于界面提示）。</summary>
    public static bool OfficialListIsBuiltin =>
        File.Exists(Path.Combine(AppPaths.DataDir, "Native_Access_SNPID_List.txt"));

    /// <summary>
    /// Service Center 目录（放已注册库的 `.xml`）。
    /// **实测真实路径是 `C:\Program Files\Common Files\Native Instruments\Service Center`** ——
    /// 注意**不是** `Environment.SpecialFolder.CommonApplicationData`（那是 `C:\ProgramData`，扫出来是 0 个）。
    /// </summary>
    public static string ServiceCenterDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Common Files", "Native Instruments", "Service Center");

    /// <summary>Native Access 记录目录（`installed_products\*.json`）。</summary>
    public static string NativeAccessDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "Native Instruments", "installed_products");

    /// <summary>尾部模板文件（随应用分发）。</summary>
    public static string TailTemplatePath => Path.Combine(AppPaths.DataDir, "nicnt-tail.bin");

    // ─────────────────────────── SNPID 查重 ───────────────────────────

    /// <summary>收集「已被占用」的 SNPID：Service Center 里已注册的 + 官方 SNPID 表里的 + Content 键里的。</summary>
    public static (HashSet<string> used, int fromSc, int fromOfficial, int fromContent, int fromNicnt, string note) CollectUsedSnpIds(IEnumerable<string>? scanRoots = null)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sc = 0, off = 0;
        var notes = new List<string>();

        // ① Service Center 下所有 .xml 的 <SNPID>
        try
        {
            if (Directory.Exists(ServiceCenterDir))
            {
                foreach (var f in Directory.EnumerateFiles(ServiceCenterDir, "*.xml"))
                {
                    try
                    {
                        string t = File.ReadAllText(f);
                        int i = t.IndexOf("<SNPID>", StringComparison.Ordinal);
                        if (i < 0) continue;
                        int j = t.IndexOf("</SNPID>", i, StringComparison.Ordinal);
                        if (j < 0) continue;
                        string v = t.Substring(i + 7, j - i - 7).Trim();
                        if (v.Length > 0 && used.Add(v)) sc++;
                    }
                    catch { }
                }
            }
            else notes.Add("未找到 Service Center 目录");
        }
        catch (Exception ex) { notes.Add("读 Service Center 失败：" + ex.Message); }

        // ② 官方 SNPID 表（每行 `SNPID|库名|厂商|产品名`）
        try
        {
            string p = OfficialSnpIdListPath;
            if (File.Exists(p))
            {
                foreach (var line in File.ReadLines(p))
                {
                    int bar = line.IndexOf('|');
                    string v = (bar > 0 ? line.Substring(0, bar) : line).Trim();
                    if (v.Length == 0) continue;
                    if (used.Add(v)) off++;
                }
            }
            else notes.Add("未找到官方 SNPID 表");
        }
        catch (Exception ex) { notes.Add("读官方 SNPID 表失败：" + ex.Message); }

        // ③ **Kontakt 自己的内容索引** `HKLM\SOFTWARE\WOW6432Node\Native Instruments\Content` 的键名
        //    —— 形如 `k2lib0<SNPID>`，反推即可得到 SNPID。
        //    这是**最可靠的一道防线**：即使某个写入路径漏写 SC XML 的 `<SNPID>`，
        //    只要 Content 键里有条目，下次查重就不会重复分配（实测踩过：重复分配 Z00）。
        int fromContent = 0;
        try
        {
            using var lm32 = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
            using var ck = lm32.OpenSubKey(@"SOFTWARE\Native Instruments\Content");
            if (ck != null)
            {
                foreach (var vn in ck.GetValueNames())
                {
                    // `k2lib0Z00` → `Z00`；`k2lib0469` → `469`；`k2libab75` → 原样保留前缀形式
                    if (!vn.StartsWith("k2lib", StringComparison.OrdinalIgnoreCase)) continue;
                    string body = vn.Substring(5);
                    string snp = body.StartsWith("0") && body.Length > 1 ? body.Substring(1) : body;
                    if (snp.Length > 0 && used.Add(snp)) fromContent++;
                }
            }
        }
        catch (Exception ex) { notes.Add("读 Content 键失败：" + ex.Message); }

        // ④ **磁盘上所有 `.nicnt` 里的 `<SNPID>`** —— 最后一道防线：
        //    即便某个库既没写 SC XML、也没写 Content 键，只要它的 `.nicnt` 还在，SNPID 就不会被重复分配。
        int fromNicnt = 0;
        try
        {
            foreach (var root in NormalizeRoots(scanRoots))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.nicnt", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files)
                {
                    try
                    {
                        // 只读前 8 KB 找 SNPID（.nicnt 常有几百 KB，不必整文件读入）
                        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        int n = (int)Math.Min(8192, fs.Length);
                        var buf = new byte[n];
                        int read = fs.Read(buf, 0, n);
                        string head = Encoding.UTF8.GetString(buf, 0, read);
                        int i = head.IndexOf("<SNPID>", StringComparison.Ordinal);
                        if (i < 0) continue;
                        int j = head.IndexOf("</SNPID>", i, StringComparison.Ordinal);
                        if (j < 0) continue;
                        string v = head.Substring(i + 7, j - i - 7).Trim();
                        if (v.Length > 0 && used.Add(v)) fromNicnt++;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { notes.Add("扫 .nicnt 失败：" + ex.Message); }

        return (used, sc, off, fromContent, fromNicnt,
                notes.Count > 0 ? string.Join("；", notes) : "四个来源均已读取");
    }

    /// <summary>把调用方给的根目录去重并过滤掉不存在的；只读。</summary>
    private static IEnumerable<string> NormalizeRoots(IEnumerable<string>? roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (roots != null)
            foreach (var r in roots)
                if (!string.IsNullOrWhiteSpace(r) && Directory.Exists(r) && seen.Add(r)) yield return r;
        if (Directory.Exists(AppPaths.DefaultRoot) && seen.Add(AppPaths.DefaultRoot))
            yield return AppPaths.DefaultRoot;
    }

    /// <summary>
    /// **自动挑一个既未注册、也不在官方表上的 SNPID**（用户无需自己试）。
    ///
    /// **逐个候选检查**（`if (!used.Contains(cand)) return cand;`）—— 已占用的会被**跳过**，
    /// 不存在「顺着 Z38 碾过去」的情况。
    ///
    /// **候选顺序按「官方表里的使用频次」从少到多动态排序**（实测官方表 3,228 个 ID 的分布）：
    /// `H`/`J` 各仅 5 个、`D`/`L` 各 9 个 …… 而 `K` 126、`S` 116、`Y` 108、`Z` 94。
    /// **优先挑官方用得最少的字母段**，可显著降低「未来官方新品占用同一编号」的撞车概率；
    /// 字母段全部用尽后再退到纯数字 `000..999`。
    /// </summary>
    public static (string snpid, string message) PickFreeSnpId(HashSet<string> used)
    {
        // 官方表里各字母开头的出现次数（读不到则所有字母同为 0，退化为字母顺序）
        var freq = new Dictionary<char, int>();
        try
        {
            if (File.Exists(OfficialSnpIdListPath))
            {
                foreach (var line in File.ReadLines(OfficialSnpIdListPath))
                {
                    int bar = line.IndexOf('|');
                    string v = (bar > 0 ? line.Substring(0, bar) : line).Trim();
                    if (v.Length == 0) continue;
                    char c0 = v[0];
                    freq[c0] = freq.TryGetValue(c0, out int n) ? n + 1 : 1;
                }
            }
        }
        catch { }

        // 字母表：**排除 I 与 O**（易与数字 1/0 混淆，官方也未使用）
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        var ordered = alphabet
            .OrderBy(c => freq.TryGetValue(c, out int n) ? n : 0)   // 官方用得少的排前面
            .ThenBy(c => c)
            .ToList();

        foreach (char c in ordered)
        {
            for (int n = 0; n <= 99; n++)
            {
                string cand = c + n.ToString("D2");
                if (!used.Contains(cand))
                    return (cand, $"自动选取 SNPID = {cand}（未被占用；该字母段官方仅用 {(freq.TryGetValue(c, out int f) ? f : 0)} 个，撞车风险低）");
            }
        }
        // 字母段用尽，再试纯数字
        for (int n = 0; n <= 999; n++)
        {
            string cand = n.ToString("D3");
            if (!used.Contains(cand)) return (cand, $"自动选取 SNPID = {cand}（未被占用）");
        }
        return ("", "❌ 没有可用的 SNPID（Z00-Z99 / Y00-Y99 / … / 000-999 全部被占用）");
    }

    // ─────────────────────────── 主 XML 生成 ───────────────────────────

    /// <summary>
    /// 生成主 XML（ProductHints）。格式与 `Nicnt Maker.exe` 产出一致：
    /// `spec="1.0.9"` / `Product version="1"` / `Type=Content` / `Visibility=1` /
    /// `AuthSystem=RAS3` / `ProductSpecific{Visibility=3, Icon=kontakt}`，**无 UPID**。
    /// </summary>
    public static string BuildMainXml(string company, string name, string snpid)
    {
        string esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\" ?>\r\n");
        sb.Append("<ProductHints spec=\"1.0.9\">\r\n\r\n");
        sb.Append("  <Product version=\"1\">\r\n");
        sb.Append("\t<Name>").Append(esc(name)).Append("</Name>\r\n");
        sb.Append("\t<Type>Content</Type>\r\n");
        sb.Append("<PoweredBy>Kontakt</PoweredBy>\r\n");
        sb.Append("    <Visibility>1</Visibility>\r\n");
        sb.Append("    <Company>").Append(esc(company)).Append("</Company>\r\n");
        sb.Append("<AuthSystem>RAS3</AuthSystem>\r\n");
        sb.Append("        <SNPID>").Append(esc(snpid)).Append("</SNPID>\r\n");
        sb.Append("\t<RegKey>").Append(esc(name)).Append("</RegKey>\r\n");
        sb.Append("    <ProductSpecific>\r\n");
        sb.Append("      <Visibility type=\"Number\">3</Visibility>\r\n");
        sb.Append("    \t<Icon>kontakt</Icon>\r\n");
        sb.Append("    </ProductSpecific>\r\n");
        sb.Append("  </Product>\r\n\r\n");
        sb.Append("</ProductHints>\r\n");
        return sb.ToString();
    }

    // ─────────────────────────── 拼装 `.nicnt` ───────────────────────────

    /// <summary>
    /// 拼装完整 `.nicnt`：头部(16) + 零填充 + 主 XML + `0x0A` + 零填充到 512058 + 尾部模板。
    /// 主 XML 变长不影响尾部位置（尾部是固定偏移）。
    /// </summary>
    public static (byte[]? bytes, string error) BuildNicnt(string company, string name, string snpid)
    {
        string tailPath = TailTemplatePath;
        if (!File.Exists(tailPath))
            return (null, $"缺少尾部模板文件：{tailPath}（应随应用分发，2,286 字节）");

        byte[] tail = File.ReadAllBytes(tailPath);
        byte[] xml = new UTF8Encoding(false).GetBytes(BuildMainXml(company, name, snpid));

        int xmlEnd = XmlOffset + xml.Length;
        if (xmlEnd + 1 > TailOffset)
            return (null, $"主 XML 过长（{xml.Length} 字节），会越过尾部固定偏移 {TailOffset}");

        var outBuf = new byte[TailOffset + tail.Length];
        Buffer.BlockCopy(Magic, 0, outBuf, 0, Magic.Length);          // 头部 16 字节
        Buffer.BlockCopy(xml, 0, outBuf, XmlOffset, xml.Length);      // 主 XML
        outBuf[xmlEnd] = 0x0A;                                        // 换行
        Buffer.BlockCopy(tail, 0, outBuf, TailOffset, tail.Length);   // 尾部模板
        return (outBuf, "");
    }

    /// <summary>
    /// **一步到位**：为库生成 `.nicnt` 并写进库目录。返回 (ok, snpid, fileName, message)。
    /// 已存在同名 `.nicnt` 时会先备份为 `.klm-backup-<时间戳>`。
    /// </summary>
    public static (bool ok, string snpid, string file, string message) GenerateFor(
        string libraryPath, string company, string name, string? forcedSnpId = null)
    {
        if (!Directory.Exists(libraryPath))
            return (false, "", "", $"库目录不存在：{libraryPath}");
        if (string.IsNullOrWhiteSpace(name))
            return (false, "", "", "库名不能为空");

        var (used, sc, off, ck, fn, note) = CollectUsedSnpIds();
        string snpid;
        string pickMsg;
        if (!string.IsNullOrWhiteSpace(forcedSnpId))
        {
            snpid = forcedSnpId.Trim();
            if (used.Contains(snpid))
                return (false, snpid, "", $"SNPID `{snpid}` 已被占用（Service Center 或官方表），请换一个");
            pickMsg = $"使用指定 SNPID = {snpid}";
        }
        else
        {
            var (p, m) = PickFreeSnpId(used);
            if (p.Length == 0) return (false, "", "", m);
            snpid = p;
            pickMsg = m;
        }

        var (bytes, err) = BuildNicnt(company, name, snpid);
        if (bytes == null) return (false, snpid, "", err);

        string fileName = Sanitize(name) + ".nicnt";
        string outPath = Path.Combine(libraryPath, fileName);
        try
        {
            if (File.Exists(outPath))
                File.Copy(outPath, outPath + ".klm-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            File.WriteAllBytes(outPath, bytes);
        }
        catch (Exception ex)
        {
            return (false, snpid, fileName, "写入 .nicnt 失败：" + ex.Message);
        }

        return (true, snpid, fileName,
            $"{pickMsg}（查重来源：Service Center {sc} 个 / 官方表 {off} 个）。已生成 {fileName}（{bytes.Length:N0} 字节）。");
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
