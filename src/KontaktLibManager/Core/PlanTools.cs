using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>计划中的一个步骤。</summary>
public sealed class PlanStep
{
    /// <summary>pending = 待办；doing = 进行中；done = 已完成；blocked = 受阻。</summary>
    public string Status { get; set; } = "pending";
    public string Title { get; set; } = "";
}

/// <summary>
/// Agent 的**任务规划**：为多步任务维护一份显式计划。
///
/// 为什么需要它：像「把这 5 个库检查一遍入库状态并整理成表」这类任务，
/// 模型很容易漏步或中途跑偏。显式计划把步骤**外化**到界面上，让用户能看见进度，
/// 也让模型每轮都能对照计划推进（业界常见做法，Claude Code / DSH 类框架都有）。
///
/// 设计取舍：
///   · **不自动生成** —— 由 Agent 判断任务确实多步时才调用 <c>update_plan</c>，
///     单轮问答不产生计划，避免噪声。
///   · **整体覆盖** —— 每次提交完整步骤列表（而不是增量 patch），模型更容易用对。
///   · **按分支隔离** —— 计划挂在分支上，与上下文隔离策略一致。
/// </summary>
public sealed class AgentPlan
{
    public long BranchId { get; set; }
    public string Goal { get; set; } = "";
    public List<PlanStep> Steps { get; set; } = new();
    public long UpdatedAt { get; set; }

    public int DoneCount => Steps.Count(s => s.Status == "done");
    public bool AllDone => Steps.Count > 0 && Steps.All(s => s.Status is "done" or "blocked");
}

/// <summary>计划工具的 JSON 包装层。</summary>
public static class PlanTools
{
    /// <summary>更新计划（整体覆盖）。</summary>
    public static string Update(PlanStore store, long branchId, string argsJson)
    {
        var args = Parse(argsJson);
        string goal = GetStr(args, "goal").Trim();
        var steps = new List<PlanStep>();

        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("steps", out var se) &&
            se.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in se.EnumerateArray())
            {
                if (steps.Count >= 20) break;
                if (it.ValueKind == JsonValueKind.String)
                {
                    steps.Add(new PlanStep { Title = (it.GetString() ?? "").Trim() });
                }
                else if (it.ValueKind == JsonValueKind.Object)
                {
                    string title = it.TryGetProperty("title", out var t) ? (t.GetString() ?? "").Trim() : "";
                    string status = it.TryGetProperty("status", out var st) ? (st.GetString() ?? "pending").Trim() : "pending";
                    if (title.Length == 0) continue;
                    if (status is not ("pending" or "doing" or "done" or "blocked")) status = "pending";
                    steps.Add(new PlanStep { Title = title, Status = status });
                }
            }
        }

        if (steps.Count == 0) return "{\"error\":\"steps 不能为空\"}";

        var plan = store.Set(branchId, goal, steps);
        return ToolJson.S(new
        {
            ok = true,
            goal = plan.Goal,
            total = plan.Steps.Count,
            done = plan.DoneCount,
            steps = plan.Steps.Select(s => new { status = s.Status, title = s.Title }),
            message = plan.AllDone ? "计划已全部完成。" : $"进度 {plan.DoneCount}/{plan.Steps.Count}。",
        });
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

    private static string GetStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";
}

/// <summary>按分支保存当前计划（进程内；随会话结束自然释放）。</summary>
public sealed class PlanStore
{
    private readonly Dictionary<long, AgentPlan> _plans = new();
    private readonly object _lock = new();

    public AgentPlan Set(long branchId, string goal, List<PlanStep> steps)
    {
        var plan = new AgentPlan
        {
            BranchId = branchId,
            Goal = goal,
            Steps = steps,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        lock (_lock) _plans[branchId] = plan;
        return plan;
    }

    public AgentPlan? Get(long branchId)
    {
        lock (_lock) return _plans.TryGetValue(branchId, out var p) ? p : null;
    }

    public void Clear(long branchId)
    {
        lock (_lock) _plans.Remove(branchId);
    }
}
