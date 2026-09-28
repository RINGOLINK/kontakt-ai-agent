using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// **KSP 符号表检索** —— 让模型「查了再写」，而不是凭印象编造命令。
///
/// **为什么需要它**：KSP 是冷门语言，训练数据覆盖不全，**模型最常见的错误是编造不存在的命令**。
/// 而 KSPCompiler 随包分发的符号数据里有**权威的 460 个内置命令**（含精确签名）。
/// 把这些数据做成可检索的接口，模型写 KSP 前就能查到「这个命令到底存不存在、参数是什么」。
///
/// **数据来源**：`data\kspc\Data\Symbols\` 下的四个 YAML（随 kspc 一起分发，**零额外依赖**）：
///   · `commands.yaml`    —— 460 个内置命令（Name / Description / ReturnType / Arguments）
///   · `callbacks.yaml`   —— 回调（on init / on note …）
///   · `uitypes.yaml`     —— UI 控件类型
///   · `variables.yaml`   —— 内置变量
///
/// **为什么手写解析而不用 YAML 库**：这四个文件的格式高度规整（固定缩进的 `键: 值` +
/// `|-` 多行块 + `- ` 列表项），手写一个针对性的解析器**足够可靠**，
/// 且**避免为一个小功能引入 YAML 依赖**（也少一个分发负担）。
/// 解析失败的条目会被跳过而不是抛异常 —— **检索工具不该因为一条脏数据就整体不可用**。
/// </summary>
public static class KspSymbols
{
    /// <summary>一个符号（命令 / 回调 / UI 类型 / 变量）。</summary>
    public sealed class Symbol
    {
        public string Kind { get; set; } = "command";   // command / callback / uitype / variable
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>返回类型（命令专有，如 `V` / `I||S||R`）。</summary>
        public string ReturnType { get; set; } = "";
        /// <summary>参数列表。</summary>
        public List<Arg> Arguments { get; set; } = new();
        /// <summary>所属分类（按名字启发式归类，见 <see cref="Classify"/>）。</summary>
        public string Category { get; set; } = "";
    }

    /// <summary>一个参数。</summary>
    public sealed class Arg
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private static List<Symbol>? _cache;
    private static string _cacheDir = "";

    /// <summary>符号目录（与 kspc.exe 同级）。</summary>
    public static string SymbolsDir => Path.Combine(AppPaths.DataDir, "kspc", "Data", "Symbols");

    /// <summary>加载（带缓存）全部符号；目录不存在时返回空表。</summary>
    public static List<Symbol> LoadAll(string? dir = null)
    {
        dir ??= SymbolsDir;
        if (_cache != null && _cacheDir == dir) return _cache;

        var list = new List<Symbol>();
        list.AddRange(ParseFile(Path.Combine(dir, "commands.yaml"), "command"));
        list.AddRange(ParseFile(Path.Combine(dir, "callbacks.yaml"), "callback"));
        list.AddRange(ParseFile(Path.Combine(dir, "uitypes.yaml"), "uitype"));
        list.AddRange(ParseFile(Path.Combine(dir, "variables.yaml"), "variable"));
        foreach (var s in list) s.Category = Classify(s);
        _cache = list; _cacheDir = dir;
        return list;
    }

    /// <summary>清缓存（测试用）。</summary>
    public static void ClearCache() { _cache = null; _cacheDir = ""; }

    // ══════════════════ 检索 ══════════════════

    /// <summary>按名字精确查（大小写不敏感）。</summary>
    public static Symbol? ByName(string name)
        => LoadAll().FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>按关键词搜（名字或描述命中），可按类别过滤。</summary>
    public static List<Symbol> Search(string keyword, string? category = null, int limit = 25)
    {
        var all = LoadAll();
        if (!string.IsNullOrWhiteSpace(category))
            all = all.Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
        if (string.IsNullOrWhiteSpace(keyword)) return all.Take(limit).ToList();

        var kw = keyword.Trim();
        return all.Where(s =>
                s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(kw, StringComparison.OrdinalIgnoreCase))
            // 名字命中排前面
            .OrderByDescending(s => s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name)
            .Take(limit).ToList();
    }

    /// <summary>列出所有类别及数量（供模型先看有哪些类）。</summary>
    public static List<(string Category, int Count)> Categories()
        => LoadAll().GroupBy(s => s.Category).Select(g => (g.Key, g.Count()))
                    .OrderByDescending(x => x.Item2).ToList();

    /// <summary>按名字启发式归类（KSP 符号表本身没有分类字段）。</summary>
    private static string Classify(Symbol s)
    {
        if (s.Kind != "command") return s.Kind;
        var n = s.Name.ToLowerInvariant();
        if (n.StartsWith("ui_") || n.Contains("menu") || n.Contains("label") || n.Contains("slider") ||
            n.Contains("knob") || n.Contains("table") || n.Contains("switch") || n.Contains("panel") ||
            n.Contains("value_edit") || n.Contains("button")) return "ui";
        if (n.Contains("note") || n.Contains("midi") || n.Contains("cc") || n.Contains("pitch") ||
            n.Contains("velocity") || n.Contains("key") || n.Contains("tune") || n.Contains("tuning")) return "midi";
        if (n.Contains("array") || n.StartsWith("array_")) return "array";
        if (n.Contains("string") || n.StartsWith("str") || n.Contains("substr") || n.Contains("concat")) return "string";
        if (n.Contains("abs") || n.Contains("sin") || n.Contains("cos") || n.Contains("tan") ||
            n.Contains("sqrt") || n.Contains("pow") || n.Contains("log") || n.Contains("floor") ||
            n.Contains("ceil") || n.Contains("round") || n.Contains("random") || n.Contains("mod") ||
            n.Contains("min") || n.Contains("max") || n.Contains("exp")) return "math";
        if (n.Contains("engine") || n.Contains("slot") || n.Contains("sampler") || n.Contains("sample") ||
            n.Contains("loop") || n.Contains("volume") || n.Contains("pan") || n.Contains("filter") ||
            n.Contains("env") || n.Contains("lfo") || n.Contains("effect") || n.Contains("reverb") ||
            n.Contains("delay") || n.Contains("chorus")) return "engine";
        if (n.Contains("file") || n.Contains("folder") || n.Contains("load") || n.Contains("save") ||
            n.Contains("path")) return "file";
        if (n.Contains("group") || n.Contains("zone") || n.Contains("purge")) return "instrument";
        return "other";
    }

    // ══════════════════ 渲染 ══════════════════

    /// <summary>把一个符号渲染成给模型看的紧凑文本（含签名与参数说明）。</summary>
    public static string Render(Symbol s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Name);
        if (s.Arguments.Count > 0)
            sb.Append('(').Append(string.Join(", ", s.Arguments.Select(a => $"{a.Name}: {a.DataType}"))).Append(')');
        if (s.ReturnType.Length > 0 && s.ReturnType != "V") sb.Append(" → ").Append(s.ReturnType);
        if (s.Description.Length > 0) sb.Append("   —— ").Append(s.Description.Replace("\n", " "));
        if (s.Arguments.Count > 0)
        {
            var withDesc = s.Arguments.Where(a => a.Description.Length > 0).ToList();
            if (withDesc.Count > 0)
                sb.Append("\n      参数：").Append(string.Join("；", withDesc.Select(a => $"{a.Name}（{a.DataType}）{a.Description.Replace("\n", " ")}")));
        }
        return sb.ToString();
    }

    // ══════════════════ 解析器 ══════════════════

    /// <summary>
    /// 解析一个符号 YAML。针对本格式的固定结构手写：
    /// 顶层 `Data:` 下是 `- Id:` 开头的条目；条目内是 `键: 值` 或 `键: |-` 多行块；
    /// `Arguments:` 下是 `- Name:` 开头的参数列表。
    /// </summary>
    public static List<Symbol> ParseFile(string path, string kind)
    {
        var result = new List<Symbol>();
        if (!File.Exists(path)) return result;
        string[] lines;
        try { lines = File.ReadAllLines(path, Encoding.UTF8); }
        catch { return result; }

        Symbol? cur = null;
        Arg? curArg = null;
        string mode = "";        // "" / "desc" / "argdesc" / "arg"
        int descIndent = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;

            // 多行块续行（缩进比块头更深）
            if (mode == "desc" || mode == "argdesc")
            {
                int ind = raw.Length - raw.TrimStart().Length;
                if (ind > descIndent)
                {
                    if (mode == "desc" && cur != null) cur.Description += (cur.Description.Length > 0 ? "\n" : "") + t;
                    else if (mode == "argdesc" && curArg != null) curArg.Description += (curArg.Description.Length > 0 ? "\n" : "") + t;
                    continue;
                }
                mode = "";
            }

            // 新条目：`- Id:` 或 `- Name:`（顶层，缩进 2）
            if (t.StartsWith("- ") && (t.Contains("Id:") || t.Contains("Name:")))
            {
                int ind = raw.Length - raw.TrimStart().Length;
                if (ind <= 3)   // 顶层条目
                {
                    cur = new Symbol { Kind = kind };
                    result.Add(cur);
                    curArg = null;
                    var rest = t[2..];
                    TrySet(cur, rest, ref mode, ref descIndent, raw);
                    continue;
                }
                else if (cur != null)   // 参数条目
                {
                    curArg = new Arg();
                    cur.Arguments.Add(curArg);
                    TrySetArg(curArg, t[2..], ref mode, ref descIndent, raw);
                    continue;
                }
            }

            if (cur == null) continue;
            if (t.StartsWith("Arguments:")) continue;

            // 参数块的普通键（缩进比条目深）
            if (curArg != null && !t.Contains(":") == false)
            {
                int ind = raw.Length - raw.TrimStart().Length;
                // 参数块的键缩进通常 ≥ 6；条目键缩进为 4
                if (ind >= 6 && TrySetArg(curArg, t, ref mode, ref descIndent, raw)) continue;
            }
            TrySet(cur, t, ref mode, ref descIndent, raw);
        }
        // 丢掉没名字的脏条目
        return result.Where(s => s.Name.Length > 0).ToList();
    }

    private static void TrySet(Symbol s, string line, ref string mode, ref int descIndent, string raw)
    {
        int c = line.IndexOf(':');
        if (c <= 0) return;
        var key = line[..c].Trim();
        var val = line[(c + 1)..].Trim();
        if (val == "|-" || val == "|" || val == ">")
        {
            mode = "desc";
            descIndent = raw.Length - raw.TrimStart().Length;
            return;
        }
        switch (key)
        {
            case "Name": s.Name = val; break;
            case "Description": s.Description = val; break;
            case "ReturnType": s.ReturnType = val; break;
        }
    }

    private static bool TrySetArg(Arg a, string line, ref string mode, ref int descIndent, string raw)
    {
        int c = line.IndexOf(':');
        if (c <= 0) return false;
        var key = line[..c].Trim();
        var val = line[(c + 1)..].Trim();
        if (val == "|-" || val == "|" || val == ">")
        {
            mode = "argdesc";
            descIndent = raw.Length - raw.TrimStart().Length;
            return true;
        }
        switch (key)
        {
            case "Name": a.Name = val; return true;
            case "DataType": a.DataType = val; return true;
            case "Description": a.Description = val; return true;
        }
        return false;
    }
}
