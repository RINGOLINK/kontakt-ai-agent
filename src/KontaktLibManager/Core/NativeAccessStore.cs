using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>
/// **Native Access 注册记录**（`installed_products\*.json`）。
///
/// **为什么需要**（用户实测报告）：
///   正式版 Kontakt 8.13.1 判断「库是否可用」时，**不只看注册表**，还看
///   `%PUBLIC%\Documents\Native Instruments\installed_products\<产品名>.json`。
///   文件内容形如：
///     {"ContentDir":"I:\\Abbey Road\\Abbey Road 50s Drummer\\Samples","ContentVersion":"1.2.0"}
///   若该文件**不存在**或 **`ContentDir` 为空**，Kontakt 会提示
///     「Library not found. Click 'Manage Libraries' to set the content via Native Access.」
///
///   实测本机 2359 条记录中，**只有 204 条有非空 ContentDir，2155 条是空的** ——
///   这正是「便携版能用、正式版报找不到库」的根因：便携版看自己的 Settings.cfg，
///   正式版看 Native Access 记录，两者是**两套独立注册**。
///
/// **ContentDir 的取值**：实测既能是**库根目录**（如 `I:\Audio Imperia\Audio Imperia Traveler Aurus`），
///   也能是**其 Samples 子目录**（如 `I:\Abbey Road\Abbey Road 50s Drummer\Samples`），两种 Kontakt 都认。
///   本类统一写**库根目录**（更通用）。
/// </summary>
public static class NativeAccessStore
{
    /// <summary>`installed_products` 目录；不存在时返回 null。</summary>
    public static string? Dir
    {
        get
        {
            try
            {
                string pub = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
                if (string.IsNullOrEmpty(pub)) return null;
                return Path.Combine(pub, "Native Instruments", "installed_products");
            }
            catch { return null; }
        }
    }

    /// <summary>该目录是否可用（存在）。</summary>
    public static bool Available
    {
        get { var d = Dir; return d != null && Directory.Exists(d); }
    }

    /// <summary>读取全部记录：产品名 → ContentDir。</summary>
    public static Dictionary<string, string> ReadAll()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = Dir;
        if (dir == null || !Directory.Exists(dir)) return map;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8));
                string cd = doc.RootElement.TryGetProperty("ContentDir", out var v) ? (v.GetString() ?? "") : "";
                map[Path.GetFileNameWithoutExtension(f)] = cd;
            }
            catch { }
        }
        return map;
    }

    /// <summary>查某个产品名当前的 ContentDir（无记录返回 null）。</summary>
    public static string? GetContentDir(string productName)
    {
        var dir = Dir;
        if (dir == null) return null;
        string f = Path.Combine(dir, Sanitize(productName) + ".json");
        if (!File.Exists(f)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8));
            return doc.RootElement.TryGetProperty("ContentDir", out var v) ? (v.GetString() ?? "") : "";
        }
        catch { return null; }
    }

    /// <summary>
    /// **写入/更新 Native Access 记录**（让正式版 Kontakt 认得这个库）。
    /// 返回 (ok, message)。会先备份原文件（若已存在）。
    /// </summary>
    public static (bool ok, string message) Register(string productName, string contentDir, string contentVersion = "1.0.0")
    {
        var dir = Dir;
        if (dir == null) return (false, "找不到 Native Access 记录目录（%PUBLIC%\\Documents\\Native Instruments\\installed_products）");
        if (string.IsNullOrWhiteSpace(productName)) return (false, "产品名为空");
        if (string.IsNullOrWhiteSpace(contentDir) || !Directory.Exists(contentDir))
            return (false, $"内容目录不存在：{contentDir}");

        try
        {
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, Sanitize(productName) + ".json");

            // 备份（首次写入前）
            if (File.Exists(f))
            {
                string bak = f + ".klm-backup";
                try { if (!File.Exists(bak)) File.Copy(f, bak, false); } catch { }
            }

            // 保持与官方一致的紧凑 JSON（无空格、不转义非 ASCII）
            var opts = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string json = JsonSerializer.Serialize(new { ContentDir = contentDir, ContentVersion = contentVersion }, opts);
            File.WriteAllText(f, json, new UTF8Encoding(false));
            return (true, $"已写入 Native Access 记录：{Path.GetFileName(f)} → {contentDir}");
        }
        catch (Exception ex) { return (false, "写入失败：" + ex.Message); }
    }

    /// <summary>删除记录（用于取消入库）。返回 (ok, message)。</summary>
    public static (bool ok, string message) Unregister(string productName)
    {
        var dir = Dir;
        if (dir == null) return (false, "找不到 Native Access 记录目录");
        try
        {
            string f = Path.Combine(dir, Sanitize(productName) + ".json");
            if (!File.Exists(f)) return (false, "没有该记录");
            string bak = f + ".klm-backup";
            try { if (!File.Exists(bak)) File.Copy(f, bak, false); } catch { }
            File.Delete(f);
            return (true, "已删除 Native Access 记录");
        }
        catch (Exception ex) { return (false, "删除失败：" + ex.Message); }
    }

    /// <summary>文件名净化（产品名可能含非法字符）。</summary>
    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString().Trim();
    }
}
