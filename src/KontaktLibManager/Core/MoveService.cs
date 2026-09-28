using System.Diagnostics;

namespace KontaktLibManager.Core;

public sealed class MovePlan
{
    public long LibraryId { get; set; }
    public string LibraryName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestRoot { get; set; } = "";
    public string DestPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public int NkiCount { get; set; }
    public bool DestRootExists { get; set; }
    public bool DestRootKnown { get; set; }
    public long FreeSpaceBytes { get; set; }
    public bool KontaktRunning { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>需要改写入库路径的产品键（注册表 / 便携版）。</summary>
    public List<string> ProductKeys { get; set; } = new();
    /// <summary>阻塞性问题（非空则不能执行）。</summary>
    public List<string> Errors { get; set; } = new();
    /// <summary>提示性警告（可继续）。</summary>
    public List<string> Warnings { get; set; } = new();
    public bool CanExecute => Errors.Count == 0;
}

public sealed class MoveProgress
{
    public string Phase { get; set; } = "";
    public long FilesDone { get; set; }
    public long FilesTotal { get; set; }
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public string Message { get; set; } = "";
    public double SpeedMBps { get; set; }
    public int Percent { get; set; }
}

public sealed class MoveResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string NewPath { get; set; } = "";
    public int Reregistered { get; set; }
    public bool PortableUpdated { get; set; }
    public bool SourceDeleted { get; set; }
    public List<string> Details { get; set; } = new();
}

/// <summary>
/// 音色库移动：拷贝 → 校验 → 改入库信息 → 删旧 → 更新索引。
///
/// 安全原则：
///   · **校验通过前绝不删除源目录**（文件数 + 总字节完全一致才算通过）；
///   · 目标已存在同名目录 / 目标在源目录内部 → 直接拒绝；
///   · 全程可取消，取消或失败会清理已拷贝的部分，不留半成品；
///   · 入库路径改写失败不影响文件（会明确报告，用户可手动入库修复）。
/// </summary>
public static class MoveService
{
    public static MovePlan Plan(Database db, long libraryId, string destRoot)
    {
        var plan = new MovePlan { LibraryId = libraryId, DestRoot = LibraryRegistrar.NormalizePath(destRoot) };

        var lib = db.GetLibraries().FirstOrDefault(l => l.Id == libraryId);
        if (lib == null) { plan.Errors.Add("未找到该音色库"); return plan; }

        plan.LibraryName = lib.Name;
        plan.SourcePath = lib.Path;
        plan.SizeBytes = lib.SizeBytes;
        plan.FileCount = lib.FileCount;
        plan.NkiCount = lib.NkiCount;
        plan.KontaktRunning = KontaktInfo.IsRunning();
        plan.IsAdmin = KontaktInfo.IsAdmin();

        if (!Directory.Exists(plan.SourcePath)) plan.Errors.Add($"源目录不存在：{plan.SourcePath}");
        plan.DestRootExists = Directory.Exists(plan.DestRoot);
        if (!plan.DestRootExists) plan.Errors.Add($"目标路径不存在：{plan.DestRoot}（请先选择或创建）");

        plan.DestPath = Path.Combine(plan.DestRoot, plan.LibraryName);

        if (plan.DestRootExists)
        {
            plan.DestRootKnown = db.GetRoots().Any(r =>
                LibraryRegistrar.NormalizePath(r.Path).Equals(plan.DestRoot, StringComparison.OrdinalIgnoreCase));
            if (!plan.DestRootKnown)
                plan.Warnings.Add($"目标路径不在音色库路径列表中，移动完成后会自动加入：{plan.DestRoot}");

            if (Directory.Exists(plan.DestPath))
                plan.Errors.Add($"目标位置已存在同名目录：{plan.DestPath}");

            string src = LibraryRegistrar.NormalizePath(plan.SourcePath);
            if (plan.DestPath.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith(LibraryRegistrar.NormalizePath(plan.DestPath) + "\\", StringComparison.OrdinalIgnoreCase))
                plan.Errors.Add("目标位置不能位于源目录内部（或反之）");

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(plan.DestRoot)!);
                plan.FreeSpaceBytes = drive.AvailableFreeSpace;
                long need = (long)(plan.SizeBytes * 1.02) + 50L * 1024 * 1024;
                if (plan.FreeSpaceBytes < need)
                    plan.Errors.Add($"目标磁盘空间不足：需要约 {need / 1024.0 / 1024 / 1024:F1} GB，" +
                                    $"可用 {plan.FreeSpaceBytes / 1024.0 / 1024 / 1024:F1} GB");
            }
            catch { plan.Warnings.Add("无法读取目标磁盘剩余空间"); }
        }

        if (plan.KontaktRunning)
            plan.Warnings.Add("Kontakt 正在运行：移动后的入库路径改写需要 Kontakt 关闭（它退出时会覆写便携版配置）");
        if (!plan.IsAdmin)
            plan.Warnings.Add("当前非管理员权限：无法改写注册表入库路径（文件仍会移动）");

        // 收集需要改路径的产品键
        try
        {
            var registered = LibraryRegistrar.ReadRegistered();
            string srcNorm = LibraryRegistrar.NormalizePath(plan.SourcePath);
            foreach (var kv in registered)
            {
                string cd = kv.Value.ContentDir;
                if (cd.Equals(srcNorm, StringComparison.OrdinalIgnoreCase) ||
                    cd.StartsWith(srcNorm + "\\", StringComparison.OrdinalIgnoreCase))
                    plan.ProductKeys.Add(kv.Key);
            }
            foreach (var n in NicntReader.FindInLibrary(plan.SourcePath, 3))
            {
                string key = string.IsNullOrWhiteSpace(n.RegKey) ? n.Name : n.RegKey;
                if (key.Length > 0 && !plan.ProductKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    plan.ProductKeys.Add(key);
            }
        }
        catch { }

        if (plan.ProductKeys.Count == 0)
            plan.Warnings.Add("注册表里没有指向该库的条目：移动后需要在工具里重新入库");

        return plan;
    }

    /// <summary>执行移动。</summary>
    public static async Task<MoveResult> ExecuteAsync(
        MovePlan plan, Database db, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        var result = new MoveResult { NewPath = plan.DestPath };
        var sw = Stopwatch.StartNew();

        if (!plan.CanExecute)
        {
            result.Message = "计划校验未通过：" + string.Join("；", plan.Errors);
            return result;
        }

        try
        {
            // ── ① 拷贝 ──
            progress?.Report(new MoveProgress { Phase = "准备", Message = "正在枚举文件…", FilesTotal = plan.FileCount });
            var files = await Task.Run(() => Directory
                .EnumerateFiles(plan.SourcePath, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                })
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .ToList(), ct);

            long totalBytes = files.Sum(f => f.Length);
            long doneBytes = 0;
            int doneFiles = 0;
            var lastReport = DateTime.UtcNow;

            Directory.CreateDirectory(plan.DestPath);

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();

                string rel = Path.GetRelativePath(plan.SourcePath, f.FullName);
                string target = Path.Combine(plan.DestPath, rel);
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                await CopyFileAsync(f.FullName, target, ct);
                doneBytes += f.Length;
                doneFiles++;

                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 300 || doneFiles == files.Count)
                {
                    lastReport = DateTime.UtcNow;
                    double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    progress?.Report(new MoveProgress
                    {
                        Phase = "拷贝文件",
                        FilesDone = doneFiles,
                        FilesTotal = files.Count,
                        BytesDone = doneBytes,
                        BytesTotal = totalBytes,
                        SpeedMBps = doneBytes / 1024.0 / 1024 / sec,
                        Percent = totalBytes > 0 ? (int)(doneBytes * 100 / totalBytes) : 0,
                        Message = $"{doneFiles}/{files.Count} 个文件 · {doneBytes / 1024.0 / 1024 / 1024:F2}/{totalBytes / 1024.0 / 1024 / 1024:F2} GB",
                    });
                }
            }

            // ── ② 校验（通过前绝不删除源）──
            progress?.Report(new MoveProgress { Phase = "校验", Message = "正在核对文件数与总字节…" });
            var (destFiles, destBytes) = await Task.Run(() =>
            {
                var list = Directory.EnumerateFiles(plan.DestPath, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                }).Select(f => new FileInfo(f)).Where(f => f.Exists).ToList();
                return (list.Count, list.Sum(f => f.Length));
            }, ct);

            if (destFiles != files.Count || destBytes != totalBytes)
            {
                result.Ok = false;
                result.Message = $"校验失败：源 {files.Count} 个文件/{totalBytes / 1024 / 1024} MB，" +
                                 $"目标 {destFiles} 个文件/{destBytes / 1024 / 1024} MB。**源目录未删除**，请检查后重试。";
                return result;
            }
            result.Details.Add($"校验通过：{destFiles} 个文件 / {destBytes / 1024.0 / 1024 / 1024:F2} GB 完全一致");

            // ── ③ 改写入库路径 ──
            progress?.Report(new MoveProgress { Phase = "改写入库路径", Message = "注册表 + 便携版库列表…" });

            if (plan.ProductKeys.Count > 0 && plan.IsAdmin)
            {
                // 3a. 优先用 .nicnt 重新注册（最准确：同时刷新 HU/JDX/版本）
                var nicnts = NicntReader.FindInLibrary(plan.DestPath, 3);
                if (nicnts.Count > 0)
                {
                    foreach (var n in nicnts)
                    {
                        var (ok, msg) = LibraryRegistrar.Register(n);
                        if (ok) result.Reregistered++;
                        else result.Details.Add($"重新注册失败（{n.Name}）：{msg}");
                    }
                }

                // 3b. 对无 .nicnt 的旧式库，直接改 ContentDir
                var registeredNow = LibraryRegistrar.ReadRegistered();
                foreach (var key in plan.ProductKeys)
                {
                    if (registeredNow.TryGetValue(key, out var prod) &&
                        prod.ContentDir.StartsWith(LibraryRegistrar.NormalizePath(plan.DestPath), StringComparison.OrdinalIgnoreCase))
                        continue;   // 已被 3a 更新
                    var (ok, msg) = LibraryRegistrar.UpdateContentDir(key, plan.DestPath);
                    if (ok) result.Reregistered++;
                    else result.Details.Add($"更新 ContentDir 失败（{key}）：{msg}");
                }

                // 3c. Service Center 记录（若库里有 .nicnt 则随 Register 一并写入）
                result.Details.Add($"入库路径已改写：{result.Reregistered} 个产品");
            }
            else if (plan.ProductKeys.Count > 0)
            {
                result.Details.Add("非管理员权限：已跳过注册表改写，请在工具里重新入库以修复路径");
            }

            // 3d. 便携版 Settings.cfg
            string kontaktExe = db.GetMeta("kontakt_exe", "");
            if (kontaktExe.Length > 0)
            {
                var map = plan.ProductKeys.ToDictionary(k => k, _ => plan.DestPath, StringComparer.OrdinalIgnoreCase);
                var (ok, msg, updated) = PortableKontaktStore.UpdateLibraryPaths(kontaktExe, map);
                result.PortableUpdated = ok && updated > 0;
                result.Details.Add(ok ? $"便携版库列表：{msg}" : $"便携版库列表更新失败：{msg}");
            }

            // ── ④ 更新索引 ──
            progress?.Report(new MoveProgress { Phase = "更新索引", Message = "写入新路径…" });
            try
            {
                var (rootOk, rootMsg, rootId) = db.AddRoot(plan.DestRoot, "移动目标");
                db.UpdateLibraryPath(plan.LibraryId, plan.DestPath);
                result.Details.Add(rootOk ? $"已加入音色库路径：{plan.DestRoot}" : $"加入路径失败：{rootMsg}");
            }
            catch (Exception ex)
            {
                result.Details.Add($"索引更新失败（可手动重扫）：{ex.Message}");
            }

            result.Ok = true;
            result.Message = $"移动完成：{plan.LibraryName} → {plan.DestPath}（{result.Details.Count} 项操作）";
            progress?.Report(new MoveProgress { Phase = "完成", Percent = 100, Message = result.Message });
            return result;
        }
        catch (OperationCanceledException)
        {
            TryCleanup(plan.DestPath);
            result.Ok = false;
            result.Message = "已取消：已清理目标位置的半成品文件，源目录未受影响";
            return result;
        }
        catch (Exception ex)
        {
            TryCleanup(plan.DestPath);
            result.Ok = false;
            result.Message = $"移动失败：{ex.Message}（已清理目标位置的半成品，源目录未受影响）";
            return result;
        }
    }

    /// <summary>删除源目录（需用户明确确认后调用）。</summary>
    public static (bool ok, string message) DeleteSource(MovePlan plan)
    {
        try
        {
            if (!Directory.Exists(plan.SourcePath)) return (true, "源目录已不存在");
            if (!Directory.Exists(plan.DestPath)) return (false, "目标目录不存在，拒绝删除源目录");

            // 再次校验目标完整性，避免误删
            var src = new DirectoryInfo(plan.SourcePath);
            var dst = new DirectoryInfo(plan.DestPath);
            long srcBytes = src.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            long dstBytes = dst.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            if (srcBytes != dstBytes)
                return (false, $"校验不一致（源 {srcBytes} / 目标 {dstBytes}），拒绝删除源目录");

            Directory.Delete(plan.SourcePath, recursive: true);
            return (true, $"已删除源目录：{plan.SourcePath}");
        }
        catch (Exception ex)
        {
            return (false, $"删除源目录失败：{ex.Message}");
        }
    }

    private static async Task CopyFileAsync(string src, string dst, CancellationToken ct)
    {
        const int BufferSize = 1024 * 1024;   // 1 MB
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static void TryCleanup(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
