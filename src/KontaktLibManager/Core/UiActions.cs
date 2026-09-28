using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>
/// 界面**写操作**工具（点击 / 输入 / 按键 / 滚动）。
///
/// 权限模型（与 run_command、run_script 一致）：
///   **预览动作 → 用户确认 → 执行**，且执行时**重新定位控件**拿实时坐标。
///
/// 两段式设计的原因：
///   · <see cref="Preview"/> 只做「定位 + 校验」，把要点的东西描述给用户看；
///   · 用户确认后 <see cref="Execute"/> 再跑一遍定位 —— 这期间界面可能变了，
///     用旧坐标点下去就是事故。**绝不缓存坐标**。
/// </summary>
public static class UiActions
{
    /// <summary>动作预览（给用户看的一句话 + 目标控件描述）。</summary>
    public sealed class PreviewResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        /// <summary>给用户看的动作描述，如「在 Kontakt 8 里点击 &lt;RadioButton&gt; 'Tools'」</summary>
        public string Description { get; set; } = "";
        /// <summary>目标控件描述（含实时矩形）</summary>
        public string Target { get; set; } = "";
        public string Action { get; set; } = "";
        public string WindowTitle { get; set; } = "";
    }

    // ══════════════════ 预览 ══════════════════

    public static PreviewResult Preview(string action, string argsJson)
    {
        var args = Parse(argsJson);
        var pr = new PreviewResult { Action = action };
        string spec = GetStr(args, "window");
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) { pr.Error = $"未找到窗口：{spec}"; return pr; }
        pr.WindowTitle = w.Value.Title;

        string name = GetStr(args, "name");
        string type = GetStr(args, "type");
        string cls = GetStr(args, "className");

        switch (action)
        {
            case "click":
            {
                var loc = UiInput.Locate(w.Value.Hwnd, name, type, cls, out string err);
                if (loc == null) { pr.Error = err; return pr; }
                pr.Target = loc.Value.Desc;
                string kind = GetBool(args, "doubleClick") ? "双击" : GetBool(args, "right") ? "右键点击" : "点击";
                pr.Description = $"在「{w.Value.Title}」里{kind}控件 {loc.Value.Desc}（屏幕坐标 {loc.Value.X},{loc.Value.Y}）";
                pr.Ok = true;
                return pr;
            }
            case "type":
            {
                string text = GetStr(args, "text");
                if (text.Length == 0) { pr.Error = "需要 text 参数"; return pr; }
                if (name.Length > 0 || cls.Length > 0)
                {
                    var loc = UiInput.Locate(w.Value.Hwnd, name, type, cls, out string err);
                    if (loc == null) { pr.Error = err; return pr; }
                    pr.Target = loc.Value.Desc;
                    pr.Description = $"在「{w.Value.Title}」里先点击 {loc.Value.Desc}，再输入文本：{Trim(text)}";
                }
                else
                {
                    pr.Description = $"向「{w.Value.Title}」（当前焦点控件）输入文本：{Trim(text)}";
                }
                pr.Ok = true;
                return pr;
            }
            case "key":
            {
                string keys = GetStr(args, "keys");
                if (keys.Length == 0) { pr.Error = "需要 keys 参数，如 Ctrl+S / Enter / F5"; return pr; }
                pr.Description = $"向「{w.Value.Title}」发送按键：{keys}";
                pr.Ok = true;
                return pr;
            }
            case "scroll":
            {
                var loc = UiInput.Locate(w.Value.Hwnd, name, type, cls, out string err);
                if (loc == null) { pr.Error = err; return pr; }
                long n = GetLong(args, "notches");
                if (n == 0) n = -3;
                pr.Target = loc.Value.Desc;
                pr.Description = $"在「{w.Value.Title}」的 {loc.Value.Desc} 处滚动 {n} 格（{(n > 0 ? "向上" : "向下")}）";
                pr.Ok = true;
                return pr;
            }
            default:
                pr.Error = "未知动作：" + action;
                return pr;
        }
    }

    // ══════════════════ 执行 ══════════════════

    public static string Execute(string action, string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}" });

        IntPtr prevFocus = IntPtr.Zero;
        try
        {
            prevFocus = UiInput.FocusWindow(w.Value.Hwnd);

            string name = GetStr(args, "name");
            string type = GetStr(args, "type");
            string cls = GetStr(args, "className");

            switch (action)
            {
                case "click":
                {
                    var loc = UiInput.Locate(w.Value.Hwnd, name, type, cls, out string err);
                    if (loc == null) return ToolJson.S(new { error = err });
                    if (GetBool(args, "doubleClick")) UiInput.DoubleClickAt(loc.Value.X, loc.Value.Y);
                    else UiInput.ClickAt(loc.Value.X, loc.Value.Y, GetBool(args, "right"));
                    Thread.Sleep(400);
                    return ToolJson.S(new { ok = true, action = "click", target = loc.Value.Desc, at = new { x = loc.Value.X, y = loc.Value.Y } });
                }
                case "type":
                {
                    string text = GetStr(args, "text");
                    if (name.Length > 0 || cls.Length > 0)
                    {
                        var loc = UiInput.Locate(w.Value.Hwnd, name, type, cls, out string err);
                        if (loc == null) return ToolJson.S(new { error = err });
                        UiInput.ClickAt(loc.Value.X, loc.Value.Y);
                        Thread.Sleep(250);
                    }
                    UiInput.SendText(text);
                    Thread.Sleep(200);
                    return ToolJson.S(new { ok = true, action = "type", chars = text.Length });
                }
                case "key":
                {
                    string keys = GetStr(args, "keys");
                    bool ok = UiInput.SendKeys(keys);
                    Thread.Sleep(250);
                    return ToolJson.S(new { ok, action = "key", keys, message = ok ? "" : "无法解析该按键组合" });
                }
                case "scroll":
                {
                    var loc = UiInput.Locate(w.Value.Hwnd, name, type, cls, out string err);
                    if (loc == null) return ToolJson.S(new { error = err });
                    long n = GetLong(args, "notches");
                    if (n == 0) n = -3;
                    UiInput.ScrollAt(loc.Value.X, loc.Value.Y, (int)n);
                    Thread.Sleep(300);
                    return ToolJson.S(new { ok = true, action = "scroll", notches = n });
                }
                default:
                    return ToolJson.S(new { error = "未知动作：" + action });
            }
        }
        catch (Exception ex)
        {
            return ToolJson.S(new { error = "执行失败：" + ex.Message });
        }
        finally
        {
            // 不抢占用户焦点：操作完把前台窗口还给用户
            UiInput.RestoreFocus(prevFocus);
        }
    }

    // ══════════════════ 小工具 ══════════════════

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "…";

    private static JsonElement Parse(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return default; }
    }

    private static string GetStr(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";

    private static long GetLong(JsonElement e, string n)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long x) ? x : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out long y) ? y : 0,
            _ => 0,
        };
    }

    private static bool GetBool(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
}
