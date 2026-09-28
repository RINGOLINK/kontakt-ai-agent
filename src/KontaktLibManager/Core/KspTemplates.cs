using System;
using System.Collections.Generic;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **KSP 脚本模板库**（「KSP 能力深化」第一步）。
///
/// **为什么需要模板**：KSP 是**冷门语言**（训练数据极少），让模型从零写极易编造出不存在的命令。
/// 模板的作用是**给一个「起手式」** —— 里面用到的命令**都是先用 <see cref="KspSymbols"/> 核对过的**，
/// 模型只需在模板上填参数、加逻辑，而不是凭空造语法。
///
/// **📌 每个模板都做了两件事**：
///   ① **只用真实存在的命令**（模板里的命令都能在 `KspSymbols` 里查到）；
///   ② **标注「可改哪里」**（占位符写成 `%%名字%%`，并给出说明）。
///
/// **用法**：`KspTemplates.List()` 看有哪些；`KspTemplates.Get(key)` 取一个；
/// `KspTemplates.Render(key, values)` 填占位符。
/// </summary>
public static class KspTemplates
{
    public sealed class Template
    {
        /// <summary>机器名（Agent 用）。</summary>
        public string Key { get; set; } = "";
        /// <summary>中文名（界面/回答用）。</summary>
        public string Title { get; set; } = "";
        /// <summary>什么时候用。</summary>
        public string When { get; set; } = "";
        /// <summary>模板正文（含 `%%占位符%%`）。</summary>
        public string Body { get; set; } = "";
        /// <summary>占位符说明：名字 → 含义。</summary>
        public Dictionary<string, string> Slots { get; set; } = new();
        /// <summary>模板用到的 KSP 命令（**都已用符号表核对过**）。</summary>
        public string[] Uses { get; set; } = Array.Empty<string>();
    }

    private static List<Template>? _cache;

    /// <summary>所有模板。</summary>
    public static List<Template> All()
    {
        if (_cache != null) return _cache;
        _cache = new List<Template>
        {
            // ── ① 最小可运行骨架 ─────────────────────────────
            new()
            {
                Key = "minimal",
                Title = "最小骨架（on init / on note）",
                When = "任何 KSP 脚本的起手式；先确保它能编译通过，再加逻辑。",
                Body = """
                { 最小 KSP 骨架 —— 编译通过后再往上加逻辑 }

                on init
                    { 声明 UI 控件（可选） }
                    declare ui_label $lbl(1, 1)
                    set_text($lbl, "%%显示文字%%")
                    make_persistent($lbl)

                    { 变量必须显式声明 }
                    declare %%变量名%%
                    %%变量名%% := %%初值%%
                end on

                on note
                    { %%音符处理说明%% }
                    %%处理逻辑%%
                end on
                """,
                Slots = new()
                {
                    ["显示文字"] = "UI 标签上显示的文字，如 \"我的音色\"",
                    ["变量名"] = "自定义变量名（KSP 里变量要 declare，如 $count 或 %vel）",
                    ["初值"] = "变量初值，如 0",
                    ["音符处理说明"] = "这个回调要做什么（写给自己看的注释）",
                    ["处理逻辑"] = "实际要执行的语句，如 %vel := $EVENT_VELOCITY",
                },
                Uses = new[] { "ui_label", "set_text", "make_persistent", "$EVENT_NOTE", "$EVENT_VELOCITY" },
            },

            // ── ② 自定义 UI 面板 ─────────────────────────────
            new()
            {
                Key = "ui_panel",
                Title = "自定义 UI 面板（旋钮 + 开关 + 标签）",
                When = "要给乐器做一个可调的界面（音量、滤波、开关等）。",
                Body = """
                { 自定义 UI 面板 —— 旋钮 + 开关 + 标签，值可持久化 }

                on init
                    { 面板标题 }
                    declare ui_label $title(1, 1)
                    set_text($title, "%%面板标题%%")
                    make_persistent($title)

                    { 旋钮：0..1000000 是 KSP 旋钮的固定量程 }
                    declare ui_knob $%%旋钮名%%(0, 1000000, 1)
                    set_text($%%旋钮名%%, "%%旋钮标签%%")
                    make_persistent($%%旋钮名%%)
                    $%%旋钮名%% := %%旋钮初值%%

                    { 开关：0/1 }
                    declare ui_switch $%%开关名%%
                    set_text($%%开关名%%, "%%开关标签%%")
                    make_persistent($%%开关名%%)
                end on

                { 旋钮被转动时触发 }
                on ui_control($%%旋钮名%%)
                    { %%旋钮作用%% }
                    %%旋钮逻辑%%
                end on

                on ui_control($%%开关名%%)
                    { %%开关作用%% }
                    %%开关逻辑%%
                end on
                """,
                Slots = new()
                {
                    ["面板标题"] = "面板顶部的文字，如 \"Attack / Release\"",
                    ["旋钮名"] = "旋钮变量名（不含 $），如 knobAttack",
                    ["旋钮标签"] = "旋钮旁显示的文字",
                    ["旋钮初值"] = "0..1000000 之间的整数，如 500000（居中）",
                    ["开关名"] = "开关变量名（不含 $），如 swEnabled",
                    ["开关标签"] = "开关旁显示的文字",
                    ["旋钮作用"] = "这个旋钮控制什么",
                    ["旋钮逻辑"] = "实际语句，如 change_vol($EVENT_ID, ...)",
                    ["开关作用"] = "这个开关控制什么",
                    ["开关逻辑"] = "实际语句",
                },
                Uses = new[] { "ui_label", "ui_knob", "ui_switch", "set_text", "make_persistent", "ui_control" },
            },

            // ── ③ 键位映射 ──────────────────────────────────
            new()
            {
                Key = "key_map",
                Title = "键位映射（把某个键重定向到别的音高）",
                When = "要让某个键发出不同音高，或做键位切换（keyswitch）。",
                Body = """
                { 键位映射：把 %%源键%% 映射到 %%目标音高%% }

                on init
                    declare const $SRC_KEY := %%源键%%
                    declare const $DST_KEY := %%目标音高%%
                end on

                on note
                    if ($EVENT_NOTE = $SRC_KEY)
                        { 忽略原音符，改发目标音高 }
                        ignore_event($EVENT_ID)
                        play_note($DST_KEY, $EVENT_VELOCITY, 0, -1)
                    end if
                end on
                """,
                Slots = new()
                {
                    ["源键"] = "源键的 MIDI 音高（0..127），如 36",
                    ["目标音高"] = "目标音高（0..127），如 48",
                },
                Uses = new[] { "ignore_event", "play_note", "$EVENT_NOTE", "$EVENT_VELOCITY", "$EVENT_ID" },
            },

            // ── ④ MIDI CC 控制 ──────────────────────────────
            new()
            {
                Key = "midi_cc",
                Title = "MIDI CC 控制（用控制器旋钮调音量/参数）",
                When = "要用 MIDI 控制器（CC 号）实时控制乐器参数。",
                Body = """
                { MIDI CC 控制：CC%%CC号%% 控制 %%被控参数%% }

                on init
                    declare const $CC_NUM := %%CC号%%
                    declare %%状态变量%%
                    %%状态变量%% := %%初值%%
                end on

                on controller
                    if ($CC_NUM = $CC_NUM)
                        %%状态变量%% := $CC_VALUE
                        { %%CC作用%% }
                        %%CC逻辑%%
                    end if
                end on
                """,
                Slots = new()
                {
                    ["CC号"] = "MIDI CC 号（0..127），如 1（调制轮）或 11（表情）",
                    ["被控参数"] = "这个 CC 控制什么",
                    ["状态变量"] = "保存 CC 值的变量，如 %ccVal",
                    ["初值"] = "初值，如 0",
                    ["CC作用"] = "收到 CC 时要做什么",
                    ["CC逻辑"] = "实际语句",
                },
                Uses = new[] { "controller", "$CC_NUM", "$CC_VALUE" },
            },

            // ── ⑤ 力度分层 ──────────────────────────────────
            new()
            {
                Key = "velocity_layer",
                Title = "力度分层（按力度切换不同采样/音量）",
                When = "要按演奏力度（velocity）切换不同层的音色或音量。",
                Body = """
                { 力度分层：%%层数%% 层，按力度切换 }

                on init
                    declare const $LAYER_COUNT := %%层数%%
                    declare const $SPLIT_1 := %%分界1%%
                    %%更多分界声明%%
                end on

                on note
                    { $EVENT_VELOCITY 是 0..127 }
                    if ($EVENT_VELOCITY < $SPLIT_1)
                        { %%第1层说明%% }
                        %%第1层逻辑%%
                    %%更多分支%%
                    else
                        { %%最高层说明%% }
                        %%最高层逻辑%%
                    end if
                end on
                """,
                Slots = new()
                {
                    ["层数"] = "分几层，如 3",
                    ["分界1"] = "第 1 层与第 2 层的力度分界，如 64",
                    ["更多分界声明"] = "如需更多层，照抄 declare const 行；否则删掉这行",
                    ["第1层说明"] = "弱力度要做什么",
                    ["第1层逻辑"] = "实际语句",
                    ["更多分支"] = "如需更多层，照抄 if/else 结构；否则删掉这行",
                    ["最高层说明"] = "强力度要做什么",
                    ["最高层逻辑"] = "实际语句",
                },
                Uses = new[] { "play_note" },
            },

            // ── ⑥ 轮指（round-robin）────────────────────────
            new()
            {
                Key = "round_robin",
                Title = "轮指（round-robin，同音重复时轮换）",
                When = "同一个音连续弹时，希望轮换不同采样（避免「机枪感」）。",
                Body = """
                { 轮指：同音重复时在 %%轮数%% 个采样间轮换 }

                on init
                    declare const $RR_COUNT := %%轮数%%
                    declare $%%轮换变量%% := 0
                end on

                on note
                    { 每次触发就前进一格，到顶回绕 }
                    $%%轮换变量%% := ($%%轮换变量%% + 1) mod $RR_COUNT
                    { %%轮换逻辑说明%% }
                    %%轮换逻辑%%
                end on
                """,
                Slots = new()
                {
                    ["轮数"] = "轮换几个采样，如 4",
                    ["轮换逻辑说明"] = "根据 $%%轮换变量%% 做什么（如按 index 选不同音色）",
                    ["轮换逻辑"] = "实际语句",
                },
                Uses = new[] { "play_note" },
            },
        };
        return _cache;
    }

    /// <summary>按 key 取模板。</summary>
    public static Template? Get(string key)
        => All().FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// **填占位符**：把 `%%名字%%` 换成实际值。
    /// **未提供的占位符保留原样**（让模型/用户看到「这里还要填」）。
    /// </summary>
    public static string Render(string key, IDictionary<string, string>? values)
    {
        var t = Get(key);
        if (t == null) return "";
        var body = t.Body;
        if (values != null)
        {
            foreach (var kv in values)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                body = body.Replace($"%%{kv.Key}%%", kv.Value ?? "");
            }
        }
        return body;
    }

    /// <summary>**校验模板用到的命令是否都真实存在**（供自检用；返回不存在的命令）。</summary>
    public static List<string> ValidateUses()
    {
        var missing = new List<string>();
        var known = new HashSet<string>(KspSymbols.LoadAll().Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var t in All())
            foreach (var u in t.Uses)
                if (!known.Contains(u) && !missing.Contains(u))
                    missing.Add(u);
        return missing;
    }

    /// <summary>列出模板（供 Agent 回答 / 界面展示）。</summary>
    public static string ListText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in All())
            sb.AppendLine($"· **{t.Key}** —— {t.Title}：{t.When}");
        return sb.ToString();
    }
}
