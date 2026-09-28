using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>Kontakt 程序候选（自动检测 + 用户手工添加，可编辑）。</summary>
public sealed class KontaktCandidate
{
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Company { get; set; } = "";
    public string Note { get; set; } = "";
    public double SizeMB { get; set; }
    /// <summary>auto = 自动检测；manual = 用户添加/编辑过</summary>
    public string Source { get; set; } = "auto";
    /// <summary>用户从列表中删除的自动项（记住了，不再显示）。</summary>
    public bool Hidden { get; set; }
    public bool Exists { get; set; } = true;
    public bool IsCurrent { get; set; }
}

/// <summary>
/// Kontakt 候选列表存储：自动检测结果与用户手工增删改合并。
/// 用户对自动项执行的「删除」以 Hidden 标记持久化，避免下次检测又冒出来。
/// </summary>
public static class KontaktCandidateStore
{
    private const string MetaKey = "kontakt_candidates";
    private const string HiddenKey = "kontakt_candidates_hidden";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<KontaktCandidate> LoadStored(Database db)
    {
        string raw = db.GetMeta(MetaKey, "");
        if (string.IsNullOrWhiteSpace(raw)) return new List<KontaktCandidate>();
        try { return JsonSerializer.Deserialize<List<KontaktCandidate>>(raw, Json) ?? new(); }
        catch { return new List<KontaktCandidate>(); }
    }

    public static void SaveStored(Database db, List<KontaktCandidate> list)
        => db.SetMeta(MetaKey, JsonSerializer.Serialize(list, Json));

    public static HashSet<string> LoadHidden(Database db)
    {
        string raw = db.GetMeta(HiddenKey, "");
        if (string.IsNullOrWhiteSpace(raw)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(raw, Json) ?? new(), StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveHidden(Database db, HashSet<string> hidden)
        => db.SetMeta(HiddenKey, JsonSerializer.Serialize(hidden.OrderBy(x => x).ToList(), Json));

    /// <summary>合并「自动检测结果」与「用户存储项」，产出最终列表。</summary>
    public static List<KontaktCandidate> Merge(Database db, KontaktScanResult scan)
    {
        var hidden = LoadHidden(db);
        var stored = LoadStored(db);
        var result = new Dictionary<string, KontaktCandidate>(StringComparer.OrdinalIgnoreCase);
        string currentExe = db.GetMeta("kontakt_exe", "");

        // 1) 用户存储项优先（手工添加/编辑过的条目应保持 manual 身份，不被自动项覆盖）
        foreach (var s in stored)
        {
            if (string.IsNullOrWhiteSpace(s.Path)) continue;
            if (hidden.Contains(s.Path)) continue;

            bool exists = File.Exists(s.Path);
            string version = s.Version, product = s.ProductName, company = s.Company;
            double sizeMb = s.SizeMB;
            if (exists)
            {
                try
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(s.Path);
                    if (VersionUtil.IsValid(vi.FileVersion)) version = vi.FileVersion!;
                    if (!string.IsNullOrWhiteSpace(vi.ProductName)) product = vi.ProductName;
                    if (!string.IsNullOrWhiteSpace(vi.CompanyName)) company = vi.CompanyName;
                    sizeMb = Math.Round(new FileInfo(s.Path).Length / 1024.0 / 1024.0, 1);
                }
                catch { }
            }
            result[s.Path] = new KontaktCandidate
            {
                Path = s.Path,
                Version = version,
                ProductName = product,
                Company = company,
                Note = exists ? (s.Note.Length > 0 ? s.Note : "用户添加") : "文件已不存在",
                SizeMB = sizeMb,
                Source = "manual",
                Exists = exists,
            };
        }

        // 2) 自动检测项（已在列表中的不重复添加）
        foreach (var i in scan.Installs)
        {
            if (hidden.Contains(i.Path)) continue;
            if (result.ContainsKey(i.Path)) continue;
            result[i.Path] = new KontaktCandidate
            {
                Path = i.Path,
                Version = i.Version,
                ProductName = i.ProductName,
                Company = i.Company,
                Note = i.Note,
                SizeMB = i.SizeMB,
                Source = "auto",
                Exists = true,
            };
        }

        var list = result.Values
            .OrderByDescending(c => VersionUtil.ParseParts(c.Version).FirstOrDefault())
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var c in list)
            c.IsCurrent = c.Path.Equals(currentExe, StringComparison.OrdinalIgnoreCase);

        return list;
    }

    /// <summary>添加一个手工候选（若已存在则更新为 manual）。</summary>
    public static (bool ok, string message) Add(Database db, string path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) return (false, "路径为空");
        if (!File.Exists(path)) return (false, $"文件不存在：{path}");

        var stored = LoadStored(db);
        stored.RemoveAll(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

        var install = KontaktInfo.DescribeForUser(path);
        stored.Add(new KontaktCandidate
        {
            Path = path,
            Version = install.Version,
            ProductName = install.ProductName,
            Company = install.Company,
            Note = install.Note,
            SizeMB = install.SizeMB,
            Source = "manual",
        });
        SaveStored(db, stored);

        // 若该项曾在隐藏名单里，取消隐藏
        var hidden = LoadHidden(db);
        if (hidden.Remove(path)) SaveHidden(db, hidden);

        return (true, $"已添加：{path}" + (install.Version.Length > 0 ? $"（v{install.Version}）" : "（未能识别版本）"));
    }

    /// <summary>修改候选路径。</summary>
    public static (bool ok, string message) Update(Database db, string oldPath, string newPath)
    {
        oldPath = (oldPath ?? "").Trim().Trim('"');
        newPath = (newPath ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(newPath)) return (false, "新路径为空");
        if (!File.Exists(newPath)) return (false, $"文件不存在：{newPath}");

        var stored = LoadStored(db);

        // 「修改」= 替换：旧路径（若与新的不同）一律从列表消失，避免又冒出来
        if (!oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase) && oldPath.Length > 0)
        {
            var hidden = LoadHidden(db);
            hidden.Add(oldPath);
            SaveHidden(db, hidden);
        }

        stored.RemoveAll(s => s.Path.Equals(oldPath, StringComparison.OrdinalIgnoreCase)
                           || s.Path.Equals(newPath, StringComparison.OrdinalIgnoreCase));

        var info = KontaktInfo.DescribeForUser(newPath);
        stored.Add(new KontaktCandidate
        {
            Path = newPath,
            Version = info.Version,
            ProductName = info.ProductName,
            Company = info.Company,
            Note = "用户编辑",
            SizeMB = info.SizeMB,
            Source = "manual",
        });
        SaveStored(db, stored);

        // 若正在使用的就是旧路径，跟随更新
        if (db.GetMeta("kontakt_exe", "").Equals(oldPath, StringComparison.OrdinalIgnoreCase))
        {
            db.SetMeta("kontakt_exe", newPath);
            db.SetMeta("kontakt_version", info.Version);
        }

        return (true, $"已修改为：{newPath}");
    }

    /// <summary>从列表删除一个候选。</summary>
    public static (bool ok, string message) Remove(Database db, string path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) return (false, "路径为空");

        var stored = LoadStored(db);
        int removed = stored.RemoveAll(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        SaveStored(db, stored);

        // 自动检测出来的项靠隐藏名单压制
        var hidden = LoadHidden(db);
        hidden.Add(path);
        SaveHidden(db, hidden);

        bool wasCurrent = db.GetMeta("kontakt_exe", "").Equals(path, StringComparison.OrdinalIgnoreCase);
        if (wasCurrent)
        {
            db.SetMeta("kontakt_exe", "");
            db.SetMeta("kontakt_version", "");
        }

        return (true, removed > 0 ? "已从列表删除" : "已从列表移除（自动检测项已记住不再显示）"
            + (wasCurrent ? "，并已清除当前选择" : ""));
    }
}
