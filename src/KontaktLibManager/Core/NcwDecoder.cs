using System.Buffers.Binary;

namespace KontaktLibManager.Core;

/// <summary>
/// NCW（Native Instruments Compressed Wave）解码器 —— C# 移植版。
///
/// 格式本质（据社区逆向整理的 FORMAT.md）：
///   · 文件头 120 字节：魔数 + 声道数 + 位深 + 采样率 + 帧数 + 块表偏移 + 数据偏移 + 数据大小
///   · 块偏移表：每块一个 u32（相对数据区），末尾多一个哨兵项
///   · 每块覆盖 512 帧，**按块交错声道**（同一偏移处依次是各声道的子块）
///   · 子块头 16 字节：魔数 0x160C9A3E + 基值 + bits + flags
///       bits > 0  → DPCM 差分编码（先输出基值，再逐个累加差值）
///       bits < 0  → 位截断，按 |bits| 宽度直接存原始样本
///       bits == 0 → 按文件位深存原始样本
///     flags bit0 = mid/side 立体声；bit1 = IEEE-754 单精度
///   · 位打包为 **LSB-first**
///
/// 移植自 github.com/monomadic/ncw（Rust，零依赖），算法逐行对应。
/// </summary>
public static class NcwDecoder
{
    private const int HeaderSize = 120;
    private const int BlockHeaderSize = 16;
    private const uint BlockMagic = 0x160C9A3E;
    private const int SamplesPerBlock = 512;

    private static readonly ulong[] FileMagics = { 0x01A89ED631010000, 0x01A89ED630010000 };

    public sealed class NcwInfo
    {
        public int Channels;
        public int BitsPerSample;
        public int SampleRate;
        public int NumSamples;      // 帧数
        public bool IsFloat;
        public double Seconds => SampleRate > 0 ? (double)NumSamples / SampleRate : 0;
    }

    /// <summary>只读文件头（用于快速探测，不解码）。</summary>
    public static NcwInfo? ReadInfo(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[HeaderSize];
            if (fs.Read(header, 0, HeaderSize) != HeaderSize) return null;
            return ParseHeader(header);
        }
        catch { return null; }
    }

    private static NcwInfo? ParseHeader(byte[] h)
    {
        ulong magic = BinaryPrimitives.ReadUInt64BigEndian(h);
        if (Array.IndexOf(FileMagics, magic) < 0) return null;

        var info = new NcwInfo
        {
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8)),
            BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10)),
            SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)),
            NumSamples = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16)),
        };
        if (info.Channels == 0 || info.SampleRate <= 0) return null;
        if (info.BitsPerSample is not (8 or 16 or 24 or 32)) return null;
        return info;
    }

    /// <summary>
    /// 把 NCW 解码为 WAV。maxSeconds &gt; 0 时只解码开头这么多秒（试听足够，且显著加快）。
    /// 返回写入的 WAV 字节数；失败返回 0。
    /// </summary>
    public static long DecodeToWav(string ncwPath, string wavPath, double maxSeconds = 6.0)
    {
        try
        {
            using var fs = File.OpenRead(ncwPath);
            var headerBytes = new byte[HeaderSize];
            if (fs.Read(headerBytes, 0, HeaderSize) != HeaderSize) return 0;

            var info = ParseHeader(headerBytes);
            if (info == null) return 0;

            uint blocksOffset = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(20));
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(24));
            if (dataOffset < blocksOffset) return 0;

            int tableEntries = (int)((dataOffset - blocksOffset) / 4);
            int numBlocks = Math.Max(0, tableEntries - 1);
            if (numBlocks == 0) return 0;

            // 块偏移表
            fs.Seek(blocksOffset, SeekOrigin.Begin);
            var tableBytes = new byte[numBlocks * 4];
            if (fs.Read(tableBytes, 0, tableBytes.Length) != tableBytes.Length) return 0;
            var blockOffsets = new uint[numBlocks];
            for (int i = 0; i < numBlocks; i++)
                blockOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(tableBytes.AsSpan(i * 4));

            int channels = info.Channels;
            int totalFrames = info.NumSamples;
            // 试听截断：只解码前 maxSeconds 秒
            int frameLimit = maxSeconds > 0
                ? Math.Min(totalFrames, (int)(info.SampleRate * maxSeconds))
                : totalFrames;

            var channelData = new List<int>[channels];
            for (int c = 0; c < channels; c++) channelData[c] = new List<int>(Math.Min(frameLimit, 1 << 20));

            bool isFloat = false;

            foreach (uint offset in blockOffsets)
            {
                if (channelData[0].Count >= frameLimit) break;
                int blockStart = channelData[0].Count;

                fs.Seek(dataOffset + offset, SeekOrigin.Begin);

                bool midSide = false;
                for (int c = 0; c < channels; c++)
                {
                    var bh = new byte[BlockHeaderSize];
                    if (fs.Read(bh, 0, BlockHeaderSize) != BlockHeaderSize) break;

                    if (BinaryPrimitives.ReadUInt32BigEndian(bh) != BlockMagic) return 0;

                    int baseValue = BinaryPrimitives.ReadInt32LittleEndian(bh.AsSpan(4));
                    short bits = BinaryPrimitives.ReadInt16LittleEndian(bh.AsSpan(8));
                    ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(bh.AsSpan(10));

                    if ((flags & 0b01) != 0) midSide = true;
                    if ((flags & 0b10) != 0) isFloat = true;

                    if (!ReadBlock(fs, info, baseValue, bits, channelData[c])) return 0;
                }

                // mid/side → left/right
                if (midSide && channels == 2)
                {
                    var mid = channelData[0];
                    var side = channelData[1];
                    for (int i = blockStart; i < mid.Count && i < side.Count; i++)
                    {
                        int m = mid[i], s = side[i];
                        if (isFloat)
                        {
                            float mf = BitConverter.Int32BitsToSingle(m);
                            float sf = BitConverter.Int32BitsToSingle(s);
                            mid[i] = BitConverter.SingleToInt32Bits(mf + sf);
                            side[i] = BitConverter.SingleToInt32Bits(mf - sf);
                        }
                        else
                        {
                            unchecked { mid[i] = m + s; side[i] = m - s; }
                        }
                    }
                }
            }

            int frames = Math.Min(frameLimit, channelData[0].Count);
            if (frames <= 0) return 0;

            return WriteWav(wavPath, channelData, frames, channels, info.BitsPerSample, isFloat, info.SampleRate);
        }
        catch { return 0; }
    }

    /// <summary>读一个子块体（512 个样本）并追加到 out。</summary>
    private static bool ReadBlock(FileStream fs, NcwInfo info, int baseValue, short bits, List<int> outSamples)
    {
        int width = Math.Abs((int)bits);
        if (width > 32) return false;

        if (bits > 0)
        {
            // DPCM：读 bits*512/8 字节，LSB-first 解包
            int byteCount = width * SamplesPerBlock / 8;
            var data = new byte[byteCount];
            if (fs.Read(data, 0, byteCount) != byteCount) return false;

            int current = baseValue;
            foreach (int delta in PackedValues(data, width))
            {
                outSamples.Add(current);
                unchecked { current += delta; }
            }
        }
        else if (bits < 0)
        {
            // 位截断：按 |bits| 宽度直接解包
            int byteCount = width * SamplesPerBlock / 8;
            var data = new byte[byteCount];
            if (fs.Read(data, 0, byteCount) != byteCount) return false;
            foreach (int v in PackedValues(data, width)) outSamples.Add(v);
        }
        else
        {
            // 原始样本，按文件位深
            int bytesPerSample = info.BitsPerSample / 8;
            int byteCount = bytesPerSample * SamplesPerBlock;
            var data = new byte[byteCount];
            if (fs.Read(data, 0, byteCount) != byteCount) return false;
            for (int i = 0; i < SamplesPerBlock; i++)
            {
                uint raw = 0;
                for (int b = 0; b < bytesPerSample; b++) raw |= (uint)data[i * bytesPerSample + b] << (8 * b);
                outSamples.Add(SignExtend(raw, bytesPerSample * 8));
            }
        }
        return true;
    }

    /// <summary>LSB-first 解包 bits 宽度的有符号整数。</summary>
    private static IEnumerable<int> PackedValues(byte[] data, int bits)
    {
        ulong mask = (1UL << bits) - 1;
        ulong acc = 0;
        int available = 0;
        int pos = 0;

        while (true)
        {
            while (available < bits)
            {
                if (pos >= data.Length) yield break;
                acc |= (ulong)data[pos++] << available;
                available += 8;
            }
            uint raw = (uint)(acc & mask);
            yield return SignExtend(raw, bits);
            acc >>= bits;
            available -= bits;
        }
    }

    private static int SignExtend(uint raw, int bits)
    {
        int shift = 32 - bits;
        unchecked { return (int)(raw << shift) >> shift; }
    }

    /// <summary>写标准 WAV（PCM 或 IEEE float）。返回文件字节数。</summary>
    private static long WriteWav(string path, List<int>[] channels, int frames, int channelCount,
                                 int bitsPerSample, bool isFloat, int sampleRate)
    {
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channelCount * bytesPerSample;
        int dataBytes = frames * blockAlign;
        int formatTag = isFloat ? 3 : 1;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs);

        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                                  // fmt 块大小
        w.Write((short)formatTag);                    // 1=PCM, 3=IEEE float
        w.Write((short)channelCount);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);             // 字节率
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);

        var frame = new byte[blockAlign];
        for (int i = 0; i < frames; i++)
        {
            int p = 0;
            for (int c = 0; c < channelCount; c++)
            {
                int v = i < channels[c].Count ? channels[c][i] : 0;
                for (int b = 0; b < bytesPerSample; b++) frame[p++] = (byte)((uint)v >> (8 * b));
            }
            w.Write(frame, 0, frame.Length);
        }
        w.Flush();
        return fs.Length;
    }
}
