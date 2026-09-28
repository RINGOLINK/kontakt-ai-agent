using System.Text.Json;
using System.Windows.Automation;

namespace KontaktLibManager.Core;

/// <summary>
/// **Kontakt 领域技能包** —— 把高频的多步操作封装成「一次调用」。
///
/// 为什么需要技能层：
///   底层工具（ui_find / ui_click）是「一次点一个控件」，
///   让模型自己拼多步操作容易漏步、点错、且每步都要确认 —— 体验很差。
///   技能层把「切到乐器页 + 选 Brand 筛选 + 搜索关键词」这类流程封装成一个动作，
///   **只确认一次**，既安全又高效。
///
/// 控件依据（实测 Kontakt 8.7.1，见 docs/Kontakt自动化控制能力评估.md）：
///   · 新版（Lumen）内容类型标签：Instruments / Combined / Tools / Leap / Loops / One-shots
///   · 新版筛选维度标签：Brand / Sound Type / Character
///   · 搜索：类名含 LumenTextField 的 Edit + 名为 "New search" 的 Button
///   · 老版（Classic）主导航：Libraries / Files / Monitor / Automation
///
/// **重要**：一律按**控件名称**定位，不按类名 ——
/// 实测同一控件的 QMLTYPE 编号会随界面状态变化（LumenTab_QMLTYPE_140 → _210），
/// 按类名匹配必然失效。
/// </summary>
public static class KontaktSkills
{
    /// <summary>新版内容类型标签（实测存在）。</summary>
    public static readonly string[] ContentTypes = { "Instruments", "Combined", "Tools", "Leap", "Loops", "One-shots" };

    /// <summary>新版筛选维度标签（实测存在）。</summary>
    public static readonly string[] Filters = { "Brand", "Sound Type", "Character" };

    /// <summary>老版（经典）界面的主导航标签，用于识别界面模式。</summary>
    public static readonly string[] ClassicTabs = { "Libraries", "Files", "Monitor", "Automation" };

    /// <summary>
    /// 界面模式。
    ///
    /// **为什么必须检测**（用户实测反馈）：Kontakt 7 以后新旧界面共存，
    /// 但**便携版的新版基础界面不可用** —— 库列表显示 "No results found" /
    /// "Content not found"，只有老版界面能正常使用。
    /// 因此 Agent 操作前必须先知道当前是哪一种，否则会在空界面上白点。
    /// </summary>
    public enum UiMode { Unknown, NewLumen, Classic }

    /// <summary>把用户口语映射到标签名。</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["instrument"] = "Instruments", ["乐器"] = "Instruments", ["音色"] = "Instruments", ["单件乐器"] = "Instruments",
        ["combined"] = "Combined", ["组合"] = "Combined", ["多件组合"] = "Combined",
        ["tool"] = "Tools", ["工具"] = "Tools",
        ["leap"] = "Leap",
        ["loop"] = "Loops", ["循环"] = "Loops", ["乐句"] = "Loops",
        ["one-shot"] = "One-shots", ["oneshot"] = "One-shots", ["单音"] = "One-shots", ["打击单音"] = "One-shots",
        ["brand"] = "Brand", ["品牌"] = "Brand", ["厂商"] = "Brand",
        ["sound type"] = "Sound Type", ["soundtype"] = "Sound Type", ["类型"] = "Sound Type", ["音色类型"] = "Sound Type",
        ["character"] = "Character", ["性格"] = "Character", ["风格"] = "Character", ["特征"] = "Character",
    };

    public static string Normalize(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return "";
        if (Aliases.TryGetValue(s, out var v)) return v;
        foreach (var t in ContentTypes.Concat(Filters))
            if (t.Equals(s, StringComparison.OrdinalIgnoreCase)) return t;
        return s;
    }

    // ══════════════════ 只读：当前界面状态 ══════════════════

    /// <summary>读取 Kontakt 当前界面模式 + 选中的内容类型/筛选维度，让 Agent 知道"现在在哪一页"。</summary>
    public static string GetUiState(string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        if (spec.Length == 0) spec = "Kontakt 8";
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}", hint = "Kontakt 可能没运行" });

        AutomationElement root;
        try { root = AutomationElement.FromHandle(w.Value.Hwnd); }
        catch (Exception ex) { return ToolJson.S(new { error = "无法读取窗口：" + ex.Message }); }

        var mode = DetectMode(root);

        string content = "", filter = "";
        foreach (var t in ContentTypes)
        {
            var el = FindByName(root, t);
            if (el != null && IsSelected(el)) { content = t; break; }
        }
        foreach (var t in Filters)
        {
            var el = FindByName(root, t);
            if (el != null && IsSelected(el)) { filter = t; break; }
        }

        string search = "";
        var edit = FindByClass(root, "LumenTextField", ControlType.Edit);
        if (edit != null)
        {
            try
            {
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out object o) && o is ValuePattern vp)
                    search = vp.Current.Value ?? "";
            }
            catch { }
        }

        string warning = "";
        if (mode == UiMode.NewLumen)
            warning = "当前是**新版（Lumen）界面**。注意：便携版的新版基础界面可能不可用"
                    + "（库列表显示 No results found / Content not found），只有老版界面能正常浏览库。"
                    + "如果用户要浏览/加载音色库而这里是空的，请先切到老版界面。";
        else if (mode == UiMode.Unknown)
            warning = "无法判定界面模式（新版内容类型标签与老版 Libraries/Files/Monitor/Automation 都没找到）。";

        return ToolJson.S(new
        {
            window = w.Value.Title,
            uiMode = mode.ToString(),
            contentType = content,
            filter = filter,
            searchText = search,
            availableContentTypes = ContentTypes,
            availableFilters = Filters,
            classicTabs = ClassicTabs,
            warning,
            note = "uiMode=NewLumen 是新版界面、Classic 是老版；contentType/filter 是当前选中标签。",
        });
    }

    // ══════════════════ 写：切换新旧界面 ══════════════════

    /// <summary>
    /// 切换新版（Lumen）/ 老版（Classic）界面。
    ///
    /// **依据用户实测给出的路径**：Kontakt 8 里 KONTAKT 下拉菜单 → Classic View
    /// （菜单项旁标注快捷键 F10；用户说明是 F10 打开菜单再点 Classic View）。
    ///
    /// 实测结论：**单按 F10 不切换界面**（控件树新增 0），所以主路径是「菜单 → Classic View」。
    /// 关键坑：该下拉是**独立弹窗**，不属于主窗口的 UIA 子树，
    /// 必须在 AutomationElement.RootElement（桌面根）里找菜单项。
    ///
    /// **为什么这个技能很关键**：便携版的新版基础界面不可用，
    /// 只有老版界面能正常浏览/加载音色库 —— 任何「浏览库」类技能都必须先切到老版。
    /// </summary>
        public static string SwitchUiMode(string argsJson)
        {
            var args = Parse(argsJson);
            string spec = GetStr(args, "window");
            if (spec.Length == 0) spec = "Kontakt 8";
            var w = UiAutomation.ResolveWindow(spec);
            if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}" });

            string want = NormalizeMode(GetStr(args, "mode"));
            if (want.Length == 0) return "{\"error\":\"需要 mode 参数：classic（老版）或 new（新版）\"}";

            var cur = DetectMode(AutomationElement.FromHandle(w.Value.Hwnd));
            string curName = cur.ToString();
            if ((want == "classic" && cur == UiMode.Classic) || (want == "new" && cur == UiMode.NewLumen))
                return ToolJson.S(new { ok = true, changed = false, uiMode = curName, message = $"已经是{ModeLabel(want)}" });

            IntPtr prev = UiInput.FocusWindow(w.Value.Hwnd);
            try
            {
                // ══════════════════════════════════════════════════════════
                // Kontakt 8 的界面切换机制（**用户实测解释 + 我们探针验证**）：
                //
                //   KONTAKT 标志右侧有**两个页面切换按钮**：
                //     · ▶ 三角形 = **标签页 B**
                //     · ⦀ 三竖杠 = **标签页 A（默认激活）**
                //
                //   标签页 A 只有**一种状态**（新版 Lumen 浏览器）——
                //   **在 A 上按 F10 毫无作用**（这正是旧实现「F10 快路径」必然失败的原因）。
                //   标签页 B 有**两种状态**：新版 + **老版 classic**，由 **F10** 切换。
                //
                //   所以正确顺序：**先切到标签页 B（点三角形）→ 再按 F10**。
                //
                //   这两个按钮在 UIA 里是**无名**的 HoverableImageButton，只能按坐标定位；
                //   用同一行的 **VIEW 按钮**作锚点最稳（实测三角形在 VIEW 左侧 56px）。
                // ══════════════════════════════════════════════════════════
                var root = AutomationElement.FromHandle(w.Value.Hwnd);
                var viewBtn = FindByName(root, "VIEW");
                if (viewBtn == null || viewBtn.Current.BoundingRectangle.Width <= 0)
                    return ToolJson.S(new { error = "找不到 VIEW 按钮，无法定位标签页切换入口" });

                var vr = viewBtn.Current.BoundingRectangle;
                int triX = (int)(vr.X - 56);
                int triY = (int)(vr.Y + vr.Height / 2);

                // ① 先点「三角形」切到标签页 B（无论当前在哪，点它都是去 B）
                UiInput.ClickAt(triX, triY);
                Thread.Sleep(2200);

                // ② 在标签页 B 上按 F10 切换 新版 ⇄ 老版
                for (int i = 0; i < 3; i++)
                {
                    UiInput.SendKeys("F10");
                    Thread.Sleep(1800);
                    var a = DetectMode(AutomationElement.FromHandle(w.Value.Hwnd));
                    if ((want == "classic" && a == UiMode.Classic) || (want == "new" && a == UiMode.NewLumen))
                        return ToolJson.S(new
                        {
                            ok = true, changed = true, method = "tabB+F10", before = curName,
                            uiMode = a.ToString(), message = $"已切换到{ModeLabel(want)}（标签页 B + F10）",
                        });
                }

                // ③ 若 F10 无效，回退：再点一次三角形（有些布局下第一次点只是聚焦）
                UiInput.ClickAt(triX, triY);
                Thread.Sleep(1500);
                UiInput.SendKeys("F10");
                Thread.Sleep(2000);

                var fin = DetectMode(AutomationElement.FromHandle(w.Value.Hwnd));
                return ToolJson.S(new
                {
                    ok = fin != cur, changed = fin != cur, method = "tabB+F10(retry)",
                    before = curName, uiMode = fin.ToString(),
                    message = fin == cur
                        ? "切换未生效。可能原因：① 该 Kontakt 版本的标签页按钮位置不同；② 便携版下标签页 B 的新版界面为空属正常，但老版应可用。"
                        : $"已切换到{ModeLabel(want)}",
                });
            }
            finally { UiInput.RestoreFocus(prev); }
        }

    // ══════════════════ 写：切换视图（内容类型 / 筛选维度）══════════════════

    /// <summary>切换内容类型 / 筛选维度（一个技能搞定多步）。</summary>
    public static string SetView(string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        if (spec.Length == 0) spec = "Kontakt 8";
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}" });

        string wantType = Normalize(GetStr(args, "contentType"));
        string wantFilter = Normalize(GetStr(args, "filter"));
        var done = new List<string>();
        var failed = new List<string>();

        // 必须先聚焦目标窗口：否则 SetCursorPos + mouse_event 的点击会落到当前前台窗口上
        IntPtr prevFocus = UiInput.FocusWindow(w.Value.Hwnd);
        try
        {
            foreach (var pair in new[] { (label: "内容类型", target: wantType), (label: "筛选维度", target: wantFilter) })
            {
                string label = pair.label, target = pair.target;
                if (target.Length == 0) continue;
                bool isType = ContentTypes.Contains(target);
                bool isFilter = Filters.Contains(target);
                if (!isType && !isFilter) { failed.Add($"{label}「{target}」不是已知标签"); continue; }

                var el = FindByName(AutomationElement.FromHandle(w.Value.Hwnd), target);
                if (el == null) { failed.Add($"{label}「{target}」在界面上找不到"); continue; }

                var r = el.Current.BoundingRectangle;
                if (r.Width <= 0) { failed.Add($"{label}「{target}」不可见"); continue; }

                // 点击 + 自校验：点完回读一次，没生效就再点一次（有些标签首次点击会被忽略）
                bool okNow = false;
                for (int attempt = 0; attempt < 2 && !okNow; attempt++)
                {
                    UiInput.ClickAt((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                    Thread.Sleep(attempt == 0 ? 700 : 900);
                    var again = FindByName(AutomationElement.FromHandle(w.Value.Hwnd), target);
                    okNow = again != null && IsSelected(again);
                }
                if (okNow) done.Add($"{label} → {target}");
                else failed.Add($"{label}「{target}」点击后状态未变化（可能该标签在当前上下文不可用）");
            }

            return ToolJson.S(new { ok = failed.Count == 0, done, failed, window = w.Value.Title });
        }
        finally { UiInput.RestoreFocus(prevFocus); }
    }

    // ══════════════════ 写：搜索 ══════════════════

    /// <summary>在 Kontakt 浏览器里搜索（填关键词 + 触发搜索）。</summary>
    public static string Search(string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        if (spec.Length == 0) spec = "Kontakt 8";
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}" });

        string q = GetStr(args, "query");

        var root = AutomationElement.FromHandle(w.Value.Hwnd);
        var edit = FindByClass(root, "LumenTextField", ControlType.Edit);
        if (edit == null) return "{\"error\":\"找不到搜索框（LumenTextField / Edit）\"}";

        IntPtr prevFocus = UiInput.FocusWindow(w.Value.Hwnd);
        try
        {
            var er = edit.Current.BoundingRectangle;
            if (er.Width <= 0) return "{\"error\":\"搜索框不可见\"}";
            UiInput.ClickAt((int)(er.X + er.Width / 2), (int)(er.Y + er.Height / 2));
            Thread.Sleep(300);

            UiInput.SendKeys("Ctrl+A");
            Thread.Sleep(120);
            UiInput.SendKeys("Delete");
            Thread.Sleep(120);

            if (q.Length > 0)
            {
                UiInput.SendText(q);
                Thread.Sleep(300);
            }

            var btn = FindByName(root, "New search");
            string trigger;
            if (btn != null && btn.Current.BoundingRectangle.Width > 0)
            {
                var br = btn.Current.BoundingRectangle;
                UiInput.ClickAt((int)(br.X + br.Width / 2), (int)(br.Y + br.Height / 2));
                trigger = "click New search";
            }
            else
            {
                UiInput.SendKeys("Enter");
                trigger = "Enter";
            }
            Thread.Sleep(1200);

            return ToolJson.S(new { ok = true, query = q, trigger, window = w.Value.Title });
        }
        finally { UiInput.RestoreFocus(prevFocus); }
    }

    // ══════════════════ 预览（供确认弹窗用）══════════════════

    public static string PreviewSetView(string argsJson)
    {
        var args = Parse(argsJson);
        string t = Normalize(GetStr(args, "contentType"));
        string f = Normalize(GetStr(args, "filter"));
        var parts = new List<string>();
        if (t.Length > 0) parts.Add("内容类型切到「" + t + "」");
        if (f.Length > 0) parts.Add("筛选维度切到「" + f + "」");
        if (parts.Count == 0) return "";
        return "在 Kontakt 浏览器里" + string.Join("、", parts);
    }

    public static string PreviewSearch(string argsJson)
    {
        var args = Parse(argsJson);
        string q = GetStr(args, "query");
        return q.Length == 0 ? "" : $"在 Kontakt 浏览器搜索框里输入「{q}」并触发搜索";
    }

    public static string PreviewSwitchUiMode(string argsJson)
    {
        var args = Parse(argsJson);
        string want = NormalizeMode(GetStr(args, "mode"));
        if (want.Length == 0) return "";
        return $"把 Kontakt 切换到{ModeLabel(want)}界面（走 KONTAKT 菜单 → Classic View）";
    }


    // ══════════════════ 老版（Classic）界面：坐标常量 ══════════════════
    //
    // 老版界面的内容区是**自绘的、不在 UIA 树里**（实测：ControlView/RawView/ContentView
    // 三种遍历器都只返回 7 个头部控件），所以只能按坐标操作。
    //
    // 下面这些偏移是**从 1026x738 的窗口截图实测量出来的**（相对窗口客户区左上角）。
    // 窗口尺寸不同需要按比例缩放 —— 见 ScaleOffset()。

    /// <summary>老版界面里搜索框「Library or Company」的中心偏移（1026x738 基准）。</summary>
    public static readonly (int X, int Y) ClassicSearchBoxOffset = (210, 130);

    /// <summary>老版界面里**第一个音色库列表项**的中心偏移（1026x738 基准）。</summary>
    public static readonly (int X, int Y) ClassicFirstItemOffset = (170, 200);

    /// <summary>老版界面里列表项的估算高度（用于推算第 N 项位置）。</summary>
    public const int ClassicItemHeight = 100;

    /// <summary>老版界面基准窗口尺寸（这些偏移都是按它量的）。</summary>
    private static readonly (int W, int H) ClassicBaseSize = (1026, 738);

    /// <summary>把基准偏移按实际窗口尺寸等比缩放。</summary>
    private static (int X, int Y) ScaleOffset((int X, int Y) off, System.Windows.Rect wr)
    {
        double sx = wr.Width / ClassicBaseSize.W;
        double sy = wr.Height / ClassicBaseSize.H;
        return ((int)(off.X * sx), (int)(off.Y * sy));
    }

    private static System.Windows.Rect WindowRect(IntPtr hwnd)
    {
        var r = new UiInput.RECT();
        UiInput.GetWindowRect(hwnd, out r);
        return new System.Windows.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    // ══════════════════ 老版：用自带搜索框过滤 ══════════════════

    /// <summary>
    /// **老版界面用 Kontakt 自带的搜索框过滤音色库**（用户建议的方案）。
    ///
    /// 为什么这是关键设计：老版列表是自绘的、拿不到控件坐标，
    /// 靠视觉模型找库名坐标误差有 ±10~50px（实测），列表项才约 100px 高，很容易点错。
    /// **先用搜索框把列表过滤成「只有目标库」，目标就固定落在第一项**，
    /// 位置可预测、无需视觉、也不受其他库干扰。
    ///
    /// 实测：搜索确实会过滤列表（输入不匹配的词后列表变空）。
    /// </summary>
    public static string ClassicSearch(string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        if (spec.Length == 0) spec = "Kontakt 8";
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}" });

        string q = GetStr(args, "query");

        IntPtr prev = UiInput.FocusWindow(w.Value.Hwnd);
        try
        {
            if (!UiInput.HasFocus(w.Value.Hwnd))
                return ToolJson.S(new { error = "无法让 Kontakt 获得焦点，界面操作不可靠（已放弃，未做任何点击）" });

            var wr = WindowRect(w.Value.Hwnd);
            var off = ScaleOffset(ClassicSearchBoxOffset, wr);
            int sx = (int)wr.X + off.X, sy = (int)wr.Y + off.Y;

            UiInput.ClickAt(sx, sy);
            Thread.Sleep(350);

            // 清空已有内容（全选 + 删除）
            UiInput.SendKeys("Ctrl+A");
            Thread.Sleep(150);
            UiInput.SendKeys("Delete");
            Thread.Sleep(150);

            if (q.Length > 0)
            {
                UiInput.SendText(q);      // SendInput + KEYEVENTF_UNICODE，不受布局影响
                Thread.Sleep(400);
                UiInput.SendKeys("Enter"); // 触发过滤
                Thread.Sleep(900);
            }

            return ToolJson.S(new { ok = true, query = q, clickedAt = new { x = sx, y = sy }, window = w.Value.Title });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    // ══════════════════ 老版：加载列表第 N 项 ══════════════════

    /// <summary>
    /// 双击老版列表里的第 N 项（1 起）以加载音色库。
    /// **建议先调 <see cref="ClassicSearch"/> 过滤**，让目标成为第 1 项再调本方法。
    /// </summary>
    public static string ClassicLoadItem(string argsJson)
    {
        var args = Parse(argsJson);
        string spec = GetStr(args, "window");
        if (spec.Length == 0) spec = "Kontakt 8";
        var w = UiAutomation.ResolveWindow(spec);
        if (w == null) return ToolJson.S(new { error = $"未找到窗口：{spec}" });

        long idxRaw = GetLong(args, "index");
        int idx = idxRaw is >= 1 and <= 20 ? (int)idxRaw : 1;

        IntPtr prev = UiInput.FocusWindow(w.Value.Hwnd);
        try
        {
            if (!UiInput.HasFocus(w.Value.Hwnd))
                return ToolJson.S(new { error = "无法让 Kontakt 获得焦点，界面操作不可靠（已放弃，未做任何点击）" });

            var wr = WindowRect(w.Value.Hwnd);
            double sy2 = wr.Height / ClassicBaseSize.H;
            var off = ScaleOffset(ClassicFirstItemOffset, wr);
            int x = (int)wr.X + off.X;
            int y = (int)wr.Y + off.Y + (int)((idx - 1) * ClassicItemHeight * sy2);

            UiInput.DoubleClickAt(x, y);
            Thread.Sleep(1500);

            return ToolJson.S(new
            {
                ok = true, index = idx, clickedAt = new { x, y },
                message = $"已双击列表第 {idx} 项。若该项显示 Content not found，说明该库未注册或磁盘未挂载。",
            });
        }
        finally { UiInput.RestoreFocus(prev); }
    }

    public static string PreviewClassicSearch(string argsJson)
    {
        var args = Parse(argsJson);
        string q = GetStr(args, "query");
        return q.Length == 0 ? "清空 Kontakt 老版界面的库搜索框" : $"在 Kontakt 老版界面的搜索框输入「{q}」以过滤音色库列表";
    }

    public static string PreviewClassicLoadItem(string argsJson)
    {
        var args = Parse(argsJson);
        long i = GetLong(args, "index");
        int idx = i is >= 1 and <= 20 ? (int)i : 1;
        return $"双击 Kontakt 老版库列表的第 {idx} 项（加载该音色库）";
    }

    // ══════════════════ 辅助 ══════════════════

    /// <summary>判定当前界面模式。</summary>
    /// <summary>
    /// 判定当前界面模式。
    ///
    /// **实测重要事实**（2026-09-18）：
    ///   · **新版（Lumen）界面**：UIA **完整暴露**（Instruments/Combined/Tools… 标签、
    ///     搜索框、Brand/Sound Type/Character 筛选，共 50+ 个控件）；
    ///   · **老版（Classic）界面**：UIA **只暴露头部**（VIEW 按钮 + 传输控件），
    ///     音色库列表、Libraries/Files/Monitor/Automation 标签、Instrument Navigator
    ///     **全部是自绘元素、不在 UIA 树里**（ControlView/RawView/ContentView 三种遍历器结果一致，
    ///     且 Kontakt 进程只有 2 个顶层窗口，内容不在别的窗口里）。
    ///
    /// 因此判定逻辑不能依赖 ClassicTabs（它们在老版模式下**根本查不到**）：
    ///   ① 有 Instruments 标签 → 新版；
    ///   ② 否则有 VIEW 按钮 → 老版（VIEW 是老版/新版共有的头部按钮）；
    ///   ③ 都没有 → Unknown。
    /// </summary>
    public static UiMode DetectMode(AutomationElement? root)
    {
        if (root == null) return UiMode.Unknown;
        if (FindByName(root, "Instruments") != null) return UiMode.NewLumen;
        // 老版：标签页查不到，但 VIEW 按钮在（老版下 UIA 只剩头部）
        if (FindByName(root, "VIEW") != null) return UiMode.Classic;
        // 兜底：万一某些版本老版真的暴露了标签
        foreach (var t in ClassicTabs)
            if (FindByName(root, t) != null) return UiMode.Classic;
        return UiMode.Unknown;
    }

    private static string NormalizeMode(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "classic" or "old" or "legacy" or "老版" or "旧版" or "经典" => "classic",
            "new" or "lumen" or "新版" or "现代" => "new",
            _ => "",
        };
    }

    private static string ModeLabel(string m) => m == "classic" ? "老版（Classic）" : "新版（Lumen）";

    private static AutomationElement? FindByName(AutomationElement root, string name)
    {
        try
        {
            var cond = new PropertyCondition(AutomationElement.NameProperty, name);
            return root.FindFirst(TreeScope.Descendants, cond);
        }
        catch { return null; }
    }

    /// <summary>
    /// 在**桌面根节点**里按名称找控件。
    /// 用于找下拉菜单项 —— 实测 Kontakt 的 KONTAKT 下拉是**独立弹窗**，
    /// 不属于主窗口的 UIA 子树，只在主窗口里找必然找不到。
    /// </summary>
    private static AutomationElement? FindOnDesktop(string name)
    {
        try
        {
            var cond = new PropertyCondition(AutomationElement.NameProperty, name);
            // 必须遍历所有匹配项并**挑屏幕内的那个** ——
            // 实测桌面上存在坐标非法的「幽灵元素」（Classic View @582,-2282，在屏幕外），
            // FindFirst 会先拿到它，点下去毫无反应。
            var all = AutomationElement.RootElement.FindAll(TreeScope.Descendants, cond);
            if (all == null) return null;
            double vx = System.Windows.SystemParameters.VirtualScreenLeft;
            double vy = System.Windows.SystemParameters.VirtualScreenTop;
            double vw = System.Windows.SystemParameters.VirtualScreenWidth;
            double vh = System.Windows.SystemParameters.VirtualScreenHeight;
            foreach (AutomationElement el in all)
            {
                var r = el.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) continue;
                if (r.X < vx || r.Y < vy || r.X > vx + vw || r.Y > vy + vh) continue;   // 屏幕外，跳过
                if (el.Current.IsOffscreen) continue;
                return el;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>按名称**子串**找控件（菜单按钮名可能是 "KONTAKT" 或含该词）。</summary>
    /// <summary>
    /// 按名称**子串**找**可点击的**控件。
    ///
    /// **实测踩坑**：直接按名字子串找 "KONTAKT" 会先命中**窗口本身**
    /// （窗口名就是 "Kontakt 8"，@26,26 1026x738 整个窗口），
    /// 于是点击落在窗口中心、菜单根本打不开。
    /// 所以这里**排除 Window / Pane / TitleBar 等容器**，只收 Button / Text / MenuItem 这类可点元素。
    /// </summary>
    private static AutomationElement? FindByNameContains(AutomationElement root, string sub)
    {
        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        int n = 0;
        while (q.Count > 0 && n < 1500)
        {
            var el = q.Dequeue(); n++;
            try
            {
                var ct = el.Current.ControlType;
                bool isContainer = ct == ControlType.Window || ct == ControlType.Pane
                               || ct == ControlType.TitleBar || ct == ControlType.MenuBar;
                var r = el.Current.BoundingRectangle;
                if (!isContainer && r.Width > 0 && r.Height > 0
                    && (el.Current.Name ?? "").Contains(sub, StringComparison.OrdinalIgnoreCase))
                    return el;

                var walker = TreeWalker.ControlViewWalker;
                var c = walker.GetFirstChild(el);
                while (c != null) { q.Enqueue(c); c = walker.GetNextSibling(c); }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 按类名找控件；**找不到时退化为按 ControlType 找**。
    /// 原因（实测）：Kontakt 切内容类型后会重新渲染，同一个控件的 QMLTYPE 编号
    /// 与类名都会变（LumenTextField_QMLTYPE_148 → 其它），只按类名匹配会失效。
    /// </summary>
    private static AutomationElement? FindByClass(AutomationElement root, string cls, ControlType? fallback = null)
    {
        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        int n = 0;
        while (q.Count > 0 && n < 1500)
        {
            var el = q.Dequeue(); n++;
            try
            {
                if ((el.Current.ClassName ?? "").Contains(cls, StringComparison.OrdinalIgnoreCase)) return el;
                if (fallback != null && el.Current.ControlType == fallback) return el;
                var walker = TreeWalker.ControlViewWalker;
                var c = walker.GetFirstChild(el);
                while (c != null) { q.Enqueue(c); c = walker.GetNextSibling(c); }
            }
            catch { }
        }
        return null;
    }

    private static bool IsSelected(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object o) && o is SelectionItemPattern si)
                return si.Current.IsSelected;
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object o2) && o2 is TogglePattern tp)
                return tp.Current.ToggleState == ToggleState.On;
        }
        catch { }
        return false;
    }

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

    private static long GetLong(JsonElement e, string n)     {         if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out var v)) return 0;         return v.ValueKind switch         {             JsonValueKind.Number => v.TryGetInt64(out long x) ? x : 0,             JsonValueKind.String => long.TryParse(v.GetString(), out long y) ? y : 0,             _ => 0,         };     } 
    private static string GetStr(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";
}
