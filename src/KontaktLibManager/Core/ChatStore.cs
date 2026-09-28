using Microsoft.Data.Sqlite;

namespace KontaktLibManager.Core;

/// <summary>一个问答会话。从某个库进入时 library_id 有值；从标签页直接进入则为空（跨库会话）。</summary>
public sealed class ChatSession
{
    public long Id { get; set; }
    public long? LibraryId { get; set; }
    public string LibraryName { get; set; } = "";
    /// <summary>所属分类（用于左侧文件夹收纳），来自音色库分类。</summary>
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    /// <summary>当前激活的分支。</summary>
    public long? ActiveBranchId { get; set; }
    public int BranchCount { get; set; }
    public int MessageCount { get; set; }
}

/// <summary>
/// 会话下的分支。
/// 约定（用户拍板）：从单库进入的会话，**单库模式一定是主分支**（ParentId = null），
/// 每触发一次跨库问答就新建一个跨库分支挂在该会话下，可同时存在多个。
/// </summary>
public sealed class ChatBranch
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    /// <summary>single = 单库问答模式；cross = 跨库问答模式。</summary>
    public string Mode { get; set; } = "single";
    /// <summary>主分支为 null；跨库分支记录它从哪个分支切换过来。</summary>
    public long? ParentId { get; set; }
    public string Title { get; set; } = "";
    public long CreatedAt { get; set; }
    public int MessageCount { get; set; }
}

public sealed class ChatEntry
{
    public long Id { get; set; }
    public long BranchId { get; set; }
    /// <summary>user / assistant。</summary>
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>推理过程（推理模型返回的 reasoning），可为空。</summary>
    public string Reasoning { get; set; } = "";
    public long CreatedAt { get; set; }
}

/// <summary>
/// 「问问AI」的会话 / 分支 / 消息存储。
///
/// 表结构（与主索引同一 SQLite 文件，独立连接）：
///   chat_sessions(id, library_id, library_name, category, title, created_at, updated_at, active_branch_id)
///   chat_branches(id, session_id, mode, parent_id, title, created_at)
///   chat_messages(id, branch_id, role, content, reasoning, created_at)
///
/// 消息**固化存储**在本地，会话与分支可随时恢复；知识库本身复用 data/kb（不在此表内）。
/// </summary>
public sealed class ChatStore
{
    private readonly string _dbPath;

    public ChatStore(string dbPath) => _dbPath = dbPath;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void EnsureCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_sessions (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id       INTEGER,
                library_name     TEXT    NOT NULL DEFAULT '',
                category         TEXT    NOT NULL DEFAULT '',
                title            TEXT    NOT NULL DEFAULT '',
                created_at       INTEGER NOT NULL DEFAULT 0,
                updated_at       INTEGER NOT NULL DEFAULT 0,
                active_branch_id INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_chat_sessions_lib ON chat_sessions(library_id);

            CREATE TABLE IF NOT EXISTS chat_branches (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
                mode       TEXT    NOT NULL DEFAULT 'single',
                parent_id  INTEGER,
                title      TEXT    NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_chat_branches_session ON chat_branches(session_id);

            CREATE TABLE IF NOT EXISTS chat_messages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                branch_id  INTEGER NOT NULL REFERENCES chat_branches(id) ON DELETE CASCADE,
                role       TEXT    NOT NULL DEFAULT '',
                content    TEXT    NOT NULL DEFAULT '',
                reasoning  TEXT    NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_chat_messages_branch ON chat_messages(branch_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ────────────────────────── 会话 ──────────────────────────

    /// <summary>新建会话，并自动创建主分支（单库模式，或从标签页进入时的跨库模式）。</summary>
    public ChatSession CreateSession(long? libraryId, string libraryName, string category, string title, string mainMode)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        long now = Now();
        long sid;
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO chat_sessions (library_id, library_name, category, title, created_at, updated_at, active_branch_id)
                VALUES ($lib, $name, $cat, $title, $now, $now, NULL);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$lib", (object?)libraryId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$name", libraryName ?? "");
            ins.Parameters.AddWithValue("$cat", category ?? "");
            ins.Parameters.AddWithValue("$title", title ?? "");
            ins.Parameters.AddWithValue("$now", now);
            sid = (long)ins.ExecuteScalar()!;
        }

        long bid;
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO chat_branches (session_id, mode, parent_id, title, created_at)
                VALUES ($sid, $mode, NULL, $title, $now);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$sid", sid);
            ins.Parameters.AddWithValue("$mode", mainMode == "cross" ? "cross" : "single");
            ins.Parameters.AddWithValue("$title", mainMode == "cross" ? "跨库问答" : "单库问答");
            ins.Parameters.AddWithValue("$now", now);
            bid = (long)ins.ExecuteScalar()!;
        }

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE chat_sessions SET active_branch_id = $b WHERE id = $s;";
            upd.Parameters.AddWithValue("$b", bid);
            upd.Parameters.AddWithValue("$s", sid);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        return GetSession(sid)!;
    }

    public ChatSession? GetSession(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.library_id, s.library_name, s.category, s.title, s.created_at, s.updated_at, s.active_branch_id,
                   (SELECT COUNT(*) FROM chat_branches b WHERE b.session_id = s.id),
                   (SELECT COUNT(*) FROM chat_messages m
                      JOIN chat_branches b2 ON b2.id = m.branch_id WHERE b2.session_id = s.id)
            FROM chat_sessions s WHERE s.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? MapSession(rd) : null;
    }

    /// <summary>按分类分组列出会话（更新的在前）。</summary>
    public List<ChatSession> ListSessions()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.library_id, s.library_name, s.category, s.title, s.created_at, s.updated_at, s.active_branch_id,
                   (SELECT COUNT(*) FROM chat_branches b WHERE b.session_id = s.id),
                   (SELECT COUNT(*) FROM chat_messages m
                      JOIN chat_branches b2 ON b2.id = m.branch_id WHERE b2.session_id = s.id)
            FROM chat_sessions s ORDER BY s.updated_at DESC;
            """;
        var list = new List<ChatSession>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(MapSession(rd));
        return list;
    }

    /// <summary>查找某库已有的会话（用于「是否使用旧会话」询问）。</summary>
    public List<ChatSession> SessionsForLibrary(long libraryId) =>
        ListSessions().Where(s => s.LibraryId == libraryId).ToList();

    public void RenameSession(long id, string title)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET title = $t, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", title ?? "");
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetActiveBranch(long sessionId, long branchId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET active_branch_id = $b, updated_at = $now WHERE id = $s;";
        cmd.Parameters.AddWithValue("$b", branchId);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteSession(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chat_sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ────────────────────────── 分支 ──────────────────────────

    public ChatBranch CreateBranch(long sessionId, string mode, long? parentId, string title)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_branches (session_id, mode, parent_id, title, created_at)
            VALUES ($sid, $mode, $pid, $title, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$mode", mode == "cross" ? "cross" : "single");
        cmd.Parameters.AddWithValue("$pid", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", title ?? "");
        cmd.Parameters.AddWithValue("$now", Now());
        long bid = (long)cmd.ExecuteScalar()!;
        SetActiveBranch(sessionId, bid);
        return ListBranches(sessionId).First(b => b.Id == bid);
    }

    public List<ChatBranch> ListBranches(long sessionId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.id, b.session_id, b.mode, b.parent_id, b.title, b.created_at,
                   (SELECT COUNT(*) FROM chat_messages m WHERE m.branch_id = b.id)
            FROM chat_branches b WHERE b.session_id = $sid ORDER BY b.id ASC;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var list = new List<ChatBranch>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new ChatBranch
            {
                Id = rd.GetInt64(0), SessionId = rd.GetInt64(1), Mode = rd.GetString(2),
                ParentId = rd.IsDBNull(3) ? null : rd.GetInt64(3),
                Title = rd.GetString(4), CreatedAt = rd.GetInt64(5),
                MessageCount = rd.IsDBNull(6) ? 0 : rd.GetInt32(6),
            });
        }
        return list;
    }

    /// <summary>取主分支（单库模式的根分支）。</summary>
    public ChatBranch? MainBranch(long sessionId) =>
        ListBranches(sessionId).FirstOrDefault(b => b.ParentId == null);

    public void DeleteBranch(long branchId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chat_branches WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", branchId);
        cmd.ExecuteNonQuery();
    }

    // ────────────────────────── 消息 ──────────────────────────

    public ChatEntry AppendMessage(long branchId, string role, string content, string reasoning = "")
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        long now = Now();
        long mid;
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO chat_messages (branch_id, role, content, reasoning, created_at)
                VALUES ($b, $r, $c, $g, $now);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$b", branchId);
            ins.Parameters.AddWithValue("$r", role ?? "");
            ins.Parameters.AddWithValue("$c", content ?? "");
            ins.Parameters.AddWithValue("$g", reasoning ?? "");
            ins.Parameters.AddWithValue("$now", now);
            mid = (long)ins.ExecuteScalar()!;
        }
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE chat_sessions SET updated_at = $now
                WHERE id = (SELECT session_id FROM chat_branches WHERE id = $b);
                """;
            upd.Parameters.AddWithValue("$now", now);
            upd.Parameters.AddWithValue("$b", branchId);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        return new ChatEntry { Id = mid, BranchId = branchId, Role = role, Content = content, Reasoning = reasoning, CreatedAt = now };
    }

    /// <summary>
    /// **会话级统计**：整个会话（含其全部分支）的「用户提问条数」与「Agent 工具调用步数」。
    ///
    /// 为什么放在服务端：前端的 `aiStats` 是内存对象，**一重启就归零**（用户反馈
    /// 「轮次和步数每次重启都会归零，这不太对吧」）。这两个数在库里本来就有，直接数即可：
    ///   · 轮次 = `chat_messages` 里该会话所有分支的 `role='user'` 条数；
    ///   · 步数 = `agent_trace` 里这些分支的工具调用条数。
    /// </summary>
    public (int turns, int steps) GetSessionStats(long sessionId)
    {
        int turns = 0, steps = 0;
        try
        {
            using var conn = Open();
            using (var c1 = conn.CreateCommand())
            {
                c1.CommandText = """
                    SELECT COUNT(*) FROM chat_messages
                     WHERE role = 'user'
                       AND branch_id IN (SELECT id FROM chat_branches WHERE session_id = $s);
                    """;
                c1.Parameters.AddWithValue("$s", sessionId);
                turns = Convert.ToInt32(c1.ExecuteScalar());
            }
            using (var c2 = conn.CreateCommand())
            {
                c2.CommandText = """
                    SELECT COUNT(*) FROM agent_trace
                     WHERE session_id IN (SELECT id FROM chat_branches WHERE session_id = $s);
                    """;
                c2.Parameters.AddWithValue("$s", sessionId);
                steps = Convert.ToInt32(c2.ExecuteScalar());
            }
        }
        catch { }
        return (turns, steps);
    }
    public List<ChatEntry> GetMessages(long branchId, int limit = 0)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = limit > 0
            ? """
              SELECT id, branch_id, role, content, reasoning, created_at FROM (
                  SELECT * FROM chat_messages WHERE branch_id = $b ORDER BY id DESC LIMIT $n
              ) ORDER BY id ASC;
              """
            : "SELECT id, branch_id, role, content, reasoning, created_at FROM chat_messages WHERE branch_id = $b ORDER BY id ASC;";
        cmd.Parameters.AddWithValue("$b", branchId);
        if (limit > 0) cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<ChatEntry>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new ChatEntry
            {
                Id = rd.GetInt64(0), BranchId = rd.GetInt64(1), Role = rd.GetString(2),
                Content = rd.GetString(3), Reasoning = rd.IsDBNull(4) ? "" : rd.GetString(4),
                CreatedAt = rd.GetInt64(5),
            });
        }
        return list;
    }

    public void ClearMessages(long branchId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chat_messages WHERE branch_id = $b;";
        cmd.Parameters.AddWithValue("$b", branchId);
        cmd.ExecuteNonQuery();
    }

    private static ChatSession MapSession(SqliteDataReader rd) => new()
    {
        Id = rd.GetInt64(0),
        LibraryId = rd.IsDBNull(1) ? null : rd.GetInt64(1),
        LibraryName = rd.GetString(2),
        Category = rd.GetString(3),
        Title = rd.GetString(4),
        CreatedAt = rd.GetInt64(5),
        UpdatedAt = rd.GetInt64(6),
        ActiveBranchId = rd.IsDBNull(7) ? null : rd.GetInt64(7),
        BranchCount = rd.IsDBNull(8) ? 0 : rd.GetInt32(8),
        MessageCount = rd.IsDBNull(9) ? 0 : rd.GetInt32(9),
    };
}
