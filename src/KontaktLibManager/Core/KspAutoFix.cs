using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KontaktLibManager.Core;

/// <summary>
/// **KSP 生成 → 编译 → 自纠 闭环**（「KSP 能力深化」第二步）。
///
/// **为什么需要它**：KSP 编译器对编译问题**只输出 Warning、退出码仍为 0**（已在 `KspCompiler` 里处理），
/// 而且**很多错误是「可机械修复」的** —— 缺 `end if`、缺 `end on`、变量没 `declare`、命令名拼错。
/// 让模型每轮都重写整段脚本既慢又容易引入新错，**不如先用规则修掉能修的**。
///
/// **📌 设计原则**：
///   · **只做【确定性】的机械修复** —— 修不了就如实报告，**不猜、不编造**；
///   · **每轮都重新编译**（不假设修好了）；
///   · **最多 N 轮**（默认 4），避免死循环；
///   · **返回完整过程**（每轮改了什么、还剩什么问题）—— 让模型/用户能看懂。
///
/// **⚠️ 能力边界（如实说明）**：
///   本类**只能修【结构/声明类】错误**（缺 end、缺 declare、命令名拼错）。
///   **逻辑错误（「语法对但行为不对」）它修不了** —— 那需要理解意图，得靠模型。
/// </summary>
public static class KspAutoFix
{
    /// <summary>一轮的记录。</summary>
    public sealed class Round
    {
        public int Index { get; set; }
        public bool Compiled { get; set; }
        public int ErrorCount { get; set; }
        /// <summary>本轮应用的修复（人话描述）。</summary>
        public List<string> Fixes { get; set; } = new();
        /// <summary>本轮仍存在的问题（未能自动修）。</summary>
        public List<string> Remaining { get; set; } = new();
    }

    public sealed class Result
    {
        public bool Ok { get; set; }
        public string FinalScript { get; set; } = "";
        public List<Round> Rounds { get; set; } = new();
        /// <summary>给模型/用户看的总结（**含「还差什么」**）。</summary>
        public string Summary { get; set; } = "";
    }

    /// <summary>
    /// **跑自纠闭环**：编译 → 按规则修 → 再编译，最多 <paramref name="maxRounds"/> 轮。
    /// </summary>
    /// <param name="compilerPath">kspc.exe 路径。</param>
    /// <param name="script">KSP 源码。</param>
    /// <param name="workDir">临时工作目录（脚本会写到这里）。</param>
    /// <param name="maxRounds">最大轮数（默认 4）。</param>
    public static Result Run(string compilerPath, string script, string workDir, int maxRounds = 4)
    {
        var res = new Result();
        if (string.IsNullOrWhiteSpace(script)) { res.Summary = "脚本为空。"; return res; }

        Directory.CreateDirectory(workDir);
        var path = Path.Combine(workDir, "autofix.ksp");
        var cur = script;

        for (int round = 1; round <= Math.Max(1, maxRounds); round++)
        {
            File.WriteAllText(path, cur, new UTF8Encoding(false));
            var cr = KspCompiler.Compile(compilerPath, path);
            var diags = cr.Diagnostics ?? new List<KspCompiler.Diagnostic>();
            var errors = diags.Where(d => !string.Equals(d.Severity, "info", StringComparison.OrdinalIgnoreCase)).ToList();

            var r = new Round { Index = round, Compiled = cr.Ok, ErrorCount = errors.Count };
            res.Rounds.Add(r);

            if (cr.Ok && errors.Count == 0)
            {
                res.Ok = true;
                res.FinalScript = cur;
                res.Summary = $"✅ 编译通过（第 {round} 轮）。" + DescribeFixes(res);
                return res;
            }

            // **尝试机械修复**
            var (fixedScript, fixes, unfixable) = ApplyRules(cur, errors);
            r.Fixes = fixes;
            r.Remaining = unfixable.Select(d => $"第 {d.Line} 行: {d.Message}").ToList();

            if (fixes.Count == 0)
            {
                // 修不动了 —— 如实返回
                res.FinalScript = cur;
                res.Summary = $"⚠ 第 {round} 轮后【无法再机械修复】—— 还剩 {errors.Count} 个问题需要人工/模型处理：\n" +
                              string.Join("\n", r.Remaining.Take(10)) + DescribeFixes(res);
                return res;
            }
            cur = fixedScript;
        }

        res.FinalScript = cur;
        res.Summary = $"⚠ 已达最大轮数（{maxRounds}）仍未通过 —— 最后剩 {res.Rounds.Last().ErrorCount} 个问题。\n" +
                      string.Join("\n", res.Rounds.Last().Remaining.Take(10)) + DescribeFixes(res);
        return res;
    }

    private static string DescribeFixes(Result res)
    {
        var all = res.Rounds.SelectMany(r => r.Fixes).ToList();
        return all.Count == 0 ? "" : "\n自动修复了 " + all.Count + " 处：\n· " + string.Join("\n· ", all.Take(12));
    }

    /// <summary>
    /// **按规则修**。返回 `(修好的脚本, 修复说明, 修不了的问题)`。
    /// **只做确定性修复** —— 认不出的问题原样返回。
    /// </summary>
    public static (string Script, List<string> Fixes, List<KspCompiler.Diagnostic> Unfixable)
        ApplyRules(string script, List<KspCompiler.Diagnostic> errors)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n').ToList();
        var fixes = new List<string>();
        var unfixable = new List<KspCompiler.Diagnostic>();

        // 先把错误按行分组（从后往前修，避免行号漂移）
        foreach (var d in errors.OrderByDescending(x => x.Line))
        {
            var msg = (d.Message ?? "").ToLowerInvariant();
            int ln = d.Line;

            // ── 规则 1：缺 end if / end on / end while / end select ──
            // 编译器的措辞不固定，所以用「消息里提到缺 end」来识别
            if (msg.Contains("end if") || msg.Contains("expected end if") || msg.Contains("endif"))
            {
                if (InsertBefore(lines, ln, "end if", out var at))
                    fixes.Add($"第 {ln} 行附近补 `end if`（插入到第 {at} 行）");
                else unfixable.Add(d);
                continue;
            }
            if (msg.Contains("end on"))
            {
                if (InsertBefore(lines, ln, "end on", out var at))
                    fixes.Add($"第 {ln} 行附近补 `end on`（插入到第 {at} 行）");
                else unfixable.Add(d);
                continue;
            }
            // **编译器的真实措辞**：缺 on 块的结尾时报
            //   `mismatched input '<EOF>' expecting 'end'`（实测）
            // 这时补 `end on`（KSP 的 on 块以 end on 结尾）
            if (msg.Contains("expecting 'end'") || msg.Contains("expecting \"end\""))
            {
                if (InsertBefore(lines, ln, "end on", out var at2))
                    fixes.Add($"第 {ln} 行附近补 `end on`（编译器报 expecting 'end'；插入到第 {at2} 行）");
                else unfixable.Add(d);
                continue;
            }

            // ── 规则 2：未声明的变量（KSP 要求 declare）──
            // 形如 `$xxx` / `%xxx` / `@xxx` / `!xxx` 的标识符
            var m = Regex.Match(d.Message ?? "", @"([\$%@!][A-Za-z_][A-Za-z0-9_]*)");
            if ((msg.Contains("undeclared") || msg.Contains("not declared") || msg.Contains("unknown variable")) && m.Success)
            {
                var v = m.Groups[1].Value;
                var decl = v[0] switch
                {
                    '$' => $"declare {v}",
                    '%' => $"declare {v}",
                    '@' => $"declare {v}",
                    _ => $"declare {v}",
                };
                // 插到 on init 之后（找不到就插到文件开头）
                int initAt = lines.FindIndex(x => x.Trim().Equals("on init", StringComparison.OrdinalIgnoreCase));
                int insertAt = initAt >= 0 ? initAt + 1 : 0;
                lines.Insert(insertAt, "    " + decl);
                fixes.Add($"补声明 `{decl}`（第 {insertAt + 1} 行）");
                continue;
            }

            // ── 规则 3：命令名拼错 → 给出相似的真实命令 ──
            var mc = Regex.Match(d.Message ?? "", @"['""]([A-Za-z_][A-Za-z0-9_]*)['""]");
            if ((msg.Contains("unknown") || msg.Contains("undefined") || msg.Contains("not found")) && mc.Success)
            {
                var bad = mc.Groups[1].Value;
                var near = KspSymbols.Search(bad, null, 3);
                if (near.Count > 0)
                {
                    // **不自动替换** —— 改命令名有风险（可能是用户自定义变量）。
                    // 只报告「可能是这个」，交给模型决定。
                    unfixable.Add(new KspCompiler.Diagnostic
                    {
                        Line = d.Line, Column = d.Column, Severity = d.Severity,
                        Message = $"未知标识符 `{bad}` —— 相似的真实 KSP 命令：" +
                                  string.Join(" / ", near.Select(s => s.Name)) + "（需模型判断是否该替换）",
                        Raw = d.Raw,
                    });
                    continue;
                }
            }

            unfixable.Add(d);
        }

        return (string.Join("\n", lines), fixes, unfixable);
    }

    /// <summary>在第 ln 行【之前】插入一行；返回实际插入位置。</summary>
    private static bool InsertBefore(List<string> lines, int ln, string text, out int at)
    {
        at = -1;
        if (ln <= 0) return false;
        at = Math.Min(ln - 1, lines.Count);
        if (at < 0) at = 0;
        lines.Insert(at, "    " + text);
        at += 1;
        return true;
    }
}
