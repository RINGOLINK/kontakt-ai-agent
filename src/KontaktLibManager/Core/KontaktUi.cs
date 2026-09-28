using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace KontaktLibManager.Core;

/// <summary>
/// Kontakt 的**按名字结构化操作**（替代脆弱的坐标点击）。
///
/// **能力来自版本**（实测对比）：
///   · 便携版 8.7.1 —— 老版界面只有 28 个 UIA 节点（仅头部，且按钮**无名**），
///                     只能坐标点击；新版浏览器（QML）可用结构化。
///   · 完整版 8.13.1 —— **全盘 Qt/QML 绘制且完全暴露**，老版界面 55 个节点，
///                     产品列表里**每个库都是带名字的 Button**（`SidePanelProductTileListItem`，
///                     支持 Invoke），头部按钮也有了名字（`Kontakt File Menu` / `Play View` / `Library`）。
///
/// 所以本类的策略是：**能按名字就按名字**（精确、不怕窗口移动/缩放），
/// 找不到名字再回退到坐标（保底）。由 <see cref="HasProductList"/> 判断当前是否具备结构化能力。
/// </summary>
public static class KontaktUi
{
    private static string S(object o) => ToolJson.S(o);

    private static System.Text.Json.JsonElement Parse(string json)
    {
        try { using var d = System.Text.Json.JsonDocument.Parse(json.Length > 0 ? json : "{}"); return d.RootElement.Clone(); }
        catch { using var d = System.Text.Json.JsonDocument.Parse("{}"); return d.RootElement.Clone(); }
    }
    private static string GetStr(System.Text.Json.JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? "") : "";

    // ══════════════ 基础查找 ══════════════

    /// <summary>广度优先按名字找（精确 → 包含）。</summary>
    private static AutomationElement? ByName(AutomationElement root, string name, bool contains = false, int max = 2000)
    {
        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        int n = 0;
        AutomationElement? loose = null;
        while (q.Count > 0 && n < max)
        {
            var cur = q.Dequeue(); n++;
            AutomationElementCollection kids;
            try { kids = cur.FindAll(TreeScope.Children, Condition.TrueCondition); } catch { continue; }
            foreach (AutomationElement k in kids)
            {
                try
                {
                    string nm = k.Current.Name ?? "";
                    if (nm.Length > 0)
                    {
                        if (nm.Equals(name, StringComparison.OrdinalIgnoreCase)) return k;
                        if (contains && loose == null && nm.Contains(name, StringComparison.OrdinalIgnoreCase)) loose = k;
                    }
                    q.Enqueue(k);
                }
                catch { }
            }
        }
        return loose;
    }

    /// <summary>按控件类名前缀找（类名后缀会变，所以用前缀）。</summary>
    private static List<AutomationElement> ByClass(AutomationElement root, string classPrefix, int max = 2000)
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

    private static bool Invoke(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object p) && p is InvokePattern ip) { ip.Invoke(); return true; }
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object p2) && p2 is SelectionItemPattern sp) { sp.Select(); return true; }
        }
        catch { }
        // 回退：坐标点击（老版本无名控件）
        try
        {
            var r = el.Current.BoundingRectangle;
            if (r.Width > 0 && r.Height > 0)
            {
                UiInput.ClickAt((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                return true;
            }
        }
        catch { }
        return false;
    }

    private static AutomationElement? ResolveWindow(string spec, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (spec.Length == 0) spec = "Kontakt 8";
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return null;
        hwnd = w.Value.Hwnd;
        return AutomationElement.FromHandle(hwnd);
    }

    /// <summary>当前是否具备「按名字操作」的结构化能力（完整版 8.13.1+ 有产品列表）。</summary>
    public static bool HasProductList(AutomationElement root)
        => ByClass(root, "SidePanelProductTileList").Count > 0;

    // ══════════════ 对外动作 ══════════════

    /// <summary>
    /// **列出声明的音色库**（读产品列表里的按钮名）。
    /// 这是「Kontakt 里到底装了哪些库」最直接的答案，不依赖我们的索引。
    /// </summary>
    public static string ListLibraries(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        var root = ResolveWindow(spec, out _);
        if (root == null) return S(new { error = $"未找到窗口：{(spec.Length == 0 ? "Kontakt 8" : spec)}" });

        var tiles = ByClass(root, "SidePanelProductTileListItem");
        var names = tiles.Select(t => { try { return t.Current.Name ?? ""; } catch { return ""; } })
                         .Where(s => s.Length > 0).ToList();

        return S(new
        {
            ok = true,
            structured = HasProductList(root),
            count = names.Count,
            libraries = names,
            note = names.Count > 0
                ? "这些是 Kontakt 当前列表里可见的库（可能需要滚动才能看到全部）。"
                : "没读到库列表。可能当前不在「产品列表」视图，或该 Kontakt 版本未暴露（便携版 8.7.1 老版界面即是如此）。",
        });
    }

    /// <summary>**按名字加载音色库**（点产品列表项，用 Invoke 而非坐标）。</summary>
    public static string LoadLibrary(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        string name = GetStr(a, "name");
        if (name.Length == 0) return S(new { error = "需要 name 参数（音色库名，可部分匹配）" });

        var root = ResolveWindow(spec, out var hwnd);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });

        // ① 优先：产品列表里的项
        var tiles = ByClass(root, "SidePanelProductTileListItem");
        AutomationElement? hit = null; string hitName = "";
        foreach (var t in tiles)
        {
            string nm = ""; try { nm = t.Current.Name ?? ""; } catch { }
            if (nm.Length == 0) continue;
            if (nm.Equals(name, StringComparison.OrdinalIgnoreCase)) { hit = t; hitName = nm; break; }
            if (hit == null && nm.Contains(name, StringComparison.OrdinalIgnoreCase)) { hit = t; hitName = nm; }
        }

        if (hit == null)
        {
            // ② 回退：全树按名字找任意可点项
            hit = ByName(root, name, contains: true);
            if (hit != null) { try { hitName = hit.Current.Name ?? name; } catch { } }
        }
        if (hit == null)
        {
            var visible = tiles.Select(t => { try { return t.Current.Name ?? ""; } catch { return ""; } })
                               .Where(s => s.Length > 0).Take(30).ToList();
            return S(new { ok = false, message = $"列表里没找到「{name}」", visible, hint = "可能需要先滚动列表，或该库不在当前分类页签下。" });
        }

        IntPtr prev = UiInput.FocusWindow(hwnd);
        try
        {
            bool ok = Invoke(hit);
            Thread.Sleep(1500);
            return S(new { ok, loaded = hitName, method = "Invoke(byName)", message = ok ? $"已点击「{hitName}」" : "点击失败" });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    /// <summary>滚动产品列表（用 ScrollBar 的 RangeValue，或用滚轮）。</summary>
    public static string ScrollList(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        string dir = GetStr(a, "direction"); if (dir.Length == 0) dir = "down";
        long amount = 3;
        if (a.TryGetProperty("amount", out var av) && av.ValueKind == System.Text.Json.JsonValueKind.Number && av.TryGetInt64(out long n)) amount = Math.Clamp(n, 1, 30);

        var root = ResolveWindow(spec, out var hwnd);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });

        // 优先用滚动条的范围值（精确），否则退回滚轮
        var bars = ByClass(root, "LumenScrollBar");
        var list = ByClass(root, "SidePanelProductTileList").FirstOrDefault();
        IntPtr prev = UiInput.FocusWindow(hwnd);
        try
        {
            if (bars.Count > 0 && list != null)
            {
                try
                {
                    if (bars[0].TryGetCurrentPattern(RangeValuePattern.Pattern, out object p) && p is RangeValuePattern rv)
                    {
                        double step = rv.Current.LargeChange > 0 ? rv.Current.LargeChange : 100;
                        double target = dir == "up" ? rv.Current.Value - step * amount : rv.Current.Value + step * amount;
                        target = Math.Max(rv.Current.Minimum, Math.Min(rv.Current.Maximum, target));
                        rv.SetValue(target);
                        Thread.Sleep(700);
                        return S(new { ok = true, method = "RangeValue", value = target, min = rv.Current.Minimum, max = rv.Current.Maximum });
                    }
                }
                catch { }
            }
            if (list != null)
            {
                var r = list.Current.BoundingRectangle;
                if (r.Width > 0)
                {
                    UiInput.ScrollAt((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2), dir == "up" ? (int)amount : -(int)amount);
                    Thread.Sleep(700);
                    return S(new { ok = true, method = "wheel" });
                }
            }
            return S(new { ok = false, message = "找不到可滚动的产品列表" });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    /// <summary>切换顶部两个页面按钮：Play View（标签页B）/ Library（标签页A）。</summary>
    public static string SwitchPage(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        string page = GetStr(a, "page");
        if (page.Length == 0) return S(new { error = "需要 page 参数：play（Play View / 标签页B）或 library（Library / 标签页A）" });

        var root = ResolveWindow(spec, out var hwnd);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });

        string target = page.StartsWith("play", StringComparison.OrdinalIgnoreCase) ? "Play View" : "Library";
        var btn = ByName(root, target);
        if (btn == null) return S(new { ok = false, message = $"未找到「{target}」按钮（该 Kontakt 版本可能未暴露按钮名）" });

        IntPtr prev = UiInput.FocusWindow(hwnd);
        try
        {
            bool ok = Invoke(btn);
            Thread.Sleep(1800);
            return S(new { ok, page = target, method = "Invoke(byName)", message = ok ? $"已切到 {target}" : "切换失败" });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    /// <summary>点一个按名字暴露的按钮（Add instrument / Add tool / 面板开关 / 排序等）。</summary>
    public static string ClickNamed(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        string name = GetStr(a, "name");
        if (name.Length == 0) return S(new { error = "需要 name 参数" });

        var root = ResolveWindow(spec, out var hwnd);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });

        var el = ByName(root, name, contains: true);
        if (el == null) return S(new { ok = false, message = $"没找到名为「{name}」的控件" });
        string got = ""; try { got = el.Current.Name ?? ""; } catch { }

        IntPtr prev = UiInput.FocusWindow(hwnd);
        try
        {
            bool ok = Invoke(el);
            Thread.Sleep(1200);
            return S(new { ok, clicked = got, method = "Invoke(byName)", message = ok ? $"已点击「{got}」" : "点击失败" });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    /// <summary>
    /// **诊断当前 Kontakt 的自动化能力**：是否具备结构化（按名字）能力、有哪些可用控件。
    /// 换版本/换界面后先跑它，能立刻知道该用哪套方案。
    /// </summary>
    public static string Capabilities(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        var root = ResolveWindow(spec, out _);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });

        bool structured = HasProductList(root);
        var headerBtns = ByClass(root, "HoverableImageButton")
            .Select(e => { try { return e.Current.Name ?? ""; } catch { return ""; } })
            .Where(s => s.Length > 0).Take(20).ToList();
        var tileNames = ByClass(root, "SidePanelProductTileListItem")
            .Select(e => { try { return e.Current.Name ?? ""; } catch { return ""; } })
            .Where(s => s.Length > 0).Take(10).ToList();

        var areas = new List<string>();
        foreach (var (cls, label) in new[]
        {
            ("SidePanelProductTileList", "产品列表"),
            ("SidePanelBrowser", "浏览器侧栏"),
            ("Navigator", "Navigator（槽位）"),
            ("FileTypeSelector", "文件类型页签"),
            ("AppHeader", "顶部工具栏"),
            ("StatusBar", "状态栏"),
            ("LumenTextField", "搜索框"),
        })
            if (ByClass(root, cls).Count > 0) areas.Add(label);

        return S(new
        {
            ok = true,
            structured,
            mode = KontaktSkills.DetectMode(root).ToString(),
            availableAreas = areas,
            headerButtons = headerBtns,
            sampleLibraries = tileNames,
            advice = structured
                ? "具备结构化能力：优先用 kontakt_load_library / kontakt_click_named / kontakt_switch_page（按名字，精确可靠）。"
                : "当前不具备结构化能力（老版本界面）：只能坐标操作，或用 kontakt_switch_ui_mode 切到新版浏览器。",
        });
    }

    // ══════════════ 文件菜单（完整版 8.13.1 完全暴露，含 14 个具名项）══════════════

    /// <summary>
    /// 打开并列出 Kontakt 的**文件菜单**（`Kontakt File Menu`）。
    /// 实测该菜单完全暴露，项都是具名 MenuItem 且支持 Invoke：
    /// New instrument / New instrument bank / Load... / Load recent / Save multi as... /
    /// Save as default multi / Reset multi / Batch resave / Collect samples / Batch compress /
    /// Global purge / Switch to Default View / Zoom / Options... / Controller
    /// </summary>
    public static string FileMenu(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        var root = ResolveWindow(spec, out var hwnd);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });

        var btn = ByName(root, "Kontakt File Menu");
        if (btn == null) return S(new { ok = false, message = "该 Kontakt 版本未暴露 'Kontakt File Menu'（便携版 8.7.1 即是如此）" });

        IntPtr prev = UiInput.FocusWindow(hwnd);
        try
        {
            Invoke(btn);
            Thread.Sleep(1200);
            var items = ByClass(root, "HidableMenuItem").Concat(ByClass(root, "LumenMenuItem"))
                .Select(e => { try { return e.Current.Name ?? ""; } catch { return ""; } })
                .Where(s => s.Length > 0).Distinct().ToList();
            return S(new
            {
                ok = true,
                count = items.Count,
                items,
                hint = "用 kontakt_menu_click 按名字点其中任意一项。",
            });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    /// <summary>
    /// 按名字点击 Kontakt 文件菜单里的一项。
    /// 常用项：'New instrument'（= Add instrument 的实际动作）、'Load...'、'Batch resave'、
    /// 'Collect samples / Batch compress'、'Global purge'、'Switch to Default View'、'Options...'。
    /// </summary>
    public static string MenuClick(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        string name = GetStr(a, "name");
        if (name.Length == 0) return S(new { error = "需要 name 参数（菜单项名，可部分匹配）" });

        var root = ResolveWindow(spec, out var hwnd);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });

        IntPtr prev = UiInput.FocusWindow(hwnd);
        try
        {
            // 菜单可能没开 —— 先确保打开
            var items = ByClass(root, "HidableMenuItem").Concat(ByClass(root, "LumenMenuItem")).ToList();
            if (items.Count == 0)
            {
                var btn = ByName(root, "Kontakt File Menu");
                if (btn == null) return S(new { ok = false, message = "未暴露 'Kontakt File Menu'" });
                Invoke(btn);
                Thread.Sleep(1200);
                items = ByClass(root, "HidableMenuItem").Concat(ByClass(root, "LumenMenuItem")).ToList();
            }

            AutomationElement? hit = null; string hitName = "";
            foreach (var it in items)
            {
                string nm = ""; try { nm = it.Current.Name ?? ""; } catch { }
                if (nm.Length == 0) continue;
                if (nm.Equals(name, StringComparison.OrdinalIgnoreCase)) { hit = it; hitName = nm; break; }
                if (hit == null && nm.Contains(name, StringComparison.OrdinalIgnoreCase)) { hit = it; hitName = nm; }
            }
            if (hit == null)
                return S(new { ok = false, message = $"菜单里没找到「{name}」", available = items.Select(i => { try { return i.Current.Name ?? ""; } catch { return ""; } }).Where(s => s.Length > 0).ToList() });

            bool ok = Invoke(hit);
            Thread.Sleep(2000);
            return S(new { ok, clicked = hitName, method = "Invoke(menuItem)", message = ok ? $"已点击菜单项「{hitName}」" : "点击失败" });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    // ══════════════ 面板开关（Side Pane / Info Pane / Keyboard）══════════════

    /// <summary>切换 Kontakt 的面板：side（F1 侧栏）/ info（F9 信息栏）/ keyboard（F3 键盘）。</summary>
    public static string TogglePane(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        string pane = GetStr(a, "pane").ToLowerInvariant();
        if (pane.Length == 0) return S(new { error = "需要 pane 参数：side / info / keyboard" });

        string key = pane switch
        {
            "side" => "Side Pane",
            "info" => "Info Pane",
            "keyboard" => "Keyboard",
            _ => "",
        };
        if (key.Length == 0) return S(new { error = "pane 只能是 side / info / keyboard" });

        var root = ResolveWindow(spec, out var hwnd);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });
        var btn = ByName(root, key, contains: true);
        if (btn == null) return S(new { ok = false, message = $"未找到「{key}」按钮" });

        IntPtr prev = UiInput.FocusWindow(hwnd);
        try
        {
            bool ok = Invoke(btn);
            Thread.Sleep(1200);
            return S(new { ok, pane = key, message = ok ? $"已切换 {key}" : "切换失败" });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    /// <summary>在「Play View（标签页B）」里添加槽位：kind=instrument / tool。</summary>
    public static string AddSlot(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        string kind = GetStr(a, "kind").ToLowerInvariant();
        if (kind.Length == 0) kind = "instrument";
        string key = kind.StartsWith("tool") ? "Add tool" : "Add instrument";

        var root = ResolveWindow(spec, out var hwnd);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });
        var btn = ByName(root, key);
        if (btn == null)
            return S(new { ok = false, message = $"未找到「{key}」（需要先在 Play View / 标签页B 里）", hint = "可先用 kontakt_switch_page page=play" });

        IntPtr prev = UiInput.FocusWindow(hwnd);
        try
        {
            bool ok = Invoke(btn);
            Thread.Sleep(1800);
            return S(new { ok, slot = key, message = ok ? $"已点击「{key}」" : "点击失败" });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    /// <summary>列出当前 Kontakt 窗口里所有**带名字且可 Invoke** 的控件（让 Agent 自己发现能做什么）。</summary>
    public static string ListNamed(string argsJson)
    {
        var a = Parse(argsJson);
        string spec = GetStr(a, "window");
        var root = ResolveWindow(spec, out _);
        if (root == null) return S(new { error = "未找到 Kontakt 窗口" });

        var res = new List<object>();
        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        int n = 0;
        while (q.Count > 0 && n < 2500 && res.Count < 200)
        {
            var cur = q.Dequeue(); n++;
            AutomationElementCollection kids;
            try { kids = cur.FindAll(TreeScope.Children, Condition.TrueCondition); } catch { continue; }
            foreach (AutomationElement k in kids)
            {
                try
                {
                    string nm = k.Current.Name ?? "";
                    if (nm.Length > 0)
                    {
                        bool inv = false, sel = false, val = false;
                        try { inv = k.TryGetCurrentPattern(InvokePattern.Pattern, out _); } catch { }
                        try { sel = k.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _); } catch { }
                        try { val = k.TryGetCurrentPattern(ValuePattern.Pattern, out _); } catch { }
                        if (inv || sel || val)
                        {
                            string cls = "", ct = "";
                            try { cls = k.Current.ClassName ?? ""; } catch { }
                            try { ct = k.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { }
                            res.Add(new { name = nm, type = ct, cls, canInvoke = inv, canSelect = sel, canSetValue = val });
                        }
                    }
                    q.Enqueue(k);
                }
                catch { }
            }
        }
        return S(new { ok = true, count = res.Count, controls = res });
    }
}
