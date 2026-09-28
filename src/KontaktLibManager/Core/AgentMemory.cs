using Microsoft.Data.Sqlite;

namespace KontaktLibManager.Core;

public sealed class MemoryEntry
{
    public long Id { get; set; }
    /// <summary>global = 跨会话通用；library = 绑定某个音色库；session = 仅本会话。</summary>
    public string Scope { get; set; } = "global";
    public long? LibraryId { get; set; }
    /// <summary>简短主题（用于去重与展示）。</summary>
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>1~5，越高越优先注入。</summary>
    public int Importance { get; set; } = 3;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>
/// Agent 的**长期记忆**：跨会话保留的用户偏好、库相关事实与结论。
///
/// 设计要点：
///   · **显式写入** —— 由 Agent 调用 <c>remember</c> 工具写入，或用户在界面上手动添加；
///     不做「自动摘要一切」的黑盒记忆（那样容易积累噪声、且不可控）。
///   · **分层作用域** —— global（跨库通用偏好）/ library（绑定某库的事实）/ session（临时）。
///   · **按重要性注入** —— 每轮对话开始时把高重要度的记忆注入 system prompt，
///     受字符预算限制，避免把上下文挤满。
///   · **可增删改查** —— 用户能查看、修改、删除任何一条，不做不可见记忆。
/// </summary>
public sealed class AgentMemory
{
    private readonly string _dbPath;

    public AgentMemory(string dbPath) => _dbPath = dbPath;

    /// <summary>每轮注入 system prompt 的记忆字符预算。</summary>
    public const int InjectCharBudget = 2000;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void EnsureCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_memory (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                scope       TEXT    NOT NULL DEFAULT 'global',
                library_id  INTEGER,
                key         TEXT    NOT NULL DEFAULT '',
                content     TEXT    NOT NULL DEFAULT '',
                importance  INTEGER NOT NULL DEFAULT 3,
                created_at  INTEGER NOT NULL DEFAULT 0,
                updated_at  INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_agent_memory_scope ON agent_memory(scope);
            CREATE INDEX IF NOT EXISTS idx_agent_memory_lib ON agent_memory(library_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>写入或更新一条记忆（同 scope + key 视为同一条，做更新）。</summary>
    public MemoryEntry Remember(string scope, long? libraryId, string key, string content, int importance)
    {
        scope = scope is "library" or "session" ? scope : "global";
        importance = Math.Clamp(importance, 1, 5);
        key = (key ?? "").Trim();
        content = (content ?? "").Trim();
        long now = Now();

        using var conn = Open();

        // 同 scope+key 已存在 → 更新
        if (key.Length > 0)
        {
            using var find = conn.CreateCommand();
            find.CommandText = "SELECT id FROM agent_memory WHERE scope=$s AND key=$k AND IFNULL(library_id,0)=IFNULL($l,0) LIMIT 1;";
            find.Parameters.AddWithValue("$s", scope);
            find.Parameters.AddWithValue("$k", key);
            find.Parameters.AddWithValue("$l", (object?)libraryId ?? DBNull.Value);
            object? hit = find.ExecuteScalar();
            if (hit is long id)
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE agent_memory SET content=$c, importance=$i, updated_at=$n WHERE id=$id;";
                upd.Parameters.AddWithValue("$c", content);
                upd.Parameters.AddWithValue("$i", importance);
                upd.Parameters.AddWithValue("$n", now);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                return Get(id)!;
            }
        }

        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO agent_memory (scope, library_id, key, content, importance, created_at, updated_at)
            VALUES ($s, $l, $k, $c, $i, $n, $n);
            SELECT last_insert_rowid();
            """;
        ins.Parameters.AddWithValue("$s", scope);
        ins.Parameters.AddWithValue("$l", (object?)libraryId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$k", key);
        ins.Parameters.AddWithValue("$c", content);
        ins.Parameters.AddWithValue("$i", importance);
        ins.Parameters.AddWithValue("$n", now);
        long nid = (long)ins.ExecuteScalar()!;
        return Get(nid)!;
    }

    public MemoryEntry? Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, scope, library_id, key, content, importance, created_at, updated_at FROM agent_memory WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? Map(rd) : null;
    }

    public List<MemoryEntry> List(string? scope = null, long? libraryId = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, scope, library_id, key, content, importance, created_at, updated_at FROM agent_memory";
        var where = new List<string>();
        if (scope != null) { where.Add("scope=$s"); cmd.Parameters.AddWithValue("$s", scope); }
        if (libraryId != null) { where.Add("library_id=$l"); cmd.Parameters.AddWithValue("$l", libraryId.Value); }
        if (where.Count > 0) cmd.CommandText += " WHERE " + string.Join(" AND ", where);
        cmd.CommandText += " ORDER BY importance DESC, updated_at DESC;";

        var list = new List<MemoryEntry>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(Map(rd));
        return list;
    }

    public void Forget(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_memory WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Update(long id, string content, int? importance = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = importance.HasValue
            ? "UPDATE agent_memory SET content=$c, importance=$i, updated_at=$n WHERE id=$id;"
            : "UPDATE agent_memory SET content=$c, updated_at=$n WHERE id=$id;";
        cmd.Parameters.AddWithValue("$c", (content ?? "").Trim());
        cmd.Parameters.AddWithValue("$n", Now());
        if (importance.HasValue) cmd.Parameters.AddWithValue("$i", Math.Clamp(importance.Value, 1, 5));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 关键词检索记忆（简易打分：命中词数 × 重要度）。
    /// 不做向量检索 —— 记忆条数通常很少，关键词足够且零成本。
    /// </summary>
    public List<MemoryEntry> Search(string query, long? libraryId, int limit = 8)
    {
        var all = List();
        if (string.IsNullOrWhiteSpace(query))
            return all.OrderByDescending(m => m.Importance).ThenByDescending(m => m.UpdatedAt).Take(limit).ToList();

        var words = query.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '，', '。', '?', '？', '!', '！', ':', '：', ';', '；' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2).Distinct().ToList();

        return all
            .Select(m =>
            {
                string hay = (m.Key + " " + m.Content).ToLowerInvariant();
                int hits = words.Count(w => hay.Contains(w));
                double score = hits * 10 + m.Importance
                             + (libraryId.HasValue && m.LibraryId == libraryId ? 5 : 0);
                return (m, score, hits);
            })
            .Where(x => x.hits > 0 || words.Count == 0)
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => x.m)
            .ToList();
    }

    /// <summary>
    /// 组装注入 system prompt 的记忆块。按「库相关 > 全局」、重要度降序，受字符预算限制。
    /// </summary>
    public string BuildPromptBlock(long? libraryId)
    {
        var all = List();
        if (all.Count == 0) return "";

        var ordered = all
            .OrderByDescending(m => libraryId.HasValue && m.LibraryId == libraryId ? 1 : 0)
            .ThenByDescending(m => m.Importance)
            .ThenByDescending(m => m.UpdatedAt)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【长期记忆】（跨会话保留，供你参考；若与用户当前说法冲突，以用户当前说法为准）");
        int used = 0;
        foreach (var m in ordered)
        {
            string tag = m.Scope == "library" ? "库" : m.Scope == "session" ? "临时" : "通用";
            string line = $"- [{tag}] " + (m.Key.Length > 0 ? m.Key + "：" : "") + m.Content;
            if (used + line.Length > InjectCharBudget) break;
            sb.AppendLine(line);
            used += line.Length;
        }
        return used > 0 ? sb.ToString() : "";
    }

    private static MemoryEntry Map(SqliteDataReader rd) => new()
    {
        Id = rd.GetInt64(0),
        Scope = rd.GetString(1),
        LibraryId = rd.IsDBNull(2) ? null : rd.GetInt64(2),
        Key = rd.GetString(3),
        Content = rd.GetString(4),
        Importance = rd.GetInt32(5),
        CreatedAt = rd.GetInt64(6),
        UpdatedAt = rd.GetInt64(7),
    };
}
