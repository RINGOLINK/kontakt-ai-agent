using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// NKI/NKM/NKC 元数据读取。
/// 契约（sqlite schema v1 配套，勿随意变更）：
///   1. 只读取文件头部前 64KB，绝不修改音色文件；
///   2. 乐器名 = 头部中第一个「非版本号形态」的 UTF-16LE 可读字符串；
///   3. 未命中则回退文件名（不含扩展名），Source 标记为 filename。
/// 依据：M0 抽样 250 个 .nki/.nkm，命中即 100% 与文件名一致（详见 docs/工具规划.md §4.2）。
/// </summary>
public static class NkiMetadataReader
{
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>
    /// **格式判定（2026-09-20 用 400 个真实 .nki 重新实测后修正）**。
    ///
    /// **旧实现是错的**：它把**偏移 0..3** 当魔数（`44 41 01 00` = "DA" + 版本），
    /// 但实测 **400 个 .nki 里偏移 0..3 有 399 种不同取值** —— 它其实是**小端长度字段**，
    /// 不是魔数：
    ///   文件 80 KB 的头部是 `44 41 01 00` = 0x00014144 = 82,244
    ///   文件 78 KB 的头部是 `aa 36 01 00` = 0x000136AA = 79,530
    ///   文件 71 KB 的头部是 `88 1b 01 00` = 0x00011B88 = 72,584
    /// 因此旧判据只在「文件大小恰好等于 82,244」时误判为 Modern（实测命中率 **0.3%**），
    /// 其余全落入 Unknown（实测 200 个样本：`Unknown=181 / Legacy=19 / Modern=0`）。
    ///
    /// **正确的判据：偏移 12..15 = `68 73 69 6e`（`"hsin"`），实测 400/400 = 100% 一致。**
    /// 旧实现里的 `12 90 A8 7F` 在同批样本中**一次都没出现**，保留为次级判据（不冲突）。
    /// </summary>
    public static NkiFormat DetectFormat(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 16)
        {
            // ✅ 实测 400/400 稳定：偏移 12..15 = "hsin"
            if (head[12] == 0x68 && head[13] == 0x73 && head[14] == 0x69 && head[15] == 0x6E)
                return NkiFormat.Modern;
        }
        if (head.Length >= 4)
        {
            // 次级判据（旧代码沿用的标记；同批 400 样本中未出现，保留以防其它 Kontakt 版本使用）
            if (head[0] == 0x12 && head[1] == 0x90 && head[2] == 0xA8 && head[3] == 0x7F)
                return NkiFormat.Legacy;
        }
        return NkiFormat.Unknown;
    }

    /// <summary>从头部缓冲中提取第一个非版本号的 UTF-16LE 字符串。</summary>
    public static string? ExtractInstrumentName(byte[] buf, int len)
    {
        if (len < 8) return null;
        int i = 0;
        while (i + 3 < len)
        {
            if (buf[i + 1] != 0)
            {
                i++;
                continue;
            }
            byte c = buf[i];
            if (c < 0x20 || c >= 0x7F)
            {
                i++;
                continue;
            }

            // 收集一段连续的 (可打印字节, 0x00) 对
            int start = i;
            int j = i;
            while (j + 1 < len && buf[j + 1] == 0 && buf[j] >= 0x20 && buf[j] < 0x7F)
                j += 2;

            int charCount = (j - start) / 2;
            if (charCount >= 4)
            {
                var sb = new StringBuilder(charCount);
                for (int k = start; k < j; k += 2)
                    sb.Append((char)buf[k]);
                string cand = sb.ToString();
                if (IsPlausibleName(cand))
                    return cand;
            }

            i = j > i ? j : i + 1;
        }
        return null;
    }

    /// <summary>
    /// 提取 NKI 头部第一个「版本号形态」的 UTF-16LE 串（如 6.4.2.93）。
    /// 实测该串即保存该乐器所用的 Kontakt 引擎版本，决定加载所需的最低 Kontakt 版本。
    /// </summary>
    public static string ExtractEngineVersion(byte[] buf, int len)
    {
        if (len < 8) return "";
        int i = 0;
        while (i + 3 < len)
        {
            if (buf[i + 1] != 0 || buf[i] < 0x30 || buf[i] > 0x39)
            {
                i++;
                continue;
            }

            int start = i;
            int j = i;
            while (j + 1 < len && buf[j + 1] == 0 &&
                   ((buf[j] >= 0x30 && buf[j] <= 0x39) || buf[j] == 0x2E))
                j += 2;

            var sb = new StringBuilder((j - start) / 2);
            for (int k = start; k < j; k += 2)
                sb.Append((char)buf[k]);
            string cand = sb.ToString();
            if (VersionUtil.IsValid(cand) && cand.Count(ch => ch == '.') >= 1)
                return cand;

            i = j > i ? j : i + 1;
        }
        return "";
    }

    public static bool IsVersionLike(string s)
    {
        if (s.Length == 0) return true;
        bool sawDigit = false;
        foreach (char ch in s)
        {
            if (ch >= '0' && ch <= '9') { sawDigit = true; continue; }
            if (ch == '.') continue;
            return false;
        }
        return sawDigit;
    }

    private static bool IsPlausibleName(string s)
    {
        if (s.Length < 4 || s.Length > 200) return false;
        if (IsVersionLike(s)) return false;

        int letters = 0;
        foreach (char ch in s)
        {
            if (char.IsLetter(ch)) letters++;
        }
        if (letters < 3) return false;

        // 排除纯资源标签（.nkc bank 里的常见串）
        switch (s)
        {
            case "Samples":
            case "Resources":
            case "KontaktInstrument":
            case "Sample-based":
                return false;
        }
        return true;
    }

    /// <summary>读取单个 NKI/NKM 文件，返回（名称, 来源, 格式, 引擎版本）。</summary>
    public static (string? name, string source, NkiFormat format, string engineVersion) ReadFromFile(string fullPath)
    {
        var buffer = new byte[MaxHeaderBytes];
        int len = 0;
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            int read;
            while (len < buffer.Length && (read = fs.Read(buffer, len, buffer.Length - len)) > 0)
                len += read;
        }
        catch
        {
            return (null, "filename", NkiFormat.Unknown, "");
        }

        var fmt = DetectFormat(buffer.AsSpan(0, len));
        string engine = ExtractEngineVersion(buffer, len);
        var name = fmt == NkiFormat.Legacy ? null : ExtractInstrumentName(buffer, len);
        if (!string.IsNullOrWhiteSpace(name))
            return (name!.Trim(), "header", fmt, engine);
        return (null, "filename", fmt, engine);
    }
}
