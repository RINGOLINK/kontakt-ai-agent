using System;
using System.Collections.Generic;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **结果自我校验（断言式 verifier）** —— 调研报告「远期」第 11 项。
///
/// **要解决的问题（报告原话）**：把「**自信地报错**」变成「**发现不一致并自纠**」。
/// 做法是：**收尾前跑一遍廉价断言 —— 声称「已入库」就复查注册表、声称「找到 N 个」就复查计数**；
/// 不通过则**带着差异再跑一轮**。
///
/// **为什么本项目特别需要它**（都是已踩过的真实案例）：
///   · 说「已交给 Kontakt 打开」，实际只是弹了个「你要用什么程序打开」对话框
///   · 说「该库的手册…」，实际拿的是**别的库**的手册
///   · 说「知识库已建」，实际界面列表没刷新、库也没落盘
///
/// **设计原则**：
///   · **廉价** —— 只查本地索引/注册表/文件存在性，不调模型、不联网；
///   · **可缺省** —— 工具没登记断言就不校验，绝不误报；
///   · **只报告事实差异** —— 不替模型下结论，把差异原文交给它自己纠正。
/// </summary>
public static class AssertionVerifier
{
    /// <summary>一条「工具声称过什么」。</summary>
    public sealed class Claim
    {
        /// <summary>断言类型，见 <see cref="Kind"/> 常量。</summary>
        public string Kind { get; set; } = "";
        /// <summary>主体（库名 / 库 id）。</summary>
        public string Subject { get; set; } = "";
        /// <summary>声称的数量（仅 <see cref="FoundCount"/> 用）。</summary>
        public int Count { get; set; }
        /// <summary>来源工具名（便于把差异归因到具体工具）。</summary>
        public string Source { get; set; } = "";
    }

    // ── 断言类型 ──
    /// <summary>声称「已把某库入库」。校验：注册表三视图 + `Content\k2lib0&lt;SNPID&gt;`。</summary>
    public const string Registered = "registered";
    /// <summary>声称「找到 N 个」。校验：重新按同一条件查索引计数。</summary>
    public const string FoundCount = "found_count";
    /// <summary>声称「某库的知识库已建好」。校验：`ManualKb.IsFresh` + 落盘文件。</summary>
    public const string KbBuilt = "kb_built";
    /// <summary>声称「已把某乐器加载到 Kontakt」。校验：Kontakt 进程是否在跑。</summary>
    public const string Loaded = "loaded";

    /// <summary>
    /// **跑一遍全部断言，返回不一致的描述列表**（空列表 = 全部通过）。
    /// </summary>
    public static List<string> Verify(Database db, IEnumerable<Claim> claims)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>();     // 去重：同一断言重复登记只校验一次

        foreach (var c in claims)
        {
            string key = c.Kind + "|" + c.Subject + "|" + c.Count;
            if (!seen.Add(key)) continue;

            try
            {
                switch (c.Kind)
                {
                    case Registered:
                        {
                            var registered = LibraryRegistrar.ReadRegistered();
                            // 库名可能不完全等于 RegKey，先按名字找库再按 ProductKey 判
                            var lib = db.GetLibraries().FirstOrDefault(l =>
                                string.Equals(l.Name, c.Subject, StringComparison.OrdinalIgnoreCase));
                            string key2 = lib?.ProductKey ?? c.Subject;
                            bool ok = registered.ContainsKey(key2) ||
                                      registered.Keys.Any(k => k.Contains(c.Subject, StringComparison.OrdinalIgnoreCase));
                            if (!ok)
                                problems.Add($"你声称「{c.Subject}」**已入库**，但注册表里查不到它" +
                                             $"（既没有 `{key2}` 键，也没有名字含它的条目）。" +
                                             $"如果入库其实失败了，请如实说明失败原因，不要宣称成功。");
                            break;
                        }

                    case KbBuilt:
                        {
                            var lib = db.GetLibraries().FirstOrDefault(l =>
                                string.Equals(l.Name, c.Subject, StringComparison.OrdinalIgnoreCase));
                            if (lib == null)
                            {
                                problems.Add($"你声称「{c.Subject}」**知识库已建**，但索引里找不到这个库。");
                                break;
                            }
                            var kb = ManualKb.Load(lib.Id);
                            var best = AiAssistant.PickBestManual(db.GetManuals(lib.Id));
                            bool fresh = best != null && ManualKb.IsFresh(kb, best.FullPath);
                            if (!fresh)
                                problems.Add($"你声称「{c.Subject}」**知识库已建好**，但复查发现" +
                                             (kb == null ? "知识库文件不存在" : "知识库不是最新的（或手册已变更）") +
                                             $"。建库是后台任务、需要时间；请如实说明「仍在建立中」而不是已完成。");
                            break;
                        }

                    case FoundCount:
                        {
                            int actual = db.GetLibraries().Count;
                            // 只对「全库计数」类断言做硬校验；子集查询无法廉价复算，故仅提示
                            if (c.Count > actual)
                                problems.Add($"你声称找到 **{c.Count}** 个，但索引里总共只有 **{actual}** 个库 —— " +
                                             $"数量对不上，请重新统计后再回答。");
                            break;
                        }

                    case Loaded:
                        {
                            if (!KontaktInfo.IsRunning())
                                problems.Add($"你声称已把「{c.Subject}」**加载到 Kontakt**，但复查发现 " +
                                             $"**Kontakt 进程并没有在运行**。请如实说明没能加载，并给出原因" +
                                             $"（例如未设置主程序路径、或需要用户手动操作）。");
                            break;
                        }
                }
            }
            catch { /* 校验本身失败不影响主流程 */ }
        }

        return problems;
    }

    /// <summary>把校验结果拼成回灌给模型的一段话（仅在有不一致时调用）。</summary>
    public static string BuildCorrection(IEnumerable<string> problems)
    {
        var list = problems.ToList();
        if (list.Count == 0) return "";
        return "【结果自检发现不一致 —— 请据此纠正，不要坚持原来的说法】\n" +
               string.Join("\n", list.Select((p, i) => $"{i + 1}. {p}")) +
               "\n\n请先用工具把事实查清楚，再给出**与事实一致**的答复；若确实做不到，就如实说明做不到。";
    }
}
