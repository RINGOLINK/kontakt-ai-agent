using System.Security.Cryptography;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// 音色库封面图缓存。
///
/// 事实：Kontakt 的 ``.nicnt`` 是一个容器 = 容器头 + ProductHints XML + **内嵌 PNG 封面**。
/// 实测各库封面均为 **905×99 宽幅横幅**（左侧为库名文字），是库浏览器里用于视觉识别的官方图。
/// 本类从 .nicnt 中提取该 PNG 并缓存到应用数据目录，供界面显示。
/// 仅读取库自带文件，不修改任何音色文件。
/// </summary>
public static class CoverStore
{

    /// <summary>
    /// 保存用户自制封面。文件名带 "custom-" 前缀，便于与 .nicnt 提取的官方封面区分
    /// （重新扫描时官方提取不会覆盖用户自制封面）。
    /// </summary>
    public static string SaveCustom(string libraryPath, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("custom|" + libraryPath));
            string name = "custom-" + Convert.ToHexString(hash)[..16].ToLowerInvariant() + ".png";
            string target = Path.Combine(Dir, name);
            File.WriteAllBytes(target, png);
            return name;
        }
        catch { return ""; }
    }
    public static string Dir
    {
        get
        {
            var d = Path.Combine(AppPaths.DataDir, "covers");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>按库路径生成稳定的封面文件名。</summary>
    public static string FileNameFor(string libraryPath)
    {
        string norm = LibraryRegistrar.NormalizePath(libraryPath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant() + ".png";
    }

    /// <summary>保存封面（已存在则跳过），返回文件名；失败返回空串。</summary>
    public static string Save(string libraryPath, byte[] png)
    {
        try
        {
            if (png.Length < 64) return "";
            string name = FileNameFor(libraryPath);
            string path = Path.Combine(Dir, name);
            if (!File.Exists(path)) File.WriteAllBytes(path, png);
            return name;
        }
        catch { return ""; }
    }

    /// <summary>清理不再使用的封面（扫描后调用，删除索引里已不存在的封面文件）。</summary>
    public static int Cleanup(IEnumerable<string> keepFileNames)
    {
        var keep = new HashSet<string>(keepFileNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "*.png"))
            {
                if (keep.Contains(Path.GetFileName(f))) continue;
                try { File.Delete(f); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }
}
