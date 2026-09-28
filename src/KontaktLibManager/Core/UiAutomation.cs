using System.Text.Json;
using System.Windows.Automation;

namespace KontaktLibManager.Core;

/// <summary>
/// **Windows UI Automation 封装层** —— 让 Agent 能"看见"并（后续）操作其他程序的界面。
///
/// 实测背景（见 docs/Kontakt自动化控制能力评估.md）：
///   · Kontakt 8 是 Qt/QML 应用，无障碍层**完整桥接到 UIA**，控件树可枚举、带精确坐标；
///   · **读状态 100% 准确**（IsSelected / Value / ToggleState）；
///   · **但 UIA Pattern 的写操作是空壳**（`Select()` 调用无异常却不生效）；
///   · 因此写操作必须走「UIA 定位 → 真实鼠标/键盘注入」，本类只负责**只读探查**部分。
///
/// 设计要点：
///   · **输出必须紧凑** —— 控件树很容易上百个节点，直接全量回传会撑爆 token。
///     默认只回传可操作控件、限制深度、按需过滤类型。
///   · **不缓存坐标** —— 窗口缩放/DPI 变化都会让坐标失效，每次操作前重新枚举。
///   · 本类**只读**，不含任何点击/输入；写操作在后续阶段单独实现并走用户确认。
/// </summary>
public static class UiAutomation
{
    /// <summary>一次最多回传的控件数（防 token 爆炸）。</summary>
    public const int MaxNodes = 120;
    /// <summary>默认遍历深度。</summary>
    public const int DefaultDepth = 6;

    // ══════════════════ 窗口定位 ══════════════════

    /// <summary>按 进程名 / 窗口标题子串 / HWND 定位窗口，返回 (hwnd, title, process)。</summary>
    /// <summary>
    /// 按 进程名 / 窗口标题 / HWND 定位窗口。
    ///
    /// **分级匹配**（严格 → 宽松），因为宽松匹配会误伤：
    /// 例如关键字 "kontakt" 同时能匹配 "Kontakt 8"（目标）和
    /// "Kontakt 音色库管理器"（我们自己的应用）。
    ///   ① 进程名**完全相等**  ② 标题**完全相等**  ③ 进程名包含
    ///   ④ 标题以关键字开头    ⑤ 标题包含（最后兜底）
    /// 命中即返回，保证优先级。
    /// </summary>
    public static (IntPtr Hwnd, string Title, string Process)? ResolveWindow(string spec)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0) return null;

        // 纯数字 = HWND
        if (long.TryParse(spec, out long h) && h != 0)
        {
            try
            {
                var el0 = AutomationElement.FromHandle(new IntPtr(h));
                if (el0 == null) return null;
                return (new IntPtr(h), el0.Current.Name, "");
            }
            catch { return null; }
        }

        string lower = spec.ToLowerInvariant();
        var cands = new List<(IntPtr H, string T, string P, int Rank)>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            if (p.MainWindowHandle == IntPtr.Zero) continue;
            string pn, tt;
            try { pn = p.ProcessName; tt = p.MainWindowTitle; } catch { continue; }
            if (tt.Length == 0) continue;
            string pl = pn.ToLowerInvariant(), tl = tt.ToLowerInvariant();

            int rank = -1;
            if (pl == lower) rank = 1;
            else if (tl == lower) rank = 2;
            else if (pl.Contains(lower)) rank = 3;
            else if (tl.StartsWith(lower)) rank = 4;
            else if (tl.Contains(lower)) rank = 5;
            if (rank < 0) continue;

            cands.Add((p.MainWindowHandle, tt, pn, rank));
        }
        if (cands.Count == 0) return null;
        var best = cands.OrderBy(c => c.Rank).First();
        return (best.H, best.T, best.P);
    }

    // ══════════════════ ① 列出可控窗口 ══════════════════

    public static string ListWindows(string argsJson)
    {
        var args = Parse(argsJson);
        string filter = GetStr(args, "filter").ToLowerInvariant();
        long limit = GetLong(args, "limit");
        int max = limit is > 0 and <= 100 ? (int)limit : 40;

        var list = new List<object>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            if (p.MainWindowHandle == IntPtr.Zero) continue;
            string pn, tt;
            try { pn = p.ProcessName; tt = p.MainWindowTitle; } catch { continue; }
            if (tt.Length == 0) continue;
            if (filter.Length > 0 && !pn.ToLowerInvariant().Contains(filter) && !tt.ToLowerInvariant().Contains(filter)) continue;

            list.Add(new { process = pn, pid = p.Id, title = tt, hwnd = p.MainWindowHandle.ToInt64() });
            if (list.Count >= max) break;
        }
        return ToolJson.S(new { count = list.Count, windows = list });
    }

    // ══════════════════ ② 枚举控件树 ══════════════════

    public static string DumpTree(string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        if (spec.Length == 0) return "{\"error\":\"需要 window 参数（进程名 / 窗口标题子串 / HWND）\"}";

        var w = ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}", hint = "可用 ui_list_windows 查看当前有哪些窗口" });

        int depth = (int)(GetLong(args, "depth") is > 0 and <= 12 ? GetLong(args, "depth") : DefaultDepth);
        bool actionableOnly = GetBool(args, "actionable_only");
        string typeFilter = GetStr(args, "types").ToLowerInvariant();   // 逗号分隔，如 "button,edit"
        int max = (int)(GetLong(args, "limit") is > 0 and <= 400 ? GetLong(args, "limit") : MaxNodes);

        AutomationElement root;
        try { root = AutomationElement.FromHandle(w.Value.Hwnd); }
        catch (Exception ex) { return ToolJson.S(new { error = "无法读取窗口：" + ex.Message }); }
        if (root == null) return "{\"error\":\"无法取得窗口的 AutomationElement\"}";

        var types = typeFilter.Length > 0
            ? typeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToHashSet()
            : null;

        var nodes = new List<object>();
        int scanned = 0;
        var q = new Queue<(AutomationElement El, int Depth)>();
        q.Enqueue((root, 0));

        while (q.Count > 0 && nodes.Count < max)
        {
            var (el, d) = q.Dequeue();
            scanned++;
            string ct = "", nm = "", cn = "", aid = "";
            try
            {
                ct = (el.Current.ControlType.ProgrammaticName ?? "").Replace("ControlType.", "");
                nm = el.Current.Name ?? "";
                cn = el.Current.ClassName ?? "";
                aid = el.Current.AutomationId ?? "";
            }
            catch { continue; }

            var pats = SupportedPatterns(el);
            bool actionable = pats.Count > 0;

            bool keep = true;
            if (actionableOnly && !actionable) keep = false;
            if (types != null && !types.Contains(ct.ToLowerInvariant())) keep = false;
            // 无名字又不可操作的节点（纯容器）默认不回传，除非根
            if (d > 0 && nm.Length == 0 && !actionable && aid.Length == 0) keep = false;

            if (keep)
            {
                var rect = el.Current.BoundingRectangle;
                nodes.Add(new
                {
                    depth = d,
                    type = ct,
                    name = nm.Length > 60 ? nm[..60] : nm,
                    cls = cn.Length > 48 ? cn[..48] : cn,
                    id = aid,
                    patterns = pats,
                    x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height,
                });
            }

            if (d < depth)
            {
                try
                {
                    var walker = TreeWalker.ControlViewWalker;
                    var child = walker.GetFirstChild(el);
                    while (child != null)
                    {
                        q.Enqueue((child, d + 1));
                        child = walker.GetNextSibling(child);
                    }
                }
                catch { }
            }
        }

        return ToolJson.S(new
        {
            window = new { title = w.Value.Title, process = w.Value.Process, hwnd = w.Value.Hwnd.ToInt64() },
            scanned,
            returned = nodes.Count,
            truncated = nodes.Count >= max,
            depth,
            nodes,
        });
    }

    // ══════════════════ ③ 查找控件 ══════════════════

    public static string Find(string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        var w = ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}" });

        string name = GetStr(args, "name");
        string type = GetStr(args, "type");
        string cls = GetStr(args, "className"); if (cls.Length == 0) cls = GetStr(args, "class");
        long limit = GetLong(args, "limit");
        int max = limit is > 0 and <= 50 ? (int)limit : 10;

        if (name.Length == 0 && type.Length == 0 && cls.Length == 0)
            return "{\"error\":\"至少提供 name / type / className 之一\"}";

        AutomationElement root;
        try { root = AutomationElement.FromHandle(w.Value.Hwnd); }
        catch (Exception ex) { return ToolJson.S(new { error = "无法读取窗口：" + ex.Message }); }

        var found = new List<object>();
        var q = new Queue<(AutomationElement El, int Depth)>();
        q.Enqueue((root, 0));
        int scanned = 0;

        while (q.Count > 0 && found.Count < max && scanned < 3000)
        {
            var (el, d) = q.Dequeue();
            scanned++;
            string ct, nm, cn;
            try
            {
                ct = (el.Current.ControlType.ProgrammaticName ?? "").Replace("ControlType.", "");
                nm = el.Current.Name ?? "";
                cn = el.Current.ClassName ?? "";
            }
            catch { continue; }

            bool ok = true;
            if (name.Length > 0 && !nm.Contains(name, StringComparison.OrdinalIgnoreCase)) ok = false;
            if (type.Length > 0 && !ct.Equals(type, StringComparison.OrdinalIgnoreCase)) ok = false;
            if (cls.Length > 0 && !cn.Contains(cls, StringComparison.OrdinalIgnoreCase)) ok = false;

            if (ok && (nm.Length > 0 || type.Length > 0 || cls.Length > 0))
            {
                var rect = el.Current.BoundingRectangle;
                found.Add(new
                {
                    depth = d, type = ct, name = nm, cls = cn,
                    patterns = SupportedPatterns(el),
                    x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height,
                    centerX = (int)(rect.X + rect.Width / 2), centerY = (int)(rect.Y + rect.Height / 2),
                });
            }

            if (d < 12)
            {
                try
                {
                    var walker = TreeWalker.ControlViewWalker;
                    var child = walker.GetFirstChild(el);
                    while (child != null) { q.Enqueue((child, d + 1)); child = walker.GetNextSibling(child); }
                }
                catch { }
            }
        }

        return ToolJson.S(new
        {
            window = new { title = w.Value.Title, process = w.Value.Process, hwnd = w.Value.Hwnd.ToInt64() },
            query = new { name, type, cls },
            scanned,
            count = found.Count,
            matches = found,
            note = found.Count == 0 ? "没有匹配的控件；可用 ui_dump_tree 看看实际有哪些控件" : "",
        });
    }

    // ══════════════════ ④ 读取控件状态 ══════════════════

    public static string GetState(string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        var w = ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}" });

        string name = GetStr(args, "name");
        string type = GetStr(args, "type");
        if (name.Length == 0) return "{\"error\":\"需要 name 参数\"}";

        AutomationElement root;
        try { root = AutomationElement.FromHandle(w.Value.Hwnd); }
        catch (Exception ex) { return ToolJson.S(new { error = "无法读取窗口：" + ex.Message }); }

        var conds = new List<Condition>
        {
            new PropertyCondition(AutomationElement.NameProperty, name),
        };
        if (type.Length > 0)
        {
            var ct = ControlType.LookupById(ControlType.Button.Id); // 占位，下面用名称匹配
            conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlTypeFromName(type) ?? ControlType.Custom));
        }

        AutomationElement? el = null;
        try
        {
            el = root.FindFirst(TreeScope.Descendants, new AndCondition(conds.ToArray()));
        }
        catch { }
        if (el == null)
        {
            // 退化为模糊匹配
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            int n = 0;
            while (q.Count > 0 && el == null && n < 2000)
            {
                var cur = q.Dequeue(); n++;
                try
                {
                    if ((cur.Current.Name ?? "").Contains(name, StringComparison.OrdinalIgnoreCase)) { el = cur; break; }
                    var walker = TreeWalker.ControlViewWalker;
                    var c = walker.GetFirstChild(cur);
                    while (c != null) { q.Enqueue(c); c = walker.GetNextSibling(c); }
                }
                catch { }
            }
        }
        if (el == null) return ToolJson.S(new { error = $"未找到名为「{name}」的控件" });

        var st = ReadState(el);
        return ToolJson.S(new { window = w.Value.Title, name, state = st });
    }

    /// <summary>读取一个控件的可读状态。</summary>
    public static Dictionary<string, object?> ReadState(AutomationElement el)
    {
        var d = new Dictionary<string, object?>();
        try
        {
            d["controlType"] = (el.Current.ControlType.ProgrammaticName ?? "").Replace("ControlType.", "");
            d["name"] = el.Current.Name;
            d["className"] = el.Current.ClassName;
            d["enabled"] = el.Current.IsEnabled;
            d["offscreen"] = el.Current.IsOffscreen;
            var r = el.Current.BoundingRectangle;
            d["rect"] = new { x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height };
        }
        catch { }

        try
        {
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object o1) && o1 is SelectionItemPattern si)
                d["selected"] = si.Current.IsSelected;
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object o2) && o2 is TogglePattern tp)
                d["toggleState"] = tp.Current.ToggleState.ToString();
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object o3) && o3 is ValuePattern vp)
            { d["value"] = vp.Current.Value; d["readOnly"] = vp.Current.IsReadOnly; }
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out object o4) && o4 is RangeValuePattern rv)
                d["rangeValue"] = rv.Current.Value;
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object o5) && o5 is ExpandCollapsePattern ec)
                d["expandState"] = ec.Current.ExpandCollapseState.ToString();
        }
        catch { }
        return d;
    }

    /// <summary>探测控件支持哪些 UIA 模式（只读探测，不执行）。</summary>
    public static List<string> SupportedPatterns(AutomationElement el)
    {
        var list = new List<string>();
        void Chk(string label, AutomationPattern p)
        {
            try { if (el.TryGetCurrentPattern(p, out _)) list.Add(label); } catch { }
        }
        Chk("Invoke", InvokePattern.Pattern);
        Chk("Select", SelectionItemPattern.Pattern);
        Chk("Toggle", TogglePattern.Pattern);
        Chk("Value", ValuePattern.Pattern);
        Chk("Expand", ExpandCollapsePattern.Pattern);
        Chk("Scroll", ScrollPattern.Pattern);
        Chk("Range", RangeValuePattern.Pattern);
        return list;
    }

    private static ControlType? ControlTypeFromName(string name) => name.ToLowerInvariant() switch
    {
        "button" => ControlType.Button,
        "edit" => ControlType.Edit,
        "radiobutton" => ControlType.RadioButton,
        "checkbox" => ControlType.CheckBox,
        "text" => ControlType.Text,
        "tab" => ControlType.Tab,
        "tabitem" => ControlType.TabItem,
        "pane" => ControlType.Pane,
        "window" => ControlType.Window,
        "combobox" => ControlType.ComboBox,
        "list" => ControlType.List,
        "listitem" => ControlType.ListItem,
        "slider" => ControlType.Slider,
        "menu" => ControlType.Menu,
        "menuitem" => ControlType.MenuItem,
        _ => null,
    };

    // ══════════════════ JSON 小工具 ══════════════════

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
