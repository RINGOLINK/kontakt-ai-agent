using System.Buffers.Binary;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>.nkx / .nkr 容器中的一个条目（文件或目录）。</summary>
public sealed class NkxItem
{
    public string Name { get; set; } = "";
    public long Offset { get; set; }        // 条目在容器中的位置
    public bool IsDirectory { get; set; }
    /// <summary>目录：子项；文件：无。</summary>
    public List<NkxItem> Children { get; set; } = new();

    // ── 文件项 ──
    /// <summary>真实数据偏移（已还原 XOR 混淆）。</summary>
    public long DataOffset { get; set; }
    public uint Size { get; set; }
    public bool Encrypted { get; set; }
    public uint KeyIndex { get; set; }

    public IEnumerable<NkxItem> Flatten()
    {
        foreach (var c in Children)
        {
            if (c.IsDirectory) foreach (var x in c.Flatten()) yield return x;
            else yield return c;
        }
    }
}

/// <summary>
/// NI 单块归档（.nkx / .nkr）读取与解包。
///
/// 格式来源：开源项目 **nkxtract**（maxton/nkxtract，GPLv3）——
/// 与本工具自行逆向出的结构完全一致（魔数 0x5E70AC54 等），故按其实现移植。
///
/// 目录结构：
///   目录头 22 字节：u32 魔数 0x5E70AC54 | u16 版本 0x0111 | u32 Id | u32 unk0 | u32 条目数 | u32 unk1
///   条目 8+N 字节：u16 条目总长 | u32 文件偏移 | u16 类型（1=目录，其他=文件）| UTF-16LE 名称
/// 文件头 31 字节：u32 魔数（0x16CCF80A 加密 / 0x4916E63C 明文）| u16 版本 0x0111 |
///                 u32 Id | u32 密钥序号 | 5 字节未知 | u32 数据长度 | u32 unk2 | u32 unk3
///   数据起点 = 文件头偏移 + 0x1F
/// </summary>
public static class NkxReader
{
    private const int DirHeaderSize = 22;
    private const int FileHeaderSize = 0x1F;

    // ────────────────────────── 读取 ──────────────────────────

    /// <summary>读取容器目录树。</summary>
    public static NkxItem? ReadTree(string path, int maxDepth = 8)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return ReadDirectory(fs, 0, "", 0, maxDepth);
        }
        catch { return null; }
    }

    private static NkxItem ReadDirectory(FileStream fs, uint offset, string name, int depth, int maxDepth)
    {
        var dir = new NkxItem { Name = name, Offset = offset, IsDirectory = true };
        if (depth > maxDepth || offset + DirHeaderSize > fs.Length) return dir;

        fs.Seek(offset, SeekOrigin.Begin);
        var h = new byte[DirHeaderSize];
        if (fs.Read(h, 0, DirHeaderSize) != DirHeaderSize) return dir;

        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != NkxCrypto.IdDir) return dir;
        ushort dirVer = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4));
        if (!NkxCrypto.IsSupportedVersion(dirVer)) return dir;
        uint entries = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(14));
        if (entries > 500000) entries = 500000;

        long pos = offset + DirHeaderSize;
        for (uint i = 0; i < entries; i++)
        {
            if (pos + 8 > fs.Length) break;
            fs.Seek(pos, SeekOrigin.Begin);
            var e = new byte[8];
            if (fs.Read(e, 0, 8) != 8) break;

            ushort entSize = BinaryPrimitives.ReadUInt16LittleEndian(e);
            uint fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(2));
            ushort entryType = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(6));
            if (entSize < 8) break;

            int nameBytes = entSize - 8;
            var nb = new byte[nameBytes];
            if (fs.Read(nb, 0, nameBytes) != nameBytes) break;
            string itemName = Encoding.Unicode.GetString(nb).TrimEnd('\0');

            if (entryType == 1)
                dir.Children.Add(ReadDirectory(fs, fileOffset, itemName, depth + 1, maxDepth));
            else
            {
                var file = ReadFile(fs, fileOffset, itemName);
                if (file != null) dir.Children.Add(file);
            }
            pos += entSize;
        }
        return dir;
    }

    private static NkxItem? ReadFile(FileStream fs, uint rawOffset, string name)
    {
        uint realOffset = rawOffset ^ NkxCrypto.FileOffsetXor;
        if (realOffset + FileHeaderSize > fs.Length) return null;

        fs.Seek(realOffset, SeekOrigin.Begin);
        var h = new byte[FileHeaderSize];
        if (fs.Read(h, 0, FileHeaderSize) != FileHeaderSize) return null;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(h);
        bool encrypted;
        if (magic == NkxCrypto.IdEncFile) encrypted = true;
        else if (magic == NkxCrypto.IdFile) encrypted = false;
        else return null;

        if (!NkxCrypto.IsSupportedVersion(BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4)))) return null;

        return new NkxItem
        {
            Name = name,
            Offset = realOffset,
            IsDirectory = false,
            DataOffset = realOffset + FileHeaderSize,
            KeyIndex = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(10)),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(19)),
            Encrypted = encrypted,
        };
    }

    // ────────────────────────── 解包 ──────────────────────────

    /// <summary>从容器取出一个文件并解密到 output；返回写入字节数，失败返回 -1。</summary>
    public static long ExtractFile(string nkxPath, NkxItem file, byte[]? xorMask, Stream output)
    {
        try
        {
            using var fs = File.OpenRead(nkxPath);
            return ExtractFile(fs, file, xorMask, output);
        }
        catch { return -1; }
    }

    public static long ExtractFile(FileStream fs, NkxItem file, byte[]? xorMask, Stream output)
    {
        try
        {
            if (file.IsDirectory) return -1;
            long remaining = file.Size;
            long read = file.DataOffset;
            if (read + remaining > fs.Length) remaining = fs.Length - read;
            if (remaining <= 0) return 0;

            fs.Seek(read, SeekOrigin.Begin);
            var buf = new byte[1024 * 1024];
            long written = 0;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buf.Length, remaining);
                int n = fs.Read(buf, 0, want);
                if (n <= 0) break;
                if (file.Encrypted && xorMask != null)
                    // ⚠️ **已知未解决**：对【加密容器】解出来的 .ncw 仍是乱码 ——
                    //   两种掩码偏移基准（文件内偏移 / 容器绝对偏移）都试过、都不对。
                    //   疑点：掩码生成或解密方案与 nkxtract 的实现有差异。
                    //   **先保持原基准**（对能解的容器是正确的），待后续研究。
                    NkxCrypto.DecryptInPlace(buf, n, read - file.DataOffset, xorMask);
                output.Write(buf, 0, n);
                read += n;
                written += n;
                remaining -= n;
            }
            return written;
        }
        catch { return -1; }
    }

    // ────────────────────────── 兼容旧接口 ──────────────────────────

    public sealed class NkxInfo
    {
        public int GroupCount { get; set; }
        public int SampleCount { get; set; }
        public List<NkxItem> Groups { get; set; } = new();
        public List<string> SampleNames { get; set; } = new();
        public string DominantExt { get; set; } = "";
    }

    /// <summary>枚举容器内容（走正规目录解析）。</summary>
    public static NkxInfo? ReadDirectory(string path, int maxSamples = 100000)
    {
        var root = ReadTree(path);
        if (root == null) return null;

        var info = new NkxInfo();
        var ext = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in root.Children.Where(c => c.IsDirectory))
        {
            info.Groups.Add(d);
            info.GroupCount++;
        }
        foreach (var f in root.Flatten())
        {
            if (info.SampleCount >= maxSamples) break;
            info.SampleNames.Add(f.Name);
            info.SampleCount++;
            string e = Path.GetExtension(f.Name).TrimStart('.').ToLowerInvariant();
            if (e.Length > 0) ext[e] = ext.TryGetValue(e, out int c) ? c + 1 : 1;
        }
        if (ext.Count > 0) info.DominantExt = ext.OrderByDescending(kv => kv.Value).First().Key;
        return info;
    }
}
