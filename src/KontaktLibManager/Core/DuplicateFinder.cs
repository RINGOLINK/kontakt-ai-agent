using System.Security.Cryptography;

namespace KontaktLibManager.Core;

/// <summary>一组内容完全相同的文件（分布在不同音色库中）。</summary>
public sealed class DuplicateGroup
{
    public long SizeBytes { get; set; }
    public string Hash { get; set; } = "";
    public List<(string LibraryName, string FullPath)> Files { get; set; } = new();
    /// <summary>除保留一份外，可回收的空间。</summary>
    public long ReclaimableBytes => SizeBytes * Math.Max(0, Files.Count - 1);
}

/// <summary>库间关系的三种常见情形（其余视为正常）。</summary>
public enum LibraryRelation
{
    /// <summary>两库内容几乎完全一致 —— 重复库，可直接清理。</summary>
    Identical,
    /// <summary>一方完整包含另一方（子集关系）—— 子集那份可以删。</summary>
    Superset,
    /// <summary>大量内容相同但不完全一致 —— 通常是同一库的不同版本/升级关系。</summary>
    Upgrade,
    /// <summary>只是部分重叠，属正常情况。</summary>
    Overlap,
}

/// <summary>两个音色库的关系判定与操作建议。</summary>
public sealed class LibrarySimilarity
{
    public long LibraryA { get; set; }
    public long LibraryB { get; set; }
    public string NameA { get; set; } = "";
    public string NameB { get; set; } = "";
    public int SharedInstruments { get; set; }
    public int TotalA { get; set; }
    public int TotalB { get; set; }
    public double Jaccard { get; set; }
    /// <summary>A 的内容有多少能在 B 中找到（0~1）。</summary>
    public double CoverA { get; set; }
    /// <summary>B 的内容有多少能在 A 中找到（0~1）。</summary>
    public double CoverB { get; set; }
    public long SizeA { get; set; }
    public long SizeB { get; set; }
    public string VersionA { get; set; } = "";
    public string VersionB { get; set; } = "";

    public LibraryRelation Relation { get; set; }
    /// <summary>关系的中文标签，如「完全重复」「A 完整包含 B」「版本升级」。A/B 用真实库名替换。</summary>
    public string RelationLabel { get; set; } = "";
    /// <summary>建议动作（面向用户的一句话）。</summary>
    public string Recommendation { get; set; } = "";
    /// <summary>建议保留的库名（空表示两者都保留）。</summary>
    public string KeepName { get; set; } = "";
    /// <summary>建议删除的库名（空表示无需删除）。</summary>
    public string RemoveName { get; set; } = "";
    /// <summary>可回收空间（仅当建议删除某库时给出）。</summary>
    public long ReclaimableBytes { get; set; }

    
// ── 路径与注册状态（用户要求：必须明确列出两库各自路径、以及注册表当前指向哪个）──
    
/// <summary>A 库在磁盘上的完整路径。</summary>
    
public string PathA { get; set; } = "";
    
/// <summary>B 库在磁盘上的完整路径。</summary>
    
public string PathB { get; set; } = "";
    
/// <summary>A 库是否就是**注册表当前指向的那个**（RegStatus == registered）。</summary>
    
public bool RegA { get; set; }
    
/// <summary>B 库是否就是**注册表当前指向的那个**。</summary>
    
public bool RegB { get; set; }
    
/// <summary>注册表里记录的路径（两库都不在其中则为空）。</summary>
    
public string RegPath { get; set; } = "";
    
/// <summary>注册表键名（用于转移注册信息）。</summary>
    
public string RegKey { get; set; } = "";
    
/// <summary>建议保留的库 id。</summary>
    
public long KeepId { get; set; }
    
/// <summary>建议保留的库路径。</summary>
    
public string KeepPath { get; set; } = "";
    
/// <summary>建议删除的库 id（0 表示不建议删除，如仅版本升级关系）。</summary>
    
public long DeleteId { get; set; }
    
/// <summary>建议删除的库路径。</summary>
    
public string DeletePath { get; set; } = "";
    
/// <summary>删除前是否必须先转移注册信息（要删的正好是注册表指向的那个）。</summary>
    
public bool NeedTransferFirst { get; set; }
}

public sealed class DupProgress
{
    public string Phase { get; set; } = "";
    public int Current { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 重复内容检测与库关系判定。
///
/// ① **跨库重复大文件**：按「大小分组 → 内容指纹比对」找出内容完全相同的文件。
/// ② **库关系判定**：按 NKI 名称集合的重叠度，把两库关系归入三种常见情形并给出操作建议：
///      · 完全重复（Jaccard 高 + 体积相近）      → 强烈建议删除其中一个
///      · 一方完整包含另一方（覆盖率≈1 且更小）   → 建议删除被包含的那份
///      · 大量重叠但不完整（覆盖率中高）          → 通常是版本升级，建议保留较新/较大者
///      · 仅部分重叠                              → 正常，不做建议
/// </summary>
public static class DuplicateFinder
{
    /// <summary>只检测大于此大小的文件（小文件收益低、数量大）。</summary>
    public const long DefaultMinBytes = 5L * 1024 * 1024;

    /// <summary>判定「完整包含」的内容覆盖率门槛。</summary>
    private const double SupersetCover = 0.97;
    /// <summary>判定「完全重复」的 Jaccard 门槛。</summary>
    private const double IdenticalJaccard = 0.95;
    /// <summary>体积相近的容差（5%）。</summary>
    private const double SizeSimilarTolerance = 0.05;

    // ────────────────────────── ① 重复大文件 ──────────────────────────

    public static List<DuplicateGroup> FindDuplicateFiles(
        Database db, long minBytes, IProgress<DupProgress>? progress, CancellationToken ct)
    {
        var result = new List<DuplicateGroup>();
        var libs = db.GetLibraries();
        var bySize = new Dictionary<long, List<(string Lib, string Path)>>();

        progress?.Report(new DupProgress { Phase = "枚举文件", Message = "正在统计大于阈值的文件…" });
        int scanned = 0;
        foreach (var lib in libs)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(lib.Path)) continue;
            try
            {
                foreach (var f in new DirectoryInfo(lib.Path).EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                }))
                {
                    ct.ThrowIfCancellationRequested();
                    if (f.Length < minBytes) continue;
                    if (!bySize.TryGetValue(f.Length, out var list))
                    {
                        list = new List<(string, string)>();
                        bySize[f.Length] = list;
                    }
                    list.Add((lib.Name, f.FullName));
                    scanned++;
                    if (scanned % 200 == 0)
                        progress?.Report(new DupProgress
                        {
                            Phase = "枚举文件", Current = scanned,
                            Message = $"已统计 {scanned:N0} 个大于 {minBytes / 1024 / 1024} MB 的文件",
                        });
                }
            }
            catch { }
        }

        var candidates = bySize.Where(kv => kv.Value.Count >= 2).ToList();
        int totalCand = candidates.Sum(c => c.Value.Count);
        progress?.Report(new DupProgress
        {
            Phase = "比对内容", Total = totalCand,
            Message = $"{candidates.Count} 个尺寸存在重复候选，共 {totalCand:N0} 个文件",
        });

        int done = 0;
        foreach (var (size, files) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var byHash = new Dictionary<string, List<(string Lib, string Path)>>();
            foreach (var (lib, path) in files)
            {
                ct.ThrowIfCancellationRequested();
                string? h = QuickHash(path);
                done++;
                if (h == null) continue;
                if (!byHash.TryGetValue(h, out var l)) { l = new List<(string, string)>(); byHash[h] = l; }
                l.Add((lib, path));
                if (done % 20 == 0)
                    progress?.Report(new DupProgress
                    {
                        Phase = "比对内容", Current = done, Total = totalCand,
                        Message = $"已比对 {done:N0}/{totalCand:N0}",
                    });
            }
            foreach (var (hash, list) in byHash)
            {
                if (list.Count < 2) continue;
                if (list.Select(x => x.Lib).Distinct().Count() < 2) continue;
                result.Add(new DuplicateGroup { SizeBytes = size, Hash = hash, Files = list });
            }
        }

        return result.OrderByDescending(g => g.ReclaimableBytes).ToList();
    }

    private static string? QuickHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            const int head = 4 * 1024 * 1024;
            const int tail = 1024 * 1024;

            var buf = new byte[head];
            int n = fs.Read(buf, 0, head);
            sha.TransformBlock(buf, 0, n, null, 0);

            if (fs.Length > head + tail)
            {
                fs.Seek(-tail, SeekOrigin.End);
                var tb = new byte[tail];
                int tn = fs.Read(tb, 0, tail);
                sha.TransformBlock(tb, 0, tn, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch { return null; }
    }

    // ────────────────────────── ② 库关系判定 ──────────────────────────

    public static List<LibrarySimilarity> FindSimilarLibraries(Database db, double minJaccard = 0.35)
    {
        var libs = db.GetLibraries();
            // **必须补注册信息**：reg_status / reg_content_dir 这两个字段**不在数据库里**，
            // 而是由 LibraryRegistrar.Enrich 在运行时根据注册表算出来的。
            // 之前这里直接用 db.GetLibraries() 的结果，导致 RegStatus 恒为空、
            // 界面永远显示「两库都未注册」（用户实测发现）。
            try
            {
                LibraryRegistrar.Enrich(libs, LibraryRegistrar.ReadRegistered(), "");
            }
            catch (Exception ex)
            {
                // 读注册表失败不应让整个比对崩掉，只是注册标记会不准
                Console.Error.WriteLine("[重复检测] 读取注册表失败：" + ex.Message);
            }
        var sets = new Dictionary<long, HashSet<string>>();
        foreach (var l in libs)
        {
            var (items, _) = db.SearchInstruments(libraryId: l.Id, kind: "nki", limit: 5000, offset: 0);
            sets[l.Id] = new HashSet<string>(items.Select(i => i.Name.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        }

        var list = new List<LibrarySimilarity>();
        var arr = libs.Where(l => sets[l.Id].Count > 0).ToList();
        for (int i = 0; i < arr.Count; i++)
        {
            for (int j = i + 1; j < arr.Count; j++)
            {
                var a = sets[arr[i].Id];
                var b = sets[arr[j].Id];
                int inter = a.Count(x => b.Contains(x));
                if (inter == 0) continue;
                int union = a.Count + b.Count - inter;
                double jac = union > 0 ? (double)inter / union : 0;
                if (jac < minJaccard) continue;

                var s = new LibrarySimilarity
                {
                    LibraryA = arr[i].Id, LibraryB = arr[j].Id,
                    NameA = arr[i].Name, NameB = arr[j].Name,
                    SharedInstruments = inter,
                    TotalA = a.Count, TotalB = b.Count,
                    Jaccard = Math.Round(jac, 3),
                    CoverA = a.Count > 0 ? Math.Round((double)inter / a.Count, 3) : 0,
                    CoverB = b.Count > 0 ? Math.Round((double)inter / b.Count, 3) : 0,
                    SizeA = arr[i].SizeBytes, SizeB = arr[j].SizeBytes,
                    VersionA = arr[i].RequiredKontakt, VersionB = arr[j].RequiredKontakt,
                };
                Classify(s, arr[i], arr[j]);
                list.Add(s);
            }
        }
        return list.OrderByDescending(s => (int)s.Relation).ThenByDescending(s => s.Jaccard).ToList();
    }

    private static void Classify(LibrarySimilarity s, LibraryRecord? libA, LibraryRecord? libB)
    {
        ClassifyCore(s, libA, libB);
        FillTargets(s, libA, libB);
    }
    /// <summary>把两库关系归入三种常见情形并生成建议。</summary>
    private static void ClassifyCore(LibrarySimilarity s, LibraryRecord? libA, LibraryRecord? libB)
    {
        double sizeMax = Math.Max(s.SizeA, s.SizeB);
        bool sizeClose = sizeMax > 0 && Math.Abs(s.SizeA - s.SizeB) / (double)sizeMax <= SizeSimilarTolerance;

        // ③ 完全重复：名称几乎全同 + 体积相近
        if (s.Jaccard >= IdenticalJaccard && sizeClose)
        {
            s.Relation = LibraryRelation.Identical;
            s.RelationLabel = "完全重复";
            // **保留策略**（用户拍板）：
            //   ① 若其中一个是注册表当前指向的 → **保留它**（删除另一个就无需动注册表，最省事）；
            //   ② 否则保留体积较大（信息更全）或版本较新的一方。
            bool keepA;
            if (s.RegA != s.RegB) keepA = s.RegA;   // 有且仅有一个是注册的 → 留它
            else keepA = IsNewer(s.VersionA, s.VersionB) || (s.VersionA == s.VersionB && s.SizeA >= s.SizeB);
            s.KeepName = keepA ? s.NameA : s.NameB;
            s.KeepId = keepA ? s.LibraryA : s.LibraryB;   // 同名库靠名字区分不了，必须记 id
            s.RemoveName = keepA ? s.NameB : s.NameA;
            s.ReclaimableBytes = keepA ? s.SizeB : s.SizeA;
            s.Recommendation =
                $"两个库内容几乎完全一致（乐器名重合 {s.Jaccard:P0}，体积相差 {Math.Abs(s.SizeA - s.SizeB) / 1048576.0:F0} MB）。" +
                $"**强烈建议删除「{s.RemoveName}」**，只保留「{s.KeepName}」，可回收约 {s.ReclaimableBytes / 1073741824.0:F1} GB。";
            return;
        }

        // ① 完整包含：一方的乐器几乎都能在另一方找到，且被包含方更小
        if (s.CoverB >= SupersetCover && s.TotalB < s.TotalA)
        {
            s.Relation = LibraryRelation.Superset;
            s.RelationLabel = $"「{s.NameA}」完整包含「{s.NameB}」";
            s.KeepName = s.NameA;
            s.KeepId = s.LibraryA;
            s.RemoveName = s.NameB;
            s.ReclaimableBytes = s.SizeB;
            s.Recommendation =
                $"「{s.NameB}」的乐器 {s.CoverB:P0} 都能在「{s.NameA}」里找到（且后者更全）。" +
                $"**建议删除「{s.NameB}」**，统一使用「{s.NameA}」，可回收约 {s.ReclaimableBytes / 1073741824.0:F1} GB。";
            return;
        }
        if (s.CoverA >= SupersetCover && s.TotalA < s.TotalB)
        {
            s.Relation = LibraryRelation.Superset;
            s.RelationLabel = $"「{s.NameB}」完整包含「{s.NameA}」";
            s.KeepName = s.NameB;
            s.KeepId = s.LibraryB;
            s.RemoveName = s.NameA;
            s.ReclaimableBytes = s.SizeA;
            s.Recommendation =
                $"「{s.NameA}」的乐器 {s.CoverA:P0} 都能在「{s.NameB}」里找到（且后者更全）。" +
                $"**建议删除「{s.NameA}」**，统一使用「{s.NameB}」，可回收约 {s.ReclaimableBytes / 1073741824.0:F1} GB。";
            return;
        }

        // ② 版本升级：大量重叠但不完整 —— 保留较新/较大者
        bool newerA = IsNewer(s.VersionA, s.VersionB);
        bool aPreferred = newerA || (s.VersionA == s.VersionB && s.SizeA >= s.SizeB);
        s.Relation = LibraryRelation.Upgrade;
        s.RelationLabel = "疑似版本升级关系";
        s.KeepName = aPreferred ? s.NameA : s.NameB;
        s.KeepId = aPreferred ? s.LibraryA : s.LibraryB;
        s.RemoveName = "";
        string verA = s.VersionA.Length > 0 ? s.VersionA : "未知";
        string verB = s.VersionB.Length > 0 ? s.VersionB : "未知";
        s.Recommendation =
            $"两库有 {s.SharedInstruments} 个同名乐器（约占「{s.NameA}」的 {s.CoverA:P0}、「{s.NameB}」的 {s.CoverB:P0}），" +
            $"但内容并不完全一致 —— 很可能是**同一音色库的不同版本/升级关系**（版本要求：A={verA}，B={verB}）。" +
            $"建议**优先使用较新的「{s.KeepName}」**；确认不需要旧版后再手动清理（工具不会自动删除）。";
    }

    /// <summary>
    /// 回填：两库路径、注册状态、建议删除目标、是否需要先转注册。
    /// 放在 Classify 末尾统一做，避免每个分支各写一遍。
    /// </summary>
    private static void FillTargets(LibrarySimilarity s, LibraryRecord? libA, LibraryRecord? libB)
    {
        s.PathA = libA?.Path ?? "";
        s.PathB = libB?.Path ?? "";
        s.RegA = libA != null && libA.RegStatus == "registered";
        s.RegB = libB != null && libB.RegStatus == "registered";
        s.RegPath = s.RegA ? (libA?.RegContentDir ?? "") : s.RegB ? (libB?.RegContentDir ?? "") : "";
        s.RegKey = s.RegA ? (libA?.ProductKey ?? "") : s.RegB ? (libB?.ProductKey ?? "") : "";

        // **按 id 判断，不能按名字** —— 同名库（如两个 "Ethno World 6 Instruments"）靠名字区分不了，
        // 会导致「保留注册的那个」规则失效（实测踩到）。
        bool keepIsA = s.KeepId != 0 ? s.KeepId == s.LibraryA : (s.KeepName.Length > 0 && s.KeepName == s.NameA);
        if (s.RemoveName.Length == 0)
        {
            // 不建议删除（版本升级关系）
            s.KeepId = keepIsA ? s.LibraryA : s.LibraryB;
            s.KeepPath = keepIsA ? s.PathA : s.PathB;
            s.DeleteId = 0;
            s.DeletePath = "";
            s.NeedTransferFirst = false;
            return;
        }

        bool removeIsA = !keepIsA;   // 与 keepIsA 互补，不依赖名字
        s.KeepId = removeIsA ? s.LibraryB : s.LibraryA;
        s.KeepPath = removeIsA ? s.PathB : s.PathA;
        s.DeleteId = removeIsA ? s.LibraryA : s.LibraryB;
        s.DeletePath = removeIsA ? s.PathA : s.PathB;
        // 要删的那个正好是注册表指向的 → 必须先转注册
        s.NeedTransferFirst = removeIsA ? s.RegA : s.RegB;
    }
    /// <summary>判断版本号 a 是否比 b 新（空视为未知，不参与比较）。</summary>
    private static bool IsNewer(string a, string b)
    {
        if (!VersionUtil.IsValid(a) || !VersionUtil.IsValid(b)) return false;
        return VersionUtil.Compare(a, b) > 0;
    }
}
