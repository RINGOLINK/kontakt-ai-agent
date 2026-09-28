using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>
/// **视觉兜底层** —— 当 UIA 控件树覆盖不到自绘区域（波形、虚拟键盘、自定义面板）时，
/// 截图交给多模态模型，把「看到什么」变成**文字**回传给 Agent。
///
/// 为什么要绕这一圈：
///   大模型的工具返回值**只能是文本**，没法直接塞一张图给模型看。
///   所以这里由工具内部完成「截图 → 视觉模型 → 文字描述」，Agent 拿到的是描述文本。
///   这样视觉能力就变成了一个普通的文本工具，能无缝接进现有的工具循环。
///
/// 定位：**兜底**，不是主力。已知控件优先走 UIA（精确、零 token、快）；
/// 只有 UIA 覆盖不到的地方才用视觉。
/// </summary>
public static class UiVision
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SW_RESTORE = 9;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>截取指定窗口，返回 JPEG 字节。</summary>
    public static byte[]? CaptureWindow(IntPtr hwnd, int maxSide = 1280, long quality = 80)
    {
        try
        {
            bool wasMin = IsIconic(hwnd);
            if (wasMin) ShowWindow(hwnd, SW_RESTORE);
            IntPtr prev = GetForegroundWindow();
            if (prev != hwnd) { SetForegroundWindow(hwnd); Thread.Sleep(320); }

            if (!GetWindowRect(hwnd, out RECT r)) return null;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return null;

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                // 优先 PrintWindow（能抓到被遮挡的窗口内容）；失败则退回屏幕拷贝
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!ok || IsBlank(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }

            // 缩放
            double scale = Math.Min(1.0, maxSide / (double)Math.Max(w, h));
            Bitmap outBmp = bmp;
            if (scale < 1.0)
            {
                int nw = Math.Max(1, (int)(w * scale)), nh = Math.Max(1, (int)(h * scale));
                outBmp = new Bitmap(nw, nh);
                using var g2 = Graphics.FromImage(outBmp);
                g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g2.DrawImage(bmp, 0, 0, nw, nh);
            }

            using var ms = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                outBmp.Save(ms, codec, ep);
            }
            if (outBmp != bmp) outBmp.Dispose();

            // 不抢占用户焦点
            if (prev != IntPtr.Zero && prev != hwnd) SetForegroundWindow(prev);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>粗判是否全黑/全白（PrintWindow 对某些窗口会返回空图）。</summary>
    private static bool IsBlank(Bitmap b)
    {
        try
        {
            var c1 = b.GetPixel(b.Width / 4, b.Height / 4);
            var c2 = b.GetPixel(b.Width * 3 / 4, b.Height * 3 / 4);
            return c1.R == c2.R && c1.G == c2.G && c1.B == c2.B &&
                   ((c1.R < 8 && c1.G < 8 && c1.B < 8) || (c1.R > 247 && c1.G > 247 && c1.B > 247));
        }
        catch { return false; }
    }

    /// <summary>把截图存到临时目录，返回路径（供用户查看 / 后续复用）。</summary>
    public static string SaveTemp(byte[] jpeg)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "klm-ui-shots");
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, "shot-" + DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".jpg");
            File.WriteAllBytes(p, jpeg);
            return p;
        }
        catch { return ""; }
    }

    /// <summary>调用多模态模型描述一张图，返回纯文本。</summary>
    public static async Task<string> DescribeAsync(AiSettings ai, byte[] jpeg, string question, CancellationToken ct)
    {
        if (jpeg == null || jpeg.Length == 0) return "（截图失败）";
        string prompt = string.IsNullOrWhiteSpace(question)
            ? "请描述这张软件界面截图：有哪些区域、按钮、标签、列表内容？用中文简洁说明，重点是「界面上有什么、在哪里」。"
            : question;
        try
        {
            return await AiClient.DescribeImageAsync(ai, jpeg, prompt, ct, 900);
        }
        catch (Exception ex)
        {
            return "（视觉描述失败：" + ex.Message + "）";
        }
    }
}
