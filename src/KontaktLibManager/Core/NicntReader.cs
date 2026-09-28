using System.Text;
using System.Xml.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// .nicnt 读取器。
/// 结构（实测）：NI 文件容器头 ``/\ NI FC MTD  /\`` + 明文 XML(ProductHints) + 封面 PNG。
/// XML 内含注册 Kontakt 库浏览器所需的全部字段：
///   Name(显示名) / RegKey(注册表键名) / HU / JDX / Visibility / AuthSystem / SNPID / Type。
/// 说明：本工具只读取这些元数据并复刻 Kontakt 自带「Add Library」的注册行为，
///       不修改 .nicnt、不伪造授权字段、不绕过任何授权校验。
/// </summary>
public static class NicntReader
{
    private const int MaxRead = 8 * 1024 * 1024;

    public static NicntInfo? Read(string nicntPath)
    {
        try
        {
            using var fs = new FileStream(nicntPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int len = (int)Math.Min(fs.Length, MaxRead);
            var buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }

            // 只取 XML 片段：从 "<?xml" 到 "</ProductHints>"
            string text = Encoding.UTF8.GetString(buf, 0, read);
            int start = text.IndexOf("<?xml", StringComparison.Ordinal);
            if (start < 0) start = text.IndexOf("<ProductHints", StringComparison.Ordinal);
            int end = text.IndexOf("</ProductHints>", StringComparison.Ordinal);
            if (start < 0 || end < 0) return null;
            end += "</ProductHints>".Length;

            string xml = text.Substring(start, end - start);
            var doc = XDocument.Parse(xml, LoadOptions.None);
            var product = doc.Descendants("Product").FirstOrDefault();
            if (product == null) return null;

            string Val(string name) => product.Element(name)?.Value?.Trim() ?? "";

            var specific = product.Element("ProductSpecific");

            var info = new NicntInfo
            {
                FilePath = nicntPath,
                Name = Val("Name"),
                RegKey = Val("RegKey"),
                Type = Val("Type"),
                PoweredBy = Val("PoweredBy"),
                ProductVisibility = Val("Visibility"),
                Company = Val("Company"),
                AuthSystem = Val("AuthSystem"),
                SnpId = Val("SNPID"),
                Upid = product.Element("UPID")?.Value?.Trim() ?? "",
                Hu = specific?.Element("HU")?.Value?.Trim() ?? "",
                Jdx = specific?.Element("JDX")?.Value?.Trim() ?? "",
                Visibility = specific?.Elements("Visibility").FirstOrDefault()?.Value?.Trim() ?? "",
                ContentVersion = product.Element("ContentVersion")?.Value?.Trim() ?? "",
                RawXml = xml,
            };

            if (string.IsNullOrWhiteSpace(info.RegKey)) info.RegKey = info.Name;
            if (string.IsNullOrWhiteSpace(info.Name)) info.Name = Path.GetFileNameWithoutExtension(nicntPath);
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 .nicnt 中提取内嵌的封面 PNG（Kontakt 库浏览器用的宽幅横幅图，实测 905×99）。
    /// 返回完整 PNG 字节；未找到返回 null。
    /// </summary>
    public static byte[]? ExtractArtwork(string nicntPath)
    {
        try
        {
            var b = File.ReadAllBytes(nicntPath);
            ReadOnlySpan<byte> pngSig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            ReadOnlySpan<byte> iendSig = stackalloc byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

            int start = IndexOf(b, pngSig);
            if (start < 0) return null;

            int end = IndexOf(b, iendSig, start);
            if (end < 0) return null;
            end += iendSig.Length;

            int len = end - start;
            if (len < 64 || len > 32 * 1024 * 1024) return null;   // 合理性上限

            var png = new byte[len];
            Array.Copy(b, start, png, 0, len);
            return png;
        }
        catch { return null; }
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int from = 0)
    {
        for (int i = from; i <= haystack.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>在一个库目录内查找所有 .nicnt（含子目录，如 Chris Hein 的多子库结构）。</summary>
    public static List<NicntInfo> FindInLibrary(string libraryPath, int maxDepth = 3)
    {
        var list = new List<NicntInfo>();
        if (!Directory.Exists(libraryPath)) return list;
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = maxDepth,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };
            foreach (var f in new DirectoryInfo(libraryPath).EnumerateFiles("*.nicnt", opts))
            {
                var info = Read(f.FullName);
                if (info != null) list.Add(info);
            }
        }
        catch { /* 忽略无法访问的目录 */ }
        return list;
    }
}
