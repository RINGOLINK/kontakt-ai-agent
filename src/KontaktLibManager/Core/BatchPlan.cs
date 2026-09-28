using System;
using System.Collections.Generic;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **批量操作的自然语言编排** —— 调研报告「远期」第 10 项。
///
/// **要解决的问题（报告原话）**：把自然语言落成**显式 DSL**（如 `foreach lib where duplicate: unlink`），
/// **复用已有 `update_plan` 外化步骤 + 已有 `ConfirmUi` 做批量确认**。
/// 「把已有的一次性工具（批量收尾、重复清理…）串成可审阅、可确认的批处理」。
///
/// **为什么需要显式 DSL 而不是让模型直接连续调工具**：
///   · **可审阅** —— 用户能先看到「要动哪些库、做什么」，再决定是否执行；
///   · **可确认** —— 一次批量确认，而不是几十次弹窗；
///   · **可复现** —— DSL 文本本身就是操作记录，能贴给别人、能存下来再跑。
///
/// **DSL 形式**：`foreach lib where &lt;条件&gt;[: &lt;动作&gt;]`（多个子句用 `;` 或换行分隔）
///
/// **条件**（可组合，用 `and` / `,` 连接）：
///   `duplicate`      与其他库内容高度重叠（Jaccard ≥ 0.55）
///   `unregistered`   带 .nicnt 但没入库
///   `nonstandard`    目录里没有 .nicnt
///   `no-kb`          有说明书但知识库没建/过期
///   `no-cover`       缺封面横幅
///   `junk`           杂质 > 50 MB
///   `uncategorized`  没有分类
///   `category=弦乐`  分类等于某值
///   `name~Adagio`    库名含某关键词
///
/// **动作**：
///   `list`           只列出（默认动作，等价于 dry-run）
///   `register`       入库（写注册表三视图 + Content\k2lib0&lt;SNPID&gt;）
///   `build-kb`       建立说明书知识库
///   `rescan-manuals` 重扫说明书清单
///   `tag:标签名`     给命中的库打标签
///
/// **安全**：**解析出的操作必须经用户确认才执行**（由调用方走 `ConfirmUi`）；
/// 本类只负责「解析 + 展开 + 逐个执行」，**不自行决定是否执行**。
/// </summary>
public static class BatchPlan
{
    /// <summary>一个已展开的批量操作（一行 = 一个库 + 一个动作）。</summary>
    public sealed class Op
    {
        public long LibraryId { get; set; }
        public string LibraryName { get; set; } = "";
        /// <summary>动作名（list/register/build-kb/rescan-manuals/tag）。</summary>
        public string Action { get; set; } = "list";
        /// <summary>动作参数（如 tag 的标签名）。</summary>
        public string Arg { get; set; } = "";
        /// <summary>为什么命中（人类可读，用于确认弹窗展示）。</summary>
        public string Reason { get; set; } = "";
    }

    /// <summary>一个子句的解析结果。</summary>
    public sealed class Clause
    {
        public List<string> Conditions { get; set; } = new();
        public string Action { get; set; } = "list";
        public string Arg { get; set; } = "";
        public string Raw { get; set; } = "";
    }

    /// <summary>解析 DSL；返回子句列表。解析失败时 <paramref name="error"/> 非空。</summary>
    public static List<Clause> Parse(string dsl, out string error)
    {
        error = "";
        var clauses = new List<Clause>();
        if (string.IsNullOrWhiteSpace(dsl)) { error = "DSL 为空"; return clauses; }

        // 子句分隔：换行 或 分号
        var parts = dsl.Replace("\r", "").Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            if (s.StartsWith("#")) continue;                 // 注释

            // 前缀 `foreach lib where ` 可省
            var body = s;
            foreach (var pre in new[] { "foreach lib where ", "foreach library where ", "foreach lib ", "for lib where " })
                if (body.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { body = body[pre.Length..]; break; }

            // 按【第一个】冒号切成「条件 : 动作」——
            // ⚠️ 必须用 IndexOf 而非 LastIndexOf：动作本身可能带冒号（如 tag:待清理），
            //    用 LastIndexOf 会把动作名切进条件里（实测踩过）。
            string condPart = body, actPart = "";
            int ci = body.IndexOf(':');
            if (ci >= 0) { condPart = body[..ci]; actPart = body[(ci + 1)..].Trim(); }

            var c = new Clause { Raw = s };
            foreach (var cond in condPart.Split(new[] { " and ", ",", " AND ", " And " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = cond.Trim();
                if (t.Length > 0) c.Conditions.Add(t);
            }

            if (actPart.Length > 0)
            {
                int ti = actPart.IndexOf(':');
                if (ti >= 0) { c.Action = actPart[..ti].Trim().ToLowerInvariant(); c.Arg = actPart[(ti + 1)..].Trim(); }
                else c.Action = actPart.ToLowerInvariant();
            }

            var known = new[] { "list", "register", "build-kb", "rescan-manuals", "tag" };
            if (!known.Contains(c.Action))
            {
                error = $"不认识的动作用「{c.Action}」。可用动作：{string.Join(" / ", known)}";
                return clauses;
            }
            if (c.Conditions.Count == 0)
            {
                error = $"子句「{s}」缺少条件（例：`foreach lib where duplicate: list`）";
                return clauses;
            }
            clauses.Add(c);
        }
        if (clauses.Count == 0) error = "DSL 里没有可执行的子句";
        return clauses;
    }

    /// <summary>
    /// **把 DSL 展开成具体操作列表**（只读分析，不执行任何写入）。
    /// </summary>
    public static List<Op> Expand(Database db, List<Clause> clauses)
    {
        var libs = db.GetLibraries().ToList();
        var ops = new List<Op>();

        // 相似库集合（只在有 duplicate 条件时算一次）
        HashSet<long>? dupIds = null;
        if (clauses.Any(c => c.Conditions.Any(x => x.Equals("duplicate", StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                dupIds = new HashSet<long>();
                foreach (var s in DuplicateFinder.FindSimilarLibraries(db, 0.55))
                { dupIds.Add(s.LibraryA); dupIds.Add(s.LibraryB); }
            }
            catch { dupIds = new HashSet<long>(); }
        }

        foreach (var c in clauses)
        {
            foreach (var lib in libs)
            {
                string reason;
                if (!Matches(db, lib, c.Conditions, dupIds, out reason)) continue;
                ops.Add(new Op
                {
                    LibraryId = lib.Id,
                    LibraryName = lib.Name,
                    Action = c.Action,
                    Arg = c.Arg,
                    Reason = reason,
                });
            }
        }
        // 去重（同一库同一动作只做一次）
        return ops.GroupBy(o => o.LibraryId + "|" + o.Action + "|" + o.Arg).Select(g => g.First()).ToList();
    }

    /// <summary>判断一个库是否满足全部条件；命中时给出人类可读的原因。</summary>
    private static bool Matches(Database db, LibraryRecord lib, List<string> conds, HashSet<long>? dupIds, out string reason)
    {
        reason = "";                       // out 参数必须在所有路径上赋值
        var hits = new List<string>();
        foreach (var raw in conds)
        {
            var c = raw.Trim();
            if (c.Equals("duplicate", StringComparison.OrdinalIgnoreCase))
            {
                if (dupIds == null || !dupIds.Contains(lib.Id)) return false;
                hits.Add("与其他库内容高度重叠");
            }
            else if (c.Equals("unregistered", StringComparison.OrdinalIgnoreCase))
            {
                if (!lib.HasNicnt) return false;
                try { if (LibraryRegistrar.ReadRegistered().ContainsKey(lib.ProductKey ?? "")) return false; } catch { return false; }
                hits.Add("带 .nicnt 但没入库");
            }
            else if (c.Equals("nonstandard", StringComparison.OrdinalIgnoreCase))
            {
                if (lib.HasNicnt) return false;
                hits.Add("目录里没有 .nicnt");
            }
            else if (c.Equals("no-kb", StringComparison.OrdinalIgnoreCase))
            {
                var mans = db.GetManuals(lib.Id);
                if (mans.Count == 0) return false;
                var best = AiAssistant.PickBestManual(mans);
                if (best == null) return false;
                try { if (ManualKb.IsFresh(ManualKb.Load(lib.Id), best.FullPath)) return false; } catch { return false; }
                hits.Add("说明书还没建知识库");
            }
            else if (c.Equals("no-cover", StringComparison.OrdinalIgnoreCase))
            {
                if (!lib.HasNicnt || !string.IsNullOrEmpty(lib.CoverFile)) return false;
                hits.Add("缺封面横幅");
            }
            else if (c.Equals("junk", StringComparison.OrdinalIgnoreCase))
            {
                if (lib.JunkBytes <= 50L * 1024 * 1024) return false;
                hits.Add($"杂质 {lib.JunkBytes / 1024 / 1024} MB");
            }
            else if (c.Equals("uncategorized", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(lib.Category)) return false;
                hits.Add("没有分类");
            }
            else if (c.StartsWith("category=", StringComparison.OrdinalIgnoreCase))
            {
                var want = c["category=".Length..].Trim();
                if (!string.Equals(lib.Category, want, StringComparison.OrdinalIgnoreCase)) return false;
                hits.Add($"分类={lib.Category}");
            }
            else if (c.StartsWith("name~", StringComparison.OrdinalIgnoreCase))
            {
                var kw = c["name~".Length..].Trim();
                if (lib.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
                hits.Add($"库名含「{kw}」");
            }
            else
            {
                // 未知条件 → 视为不匹配（保守：宁可少动，不可误动）
                return false;
            }
        }
        reason = string.Join("、", hits);
        return true;
    }
}
