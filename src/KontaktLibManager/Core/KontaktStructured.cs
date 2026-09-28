using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace KontaktLibManager.Core;

/// <summary>
/// Kontakt 新版（Lumen/QML）浏览器的**结构化自动化**。
///
/// **为什么能这么做**（探针实测结论）：
///   新版浏览器是 Qt Quick/QML 写的，Qt 为 QML **内置了 QAccessible 无障碍实现**，
///   所以 UIA 里能拿到完整控件树，并且三种 Pattern **全部可用**：
///     · Invoke        —— 按钮可直接「调用」，不用鼠标
///     · SelectionItem —— 页签可直接「选中」
///     · Value         —— 输入框可直接读写文本
///
///   这比坐标点击**精确得多、也不怕窗口移动/缩放**。
///
/// **限制**：老版（classic）界面是自绘 QWidget，**没有注册任何无障碍对象**
///   （UIA 只有 28 个头部节点、MSAA accChildCount=1），只能走坐标点击。
///   所以本类只服务新版浏览器；老版请用 <see cref="KontaktSkills.ClassicSearch"/> 等坐标方案。
/// </summary>
public static class KontaktStructured
{
    private static string S(object o) => ToolJson.S(o);

    private static System.Text.Json.JsonElement Parse(string json)
    {
        try { using var d = System.Text.Json.JsonDocument.Parse(json.Length > 0 ? json : "{}"); return d.RootElement.Clone(); }
        catch { using var d = System.Text.Json.JsonDocument.Parse("{}"); return d.RootElement.Clone(); }
    }

    private static string GetStr(System.Text.Json.JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static bool GetBool(System.Text.Json.JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

    /// <summary>按控件类名前缀 + 名字查找（新版控件的类名很有辨识度，如 LumenTab_QMLTYPE_39）。</summary>
    private static AutomationElement? FindByClass(AutomationElement root, string classPrefix, string? nameEquals = null, int max = 800)
    {
        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        int n = 0;
        while (q.Count > 0 && n < max)
        {
            var cur = q.Dequeue(); n++;
            AutomationElementCollection kids;
            try { kids = cur.FindAll(TreeScope.Children, Condition.TrueCondition); } catch { continue; }
            foreach (AutomationElement k in kids)
            {
                try
                {
                    string cls = k.Current.ClassName ?? "";
                    if (cls.StartsWith(classPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (nameEquals == null) return k;
                        string nm = k.Current.Name ?? "";
                        if (nm.Equals(nameEquals, StringComparison.OrdinalIgnoreCase)) return k;
                    }
                    q.Enqueue(k);
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>找出所有指定类名前缀的元素。</summary>
    private static List<AutomationElement> FindAllByClass(AutomationElement root, string classPrefix, int max = 800)
    {
        var res = new List<AutomationElement>();
        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        int n = 0;
        while (q.Count > 0 && n < max)
        {
            var cur = q.Dequeue(); n++;
            AutomationElementCollection kids;
            try { kids = cur.FindAll(TreeScope.Children, Condition.TrueCondition); } catch { continue; }
            foreach (AutomationElement k in kids)
            {
                try
                {
                    string cls = k.Current.ClassName ?? "";
                    if (cls.StartsWith(classPrefix, StringComparison.OrdinalIgnoreCase)) res.Add(k);
                    q.Enqueue(k);
                }
                catch { }
            }
        }
        return res;
    }

    /// <summary>用 SelectionItem 选中一个页签（比坐标点击可靠）。</summary>
    private static bool SelectItem(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object p) && p is SelectionItemPattern sip)
            {
                sip.Select();
                return true;
            }
        }
        catch { }
        // 回退：用 Invoke
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object p2) && p2 is InvokePattern ip)
            {
                ip.Invoke();
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>用 Value 模式写文本（搜索框）。</summary>
    private static bool SetValue(AutomationElement el, string text)
    {
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object p) && p is ValuePattern vp)
            {
                vp.SetValue(text);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>把新版浏览器里的可操作控件列出来（供 Agent 了解界面现状）。</summary>
    public static string DumpControls(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        if (spec.Length == 0) spec = "Kontakt 8";
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return S(new { error = $"未找到窗口：{spec}" });

        var root = AutomationElement.FromHandle(w.Value.Hwnd);
        var mode = KontaktSkills.DetectMode(root);
        var items = new List<object>();

        foreach (var (prefix, kind) in new[]
        {
            ("LumenTextField", "searchBox"),
            ("LumenTab_QMLTYPE", "typeTab"),
            ("LumenTabWithSeparator", "filterTab"),
            ("LumenButton", "button"),
            ("LumenMenuOpener", "menu"),
            ("HoverableTextButton", "headerButton"),
            ("HoverableImageButton", "iconButton"),
        })
        {
            foreach (var el in FindAllByClass(root, prefix).Take(30))
            {
                string name = "", cls = "", id = "";
                System.Windows.Rect r = System.Windows.Rect.Empty;
                var pats = new List<string>();
                try { name = el.Current.Name ?? ""; } catch { }
                try { cls = el.Current.ClassName ?? ""; } catch { }
                try { id = el.Current.AutomationId ?? ""; } catch { }
                try { r = el.Current.BoundingRectangle; } catch { }
                try
                {
                    if (el.TryGetCurrentPattern(InvokePattern.Pattern, out _)) pats.Add("Invoke");
                    if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)) pats.Add("SelectionItem");
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out _)) pats.Add("Value");
                }
                catch { }
                items.Add(new
                {
                    kind, name, cls, id,
                    x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height,
                    patterns = pats,
                });
            }
        }

        return S(new
        {
            ok = true,
            uiMode = mode.ToString(),
            structured = mode == KontaktSkills.UiMode.NewLumen,
            note = mode == KontaktSkills.UiMode.NewLumen
                ? "新版浏览器支持结构化操作（Invoke/SelectionItem/Value）。"
                : "当前是老版界面（自绘 QWidget，无无障碍对象），只能用坐标操作；可先用 kontakt_switch_ui_mode 切到新版。",
            count = items.Count,
            controls = items,
        });
    }

    /// <summary>选中新版浏览器的「类型」页签：Instruments / Combined / Tools / Leap / Loops / One-shots。</summary>
    public static string SelectType(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window"); if (spec.Length == 0) spec = "Kontakt 8";
        string want = GetStr(a, "type");
        if (want.Length == 0) return S(new { error = "需要 type 参数", options = KontaktSkills.ContentTypes });

        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return S(new { error = $"未找到窗口：{spec}" });
        var root = AutomationElement.FromHandle(w.Value.Hwnd);

        var tabs = FindAllByClass(root, "LumenTab_QMLTYPE");
        foreach (var t in tabs)
        {
            string nm = "";
            try { nm = t.Current.Name ?? ""; } catch { }
            if (nm.Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                bool ok = SelectItem(t);
                return S(new { ok, type = nm, method = "SelectionItem", message = ok ? $"已切到「{nm}」" : "该页签不支持编程选中" });
            }
        }
        return S(new { ok = false, message = $"没找到类型页签「{want}」", found = tabs.Select(t => { try { return t.Current.Name; } catch { return ""; } }).Where(s => s.Length > 0).ToList() });
    }

    /// <summary>选中筛选页签：Brand / Sound Type / Character。</summary>
    public static string SelectFilter(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window"); if (spec.Length == 0) spec = "Kontakt 8";
        string want = GetStr(a, "filter");
        if (want.Length == 0) return S(new { error = "需要 filter 参数", options = KontaktSkills.Filters });

        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return S(new { error = $"未找到窗口：{spec}" });
        var root = AutomationElement.FromHandle(w.Value.Hwnd);

        foreach (var t in FindAllByClass(root, "LumenTabWithSeparator"))
        {
            string nm = "";
            try { nm = t.Current.Name ?? ""; } catch { }
            if (nm.Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                bool ok = SelectItem(t);
                return S(new { ok, filter = nm, method = "SelectionItem", message = ok ? $"已切到筛选「{nm}」" : "该页签不支持编程选中" });
            }
        }
        return S(new { ok = false, message = $"没找到筛选页签「{want}」" });
    }

    /// <summary>直接往搜索框写文本（用 Value 模式，不模拟键盘）。</summary>
    public static string SetSearch(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window"); if (spec.Length == 0) spec = "Kontakt 8";
        string text = GetStr(a, "text");
        bool submit = GetBool(a, "submit");

        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return S(new { error = $"未找到窗口：{spec}" });
        var root = AutomationElement.FromHandle(w.Value.Hwnd);

        var box = FindByClass(root, "LumenTextField");
        if (box == null) return S(new { ok = false, message = "没找到搜索框（当前可能不是新版浏览器界面）" });

        bool ok = SetValue(box, text);
        if (!ok) return S(new { ok = false, message = "搜索框不支持 Value 模式" });

        if (submit)
        {
            // 新版搜索是即输即筛，无需回车；这里只在用户明确要求时补一次
            UiInput.SendKeys("ENTER");
            Thread.Sleep(600);
        }
        Thread.Sleep(500);
        return S(new { ok = true, text, method = "ValuePattern", message = $"已把搜索框设为「{text}」" });
    }

    /// <summary>按名字点一个新版按钮（用 Invoke，不用坐标）。</summary>
    public static string ClickButton(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window"); if (spec.Length == 0) spec = "Kontakt 8";
        string name = GetStr(a, "name");
        if (name.Length == 0) return S(new { error = "需要 name 参数" });

        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return S(new { error = $"未找到窗口：{spec}" });
        var root = AutomationElement.FromHandle(w.Value.Hwnd);

        // 优先精确匹配，其次包含匹配
        AutomationElement? hit = null;
        foreach (var prefix in new[] { "LumenButton", "LumenMenuOpener", "HoverableTextButton", "HoverableImageButton", "LumenTab" })
        {
            foreach (var el in FindAllByClass(root, prefix))
            {
                string nm = "";
                try { nm = el.Current.Name ?? ""; } catch { }
                if (nm.Length == 0) continue;
                if (nm.Equals(name, StringComparison.OrdinalIgnoreCase)) { hit = el; break; }
                if (hit == null && nm.Contains(name, StringComparison.OrdinalIgnoreCase)) hit = el;
            }
            if (hit != null) break;
        }
        if (hit == null) return S(new { ok = false, message = $"没找到名为「{name}」的按钮" });

        bool ok = SelectItem(hit);   // Invoke 或 SelectionItem
        string got = "";
        try { got = hit.Current.Name ?? ""; } catch { }
        return S(new { ok, name = got, method = "Invoke", message = ok ? $"已点击「{got}」" : "该控件不支持编程点击" });
    }
}
