namespace KontaktLibManager.Core;

/// <summary>NKI 文件格式年代。契约：与 C++/JUCE 侧保持一致。</summary>
public enum NkiFormat
{
    Unknown = 0,
    /// <summary>Kontakt 4+ ：magic 44 41 01 00，头部含 UTF-16LE 乐器名。</summary>
    Modern = 1,
    /// <summary>Kontakt 2/3 时代：magic 12 90 A8 7F，文件内不存可读名称，靠文件名兜底。</summary>
    Legacy = 2,
}

public sealed class LibraryRecord
{
    public long Id { get; set; }
    public long RootId { get; set; }
    public string RootPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Category { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public int NkiCount { get; set; }
    public int NkmCount { get; set; }
    public int NkcCount { get; set; }
    public bool HasNicnt { get; set; }
    public int JunkCount { get; set; }
    public long JunkBytes { get; set; }
    public string Notes { get; set; } = "";
    public string LastScannedAt { get; set; } = "";

    /// <summary>.nicnt 里的注册表键名（RegKey），无 .nicnt 时为空。</summary>
    public string ProductKey { get; set; } = "";
    /// <summary>入库状态：registered / incomplete / partial / path-mismatch / missing / non-standard</summary>
    public string RegStatus { get; set; } = "unknown";
    /// <summary>缺少 Service Center 记录的产品键（记录不完整时非空）。</summary>
    public List<string> ScMissingRecords { get; set; } = new();
    /// <summary>Kontakt 注册表里指向的实际路径。</summary>
    public string RegContentDir { get; set; } = "";
    /// <summary>库内 NKI 要求的最高 Kontakt 引擎版本（如 7.10.1.0）。</summary>
    public string RequiredKontakt { get; set; } = "";
    /// <summary>版本兼容状态：ok / too-old / unknown</summary>
    public string CompatStatus { get; set; } = "unknown";

    /// <summary>封面图文件名（位于应用数据目录 covers/ 下），无封面为空。</summary>
    public string CoverFile { get; set; } = "";
    /// <summary>库内说明书数量。</summary>
    public int ManualCount { get; set; }
}

/// <summary>音色库自带的说明文档（说明书/手册）。</summary>
public sealed class ManualRecord
{
    public long Id { get; set; }
    public long LibraryId { get; set; }
    public string LibraryName { get; set; } = "";
    public string LibraryPath { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ext { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>是否为「手册」候选（文件名或所在目录含 manual/guide/documentation 等）。</summary>
    public bool IsPrimary { get; set; }

    public string FullPath => LibraryPath.Length > 0 ? Path.Combine(LibraryPath, RelPath) : RelPath;
}

public sealed class InstrumentRecord
{
    public long Id { get; set; }
    public long LibraryId { get; set; }
    public string LibraryName { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public long SizeBytes { get; set; }
    public long Mtime { get; set; }
    public string Source { get; set; } = ""; // "header" | "filename"
    public string Format { get; set; } = ""; // modern | legacy | unknown
    /// <summary>NKI 头部记录的 Kontakt 引擎版本（保存该乐器所用版本）。</summary>
    public string EngineVersion { get; set; } = "";
    /// <summary>所属根目录（同名库可能存在于多个根下）。</summary>
    public string RootPathHint { get; set; } = "";
    /// <summary>所属音色库的完整路径（乐器中心用于拼出完整文件路径）。</summary>
    public string LibraryPath { get; set; } = "";
    /// <summary>演奏法分类（键位切换/连奏/短音/长音/循环乐句/打击/效果/基础音色/多轨合奏）。</summary>
    public string Articulation { get; set; } = "";
}

/// <summary>音色库根目录（可配置多个，对应多块硬盘）。</summary>
public sealed class LibraryRoot
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string AddedAt { get; set; } = "";
    public string LastScannedAt { get; set; } = "";
    public int LibraryCount { get; set; }
    public long SizeBytes { get; set; }
    public bool Exists { get; set; } = true;
}

/// <summary>.nicnt 元数据（明文 XML，含注册所需全部字段）。</summary>
public sealed class NicntInfo
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string RegKey { get; set; } = "";
    public string Hu { get; set; } = "";
    public string Jdx { get; set; } = "";
    public string Visibility { get; set; } = "";
    public string ProductVisibility { get; set; } = "";
    public string AuthSystem { get; set; } = "";
    public string SnpId { get; set; } = "";
    public string Type { get; set; } = "";
    public string PoweredBy { get; set; } = "";
    public string Company { get; set; } = "";
    public string ContentVersion { get; set; } = "";
    public string Upid { get; set; } = "";
    public bool HasAuthPair => !string.IsNullOrEmpty(Hu) && !string.IsNullOrEmpty(Jdx);

    /// <summary>
    /// ProductHints XML 原文（从 .nicnt 中提取的完整 XML 块）。
    /// 关键：NI 的 Service Center 记录文件就是这个 XML 的副本，
    /// 实测 3/3 与 .nicnt 逐字符一致（仅空白差异）。缺这一步会导致
    /// Kontakt 能列出库但加载乐器时报 "library that is not installed currently"。
    /// </summary>
    public string RawXml { get; set; } = "";
}

/// <summary>本机安装的 Kontakt 程序。</summary>
public sealed class KontaktInstall
{
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Company { get; set; } = "";
    public string Description { get; set; } = "";
    public double SizeMB { get; set; }
    public string Source { get; set; } = "";   // registry | common-path | manual
    public string Note { get; set; } = "";     // 判定说明（给用户看为什么收录/忽略）
    public bool Selected { get; set; }
    public bool Exists { get; set; } = true;
    public bool IsManual { get; set; }
}

/// <summary>Kontakt 探测结果：可用程序 + 被忽略的安装包（透明化）。</summary>
public sealed class KontaktScanResult
{
    public List<KontaktInstall> Installs { get; set; } = new();
    public List<KontaktInstall> Ignored { get; set; } = new();
    public string CurrentExe { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool KontaktRunning { get; set; }
}

/// <summary>入库状态统计与逐库明细。</summary>
public sealed class RegistrationReport
{
    public int Total { get; set; }
    public int Registered { get; set; }
    /// <summary>注册表已写但缺 Service Center 记录（乐器仍加载不了）。</summary>
    public int Incomplete { get; set; }
    public int PathMismatch { get; set; }
    public int Missing { get; set; }
    public int NonStandard { get; set; }
    public bool IsAdmin { get; set; }
    public bool KontaktRunning { get; set; }
    /// <summary>便携版 Kontakt 根目录（非便携版为空）。</summary>
    public string PortableRoot { get; set; } = "";
    public string PortableSettingsCfg { get; set; } = "";
    public string PortableSettingsCfgModified { get; set; } = "";
    public string LibraryManagerPath { get; set; } = "";
    /// <summary>便携版自身库列表中的条目数。</summary>
    public int PortableLibraryCount { get; set; }
    /// <summary>注册表有、但便携版列表里没有的库数。</summary>
    public int PendingManager { get; set; }
    public string RegistryNote { get; set; } = "";
    public string ServiceCenterDir { get; set; } = "";
    public List<LibraryRecord> Items { get; set; } = new();
}

/// <summary>版本兼容汇总。</summary>
public sealed class CompatReport
{
    public string KontaktVersion { get; set; } = "";
    public string KontaktPath { get; set; } = "";
    public int Ok { get; set; }
    public int TooOld { get; set; }
    public int Unknown { get; set; }
    public List<LibraryRecord> Incompatible { get; set; } = new();
}

public sealed class JunkFileRecord
{
    public long Id { get; set; }
    public long LibraryId { get; set; }
    public string LibraryName { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string Kind { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool SuggestedKeep { get; set; }
    /// <summary>完整路径（由库路径 + 相对路径拼出）。</summary>
    public string FullPath { get; set; } = "";
}

public sealed class ScanProgress
{
    public string Phase { get; set; } = "";
    public string CurrentRoot { get; set; } = "";
    public int RootsDone { get; set; }
    public int RootsTotal { get; set; }
    public int LibrariesDone { get; set; }
    public int LibrariesTotal { get; set; }
    public string CurrentLibrary { get; set; } = "";
    public long FilesIndexed { get; set; }
    public double ElapsedSeconds { get; set; }
}

public sealed class ScanResult
{
    public string Root { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public double ElapsedSeconds { get; set; }
    public long TotalBytes { get; set; }
    public long TotalFiles { get; set; }
    public int TotalInstruments { get; set; }
    public int Errors { get; set; }
    public List<LibraryRoot> Roots { get; set; } = new();
    public List<LibraryRecord> Libraries { get; set; } = new();
    public List<InstrumentRecord> Instruments { get; set; } = new();
    public List<JunkFileRecord> JunkFiles { get; set; } = new();
    public List<ManualRecord> Manuals { get; set; } = new();
    public List<AudioClip> AudioClips { get; set; } = new();
}

public sealed class CategoryStat
{
    public string Category { get; set; } = "";
    public int LibraryCount { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>健康检查发现的问题项。</summary>
public sealed class HealthIssue
{
    public long LibraryId { get; set; }
    public string LibraryName { get; set; } = "";
    /// <summary>junk | no-manual | unknown-version | no-cover</summary>
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string Detail { get; set; } = "";
    public long SizeBytes { get; set; }
}

/// <summary>可直接播放的音频片段（采样或产品演示音频）。</summary>
public sealed class AudioClip
{
    public long Id { get; set; }
    public long LibraryId { get; set; }
    public string LibraryName { get; set; } = "";
    /// <summary>所属音色库的完整路径（写库时按路径匹配，避免同名库歧义）。</summary>
    public string LibraryPath { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ext { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>sample=库内采样；demo=产品演示/预览音频。</summary>
    public string Kind { get; set; } = "sample";
}

public sealed class DashboardData
{
    public bool HasData { get; set; }
    public string Root { get; set; } = "";
    public int RootCount { get; set; }
    public string LastScannedAt { get; set; } = "";
    public int LibraryCount { get; set; }
    public long TotalBytes { get; set; }
    public long TotalFiles { get; set; }
    public int InstrumentCount { get; set; }
    public int InstrumentNamedFromHeader { get; set; }
    /// <summary>无 .nicnt 的库数量（旧式 EX 库，Kontakt 浏览器无法注册）。</summary>
    public int LibrariesWithoutNicnt { get; set; }
    public int JunkCount { get; set; }
    public long JunkBytes { get; set; }
    /// <summary>旧格式 NKI 数量（Kontakt 2/3 时代，文件内无名称）。</summary>
    public int LegacyInstrumentCount { get; set; }
    public List<CategoryStat> Categories { get; set; } = new();
    public List<LibraryRecord> TopLibraries { get; set; } = new();
    public double LastScanSeconds { get; set; }
}
