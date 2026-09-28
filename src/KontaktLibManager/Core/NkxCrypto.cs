using System.Security.Cryptography;
using Microsoft.Win32;

namespace KontaktLibManager.Core;

/// <summary>
/// .nkx / .nkr 容器的解密与解包。
///
/// 算法来源：开源项目 **nkxtract**（maxton/nkxtract，GPLv3）的 C# 实现，
/// 与本工具自行逆向出的目录结构完全吻合（魔数 0x5E70AC54 等）。
///
/// 方案要点：
///   · **密钥来自注册表** `HKLM\SOFTWARE\Native Instruments\&lt;库名&gt;`：
///       `JDX` = AES-128 密钥（十六进制，可能含空格）
///       `HU`  = 初始向量 IV（十六进制，可能含空格）
///     —— 这两个字段本工具在入库时就已经读取，因此无需额外授权信息。
///   · **文件偏移经过 XOR 混淆**：真实偏移 = 条目里的 file_offset ^ 0x1F4E0C8D。
///   · **数据加密**：64 KB 密钥流循环异或。密钥流 = LCG 伪随机字节 ⊕ AES-128-ECB(计数器)，
///     计数器从 IV 开始、每 16 字节按**大端**递增。密钥流算一次（64 KB）后整文件循环复用。
/// </summary>
public static class NkxCrypto
{
    /// <summary>文件偏移 XOR 掩码（nkxtract: FileOffsetXor）。</summary>
    public const uint FileOffsetXor = 0x1F4E0C8D;

    public const uint IdDir = 0x5E70AC54;
    public const uint IdEncFile = 0x16CCF80A;
    public const uint IdFile = 0x4916E63C;
    public const ushort Version = 0x0111;
    /// <summary>实测存在 0x0110 与 0x0111 两种版本号，均按同一布局解析。</summary>
    public static bool IsSupportedVersion(ushort v) => v is 0x0110 or 0x0111;

    private const int XorLength = 0x10000;   // 64 KB 密钥流
    private const int LcgSeed = unchecked((int)0x608DA0A2);
    private const int LcgMul = 0x343FD;
    private const int LcgAdd = 0x269EC3;

    /// <summary>从注册表读取某库的 JDX(密钥) / HU(IV)。</summary>
    public static (byte[] key, byte[] iv)? LoadKey(string regKey)
    {
        if (string.IsNullOrWhiteSpace(regKey)) return null;

        // 🔴 **重要修复（2026-09-24）** —— 原实现把【整串】当键名：
        //     baseKey.OpenSubKey(Path.Combine(@"SOFTWARE\Native Instruments", regKey))
        //   但调用方传进来的是 libraries.product_key，它是【分号分隔的候选名列表】：
        //     "Ethno World 6 Instruments;Best Service Ethno World 6 - Instruments Lib"
        //   ⇒ 拼出的路径含分号、必然打不开 ⇒ 实测 1,141 个 .nkx 因「拿不到密钥」失败
        //     （而界面显示「已入库」是因为【库注册】与【容器可解密】是两件事）。
        //   ⇒ 正确做法：**按分号拆开、逐个候选名尝试**；并**同时试 32/64 位注册表视图**
        //     （实测两处都有条目，只查一处会漏）。
        var candidates = regKey.Split(';', StringSplitOptions.RemoveEmptyEntries)
                               .Select(s => s.Trim())
                               .Where(s => s.Length > 0)
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();
        foreach (var name in candidates)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var k = baseKey.OpenSubKey($@"SOFTWARE\Native Instruments\{name}");
                    if (k == null) continue;

                    string? jdx = k.GetValue("JDX") as string;
                    string? hu = k.GetValue("HU") as string;
                    if (string.IsNullOrEmpty(jdx) || string.IsNullOrEmpty(hu)) continue;

                    var key = FromHexCompact(jdx);
                    var iv = FromHexCompact(hu);
                    if (key.Length == 0 || iv.Length == 0) continue;
                    return (key, iv);
                }
                catch { /* 试下一个 */ }
            }
        }
        return null;
    }

    /// <summary>十六进制字符串 → 字节数组（忽略空格与非法字符；nkxtract 的 FromHexCompact）。</summary>
    public static byte[] FromHexCompact(string s)
    {
        var list = new List<byte>();
        string t = (s ?? "").Replace(" ", "").Replace("-", "");
        for (int x = 0; x + 1 < t.Length;)
        {
            byte result = 0;
            for (int i = 0; i < 2; i++, x++)
            {
                char ch = t[x];
                int sub;
                if (ch >= '0' && ch <= '9') sub = '0';
                else if (ch >= 'a' && ch <= 'f') sub = 'a' - 10;
                else if (ch >= 'A' && ch <= 'F') sub = 'A' - 10;
                else { i--; continue; }
                result <<= 4;
                result |= (byte)(ch - sub);
            }
            list.Add(result);
        }
        return list.ToArray();
    }

    /// <summary>构造 64 KB 密钥流（LCG 字节 ⊕ AES-128-ECB 计数器）。</summary>
    public static byte[] BuildXorMask(byte[] key, byte[] iv)
    {
        var mask = new byte[XorLength];

        // ① LCG 伪随机字节
        unchecked
        {
            int seed = LcgSeed;
            for (int i = 0; i < XorLength; i++)
            {
                seed = seed * LcgMul + LcgAdd;
                mask[i] = (byte)(seed >> 16);
            }
        }

        // ② 与 AES-128-ECB 计数器流异或（计数器从 IV 起，每 16 字节大端 +1）
        var counter = new byte[16];
        Array.Copy(iv, counter, Math.Min(16, iv.Length));
        var crypted = new byte[16];

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        // 密钥长度自适应：JDX 实测有 16 字节(AES-128) 与 32 字节(AES-256) 两种
        byte[] useKey = NormalizeKey(key);
        aes.KeySize = useKey.Length * 8;
        aes.Key = useKey;
        using var enc = aes.CreateEncryptor();
        enc.TransformBlock(counter, 0, 16, crypted, 0);

        int counterLoc = 0;
        for (int i = 0; i < XorLength; i++)
        {
            if (i != 0 && i % 16 == 0)
            {
                for (int j = 15; j >= 0; j--)
                    if (++counter[j] != 0) break;
                counterLoc = 0;
                enc.TransformBlock(counter, 0, 16, crypted, 0);
            }
            mask[i] ^= crypted[counterLoc];
            counterLoc++;
        }
        return mask;
    }

    /// <summary>把密钥规整为 AES 合法长度（16 / 24 / 32 字节）。</summary>
    private static byte[] NormalizeKey(byte[] key)
    {
        if (key.Length is 16 or 24 or 32) return key;
        int n = key.Length >= 32 ? 32 : key.Length >= 24 ? 24 : 16;
        var k = new byte[n];
        Array.Copy(key, k, Math.Min(n, key.Length));
        return k;
    }

    /// <summary>用密钥流循环异或解密一段数据（就地修改）。</summary>
    public static void DecryptInPlace(byte[] buffer, int count, long startPosition, byte[] mask)
    {
        for (int i = 0; i < count; i++)
            buffer[i] ^= mask[(int)((startPosition + i) % XorLength)];
    }
}
