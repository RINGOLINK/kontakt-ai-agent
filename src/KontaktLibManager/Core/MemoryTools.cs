using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>
/// 长期记忆工具的 JSON 包装层（把 <see cref="AgentMemory"/> 暴露给 Agent）。
///
/// 与 <see cref="AgentTools"/> 分开的原因：记忆属于「会改数据」的操作，
/// 但改的是本工具自己的记忆库（不是用户的音色库文件），风险极低，
/// 因此按用户定的权限模型归入「只读白名单直通」一侧 —— 不需要逐条确认，
/// 但**用户随时能在界面上查看、修改、删除任何一条**，不存在不可见记忆。
/// </summary>
public static class MemoryTools
{
    public static string Remember(AgentToolContext ctx, string argsJson)
    {
        var m = ctx.Memory;
        if (m == null) return "{\"error\":\"记忆功能不可用\"}";
        var args = Parse(argsJson);

        string content = GetStr(args, "content").Trim();
        if (content.Length == 0) return "{\"error\":\"content 不能为空\"}";
        if (content.Length > 500) content = content[..500];

        string key = GetStr(args, "key").Trim();
        string scope = GetStr(args, "scope").Trim().ToLowerInvariant();
        if (scope.Length == 0) scope = "global";
        long imp = GetLong(args, "importance");
        int importance = imp is >= 1 and <= 5 ? (int)imp : 3;

        long? libId = scope == "library" ? ctx.CurrentLibraryId : null;
        if (scope == "library" && libId == null) scope = "global";   // 没有绑定库就降级为通用

        var e = m.Remember(scope, libId, key, content, importance);
        return ToolJson.S(new
        {
            ok = true,
            id = e.Id,
            scope = e.Scope,
            key = e.Key,
            content = e.Content,
            importance = e.Importance,
            message = "已记住。用户可在「问问AI → 记忆」里查看与修改。",
        });
    }

    public static string Recall(AgentToolContext ctx, string argsJson)
    {
        var m = ctx.Memory;
        if (m == null) return "{\"error\":\"记忆功能不可用\"}";
        var args = Parse(argsJson);

        string q = GetStr(args, "query");
        long lim = GetLong(args, "limit");
        int limit = lim is > 0 and <= 30 ? (int)lim : 8;

        var list = m.Search(q, ctx.CurrentLibraryId, limit);
        return ToolJson.S(new
        {
            query = q,
            count = list.Count,
            items = list.Select(e => new
            {
                id = e.Id,
                scope = e.Scope,
                libraryId = e.LibraryId,
                key = e.Key,
                content = e.Content,
                importance = e.Importance,
            }),
            note = list.Count == 0 ? "没有匹配的记忆" : "",
        });
    }

    public static string Forget(AgentToolContext ctx, string argsJson)
    {
        var m = ctx.Memory;
        if (m == null) return "{\"error\":\"记忆功能不可用\"}";
        var args = Parse(argsJson);
        long id = GetLong(args, "id");
        if (id <= 0) return "{\"error\":\"缺少有效的 id（可先用 recall 查询）\"}";

        var e = m.Get(id);
        if (e == null) return $"{{\"error\":\"没有 id={id} 的记忆\"}}";
        m.Forget(id);
        return ToolJson.S(new { ok = true, id, forgotten = e.Content });
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

    private static long GetLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long n) ? n : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out long m) ? m : 0,
            _ => 0,
        };
    }
}
