using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace KontaktLibManager.Core;

/// <summary>
/// 音色库扫描器：多根目录 → 枚举统计 → 提取 NKI 名称与引擎版本 → 采集 .nicnt 产品键 → 识别杂质。
/// 性能基线：单根 62 库 / 162,442 文件 / 1.67 TB 含名称提取约 0.4–4 秒。
/// </summary>
public sealed class LibraryScanner
{
    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".torrent", ".url", ".rar", ".zip", ".7z", ".exe", ".dmg", ".iso", ".nfo"
    };

    /// <summary>库自带资产，默认不勾选清理。</summary>
    private static readonly HashSet<string> KeepSuggestionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Logic_Icons.zip",
        "DelayComp-Win64-1.2.0.zip",
    };

    /// <summary>说明书类文档扩展名。</summary>
    private static readonly HashSet<string> ManualExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".rtf", ".chm", ".epub", ".docx", ".txt", ".html", ".htm"
    };

    /// <summary>
    /// **明确的「不是说明书」的文件名关键词** —— 机械过滤第一层。
    ///
    /// 为什么需要：原来只按扩展名收（`.txt/.rtf/.pdf` 全收），实测一个库里
    /// `license.rtf`（授权书）、`readme.txt`、`changelog` 都会被当成说明书，
    /// 建知识库时白白烧 token 且污染检索结果。
    /// </summary>
    private static readonly string[] NonManualKeywords =
    {
        "license", "licence", "eula", "授权", "许可",
        "readme", "changelog", "changes", "release-note", "releasenotes", "release_note",
        "install", "setup", "uninstall", "注册", "安装说明",
        "credits", "thanks", "acknowledg", "copyright",
        "version", "history", "known-issue", "knownissue", "faq",
        "quick-start", "quickstart",   // 快速入门偏简略，正文手册更权威；如误杀可在下方白名单补救
        // ── 用户实测补充的规则 ──
        "thirdpartycontent", "third-party-content", "third_party_content",   // 第三方内容声明
        "licensing", "licence-agreement", "license-agreement",              // 许可协议
        "agreement", "terms", "notice", "disclaimer",                       // 协议/声明类
        "screenshot", "presskit", "press-kit",                              // 宣传素材
    };

    /// <summary>
    /// **明确是说明书的文件名关键词** —— 命中则**直接放行**（即使它同时含上面的词）。
    /// 例：`Kontakt 8 Manual.pdf`、`Damage 2 User Guide.pdf`。
    /// </summary>
    private static readonly string[] ManualKeywords =
    {
        "manual", "handbook", "user-guide", "userguide", "user guide", "guide",
        "说明书", "手册", "使用说明", "操作手册", "documentation", "reference",
    };

    /// <summary>
    /// 机械判断「这份文档像不像说明书」。
    /// 规则（先白后黑，避免误杀）：
    ///   ① 文件名命中 <see cref="ManualKeywords"/> → **是**；
    ///   ② 文件名命中 <see cref="NonManualKeywords"/> → **不是**；
    ///   ③ 都没有 → 按大小兜底：**小于 8 KB 的多半是 readme/说明片段**，判为不是；
    ///      否则判为是（宁可多收，用户还能在界面上手动建库）。
    /// </summary>
    /// <summary>小于此体积的文档基本不可能是正文手册（用户实测：50 KB 以下多为 readme/残留）。</summary>
    public const long MinManualBytes = 50 * 1024;
    public static bool LooksLikeManual(string fileName, long sizeBytes)
    {
        string raw = fileName ?? "";
        string n = raw.ToLowerInvariant();
        // ① **系统残留快照**：macOS 的 AppleDouble 副档（`._xxx.pdf`）以及 .DS_Store 之类，
        //    体积极小、内容是元数据而非文档 —— 一律排除。
        if (raw.StartsWith("._", StringComparison.Ordinal)) return false;
        if (n is ".ds_store" or "thumbs.db" or "desktop.ini") return false;
        // ② 白名单优先（文件名明确说是手册的，即使体积小也放行）
        if (ManualKeywords.Any(k => n.Contains(k))) return true;
        // ③ 黑名单（第三方内容声明 / 许可协议 / readme / changelog …）
        if (NonManualKeywords.Any(k => n.Contains(k))) return false;
        // ④ 体积兜底：用户实测「小于 50 KB 大概率不是说明书」
        return sizeBytes >= MinManualBytes;
    }

    /// <summary>
    /// **定向重扫某个库的说明书清单**（只找文档、不重扫全部文件，秒级完成）。
    ///
    /// 用途：机械过滤规则（<see cref="LooksLikeManual"/>）上线后，**已有的清单是用旧规则收的**
    ///（只按扩展名，`license.rtf`/`readme.txt` 全在里面），需要按新规则重建。
    /// 走完整扫描要遍历 60 多万文件，太重；这里只找文档扩展名再过滤。
    /// </summary>
    public static List<ManualRecord> RescanManuals(string libPath, string libName)
    {
        var list = new List<ManualRecord>();
        if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath)) return list;
        try
        {
            foreach (var f in new DirectoryInfo(libPath).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (list.Count >= MaxManualsPerLibrary) break;
                string ext = f.Extension;
                if (!ManualExtensions.Contains(ext)) continue;
                if (!LooksLikeManual(f.Name, f.Length)) continue;
                string rel = Path.GetRelativePath(libPath, f.FullName);
                list.Add(new ManualRecord
                {
                    LibraryName = libName,
                    LibraryPath = libPath,
                    RelPath = rel,
                    Name = f.Name,
                    Ext = ext.TrimStart('.').ToLowerInvariant(),
                    SizeBytes = f.Length,
                    IsPrimary = IsPrimaryManual(rel, f.Name),
                });
            }
        }
        catch { }
        // 主手册排前面，其余按大小降序（大文档更像正文手册）
        return list.OrderByDescending(m => m.IsPrimary).ThenByDescending(m => m.SizeBytes).ToList();
    }

    private const int MaxManualsPerLibrary = 40;
    /// <summary>每库最多采集多少条可试听音频（演示音频优先，其余随机取）。</summary>
    private const int MaxAudioPerLibrary = 160;
    /// <summary>每库最多采集多少条 .ncw 采样（解码试听用）。</summary>
    private const int MaxNcwPerLibrary = 240;
    /// <summary>可直接播放的音频扩展名（.ncw/.nkr 为 NI 专有格式，无法解码）。</summary>
    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".aif", ".aiff", ".ogg", ".mp3", ".flac", ".m4a", ".ncw", ".nkx" };

    private static readonly EnumerationOptions EnumOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public async Task<ScanResult> ScanAsync(IReadOnlyList<LibraryRoot> roots, IProgress<ScanProgress>? progress, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult
        {
            Root = string.Join("; ", roots.Select(r => r.Path)),
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        // 展开为「根 → 库目录」列表
        var jobs = new List<(LibraryRoot Root, string LibPath)>();
        var rootRecords = new List<LibraryRoot>();

        // 扫描开始时读一次注册表：用于为无 .nicnt 的旧式库补齐产品键
        Dictionary<string, RegisteredProduct> registeredProducts;
        try { registeredProducts = LibraryRegistrar.ReadRegistered(); }
        catch { registeredProducts = new Dictionary<string, RegisteredProduct>(StringComparer.OrdinalIgnoreCase); }

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var rec = new LibraryRoot
            {
                Id = root.Id,
                Path = root.Path,
                Label = root.Label,
                Enabled = root.Enabled,
                AddedAt = root.AddedAt,
                Exists = Directory.Exists(root.Path),
                LastScannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            rootRecords.Add(rec);

            if (!rec.Exists) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root.Path))
                    jobs.Add((rec, dir));
            }
            catch (Exception)
            {
                result.Errors++;
            }
        }

        var pendingNameFiles = new List<(LibraryRecord Lib, string FullPath, string RelPath, string Kind)>();
        long filesIndexed = 0;
        int libIndex = 0;

        // ── 阶段 1：枚举与统计 ─────────────────────────────
        foreach (var (rootRec, libPath) in jobs)
        {
            ct.ThrowIfCancellationRequested();
            string libName = System.IO.Path.GetFileName(libPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));

            var lib = new LibraryRecord
            {
                RootId = rootRec.Id,
                RootPath = rootRec.Path,
                Name = libName,
                Path = libPath,
                Category = LibraryClassifier.Classify(libName),
                LastScannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            progress?.Report(new ScanProgress
            {
                Phase = "统计文件",
                CurrentRoot = rootRec.Path,
                RootsTotal = roots.Count,
                RootsDone = rootRecords.IndexOf(rootRec),
                LibrariesDone = libIndex,
                LibrariesTotal = jobs.Count,
                CurrentLibrary = libName,
                FilesIndexed = filesIndexed,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
            });

            var nicntPaths = new List<string>();
            var manualPaths = new List<string>();
            var audioClips = new List<(string LibraryName, string LibraryPath, string RelPath, string Name, string Ext, long Size, string Kind)>();
            try
            {
                var dirInfo = new DirectoryInfo(libPath);
                foreach (var f in dirInfo.EnumerateFiles("*", EnumOptions))
                {
                    ct.ThrowIfCancellationRequested();
                    lib.FileCount++;
                    lib.SizeBytes += f.Length;
                    filesIndexed++;
                    rootRec.LibraryCount++;
                    rootRec.SizeBytes += f.Length;

                    string ext = f.Extension;
                    switch (ext.ToLowerInvariant())
                    {
                        case ".nki":
                            lib.NkiCount++;
                            pendingNameFiles.Add((lib, f.FullName, RelPath(libPath, f.FullName), "nki"));
                            break;
                        case ".nkm":
                            lib.NkmCount++;
                            pendingNameFiles.Add((lib, f.FullName, RelPath(libPath, f.FullName), "nkm"));
                            break;
                        case ".nkc":
                            lib.NkcCount++;
                            break;
                        case ".nicnt":
                            lib.HasNicnt = true;
                            nicntPaths.Add(f.FullName);
                            break;
                    }

                    // 说明书采集（限量，避免个别库塞满）
                    // **机械过滤**：扩展名之外再看「像不像说明书」，
                    // 挡掉 license.rtf / readme.txt / changelog 这类非手册文档（实测混进来很多）。
                    if (ManualExtensions.Contains(ext)
                        && LooksLikeManual(f.Name, f.Length)
                        && manualPaths.Count < MaxManualsPerLibrary)
                        manualPaths.Add(f.FullName);

                    // 可试听音频采集：优先产品演示音频（mp3/ogg），其次真实采样（wav/aif）
                    // 注意：.ncw 是 NI 专有压缩格式，外部无法解码，不纳入试听
                    string lowExt = ext.ToLowerInvariant();
                    int audioCap = lowExt is ".ncw" or ".nkx" ? MaxNcwPerLibrary : MaxAudioPerLibrary;
                    int audioCount = lowExt is ".ncw" or ".nkx" ? audioClips.Count(a => a.Kind == "ncw" || a.Kind == "nkx") : audioClips.Count;
                    if (AudioExtensions.Contains(lowExt) && audioCount < audioCap)
                    {
                        string relAudio = RelPath(libPath, f.FullName);
                        string lowRel = relAudio.ToLowerInvariant();
                        bool isDemo = lowExt is ".mp3" or ".ogg"
                                      || lowRel.Contains("demo") || lowRel.Contains("preview")
                                      || lowRel.Contains("audio example");
                        // .ncw 是 NI 专有压缩采样：可被本工具解码后试听（kind 单独标记）
                        if (lowExt == ".ncw") isDemo = false;
                        // .nkx 是加密单块容器：可按需解包出 NCW 再试听
                        if (lowExt == ".nkx") isDemo = false;
                        // 演示音频优先保留；采样按文件大小过滤掉极短的占位文件
                        if (isDemo || f.Length > 8 * 1024)
                        {
                            audioClips.Add((lib.Name, libPath, relAudio, System.IO.Path.GetFileName(f.FullName),
                                            lowExt.TrimStart('.'), f.Length, lowExt == ".ncw" ? "ncw" : (lowExt == ".nkx" ? "nkx" : (isDemo ? "demo" : "sample"))));
                        }
                    }

                    if (JunkExtensions.Contains(ext))
                    {
                        lib.JunkCount++;
                        lib.JunkBytes += f.Length;
                        result.JunkFiles.Add(new JunkFileRecord
                        {
                            LibraryName = lib.Name,
                            RelPath = RelPath(libPath, f.FullName),
                            Kind = ext.TrimStart('.').ToLowerInvariant(),
                            SizeBytes = f.Length,
                            SuggestedKeep = KeepSuggestionNames.Contains(f.Name),
                        });
                    }
                }
            }
            catch (Exception)
            {
                result.Errors++;
            }

            // ── 封面图：从 .nicnt 提取内嵌 PNG（Kontakt 官方横幅，实测 905×99）──
            foreach (var nicntPath in nicntPaths)
            {
                var art = NicntReader.ExtractArtwork(nicntPath);
                if (art == null) continue;
                lib.CoverFile = CoverStore.Save(libPath, art);
                if (lib.CoverFile.Length > 0) break;
            }

            // ── 说明书清单 ──
            foreach (var m in manualPaths)
            {
                string rel = RelPath(libPath, m);
                string name = System.IO.Path.GetFileName(m);
                string mExt = System.IO.Path.GetExtension(m).TrimStart('.').ToLowerInvariant();
                long size = 0;
                try { size = new FileInfo(m).Length; } catch { }

                result.Manuals.Add(new ManualRecord
                {
                    LibraryName = lib.Name,
                    LibraryPath = libPath,
                    RelPath = rel,
                    Name = name,
                    Ext = mExt,
                    SizeBytes = size,
                    IsPrimary = IsPrimaryManual(rel, name),
                });
            }

            // ── 可试听音频片段 ──
            // 注意：必须在说明书循环「之外」——否则没有说明书的库永远采不到音频，
            // 而有说明书的库会被重复复制 N 次（N = 说明书数量）。
            foreach (var a in audioClips)
            {
                result.AudioClips.Add(new AudioClip
                {
                    LibraryName = a.LibraryName,
                    LibraryPath = a.LibraryPath,
                    RelPath = a.RelPath,
                    Name = a.Name,
                    Ext = a.Ext,
                    SizeBytes = a.Size,
                    Kind = a.Kind,
                });
            }
            lib.ManualCount = result.Manuals.Count(m => m.LibraryName.Equals(lib.Name, StringComparison.OrdinalIgnoreCase));

            // .nicnt 产品键（一个库可含多个子库，如 Chris Hein Solo Strings 有 4 个）
            if (nicntPaths.Count > 0)
            {
                var keys = new List<string>();
                foreach (var p in nicntPaths)
                {
                    var info = NicntReader.Read(p);
                    if (info == null) continue;
                    string key = string.IsNullOrWhiteSpace(info.RegKey) ? info.Name : info.RegKey;
                    if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                        keys.Add(key);
                }
                lib.ProductKey = string.Join(";", keys);
            }

            // 注册表产品键：无 .nicnt 的旧式库只能靠注册表确定产品键，
            // 而「修复入库路径 / 取消注册」等功能都依赖它，故必须落库。
            try
            {
                var fromRegistry = LibraryRegistrar.ResolveProductKeys(libName, libPath, registeredProducts);
                if (fromRegistry.Count > 0)
                {
                    var merged = lib.ProductKey
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    foreach (var k in fromRegistry)
                        if (!merged.Contains(k, StringComparer.OrdinalIgnoreCase)) merged.Add(k);
                    lib.ProductKey = string.Join(";", merged);
                }
            }
            catch { }

            result.Libraries.Add(lib);
            libIndex++;
        }

        // ── 阶段 2：并行提取 NKI/NKM 名称与引擎版本 ─────────
        progress?.Report(new ScanProgress
        {
            Phase = "读取乐器名与版本",
            RootsTotal = roots.Count,
            RootsDone = roots.Count,
            LibrariesDone = jobs.Count,
            LibrariesTotal = jobs.Count,
            FilesIndexed = filesIndexed,
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
        });

        var instruments = new ConcurrentBag<InstrumentRecord>();
        var engineByLibrary = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int done = 0;

        await Task.Run(() =>
        {
            var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) };
            Parallel.ForEach(pendingNameFiles, po, item =>
            {
                var (lib, fullPath, relPath, kind) = item;
                string stem = System.IO.Path.GetFileNameWithoutExtension(fullPath);

                var rec = new InstrumentRecord
                {
                    LibraryName = lib.Name,
                    RootPathHint = lib.RootPath,
                    RelPath = relPath,
                    Kind = kind,
                };

                try
                {
                    var fi = new FileInfo(fullPath);
                    rec.SizeBytes = fi.Length;
                    rec.Mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();

                    var (name, source, fmt, engine) = NkiMetadataReader.ReadFromFile(fullPath);
                    rec.Name = string.IsNullOrWhiteSpace(name) ? stem : name!;
                    rec.Source = source;
                    rec.Format = fmt switch
                    {
                        NkiFormat.Modern => "modern",
                        NkiFormat.Legacy => "legacy",
                        _ => "unknown",
                    };
                    rec.EngineVersion = engine;

                    if (VersionUtil.IsValid(engine))
                    {
                        engineByLibrary.AddOrUpdate(lib.Name, engine, (_, cur) => VersionUtil.Compare(engine, cur) > 0 ? engine : cur);
                    }
                }
                catch
                {
                    rec.Name = stem;
                    rec.Source = "filename";
                    rec.Format = "unknown";
                }

                // 演奏法分类：与名称来源无关，统一在此计算（文件名 + 相对路径）
                rec.Articulation = ArticulationClassifier.Classify(rec.Name, rec.RelPath, rec.Kind);

                instruments.Add(rec);

                int n = Interlocked.Increment(ref done);
                if (n % 200 == 0)
                {
                    progress?.Report(new ScanProgress
                    {
                        Phase = "读取乐器名与版本",
                        RootsTotal = roots.Count,
                        RootsDone = roots.Count,
                        LibrariesDone = jobs.Count,
                        LibrariesTotal = jobs.Count,
                        CurrentLibrary = lib.Name,
                        FilesIndexed = n,
                        ElapsedSeconds = sw.Elapsed.TotalSeconds,
                    });
                }
            });
        }, ct);

        foreach (var lib in result.Libraries)
        {
            if (engineByLibrary.TryGetValue(lib.Name, out string? eng) && !string.IsNullOrEmpty(eng))
                lib.RequiredKontakt = eng;
        }

        result.Instruments.AddRange(instruments
            .OrderBy(i => i.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.RelPath, StringComparer.OrdinalIgnoreCase));
        result.TotalInstruments = result.Instruments.Count;
        result.TotalFiles = filesIndexed;
        result.TotalBytes = result.Libraries.Sum(l => l.SizeBytes);
        result.Roots = rootRecords;
        sw.Stop();
        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        result.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        progress?.Report(new ScanProgress
        {
            Phase = "完成",
            RootsTotal = roots.Count,
            RootsDone = roots.Count,
            LibrariesDone = jobs.Count,
            LibrariesTotal = jobs.Count,
            FilesIndexed = filesIndexed,
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
        });

        return result;
    }

    private static string RelPath(string root, string fullPath)
    {
        string r = root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? fullPath[r.Length..] : fullPath;
    }

    /// <summary>判定是否为主要说明书（文件名或所在目录含 manual/guide/documentation/说明 等）。</summary>
    private static bool IsPrimaryManual(string relPath, string fileName)
    {
        string s = (relPath + " " + fileName).ToLowerInvariant();
        return s.Contains("manual") || s.Contains("user guide") || s.Contains("userguide")
            || s.Contains("documentation") || s.Contains("说明书") || s.Contains("操作手册")
            || s.Contains("quick start") || s.Contains("quickstart") || s.Contains("guide");
    }
}
