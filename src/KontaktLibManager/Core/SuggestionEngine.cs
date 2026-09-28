using System;
using System.Collections.Generic;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **主动建议引擎**（调研报告「远期」第 12 项）。
///
/// **要解决的问题**：应用原本是「你问我答」——用户得先想到要做什么，才来操作。
/// 报告的原话是「从『问答』升级为『助手』，**是最容易被用户感知的价值**」。
///
/// **做法**：扫描完（或用户主动点）后，**复用已有的分析构件**跑一遍体检，
/// 把「值得做的事」按**影响量**排好队，每条都带上**一键可执行的动作**。
///
/// **复用的构件**（不重复造轮子）：
///   · <see cref="DuplicateFinder.FindDuplicateFiles"/> —— 重复大文件
///   · <see cref="DuplicateFinder.FindSimilarLibraries"/> —— 高度重叠的库
///   · <see cref="LibraryClassifier"/> —— 分类覆盖情况
///   · <see cref="ManualKb.IsFresh"/> —— 说明书是否已建知识库
///
/// **约束**：**纯本地、无网络、无外部依赖**（报告特别强调「不要为了更聪明而引入云端服务或大模型依赖」——
/// 3.39TB 音色库的用户很在意「数据不出机器」）。
/// </summary>
public static class SuggestionEngine
{
    /// <summary>一条建议。</summary>
    public sealed class Suggestion
    {
        /// <summary>级别：high（该处理）/ info（可优化）。</summary>
        public string Level { get; set; } = "info";
        /// <summary>短标题，如「12 个库还没入库」。</summary>
        public string Title { get; set; } = "";
        /// <summary>一句话说明为什么值得做。</summary>
        public string Detail { get; set; } = "";
        /// <summary>影响量（用于排序；越大越优先）。可回收字节数或涉及库数。</summary>
        public long Weight { get; set; }
        /// <summary>一键动作的 RPC 名（前端据此调用）；空表示只提示、无动作。</summary>
        public string Action { get; set; } = "";
        /// <summary>动作参数（JSON 串，原样传给 RPC）。</summary>
        public string ActionArgs { get; set; } = "{}";
        /// <summary>动作按钮文案。</summary>
        public string ActionLabel { get; set; } = "";
        /// <summary>涉及的库名（最多列 8 个，供界面展示）。</summary>
        public List<string> Samples { get; set; } = new();
    }

    /// <summary>
    /// 跑一遍体检，返回**按影响量降序**排好的建议列表。
    /// </summary>
    /// <param name="db">索引库。</param>
    /// <param name="includeSlow">是否包含耗时项（重复文件扫描会读文件大小/哈希，较慢）。</param>
    public static List<Suggestion> Analyze(Database db, bool includeSlow = false)
    {
        var list = new List<Suggestion>();
        List<LibraryRecord> libs;
        try { libs = db.GetLibraries().ToList(); } catch { return list; }
        if (libs.Count == 0) return list;

        // ── ① 还没入库的库（有 .nicnt 才算「标准库」，才有入库的可能）──
        try
        {
            var registered = LibraryRegistrar.ReadRegistered();
            var pending = libs.Where(l => l.HasNicnt && !registered.ContainsKey(l.ProductKey ?? ""))
                              .Select(l => l.Name).ToList();
            if (pending.Count > 0)
                list.Add(new Suggestion
                {
                    Level = "high",
                    Title = $"{pending.Count} 个库还没入库",
                    Detail = "它们带 .nicnt 但注册表里没有记录，Kontakt 的 Library 浏览器里看不到。" +
                             "入库后可一键打开、也能用 Quick-Load。",
                    Weight = 1_000_000L + pending.Count,
                    Action = "registerLibraries",
                    ActionLabel = "去入库",
                    Samples = pending.Take(8).ToList(),
                });
        }
        catch { }

        // ── ② 说明书还没建知识库的库 ──
        try
        {
            var needKb = new List<string>();
            foreach (var l in libs)
            {
                var mans = db.GetManuals(l.Id);
                if (mans.Count == 0) continue;
                var best = AiAssistant.PickBestManual(mans);
                if (best == null) continue;
                if (!ManualKb.IsFresh(ManualKb.Load(l.Id), best.FullPath)) needKb.Add(l.Name);
            }
            if (needKb.Count > 0)
                list.Add(new Suggestion
                {
                    Level = "high",
                    Title = $"{needKb.Count} 个库的说明书还没建知识库",
                    Detail = "建库后「问问AI」才能检索这些库的手册原文（不建库时只能凭模型通用知识回答）。" +
                             "可在「知识库」面板点「批量建立」一次跑完。",
                    Weight = 900_000L + needKb.Count,
                    Action = "kbBuildAll",
                    ActionLabel = "批量建立",
                    Samples = needKb.Take(8).ToList(),
                });
        }
        catch { }

        // ── ③ 高度重叠的库（复用 FindSimilarLibraries）──
        try
        {
            var similar = DuplicateFinder.FindSimilarLibraries(db, 0.55);
            if (similar.Count > 0)
            {
                var names = similar.Take(8).Select(s => s.NameA).Concat(similar.Take(8).Select(s => s.NameB))
                                   .Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                list.Add(new Suggestion
                {
                    Level = "info",
                    Title = $"{similar.Count} 组库内容高度重叠",
                    Detail = "这些库的 NKI 名称集合重合度很高，可能是同一套音色的不同版本（完整版/精简版）。" +
                             "确认后可考虑只保留一个，或给它们加标签区分。",
                    Weight = 700_000L + similar.Count,
                    Action = "find_duplicates",
                    ActionLabel = "查看详情",
                    Samples = names,
                });
            }
        }
        catch { }

        // ── ④ 有杂质的库（可回收空间）──
        try
        {
            var junky = libs.Where(l => l.JunkBytes > 50L * 1024 * 1024)
                            .OrderByDescending(l => l.JunkBytes).ToList();
            if (junky.Count > 0)
            {
                long total = junky.Sum(l => l.JunkBytes);
                list.Add(new Suggestion
                {
                    Level = "info",
                    Title = $"{junky.Count} 个库可清理杂质（约 {FormatBytes(total)}）",
                    Detail = "杂质含 macOS 残留（.DS_Store / ._ 副档）、安装包、日志等非音色文件。" +
                             "「健康检查」页可逐项确认后清理。",
                    Weight = 600_000L + total / (1024 * 1024),
                    Action = "",
                    ActionLabel = "",
                    Samples = junky.Take(8).Select(l => l.Name).ToList(),
                });
            }
        }
        catch { }

        // ── ⑤ 缺封面横幅的库 ──
        try
        {
            var noCover = libs.Where(l => l.HasNicnt && string.IsNullOrEmpty(l.CoverFile))
                              .Select(l => l.Name).ToList();
            if (noCover.Count > 0)
                list.Add(new Suggestion
                {
                    Level = "info",
                    Title = $"{noCover.Count} 个库没有封面横幅",
                    Detail = "Kontakt 的库列表横幅读的是库目录下的 Wallpaper.png（905×99 RGB）。" +
                             "没有它库仍能正常使用，只是列表里显示为默认灰底 —— **无封面 ≠ 库损坏**。",
                    Weight = 300_000L + noCover.Count,
                    Action = "",
                    ActionLabel = "",
                    Samples = noCover.Take(8).ToList(),
                });
        }
        catch { }

        // ── ⑥ 非标准库（无 .nicnt，进不了注册表）──
        try
        {
            var nonStd = libs.Where(l => !l.HasNicnt).Select(l => l.Name).ToList();
            if (nonStd.Count > 0)
                list.Add(new Suggestion
                {
                    Level = "info",
                    Title = $"{nonStd.Count} 个非标准库（目录内没有 .nicnt）",
                    Detail = "这类库无法注册进 Kontakt 的 Library 浏览器，但可以在 Kontakt 的 Files 浏览器里" +
                             "直接加载乐器；也可以用本工具的「生成 .nicnt 并入库」为它们补一份元数据。",
                    Weight = 200_000L + nonStd.Count,
                    Action = "",
                    ActionLabel = "",
                    Samples = nonStd.Take(8).ToList(),
                });
        }
        catch { }

        // ── ⑦ 还没分类的库 ──
        try
        {
            var unclassified = libs.Where(l => string.IsNullOrWhiteSpace(l.Category))
                                   .Select(l => l.Name).ToList();
            if (unclassified.Count > 0)
                list.Add(new Suggestion
                {
                    Level = "info",
                    Title = $"{unclassified.Count} 个库没有分类",
                    Detail = "分类由库名自动推断（打击乐/电影配乐/弦乐/氛围音效/钢琴键盘…）。" +
                             "未分类的库在「音色库列表」里会归到「未分类」，可用标签手工补充。",
                    Weight = 100_000L + unclassified.Count,
                    Action = "",
                    ActionLabel = "",
                    Samples = unclassified.Take(8).ToList(),
                });
        }
        catch { }

        // ── ⑧ 重复大文件（耗时项，默认跳过）──
        if (includeSlow)
        {
            try
            {
                var groups = DuplicateFinder.FindDuplicateFiles(db, 50L * 1024 * 1024, null, CancellationToken.None);
                if (groups.Count > 0)
                {
                    long reclaim = groups.Sum(g => g.ReclaimableBytes);
                    list.Add(new Suggestion
                    {
                        Level = "info",
                        Title = $"{groups.Count} 组重复大文件（可回收约 {FormatBytes(reclaim)}）",
                        Detail = "这些文件在不同库里内容完全相同（通常是同一套采样的重复拷贝）。" +
                                 "检测只读、不会删除任何文件。",
                        Weight = 500_000L + reclaim / (1024 * 1024),
                        Action = "find_duplicates",
                        ActionLabel = "查看详情",
                        Samples = groups.Take(5).Select(g => g.Files.FirstOrDefault().LibraryName ?? "").Where(x => x.Length > 0).ToList(),
                    });
                }
            }
            catch { }
        }

        return list.OrderByDescending(s => s.Weight).ToList();
    }

    private static string FormatBytes(long b)
    {
        if (b >= 1024L * 1024 * 1024) return $"{b / 1024.0 / 1024 / 1024:F1} GB";
        if (b >= 1024L * 1024) return $"{b / 1024.0 / 1024:F0} MB";
        if (b >= 1024) return $"{b / 1024.0:F0} KB";
        return $"{b} B";
    }
}
