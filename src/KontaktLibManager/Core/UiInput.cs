using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace KontaktLibManager.Core;

/// <summary>
/// **输入注入层** —— 用真实鼠标/键盘操作外部程序。
///
/// 为什么必须走注入而不是 UIA Pattern：
///   实测发现 Kontakt 8（Qt/QML）把 UIA 的写操作暴露成**空壳** ——
///   `SelectionItemPattern.Select()` 调用无异常，但界面状态不变。
///   而「UIA 定位 + 真实鼠标点击」经实测**完全有效**。
///   详见 docs/Kontakt自动化控制能力评估.md。
///
/// 安全约束（对应产品既定的权限模型）：
///   · 所有写操作**必须先经用户确认**（由调用方负责，本类只负责执行）；
///   · 操作前**重新定位控件**拿实时坐标，绝不使用缓存坐标（窗口缩放/DPI 会失效）；
///   · 执行前校验目标坐标确实落在预期控件的矩形内（防止界面变化后点错位置）；
///   · 操作后**恢复用户原本的前台窗口**，不抢占用户的焦点。
/// </summary>
public static class UiInput
{
    // ══════════════════ Win32 ══════════════════

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

    /// <summary>窗口矩形（供技能层按窗口尺寸缩放坐标用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);     [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);     [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();     [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);     [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;

    // ══════════════════ 焦点管理 ══════════════════

    /// <summary>把目标窗口带到前台（最小化则先还原）。返回原来的前台窗口，供事后恢复。</summary>
    /// <summary>
    /// 把目标窗口带到前台并**确保它真的拿到焦点**。返回原来的前台窗口，供事后恢复。
    ///
    /// **为什么不能只调 SetForegroundWindow**（实测踩坑）：
    /// Windows 有「非前台进程不能抢焦点」的限制 —— 我们的应用不是前台时，
    /// SetForegroundWindow 会**静默失败**，之后发的按键/点击全都落到别的窗口上。
    /// 实测：调用后 GetForegroundWindow() 返回的仍是旧窗口，Kontakt 的 F10 完全无效。
    ///
    /// 解法：AttachThreadInput 把当前线程的输入队列临时挂到前台线程上，
    /// 这样就有权限调 SetForegroundWindow；再配合 minimize+restore 的经典兜底。
    /// </summary>
    /// <summary>
    /// 把目标窗口带到前台并**确保它真的拿到焦点**。返回原来的前台窗口，供事后恢复。
    ///
    /// **为什么不能只调 SetForegroundWindow**（实测踩坑）：
    /// Windows 有「非前台进程不能抢焦点」的限制 —— 我们的应用不是前台时，
    /// SetForegroundWindow 会**静默失败**，之后发的按键/点击全都落到别的窗口上。
    /// 实测：调用后 GetForegroundWindow() 返回的仍是旧窗口，Kontakt 的 F10 完全无效。
    ///
    /// **为什么还要重试**：AttachThreadInput 的成功率不稳定（实测同一段代码有时成有时不成），
    /// 所以这里循环尝试并用 GetForegroundWindow() **验证**，确认拿到焦点才返回。
    /// </summary>
    public static IntPtr FocusWindow(IntPtr target, int maxAttempts = 4)
    {
        IntPtr prev = GetForegroundWindow();
        if (target == IntPtr.Zero) return prev;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (GetForegroundWindow() == target) break;
            try
            {
                if (IsIconic(target)) ShowWindow(target, SW_RESTORE);

                uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
                uint myThread = GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != myThread)
                    attached = AttachThreadInput(fgThread, myThread, true);
                try
                {
                    SetForegroundWindow(target);
                    BringWindowToTop(target);
                }
                finally
                {
                    if (attached) AttachThreadInput(fgThread, myThread, false);
                }

                // 兜底：仍未拿到焦点时用 minimize + restore 的经典手法强制激活
                if (GetForegroundWindow() != target)
                {
                    ShowWindow(target, 6);   // SW_MINIMIZE
                    Thread.Sleep(90);
                    ShowWindow(target, SW_RESTORE);
                    Thread.Sleep(140);
                    SetForegroundWindow(target);
                }
            }
            catch { }
            Thread.Sleep(attempt == 0 ? 250 : 350);
        }
        return prev;
    }

    /// <summary>当前前台窗口是否就是目标窗口（用于确认焦点真的拿到了）。</summary>
    public static bool HasFocus(IntPtr target) => target != IntPtr.Zero && GetForegroundWindow() == target;

    /// <summary>把焦点还给用户原来的窗口。</summary>
    public static void RestoreFocus(IntPtr prev)
    {
        try
        {
            if (prev != IntPtr.Zero && prev != GetForegroundWindow())
            {
                SetForegroundWindow(prev);
                Thread.Sleep(120);
            }
        }
        catch { }
    }

    // ══════════════════ 鼠标 ══════════════════

    public static void ClickAt(int x, int y, bool right = false)
    {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        mouse_event(right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }

    public static void DoubleClickAt(int x, int y)
    {
        ClickAt(x, y);
        Thread.Sleep(90);
        ClickAt(x, y);
    }

    public static void ScrollAt(int x, int y, int notches)
    {
        SetCursorPos(x, y);
        Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(notches * 120)), IntPtr.Zero);
    }

    // ══════════════════ 键盘 ══════════════════

    /// <summary>用 Unicode 注入输入文本（不受输入法/键盘布局影响）。</summary>
    public static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (char c in text)
        {
            var down = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } } };
            var up = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } };
            SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(12);
        }
    }

    /// <summary>发送按键组合，如 "Ctrl+S" / "Enter" / "F5" / "Alt+F4"。</summary>
    public static bool SendKeys(string combo)
    {
        var parts = (combo ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (parts.Length == 0) return false;

        var mods = new List<byte>();
        byte mainVk = 0;
        foreach (var p in parts)
        {
            byte vk = NameToVk(p);
            if (vk == 0) return false;
            if (IsModifier(p)) mods.Add(vk); else mainVk = vk;
        }
        if (mainVk == 0 && mods.Count > 0) { mainVk = mods[^1]; mods.RemoveAt(mods.Count - 1); }

        // **必须带正确的扫描码**：实测 keybd_event(vk, 0, ...) 传 0 扫描码时，
        // 功能键（F10 等）在 Kontakt 里完全不生效 —— 用 MapVirtualKey 取真实扫描码。
        foreach (var m in mods) keybd_event(m, (byte)MapVirtualKey(m, 0), 0, IntPtr.Zero);
        Thread.Sleep(40);
        if (mainVk != 0)
        {
            byte sc = (byte)MapVirtualKey(mainVk, 0);
            keybd_event(mainVk, sc, 0, IntPtr.Zero);
            Thread.Sleep(50);
            keybd_event(mainVk, sc, KEYEVENTF_KEYUP, IntPtr.Zero);
        }
        Thread.Sleep(40);
        for (int i = mods.Count - 1; i >= 0; i--)
            keybd_event(mods[i], (byte)MapVirtualKey(mods[i], 0), KEYEVENTF_KEYUP, IntPtr.Zero);
        return true;
    }

    private static bool IsModifier(string s) =>
        s.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || s.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Alt", StringComparison.OrdinalIgnoreCase) || s.Equals("Shift", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Win", StringComparison.OrdinalIgnoreCase);

    /// <summary>按键名 → 虚拟键码。</summary>
    public static byte NameToVk(string name)
    {
        string n = name.Trim().ToUpperInvariant();
        switch (n)
        {
            case "CTRL": case "CONTROL": return 0x11;
            case "ALT": return 0x12;
            case "SHIFT": return 0x10;
            case "WIN": return 0x5B;
            case "ENTER": case "RETURN": return 0x0D;
            case "TAB": return 0x09;
            case "ESC": case "ESCAPE": return 0x1B;
            case "SPACE": return 0x20;
            case "BACKSPACE": return 0x08;
            case "DELETE": case "DEL": return 0x2E;
            case "HOME": return 0x24;
            case "END": return 0x23;
            case "PAGEUP": return 0x21;
            case "PAGEDOWN": return 0x22;
            case "UP": return 0x26;
            case "DOWN": return 0x28;
            case "LEFT": return 0x25;
            case "RIGHT": return 0x27;
        }
        if (n.Length >= 2 && n[0] == 'F' && int.TryParse(n[1..], out int f) && f >= 1 && f <= 24)
            return (byte)(0x70 + f - 1);
        if (n.Length == 1)
        {
            char c = n[0];
            if (c >= 'A' && c <= 'Z') return (byte)c;
            if (c >= '0' && c <= '9') return (byte)c;
        }
        return 0;
    }

    // ══════════════════ 安全定位 ══════════════════

    /// <summary>一次「定位 → 校验 → 点击」的结果。</summary>
    public sealed class ActResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string Target { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
    }

    /// <summary>
    /// 按控件描述**重新定位**并返回其中心点；顺带做坐标合理性校验。
    /// 关键：不使用任何缓存坐标 —— 每次都重新枚举，保证点的是当前真实位置。
    /// </summary>
    public static (int X, int Y, string Desc)? Locate(IntPtr hwnd, string name, string type, string cls, out string error)
    {
        error = "";
        AutomationElement root;
        try { root = AutomationElement.FromHandle(hwnd); }
        catch (Exception ex) { error = "无法读取窗口：" + ex.Message; return null; }
        if (root == null) { error = "无法取得窗口元素"; return null; }

        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        int scanned = 0;
        while (q.Count > 0 && scanned < 3000)
        {
            var el = q.Dequeue(); scanned++;
            string ct, nm, cn;
            try
            {
                ct = (el.Current.ControlType.ProgrammaticName ?? "").Replace("ControlType.", "");
                nm = el.Current.Name ?? "";
                cn = el.Current.ClassName ?? "";
            }
            catch { continue; }

            bool ok = true;
            if (name.Length > 0 && !nm.Equals(name, StringComparison.OrdinalIgnoreCase)) ok = false;
            if (type.Length > 0 && !ct.Equals(type, StringComparison.OrdinalIgnoreCase)) ok = false;
            if (cls.Length > 0 && !cn.Contains(cls, StringComparison.OrdinalIgnoreCase)) ok = false;

            if (ok && (name.Length > 0 || type.Length > 0 || cls.Length > 0))
            {
                var r = el.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) { error = $"控件「{nm}」尺寸为 0（可能不可见）"; return null; }
                if (el.Current.IsOffscreen) { error = $"控件「{nm}」在屏幕外，无法点击"; return null; }
                return ((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2),
                        $"<{ct}> '{nm}' rect={r.X:F0},{r.Y:F0} {r.Width:F0}x{r.Height:F0}");
            }

            if (scanned < 2500)
            {
                try
                {
                    var walker = TreeWalker.ControlViewWalker;
                    var c = walker.GetFirstChild(el);
                    while (c != null) { q.Enqueue(c); c = walker.GetNextSibling(c); }
                }
                catch { }
            }
        }
        error = $"未找到匹配控件（name='{name}' type='{type}' className='{cls}'）";
        return null;
    }
}
