using Docnet.Core;
using Docnet.Core.Models;

namespace KontaktLibManager.Core;

/// <summary>
/// PDF 文字提取与页面渲染（基于 Docnet.Core / PDFium）。
///
/// 说明书解析策略（按用户要求）：
///   · 先抽取文字 —— 快速、省 token，覆盖绝大多数手册；
///   · 页面渲染按需进行 —— 当回答引用了某一页、或该页以图为主时，
///     才把那一页渲染成 PNG 展示给用户看例图（不预渲染，零冗余开销）。
///
/// PDFium 非线程安全，故所有调用串行化。
/// </summary>
public static class PdfText
{
    private static readonly object Gate = new();

    /// <summary>渲染缩放（1.0 ≈ 72dpi；1.6 兼顾清晰度与体积）。</summary>
    public const double DefaultScale = 1.6;

    public static int GetPageCount(string pdfPath)
    {
        lock (Gate)
        {
            using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(0.5));
            return doc.GetPageCount();
        }
    }

    /// <summary>逐页抽取文字。progress 回调 (当前页, 总页数)。</summary>
    public static List<string> ExtractAllText(string pdfPath, Action<int, int>? progress = null)
    {
        var pages = new List<string>();
        lock (Gate)
        {
            using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
            int count = doc.GetPageCount();
            for (int i = 0; i < count; i++)
            {
                string text = "";
                try
                {
                    using var page = doc.GetPageReader(i);
                    text = page.GetText() ?? "";
                }
                catch { /* 个别页解析失败不影响整体 */ }
                pages.Add(Normalize(text));
                progress?.Invoke(i + 1, count);
            }
        }
        return pages;
    }

    /// <summary>渲染指定页为 PNG 字节（0 基页号）。</summary>
    public static byte[]? RenderPagePng(string pdfPath, int pageIndex, double scale = DefaultScale)
    {
        lock (Gate)
        {
            try
            {
                using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
                if (pageIndex < 0 || pageIndex >= doc.GetPageCount()) return null;
                using var page = doc.GetPageReader(pageIndex);
                int w = page.GetPageWidth();
                int h = page.GetPageHeight();
                var bgra = page.GetImage();
                if (bgra == null || bgra.Length < w * h * 4) return null;
                return PngWriter.FromBgra(bgra, w, h);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>取某页渲染后的 BGRA 像素缓冲（供质量检测复用）。</summary>
    public static byte[]? RenderPageBgra(string pdfPath, int pageIndex, double scale = DefaultScale)
    {
        lock (Gate)
        {
            try
            {
                using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
                if (pageIndex < 0 || pageIndex >= doc.GetPageCount()) return null;
                using var page = doc.GetPageReader(pageIndex);
                return page.GetImage();
            }
            catch { return null; }
        }
    }

    /// <summary>某页在指定缩放下的渲染宽度（像素）。</summary>
    public static int GetPageWidth(string pdfPath, int pageIndex, double scale = DefaultScale)
    {
        lock (Gate)
        {
            try
            {
                using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
                if (pageIndex < 0 || pageIndex >= doc.GetPageCount()) return 0;
                using var page = doc.GetPageReader(pageIndex);
                return page.GetPageWidth();
            }
            catch { return 0; }
        }
    }

    /// <summary>某页在指定缩放下的渲染高度（像素）。</summary>
    public static int GetPageHeight(string pdfPath, int pageIndex, double scale = DefaultScale)
    {
        lock (Gate)
        {
            try
            {
                using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
                if (pageIndex < 0 || pageIndex >= doc.GetPageCount()) return 0;
                using var page = doc.GetPageReader(pageIndex);
                return page.GetPageHeight();
            }
            catch { return 0; }
        }
    }
    /// <summary>
    /// **判断渲染结果是否有有效内容**（用于「看原页图」的降级）。
    ///
    /// 背景（用户实测）：`Studio Drummer Manual French.pdf` 渲染出来**全是灰色色块**、
    /// `Orchestral Essentials 2 Reference Manual.pdf` **什么都没有**。
    /// 实测确认尺寸与缓冲长度都正确（`GetImage().Length == w*h*4`），所以不是取图的问题，
    /// 而是 **Docnet 自带的是精简版 PDFium，没有系统字体回退**：
    ///   · 内嵌字体缺失/子集异常 → PDFium 用**灰方块占位**（就是「色块」）；
    ///   · 内容为白底白字、或用了不支持的颜色空间 → **看起来一片空白**。
    ///
    /// 这里做**轻量统计**判断是否属于这两类：
    ///   · 采样若干像素，统计**主导色占比**与**不同颜色数**；
    ///   · 主导色 > 92%（几乎纯色）或不同颜色 < 6 种 → 判为「无有效内容」。
    /// 调用方据此**降级显示抽取文本**，而不是给用户看一片灰。
    /// </summary>
    public static bool LooksBlank(byte[] bgra, int width, int height)
    {
        if (bgra == null || width <= 0 || height <= 0) return true;
        int total = width * height;
        if (bgra.Length < total * 4) return true;

        var hist = new Dictionary<int, int>();
        int step = Math.Max(1, total / 4000);          // 最多采样约 4000 个像素
        int sampled = 0;
        for (int i = 0; i < total; i += step)
        {
            int s = i * 4;
            int key = (bgra[s + 2] << 16) | (bgra[s + 1] << 8) | bgra[s];   // RGB
            hist[key] = hist.TryGetValue(key, out int c) ? c + 1 : 1;
            sampled++;
        }
        if (sampled == 0) return true;
        int dominant = hist.Values.Max();
        return hist.Count < 6 || dominant * 100.0 / sampled > 92.0;
    }
    /// <summary>规范化抽取文本：统一换行、压缩多余空行、去掉行尾连字符。</summary>
    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        // 行尾连字符断词（英文手册常见）
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\w)-\n(\w)", "$1$2");
        return text.Trim();
    }
}
