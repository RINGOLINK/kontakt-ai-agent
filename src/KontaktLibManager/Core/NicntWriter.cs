using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// **为没有 `.nicnt` 的库生成注册文件**（.nicnt + Service Center XML）。
///
/// **格式来源**：实测真实 `.nicnt`（`Audio Imperia Artifact Fractal.nicnt`）与
/// Service Center XML（`Abbey Road 50s Drummer.xml`），并参照用户提供的
/// **Nicnt Maker** 工具包内嵌模板与说明。
///
/// **`.nicnt` 结构**：
///   ① 头部标记 `/\ NI FC MTD  /\`（注意 MTD 后是**两个空格**）
///   ② 纯文本 `ProductHints` XML
///   ③ 可选内嵌 PNG 横幅（本类不写，保持最小可用）
///
/// **Service Center XML 的关键差异**（实测对照得出）：
///   · 多出 **`<UPID>`** —— 它是串联「内容路径 ↔ 授权 ↔ 安装状态」的主键，
///     与 `komplete.db3.k_content_path.upid` 对应；
///   · `spec="1.0.16"`、`<Product version="4">`；
///   · 含 `<Relevance>` 与 `<Visibility target="Standalone">`；
///   · `ProductSpecific` 里放 `HU`/`JDX`（**只有官方库有，第三方无法伪造**）。
///
/// **说明（来自 Nicnt Maker 文档）**：`nicnt` 与 Service Center 的 XML **必须匹配**；
/// 自定义库建议 SNPID 用「1 个未占用字母 + 2 位数字」（如 D00~D99），3 字母在部分
/// AMD 机器上显示不全。
/// </summary>
public static class NicntWriter
{
    public const string Magic = "/\\ NI FC MTD  /\\";

    /// <summary>已占用的 SNPID 集合（从清单文件读；读不到则返回空集）。</summary>
    public static HashSet<string> LoadUsedSnpIds(params string[] listFiles)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in listFiles)
        {
            try
            {
                if (!File.Exists(f)) continue;
                foreach (var line in File.ReadLines(f, Encoding.UTF8))
                {
                    int i = line.IndexOf('|');
                    if (i <= 0) continue;
                    string id = line[..i].Trim();
                    if (id.Length is >= 1 and <= 4) set.Add(id);
                }
            }
            catch { }
        }
        return set;
    }

    /// <summary>
    /// 分配一个**未占用**的 SNPID：优先用「1 字母 + 2 数字」（Nicnt Maker 推荐，
    /// 从 D00 起避开常见官方段），冲突则顺延。
    /// </summary>
    public static string AllocateSnpId(HashSet<string> used)
    {
        // 首选字母段：D / E / F / G（官方少用），从 D00 起
        foreach (char letter in "DEFGHIJKLMNOPQRSTUVWXYZ")
            for (int n = 0; n <= 99; n++)
            {
                string id = $"{letter}{n:D2}";
                if (!used.Contains(id)) return id;
            }
        // 兜底：纯数字 3 位
        for (int n = 900; n <= 999; n++)
        {
            string id = n.ToString();
            if (!used.Contains(id)) return id;
        }
        return "";
    }

    /// <summary>生成 `.nicnt` 的完整字节（头部 + ProductHints XML）。</summary>
    public static byte[] BuildNicnt(string productName, string company, string snpId, string authSystem = "RAS2")
    {
        string xml =
            "<ProductHints spec=\"1.0.9\">\r\n" +
            "\r\n" +
            "  <Product version=\"1\">\r\n" +
            $"    <Company>{Esc(company)}</Company>\r\n" +
            $"    <Name>{Esc(productName)}</Name>\r\n" +
            $"    <RegKey>{Esc(productName)}</RegKey>\r\n" +
            "    <Type>Content</Type>\r\n" +
            "    <PoweredBy>Kontakt</PoweredBy>\r\n" +
            "    <Icon>kontakt</Icon>\r\n" +
            "    <Visibility>1</Visibility>\r\n" +
            $"    <AuthSystem>{Esc(authSystem)}</AuthSystem>\r\n" +
            $"    <SNPID>{Esc(snpId)}</SNPID>\r\n" +
            "    <ProductSpecific>\r\n" +
            "      <Visibility type=\"Number\">3</Visibility>\r\n" +
            "    </ProductSpecific>\r\n" +
            "  </Product>\r\n" +
            "\r\n" +
            "</ProductHints>\r\n";

        using var ms = new MemoryStream();
        var head = Encoding.ASCII.GetBytes(Magic);
        ms.Write(head, 0, head.Length);
        var body = Encoding.UTF8.GetBytes(xml);
        ms.Write(body, 0, body.Length);
        return ms.ToArray();
    }

    /// <summary>生成 Service Center XML（含 UPID；有 HU/JDX 时一并写入）。</summary>
    public static string BuildServiceCenterXml(string productName, string company, string snpId, string upid,
                                               string hu = "", string jdx = "", string authSystem = "RAS2")
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\" ?>\r\n");
        sb.Append("<ProductHints spec=\"1.0.16\">\r\n\r\n");
        sb.Append("  <Product version=\"4\">\r\n");
        sb.Append($"    <UPID>{Esc(upid)}</UPID>\r\n");
        sb.Append($"    <Name>{Esc(productName)}</Name>\r\n");
        sb.Append("    <Type>Content</Type>\r\n");
        sb.Append("    <Relevance>\r\n");
        sb.Append("      <Application nativeContent=\"true\">kontakt</Application>\r\n");
        sb.Append("      <Application>maschine</Application>\r\n");
        sb.Append("    </Relevance>\r\n");
        sb.Append("    <PoweredBy>Kontakt</PoweredBy>\r\n");
        sb.Append("    <Visibility target=\"Standalone\">3</Visibility>\r\n");
        sb.Append($"    <Company>{Esc(company)}</Company>\r\n");
        sb.Append($"    <AuthSystem>{Esc(authSystem)}</AuthSystem>\r\n");
        sb.Append($"    <SNPID>{Esc(snpId)}</SNPID>\r\n");
        sb.Append($"    <RegKey>{Esc(productName)}</RegKey>\r\n");
        sb.Append("    <Icon>kontakt</Icon>\r\n");
        sb.Append("    <ProductSpecific>\r\n");
        if (hu.Length > 0) sb.Append($"      <HU>{Esc(hu)}</HU>\r\n");
        if (jdx.Length > 0) sb.Append($"      <JDX>{Esc(jdx)}</JDX>\r\n");
        sb.Append("      <Visibility type=\"Number\">3</Visibility>\r\n");
        sb.Append("    </ProductSpecific>\r\n");
        sb.Append("  </Product>\r\n\r\n");
        sb.Append("</ProductHints>\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// **为一个库生成并落盘注册文件**（`.nicnt` 放库根；SC XML 放 Service Center 目录）。
    /// 返回 (ok, message, snpId, upid, nicntPath, scPath)。
    /// </summary>
    public static (bool ok, string message, string snpId, string upid, string nicntPath, string scPath)
        GenerateFor(string libraryName, string libraryPath, string? snpListFile = null, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(libraryName)) return (false, "库名为空", "", "", "", "");
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return (false, $"库目录不存在：{libraryPath}", "", "", "", "");

        // 已占用的 SNPID
        var used = LoadUsedSnpIds(
            snpListFile ?? "",
            @"<user>\Downloads\Kontakt 8\Native Access Products Report\Native_Access_SNPID_List.txt",
            @"<user>\AppData\Local\Temp\RarSFX5\Data\parser\Native_Access_SNPID.txt");
        // **已生成的 SC XML 里的 SNPID 也要算占用** ——
        // 否则连续生成会重复分配同一个 SNPID（实测踩到：两个库都拿到 D00）。
        try
        {
            foreach (var f in Directory.GetFiles(ScDir(), "*.xml"))
            {
                try
                {
                    string txt = File.ReadAllText(f, Encoding.UTF8);
                    int a = txt.IndexOf("<SNPID>", StringComparison.OrdinalIgnoreCase);
                    if (a < 0) continue;
                    a += "<SNPID>".Length;
                    int b = txt.IndexOf("</SNPID>", a, StringComparison.OrdinalIgnoreCase);
                    if (b > a) used.Add(txt[a..b].Trim());
                }
                catch { }
            }
        }
        catch { }

        // 同一次调用内也要去重：把「本次已分配」的 SNPID 记进 used

        string snp = AllocateSnpId(used);
        used.Add(snp);   // 立即占位，避免同一进程内重复分配
        if (snp.Length == 0) return (false, "无法分配未占用的 SNPID", "", "", "", "");

        string upid = Guid.NewGuid().ToString("D");
        string company = libraryName;

        // ① .nicnt → 库根目录
        string nicntPath = Path.Combine(libraryPath, Sanitize(libraryName) + ".nicnt");
        if (File.Exists(nicntPath) && !overwrite)
            return (false, $"已存在 .nicnt：{Path.GetFileName(nicntPath)}（未覆盖）", snp, upid, nicntPath, "");

        try
        {
            File.WriteAllBytes(nicntPath, BuildNicnt(libraryName, company, snp));

            // ② Service Center XML
            string sc = ScDir();
            Directory.CreateDirectory(sc);
            string scPath = Path.Combine(sc, Sanitize(libraryName) + ".xml");
            File.WriteAllText(scPath, BuildServiceCenterXml(libraryName, company, snp, upid), new UTF8Encoding(false));

            return (true,
                $"已生成注册文件：SNPID={snp}  UPID={upid}\n  .nicnt → {nicntPath}\n  SC XML → {scPath}",
                snp, upid, nicntPath, scPath);
        }
        catch (Exception ex) { return (false, "生成失败：" + ex.Message, snp, upid, nicntPath, ""); }
    }

    private static string ScDir()
    {
        try { return LibraryRegistrar.ServiceCenterDir(); }
        catch { return @"C:\Program Files\Common Files\Native Instruments\Service Center"; }
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString().Trim();
    }

    private static string Esc(string s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
