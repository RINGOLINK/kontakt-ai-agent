namespace KontaktLibManager.Core;

/// <summary>Kontakt 版本号解析与比较（如 7.10.1.0 / 5.8.1.43）。</summary>
public static class VersionUtil
{
    public static int[] ParseParts(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return Array.Empty<int>();
        var parts = v.Trim().Split('.');
        var nums = new List<int>();
        foreach (var p in parts)
        {
            if (int.TryParse(p, out int n)) nums.Add(n);
            else break;
        }
        return nums.ToArray();
    }

    /// <summary>a &gt; b 返回 1，a &lt; b 返回 -1，相等返回 0。缺失段按 0 处理。</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = ParseParts(a);
        var pb = ParseParts(b);
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y ? 1 : -1;
        }
        return 0;
    }

    public static bool IsValid(string? v) => ParseParts(v).Length >= 2;

    /// <summary>从文件名里猜版本，如 KontaktPortable_v871.exe → 8.7.1。</summary>
    public static string GuessFromFileName(string fileName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            fileName, @"(?:v|_v)?(\d)(\d)(\d)(?!\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
            return $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";
        return "";
    }
}
