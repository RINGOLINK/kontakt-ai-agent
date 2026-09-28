using Microsoft.Data.Sqlite;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// SQLite 索引层 —— 「可移植契约」schema v3。
/// v2 → v3：libraries 增加 cover_file（.nicnt 提取的封面横幅）；新增 manuals 表（库自带说明书清单）。
/// 索引为可重建的派生数据，版本不符时直接重建。
/// </summary>
public sealed partial class Database
{
    public const int SchemaVersion = 5;

    private readonly string _connectionString;

    public string DbPath { get; }

    public Database(string dbPath)
    {
        DbPath = dbPath;
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void EnsureCreated()
    {
        using var conn = Open();

        int existing = 0;
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "SELECT value FROM app_meta WHERE key='schema_version';";
            existing = int.TryParse(check.ExecuteScalar() as string, out int v) ? v : 0;
        }
        catch { existing = 0; }


        if (existing != 0 && existing != SchemaVersion)
        {
            // 索引是派生数据，直接重建
            using var drop = conn.CreateCommand();
            drop.CommandText = """
                DROP TABLE IF EXISTS instruments;
                DROP TABLE IF EXISTS manuals;
                DROP TABLE IF EXISTS junk_files;
                DROP TABLE IF EXISTS health_issues;
                DROP TABLE IF EXISTS favorites;
                DROP TABLE IF EXISTS tag_map;
                DROP TABLE IF EXISTS tags;
                DROP TABLE IF EXISTS libraries;
                DROP TABLE IF EXISTS roots;
                DROP TABLE IF EXISTS scan_runs;
                DROP TABLE IF EXISTS app_meta;
                """;

            drop.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS roots (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                path            TEXT    NOT NULL,
                label           TEXT    NOT NULL DEFAULT '',
                enabled         INTEGER NOT NULL DEFAULT 1,
                added_at        TEXT    NOT NULL DEFAULT '',
                last_scanned_at TEXT    NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_roots_path ON roots(path);

            CREATE TABLE IF NOT EXISTS libraries (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                root_id           INTEGER NOT NULL DEFAULT 0,
                name              TEXT    NOT NULL,
                path              TEXT    NOT NULL,
                category          TEXT    NOT NULL DEFAULT '',
                size_bytes        INTEGER NOT NULL DEFAULT 0,
                file_count        INTEGER NOT NULL DEFAULT 0,
                nki_count         INTEGER NOT NULL DEFAULT 0,
                nkm_count         INTEGER NOT NULL DEFAULT 0,
                nkc_count         INTEGER NOT NULL DEFAULT 0,
                has_nicnt         INTEGER NOT NULL DEFAULT 0,
                junk_count        INTEGER NOT NULL DEFAULT 0,
                junk_bytes        INTEGER NOT NULL DEFAULT 0,
                product_key       TEXT    NOT NULL DEFAULT '',
                required_kontakt  TEXT    NOT NULL DEFAULT '',
                cover_file        TEXT    NOT NULL DEFAULT '',
                notes             TEXT    NOT NULL DEFAULT '',
                last_scanned_at   TEXT    NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_libraries_path ON libraries(path);
            CREATE INDEX IF NOT EXISTS idx_libraries_root ON libraries(root_id);

            CREATE TABLE IF NOT EXISTS manuals (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id   INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                rel_path     TEXT    NOT NULL,
                name         TEXT    NOT NULL,
                ext          TEXT    NOT NULL DEFAULT '',
                size_bytes   INTEGER NOT NULL DEFAULT 0,
                is_primary   INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_manuals_library ON manuals(library_id);

            CREATE TABLE IF NOT EXISTS instruments (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id     INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                rel_path       TEXT    NOT NULL,
                name           TEXT    NOT NULL,
                kind           TEXT    NOT NULL,
                size_bytes     INTEGER NOT NULL DEFAULT 0,
                mtime          INTEGER NOT NULL DEFAULT 0,
                name_source    TEXT    NOT NULL DEFAULT '',
                file_format    TEXT    NOT NULL DEFAULT '',
                engine_version TEXT    NOT NULL DEFAULT '',
                articulation   TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_instruments_library ON instruments(library_id);
            CREATE INDEX IF NOT EXISTS idx_instruments_name ON instruments(name);

            CREATE TABLE IF NOT EXISTS favorites (
                instrument_id INTEGER PRIMARY KEY REFERENCES instruments(id) ON DELETE CASCADE,
                created_at    TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS tags (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS tag_map (
                entity_type TEXT NOT NULL,
                entity_id   INTEGER NOT NULL,
                tag_id      INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (entity_type, entity_id, tag_id)
            );

            CREATE TABLE IF NOT EXISTS agent_trace (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id   INTEGER NOT NULL DEFAULT 0,
                step         INTEGER NOT NULL DEFAULT 0,
                tool_name    TEXT    NOT NULL DEFAULT '',
                args_hash    TEXT    NOT NULL DEFAULT '',
                args_preview TEXT    NOT NULL DEFAULT '',
                ok           INTEGER NOT NULL DEFAULT 1,
                elapsed_ms   INTEGER NOT NULL DEFAULT 0,
                result_chars INTEGER NOT NULL DEFAULT 0,
                error        TEXT    NOT NULL DEFAULT '',
                created_at   TEXT    NOT NULL DEFAULT '',
                prompt_tokens     INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens  INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_trace_session ON agent_trace(session_id, id);

            CREATE TABLE IF NOT EXISTS audio_clips (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id INTEGER NOT NULL,
                rel_path TEXT NOT NULL,
                name TEXT NOT NULL,
                ext TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                kind TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS audio_features (
                clip_id INTEGER PRIMARY KEY,
                library_id INTEGER NOT NULL,
                dim INTEGER NOT NULL,
                vec BLOB NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_af_library ON audio_features(library_id);
            CREATE INDEX IF NOT EXISTS idx_audio_lib ON audio_clips(library_id);
            CREATE TABLE IF NOT EXISTS junk_files (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id     INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                rel_path       TEXT    NOT NULL,
                kind           TEXT    NOT NULL,
                size_bytes     INTEGER NOT NULL DEFAULT 0,
                suggested_keep INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS health_issues (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                rule       TEXT NOT NULL,
                rel_path   TEXT NOT NULL DEFAULT '',
                detail     TEXT NOT NULL DEFAULT '',
                status     TEXT NOT NULL DEFAULT 'open'
            );

            CREATE TABLE IF NOT EXISTS scan_runs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at      TEXT NOT NULL DEFAULT '',
                finished_at     TEXT NOT NULL DEFAULT '',
                mode            TEXT NOT NULL DEFAULT 'full',
                roots_count     INTEGER NOT NULL DEFAULT 0,
                libraries_found INTEGER NOT NULL DEFAULT 0,
                files_indexed   INTEGER NOT NULL DEFAULT 0,
                errors          INTEGER NOT NULL DEFAULT 0,
                elapsed_seconds REAL NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS app_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();

            // **老库迁移**：给 agent_trace 补 token 三列（必须放在 C# 里，不能写进 SQL 字符串）
            MigrateTraceTokens(conn);
        // 迁移：旧库补齐新列（建表之后再执行；列已存在会抛异常，忽略即可）
        try
        {
            using var mig = conn.CreateCommand();
            mig.CommandText = "ALTER TABLE instruments ADD COLUMN articulation TEXT NOT NULL DEFAULT '';";
            mig.ExecuteNonQuery();
        }

        catch { }

        SetMeta(conn, "schema_version", SchemaVersion.ToString());
    }

    // ── 根目录管理 ─────────────────────────────────────
    public List<LibraryRoot> GetRoots()
    {
        var list = new List<LibraryRoot>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.path, r.label, r.enabled, r.added_at, r.last_scanned_at,
                   (SELECT COUNT(*) FROM libraries l WHERE l.root_id = r.id),
                   (SELECT COALESCE(SUM(size_bytes),0) FROM libraries l WHERE l.root_id = r.id)
            FROM roots r ORDER BY r.id;
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string path = rd.GetString(1);
            list.Add(new LibraryRoot
            {
                Id = rd.GetInt64(0),
                Path = path,
                Label = rd.GetString(2),
                Enabled = rd.GetInt32(3) != 0,
                AddedAt = rd.GetString(4),
                LastScannedAt = rd.GetString(5),
                LibraryCount = rd.GetInt32(6),
                SizeBytes = rd.GetInt64(7),
                Exists = Directory.Exists(path),
            });
        }
        return list;
    }

    public (bool ok, string message, long id) AddRoot(string path, string label = "")
    {
        path = LibraryReghPath(path);
        if (string.IsNullOrWhiteSpace(path)) return (false, "路径为空", 0);
        if (!Directory.Exists(path)) return (false, $"目录不存在：{path}", 0);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO roots (path, label, enabled, added_at) VALUES ($p, $l, 1, $t)
            ON CONFLICT(path) DO NOTHING;
            SELECT id FROM roots WHERE path = $p;
            """;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$l", label ?? "");
        cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        object? id = cmd.ExecuteScalar();
        if (id == null) return (false, "添加失败", 0);
        return (true, $"已添加：{path}", Convert.ToInt64(id));
    }

    private static string LibraryReghPath(string p) => (p ?? "").Trim().Trim('"').TrimEnd('\\', '/');

    /// <summary>
    /// 从索引里移除一个音色库及其关联记录（乐器 / 说明书）。
    /// **只删索引、不动磁盘文件** —— 磁盘删除由调用方负责，
    /// 这样「删文件」与「删记录」两步可以分开确认与回滚。
    /// </summary>
    public void DeleteLibrary(long id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var sql in new[]
        {
            "DELETE FROM instruments WHERE library_id = $id;",
            "DELETE FROM manuals     WHERE library_id = $id;",
            "DELETE FROM libraries   WHERE id         = $id;",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            try { cmd.ExecuteNonQuery(); } catch { }
        }
        tx.Commit();
    }
    public (bool ok, string message) RemoveRoot(long id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = """
                DELETE FROM junk_files WHERE library_id IN (SELECT id FROM libraries WHERE root_id = $id);
                DELETE FROM audio_clips WHERE library_id IN (SELECT id FROM libraries WHERE root_id = $id);
                DELETE FROM instruments WHERE library_id IN (SELECT id FROM libraries WHERE root_id = $id);
                DELETE FROM libraries WHERE root_id = $id;
                DELETE FROM roots WHERE id = $id;
                """;
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return (true, "已移除该路径及其索引数据");
    }

    public (bool ok, string message) SetRootEnabled(long id, bool enabled)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE roots SET enabled = $e WHERE id = $id;";
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        return (cmd.ExecuteNonQuery() > 0, enabled ? "已启用" : "已停用");
    }

    // ── 写入扫描结果 ───────────────────────────────────
    public void SaveScanResult(ScanResult result)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // 根目录：保留用户配置，仅刷新扫描时间；新根则插入
        using (var rootCmd = conn.CreateCommand())
        {
            rootCmd.Transaction = tx;
            rootCmd.CommandText = """
                INSERT INTO roots (path, label, enabled, added_at, last_scanned_at)
                VALUES ($p, '', 1, $t, $t)
                ON CONFLICT(path) DO UPDATE SET last_scanned_at = excluded.last_scanned_at;
                """;
            var pP = rootCmd.CreateParameter(); pP.ParameterName = "$p"; rootCmd.Parameters.Add(pP);
            var pT = rootCmd.CreateParameter(); pT.ParameterName = "$t"; rootCmd.Parameters.Add(pT);
            foreach (var r in result.Roots)
            {
                pP.Value = r.Path;
                pT.Value = r.LastScannedAt;
                rootCmd.ExecuteNonQuery();
            }
        }

        var rootIdByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT id, path FROM roots;";
            using var rd = q.ExecuteReader();
            while (rd.Read()) rootIdByPath[rd.GetString(1)] = rd.GetInt64(0);
        }

        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM instruments; DELETE FROM manuals; DELETE FROM junk_files; DELETE FROM audio_clips; DELETE FROM libraries;";
            clear.ExecuteNonQuery();
        }

        var libIdByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var libCmd = conn.CreateCommand())
        {
            libCmd.Transaction = tx;
            libCmd.CommandText = """
                INSERT INTO libraries (root_id, name, path, category, size_bytes, file_count, nki_count, nkm_count,
                                       nkc_count, has_nicnt, junk_count, junk_bytes, product_key, required_kontakt,
                                       cover_file, notes, last_scanned_at)
                VALUES ($root, $name, $path, $cat, $size, $files, $nki, $nkm, $nkc, $nicnt, $junkc, $junkb,
                        $pkey, $req, $cover, $notes, $scanned);
                SELECT last_insert_rowid();
                """;
            var pRoot = libCmd.CreateParameter(); pRoot.ParameterName = "$root"; libCmd.Parameters.Add(pRoot);
            var pName = libCmd.CreateParameter(); pName.ParameterName = "$name"; libCmd.Parameters.Add(pName);
            var pPath = libCmd.CreateParameter(); pPath.ParameterName = "$path"; libCmd.Parameters.Add(pPath);
            var pCat = libCmd.CreateParameter(); pCat.ParameterName = "$cat"; libCmd.Parameters.Add(pCat);
            var pSize = libCmd.CreateParameter(); pSize.ParameterName = "$size"; libCmd.Parameters.Add(pSize);
            var pFiles = libCmd.CreateParameter(); pFiles.ParameterName = "$files"; libCmd.Parameters.Add(pFiles);
            var pNki = libCmd.CreateParameter(); pNki.ParameterName = "$nki"; libCmd.Parameters.Add(pNki);
            var pNkm = libCmd.CreateParameter(); pNkm.ParameterName = "$nkm"; libCmd.Parameters.Add(pNkm);
            var pNkc = libCmd.CreateParameter(); pNkc.ParameterName = "$nkc"; libCmd.Parameters.Add(pNkc);
            var pNicnt = libCmd.CreateParameter(); pNicnt.ParameterName = "$nicnt"; libCmd.Parameters.Add(pNicnt);
            var pJunkC = libCmd.CreateParameter(); pJunkC.ParameterName = "$junkc"; libCmd.Parameters.Add(pJunkC);
            var pJunkB = libCmd.CreateParameter(); pJunkB.ParameterName = "$junkb"; libCmd.Parameters.Add(pJunkB);
            var pPkey = libCmd.CreateParameter(); pPkey.ParameterName = "$pkey"; libCmd.Parameters.Add(pPkey);
            var pReq = libCmd.CreateParameter(); pReq.ParameterName = "$req"; libCmd.Parameters.Add(pReq);
            var pCover = libCmd.CreateParameter(); pCover.ParameterName = "$cover"; libCmd.Parameters.Add(pCover);
            var pNotes = libCmd.CreateParameter(); pNotes.ParameterName = "$notes"; libCmd.Parameters.Add(pNotes);
            var pScan = libCmd.CreateParameter(); pScan.ParameterName = "$scanned"; libCmd.Parameters.Add(pScan);

            foreach (var lib in result.Libraries)
            {
                pRoot.Value = rootIdByPath.TryGetValue(lib.RootPath, out long rid) ? rid : 0;
                pName.Value = lib.Name;
                pPath.Value = lib.Path;
                pCat.Value = lib.Category;
                pSize.Value = lib.SizeBytes;
                pFiles.Value = lib.FileCount;
                pNki.Value = lib.NkiCount;
                pNkm.Value = lib.NkmCount;
                pNkc.Value = lib.NkcCount;
                pNicnt.Value = lib.HasNicnt ? 1 : 0;
                pJunkC.Value = lib.JunkCount;
                pJunkB.Value = lib.JunkBytes;
                pPkey.Value = lib.ProductKey;
                pReq.Value = lib.RequiredKontakt;
                pCover.Value = lib.CoverFile;
                pNotes.Value = lib.Notes;
                pScan.Value = lib.LastScannedAt;
                libIdByPath[lib.Path] = (long)libCmd.ExecuteScalar()!;
            }
        }

        // ── 说明书清单 ──
        using (var manCmd = conn.CreateCommand())
        {
            manCmd.Transaction = tx;
            manCmd.CommandText = """
                INSERT INTO manuals (library_id, rel_path, name, ext, size_bytes, is_primary)
                VALUES ($lib, $rel, $name, $ext, $size, $primary);
                """;
            var mLib = manCmd.CreateParameter(); mLib.ParameterName = "$lib"; manCmd.Parameters.Add(mLib);
            var mRel = manCmd.CreateParameter(); mRel.ParameterName = "$rel"; manCmd.Parameters.Add(mRel);
            var mName = manCmd.CreateParameter(); mName.ParameterName = "$name"; manCmd.Parameters.Add(mName);
            var mExt = manCmd.CreateParameter(); mExt.ParameterName = "$ext"; manCmd.Parameters.Add(mExt);
            var mSize = manCmd.CreateParameter(); mSize.ParameterName = "$size"; manCmd.Parameters.Add(mSize);
            var mPrim = manCmd.CreateParameter(); mPrim.ParameterName = "$primary"; manCmd.Parameters.Add(mPrim);

            foreach (var man in result.Manuals)
            {
                string? libPath = result.Libraries
                    .FirstOrDefault(l => string.Equals(l.Name, man.LibraryName, StringComparison.OrdinalIgnoreCase))?.Path;
                if (libPath == null || !libIdByPath.TryGetValue(libPath, out long libId)) continue;
                mLib.Value = libId;
                mRel.Value = man.RelPath;
                mName.Value = man.Name;
                mExt.Value = man.Ext;
                mSize.Value = man.SizeBytes;
                mPrim.Value = man.IsPrimary ? 1 : 0;
                manCmd.ExecuteNonQuery();
            }
        }

        // ── 可试听音频片段 ──
        using (var audCmd = conn.CreateCommand())
        {
            audCmd.Transaction = tx;
            audCmd.CommandText = """
                INSERT INTO audio_clips (library_id, rel_path, name, ext, size_bytes, kind)
                VALUES ($lib, $rel, $name, $ext, $size, $kind);
                """;
            var aLib = audCmd.CreateParameter(); aLib.ParameterName = "$lib"; audCmd.Parameters.Add(aLib);
            var aRel = audCmd.CreateParameter(); aRel.ParameterName = "$rel"; audCmd.Parameters.Add(aRel);
            var aName = audCmd.CreateParameter(); aName.ParameterName = "$name"; audCmd.Parameters.Add(aName);
            var aExt = audCmd.CreateParameter(); aExt.ParameterName = "$ext"; audCmd.Parameters.Add(aExt);
            var aSize = audCmd.CreateParameter(); aSize.ParameterName = "$size"; audCmd.Parameters.Add(aSize);
            var aKind = audCmd.CreateParameter(); aKind.ParameterName = "$kind"; audCmd.Parameters.Add(aKind);

            foreach (var clip in result.AudioClips)
            {
                // 优先按路径匹配（同名库也能区分）；旧数据没有路径时退回按名称匹配
                string? libPath = clip.LibraryPath;
                if (string.IsNullOrEmpty(libPath) || !libIdByPath.ContainsKey(libPath))
                    libPath = result.Libraries
                        .FirstOrDefault(l => string.Equals(l.Name, clip.LibraryName, StringComparison.OrdinalIgnoreCase))?.Path;
                if (libPath == null || !libIdByPath.TryGetValue(libPath, out long libId)) continue;
                aLib.Value = libId;
                aRel.Value = clip.RelPath;
                aName.Value = clip.Name;
                aExt.Value = clip.Ext;
                aSize.Value = clip.SizeBytes;
                aKind.Value = clip.Kind;
                audCmd.ExecuteNonQuery();
            }
        }

        using (var insCmd = conn.CreateCommand())
        {
            insCmd.Transaction = tx;
            insCmd.CommandText = """
                INSERT INTO instruments (library_id, rel_path, name, kind, size_bytes, mtime, name_source, file_format, engine_version, articulation)
                VALUES ($lib, $rel, $name, $kind, $size, $mtime, $src, $fmt, $eng, $art);
                """;
            var pLib = insCmd.CreateParameter(); pLib.ParameterName = "$lib"; insCmd.Parameters.Add(pLib);
            var pRel = insCmd.CreateParameter(); pRel.ParameterName = "$rel"; insCmd.Parameters.Add(pRel);
            var pName = insCmd.CreateParameter(); pName.ParameterName = "$name"; insCmd.Parameters.Add(pName);
            var pKind = insCmd.CreateParameter(); pKind.ParameterName = "$kind"; insCmd.Parameters.Add(pKind);
            var pSize = insCmd.CreateParameter(); pSize.ParameterName = "$size"; insCmd.Parameters.Add(pSize);
            var pMtime = insCmd.CreateParameter(); pMtime.ParameterName = "$mtime"; insCmd.Parameters.Add(pMtime);
            var pSrc = insCmd.CreateParameter(); pSrc.ParameterName = "$src"; insCmd.Parameters.Add(pSrc);
            var pFmt = insCmd.CreateParameter(); pFmt.ParameterName = "$fmt"; insCmd.Parameters.Add(pFmt);
            var pEng = insCmd.CreateParameter(); pEng.ParameterName = "$eng"; insCmd.Parameters.Add(pEng);
            var pArt = insCmd.CreateParameter(); pArt.ParameterName = "$art"; insCmd.Parameters.Add(pArt);

            foreach (var inst in result.Instruments)
            {
                // 按库路径定位（同名库可存在于不同根目录）
                string? libPath = result.Libraries
                    .FirstOrDefault(l => string.Equals(l.Name, inst.LibraryName, StringComparison.OrdinalIgnoreCase)
                                      && l.RootPath == inst.RootPathHint)?.Path;
                if (libPath == null)
                {
                    libPath = result.Libraries
                        .FirstOrDefault(l => string.Equals(l.Name, inst.LibraryName, StringComparison.OrdinalIgnoreCase))?.Path;
                }
                if (libPath == null || !libIdByPath.TryGetValue(libPath, out long libId)) continue;

                pLib.Value = libId;
                pRel.Value = inst.RelPath;
                pName.Value = inst.Name;
                pKind.Value = inst.Kind;
                pSize.Value = inst.SizeBytes;
                pMtime.Value = inst.Mtime;
                pSrc.Value = inst.Source;
                pFmt.Value = inst.Format;
                pEng.Value = inst.EngineVersion;
                pArt.Value = inst.Articulation;
                insCmd.ExecuteNonQuery();
            }
        }

        using (var junkCmd = conn.CreateCommand())
        {
            junkCmd.Transaction = tx;
            junkCmd.CommandText = """
                INSERT INTO junk_files (library_id, rel_path, kind, size_bytes, suggested_keep)
                VALUES ($lib, $rel, $kind, $size, $keep);
                """;
            var pLib = junkCmd.CreateParameter(); pLib.ParameterName = "$lib"; junkCmd.Parameters.Add(pLib);
            var pRel = junkCmd.CreateParameter(); pRel.ParameterName = "$rel"; junkCmd.Parameters.Add(pRel);
            var pKind = junkCmd.CreateParameter(); pKind.ParameterName = "$kind"; junkCmd.Parameters.Add(pKind);
            var pSize = junkCmd.CreateParameter(); pSize.ParameterName = "$size"; junkCmd.Parameters.Add(pSize);
            var pKeep = junkCmd.CreateParameter(); pKeep.ParameterName = "$keep"; junkCmd.Parameters.Add(pKeep);

            foreach (var j in result.JunkFiles)
            {
                string? libPath = result.Libraries
                    .FirstOrDefault(l => string.Equals(l.Name, j.LibraryName, StringComparison.OrdinalIgnoreCase))?.Path;
                if (libPath == null || !libIdByPath.TryGetValue(libPath, out long libId)) continue;
                pLib.Value = libId;
                pRel.Value = j.RelPath;
                pKind.Value = j.Kind;
                pSize.Value = j.SizeBytes;
                pKeep.Value = j.SuggestedKeep ? 1 : 0;
                junkCmd.ExecuteNonQuery();
            }
        }

        using (var runCmd = conn.CreateCommand())
        {
            runCmd.Transaction = tx;
            runCmd.CommandText = """
                INSERT INTO scan_runs (started_at, finished_at, mode, roots_count, libraries_found, files_indexed, errors, elapsed_seconds)
                VALUES ($start, $end, 'full', $roots, $libs, $files, $errors, $elapsed);
                """;
            runCmd.Parameters.AddWithValue("$start", result.StartedAt);
            runCmd.Parameters.AddWithValue("$end", result.FinishedAt);
            runCmd.Parameters.AddWithValue("$roots", result.Roots.Count);
            runCmd.Parameters.AddWithValue("$libs", result.Libraries.Count);
            runCmd.Parameters.AddWithValue("$files", result.TotalFiles);
            runCmd.Parameters.AddWithValue("$errors", result.Errors);
            runCmd.Parameters.AddWithValue("$elapsed", result.ElapsedSeconds);
            runCmd.ExecuteNonQuery();
        }

        SetMeta(conn, "root", result.Root, tx);
        SetMeta(conn, "last_scanned_at", result.FinishedAt, tx);
        SetMeta(conn, "last_scan_seconds", result.ElapsedSeconds.ToString("F1"), tx);
        tx.Commit();
    }

    // ── 设置 ───────────────────────────────────────────
    public string GetMeta(string key, string fallback = "")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string ?? fallback;
    }

    public void SetMeta(string key, string value) { using var conn = Open(); SetMeta(conn, key, value); }

    private static void SetMeta(SqliteConnection conn, string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO app_meta (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ── 查询 ───────────────────────────────────────────
    public DashboardData GetDashboard()
    {
        var data = new DashboardData();
        using var conn = Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(size_bytes),0), COALESCE(SUM(file_count),0),
                       COALESCE(SUM(junk_count),0), COALESCE(SUM(junk_bytes),0),
                       COALESCE(SUM(CASE WHEN has_nicnt = 0 THEN 1 ELSE 0 END),0)
                FROM libraries;
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                data.LibraryCount = r.GetInt32(0);
                data.TotalBytes = r.GetInt64(1);
                data.TotalFiles = r.GetInt64(2);
                data.JunkCount = r.GetInt32(3);
                data.JunkBytes = r.GetInt64(4);
                data.LibrariesWithoutNicnt = r.GetInt32(5);
            }
        }

        data.HasData = data.LibraryCount > 0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN name_source = 'header' THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN file_format = 'legacy' THEN 1 ELSE 0 END),0)
                FROM instruments;
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                data.InstrumentCount = r.GetInt32(0);
                data.InstrumentNamedFromHeader = r.GetInt32(1);
                data.LegacyInstrumentCount = r.GetInt32(2);
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT category, COUNT(*), COALESCE(SUM(size_bytes),0) FROM libraries GROUP BY category;";
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, CategoryStat>(StringComparer.Ordinal);
            while (r.Read())
                map[r.GetString(0)] = new CategoryStat { Category = r.GetString(0), LibraryCount = r.GetInt32(1), SizeBytes = r.GetInt64(2) };

            // 按体积降序（图表更易读）；补齐 0 值类别，保证「合成器」这类当前为空的类别也始终可见
            foreach (var cat in LibraryClassifier.DisplayOrder)
            {
                if (map.TryGetValue(cat, out var stat)) data.Categories.Add(stat);
                else data.Categories.Add(new CategoryStat { Category = cat, LibraryCount = 0, SizeBytes = 0 });
            }
            foreach (var kv in map)
                if (!LibraryClassifier.DisplayOrder.Contains(kv.Key)) data.Categories.Add(kv.Value);

            data.Categories = data.Categories.OrderByDescending(c => c.SizeBytes).ToList();
        }

        data.TopLibraries = GetLibraries("size_bytes DESC", 16);
        data.RootCount = GetRoots().Count;
        data.Root = GetMeta("root");
        data.LastScannedAt = GetMeta("last_scanned_at");
        double.TryParse(GetMeta("last_scan_seconds", "0"), out double secs);
        data.LastScanSeconds = secs;
        return data;
    }

    /// <summary>
    /// 取某库的可试听音频片段。优先返回产品演示音频（demo），
    /// 其次是真实采样（sample）；同类型内随机抽样，便于「随便听听这个库」。
    /// </summary>
    /// <summary>
    /// **取一个库里【全部】可分析的音频片段 —— 稳定、有序、无随机、无上限。**
    ///
    /// **为什么不能用 GetAudioClips**：那个方法是给「试听换一批」设计的 ——
    /// 它每库只取 limit 个，**且排序里带随机**（注释原话：「同优先级内随机，保证『换一批』每次不同」）。
    /// 用它做批量提取会出大问题（已实测踩到）：
    ///   · **每次调用返回的清单都不一样** ⇒ 进度起点忽高忽低（实测从 4455 掉到 1400）
    ///   · **「续跑」永远无法真正完成** —— 每次都换一批新的，永远有没提过的
    ///   · **每库只覆盖前 limit 个** ⇒ 片段多的库有大半永远分析不到
    /// 所以批量任务**必须**走这个专用查询：按 id 排序（稳定）、不限条数（完整）。
    /// </summary>
    public List<AudioClip> GetAnalyzableClips(long libraryId)
    {
        var list = new List<AudioClip>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.library_id, l.name, a.rel_path, a.name, a.ext, a.size_bytes, a.kind
            FROM audio_clips a JOIN libraries l ON l.id = a.library_id
            WHERE a.library_id = $lib
            ORDER BY a.id
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AudioClip
            {
                Id = r.GetInt64(0), LibraryId = r.GetInt64(1), LibraryName = r.GetString(2),
                RelPath = r.GetString(3), Name = r.GetString(4), Ext = r.GetString(5),
                SizeBytes = r.GetInt64(6), Kind = r.IsDBNull(7) ? "" : r.GetString(7),
            });
        }
        return list;
    }

    /// <summary>
    /// **按名称【强过滤】取可试听片段**（2026-09-26 新增）。
    ///
    /// 与 <see cref="GetAudioClips"/> 的区别：
    ///   · `GetAudioClips` 的 `preferName` **只影响排序、不过滤** —— 它服务于界面的
    ///     「换一批」按钮（每次随机给几条），语义是「优先给相关的」。
    ///   · 本方法**要求名称/路径真的命中关键词**，不命中就不返回。
    ///
    /// **为什么必须新增**：Agent 用 `audition` 的 `match=cymbal` 想找镲片采样，
    /// 旧实现只把命中的排前面、**仍然返回大量不命中的** ⇒ Agent 拿到一堆非镲片、
    /// 于是误判「audition 不灵」（用户实测反馈：「筛选精度还不是很高」）。
    /// </summary>
    /// <summary>
    /// **按名称强过滤 —— 原始版，不套质量策略**（2026-09-27 新增）。
    ///
    /// 为什么拆出来：audition 需要**先看候选总数、再筛选**，这样才能在返回 0 条时
    /// 如实说明原因（是「只有 .nksn 预览」还是「全是远麦位」），而不是笼统说
    /// 「.ncw 无法解码」—— 实测 Agent 就这么误报过，用户被误导。
    /// </summary>
    public List<AudioClip> GetAudioClipsMatchingRaw(long libraryId, int limit, string filterName)
    {
        var list = new List<AudioClip>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var terms = (filterName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return list;

        var sb = new System.Text.StringBuilder();
        sb.Append("""
            SELECT a.id, a.library_id, l.name, a.rel_path, a.name, a.ext, a.size_bytes, a.kind
            FROM audio_clips a JOIN libraries l ON l.id = a.library_id
            WHERE a.library_id = $lib
            """);
        for (int i = 0; i < terms.Length; i++)
            sb.Append($" AND (a.name LIKE '%' || $t{i} || '%' OR a.rel_path LIKE '%' || $t{i} || '%')");
        sb.Append("""

            ORDER BY CASE a.kind WHEN 'demo' THEN 0 ELSE 1 END, RANDOM()
            LIMIT $n;
            """);
        cmd.CommandText = sb.ToString();
        cmd.Parameters.AddWithValue("$lib", libraryId);
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 300));
        for (int i = 0; i < terms.Length; i++)
            cmd.Parameters.AddWithValue($"$t{i}", terms[i]);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new AudioClip
            {
                Id = rd.GetInt64(0),
                LibraryId = rd.GetInt64(1),
                LibraryName = rd.GetString(2),
                RelPath = rd.GetString(3),
                Name = rd.GetString(4),
                Ext = rd.GetString(5),
                SizeBytes = rd.GetInt64(6),
                Kind = rd.GetString(7),
            });
        }
        return list;
    }

    public List<AudioClip> GetAudioClipsMatching(long libraryId, int limit, string filterName)
    {
        var list = new List<AudioClip>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 支持空格分隔多个词：**必须全部命中**（AND），避免「cymbal ride」被放宽成只匹配其一
        var terms = (filterName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return list;

        var sb = new System.Text.StringBuilder();
        sb.Append("""
            SELECT a.id, a.library_id, l.name, a.rel_path, a.name, a.ext, a.size_bytes, a.kind
            FROM audio_clips a JOIN libraries l ON l.id = a.library_id
            WHERE a.library_id = $lib
            """);
        for (int i = 0; i < terms.Length; i++)
            sb.Append($" AND (a.name LIKE '%' || $t{i} || '%' OR a.rel_path LIKE '%' || $t{i} || '%')");
        // 演示音频优先，其次随机（保证同批多样），最后按 id 稳定
        sb.Append("""

            ORDER BY CASE a.kind WHEN 'demo' THEN 0 ELSE 1 END, RANDOM()
            LIMIT $n;
            """);
        cmd.CommandText = sb.ToString();
        cmd.Parameters.AddWithValue("$lib", libraryId);
        // 🔴 多查 6 倍 —— 质量筛选（去预览 / 去组合名 / 去远麦位）会淘汰一部分（2026-09-27）
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit * 6, 1, 300));
        for (int i = 0; i < terms.Length; i++)
            cmd.Parameters.AddWithValue($"$t{i}", terms[i]);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new AudioClip
            {
                Id = rd.GetInt64(0),
                LibraryId = rd.GetInt64(1),
                LibraryName = rd.GetString(2),
                RelPath = rd.GetString(3),
                Name = rd.GetString(4),
                Ext = rd.GetString(5),
                SizeBytes = rd.GetInt64(6),
                Kind = rd.GetString(7),
            });
        }
        // 🔴 **质量筛选（2026-09-27，用户要求）** ——
        //   ① 剔除 .nksn/.nki 预览；② 剔除组合乐器名；
        //   ③ 麦位策略：有主/近麦就剔掉远麦位；**只有远麦位的库整个忽略**（用户原话）。
        return AudioQuality.ApplyMicPolicy(list).Take(limit).ToList();
    }

    /// <summary>
    /// **跨库检索可试听片段**（2026-09-26 新增）。
    ///
    /// 为什么需要：`find_audition` 原本在【不传 library】时用 `library_id = 0` 查询 ⇒ **必然 0 条**
    /// （实测：Agent 想「全局找 cymbal 采样」，得到 0 结果，只好逐库试 `audition`，
    ///  又因不传 match 拿到随机采样 ⇒ 推荐出 kick / hammer / shaker 等完全无关的东西）。
    /// ⇒ 本方法支持「不指定库时【跨全部库】按名称检索」，并按库聚拢、每库限几条以保证多样性。
    /// </summary>
    /// <param name="keywords">名称关键词（空格分隔 = 必须全部命中）</param>
    /// <param name="limit">返回总条数上限</param>
    /// <param name="libraryId">> 0 时限定该库；= 0 时检索全部库</param>
    public List<AudioClip> SearchAudioClips(string keywords, int limit, long libraryId = 0)
    {
        var list = new List<AudioClip>();
        var terms = (keywords ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sb = new System.Text.StringBuilder();
        sb.Append("""
            SELECT a.id, a.library_id, l.name, a.rel_path, a.name, a.ext, a.size_bytes, a.kind
            FROM audio_clips a JOIN libraries l ON l.id = a.library_id
            WHERE 1 = 1
            """);
        if (libraryId > 0) sb.Append(" AND a.library_id = $lib");
        for (int i = 0; i < terms.Length; i++)
            sb.Append($" AND (a.name LIKE '%' || $t{i} || '%' OR a.rel_path LIKE '%' || $t{i} || '%')");
        // 演示音频优先；同库内随机 ⇒ 跨库结果自然分散
        sb.Append("""

            ORDER BY CASE a.kind WHEN 'demo' THEN 0 ELSE 1 END, RANDOM()
            LIMIT $n;
            """);
        cmd.CommandText = sb.ToString();
        if (libraryId > 0) cmd.Parameters.AddWithValue("$lib", libraryId);
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 200));
        for (int i = 0; i < terms.Length; i++)
            cmd.Parameters.AddWithValue($"$t{i}", terms[i]);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new AudioClip
            {
                Id = rd.GetInt64(0),
                LibraryId = rd.GetInt64(1),
                LibraryName = rd.GetString(2),
                RelPath = rd.GetString(3),
                Name = rd.GetString(4),
                Ext = rd.GetString(5),
                SizeBytes = rd.GetInt64(6),
                Kind = rd.GetString(7),
            });
        }
        return list;
    }

    /// <summary>
    /// **多样性候选挑选**（2026-09-26 新增，用户提出的架构）。
    ///
    /// 用户原话：「先检索文本，筛选有对应文本的库…再结合簇分布挑选对应数量的簇…
    /// 除了兜底的文本匹配，剩下的库可以按照声学特征进行创造性匹配。」
    ///
    /// 为什么必须做在工具里：这件事需要【文本检索 + 嵌入距离 + 跨库分组】三者组合，
    /// 而 Agent 手上只有单点工具（find_audition 只能文本搜、map_find_similar 只能同库 KNN）
    /// ⇒ 实测它只能「逐个库调 audition」，既跑偏又覆盖不全。
    ///
    /// 算法（**不用簇 ID —— HDBSCAN 重算后 ID 全变、且有 21% 是噪声 -1**）：
    ///   1. 文本检索出候选（带 matchedBy：name=强证据 / path=弱证据）
    ///   2. **保底**：优先取 matchedBy=name 的，且**同库尽量不重复**（跨库分散）
    ///   3. **扩展**：对剩余候选按 **MERT 嵌入做「最远点采样」**（逐个挑离已选最远的）
    ///      ⇒ 得到内在多样、且不依赖聚类稳定性的选择
    ///   4. **每批**：为每个入选项，从【它所属库】里再取 perBatch 个同类（按嵌入最近）
    /// </summary>
    public List<(AudioClip Seed, List<AudioClip> Batch, string MatchedBy, float Diversity)>
        FindDiverseCandidates(string query, int count, int guarantee, int perBatch)
    {
        var result = new List<(AudioClip, List<AudioClip>, string, float)>();
        count = Math.Clamp(count, 1, 12);
        perBatch = Math.Clamp(perBatch, 1, 6);
        guarantee = Math.Clamp(guarantee, 0, count);

        // ① 文本检索（放宽取，后面再筛）
        var raw = SearchAudioClips(query, 400, 0);
        if (raw.Count == 0) return result;
        // 🔴 **质量筛选（2026-09-27，用户要求）** ——
        //   ① 剔除 .nksn/.nki 预览（那些不是采样，用户听到的是预设片段）
        //   ② 剔除组合乐器名（Cymbals and Gongs / Cymbals and Bassdrum）
        //   ③ 麦位策略：有主/近麦就剔掉远距离麦位；**只有远麦位的库整个忽略**
        var cand = AudioQuality.ApplyMicPolicy(raw);
        if (cand.Count == 0) return result;

        // ② 读嵌入（MERT 768 维 float32）—— 只读有嵌入的
        var vecs = LoadVectors("mert_features", cand.Select(c => c.Id).ToList());

        // ③ 分强/弱证据
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Strong(AudioClip c)
        {
            string nm = (c.Name ?? "").ToLowerInvariant();
            return terms.Length > 0 && terms.All(x => nm.Contains(x.ToLowerInvariant()));
        }
        var strong = cand.Where(Strong).ToList();
        var weak = cand.Where(c => !Strong(c)).ToList();

        var picked = new List<AudioClip>();
        var pickedLibs = new HashSet<long>();
        var pickedIds = new HashSet<long>();

        // ④ 保底：强证据优先，且跨库（同库只取一个，取完再放开）
        foreach (var c in strong.OrderBy(c => pickedLibs.Contains(c.LibraryId) ? 1 : 0))
        {
            if (picked.Count >= guarantee) break;
            if (pickedIds.Contains(c.Id)) continue;
            // 只有全部库都用过一遍后，才允许同库再来一个
            if (pickedLibs.Contains(c.LibraryId) && pickedLibs.Count < guarantee) continue;
            picked.Add(c); pickedIds.Add(c.Id); pickedLibs.Add(c.LibraryId);
        }

        // ⑤ 扩展：最远点采样（在强+弱合集上做，按 MERT 嵌入）
        var pool = strong.Concat(weak).Where(c => !pickedIds.Contains(c.Id) && vecs.ContainsKey(c.Id)).ToList();
        while (picked.Count < count && pool.Count > 0)
        {
            AudioClip? best = null; float bestD = -1f;
            foreach (var c in pool)
            {
                float d = picked.Count == 0
                    ? 1f
                    : picked.Min(s => vecs.ContainsKey(s.Id) ? CosineDistance(vecs[s.Id], vecs[c.Id]) : 0f);
                if (d > bestD) { bestD = d; best = c; }
            }
            if (best == null) break;
            // 扩展项也尽量跨库（能跨就跨，跨不了才同库）
            if (pickedLibs.Contains(best.LibraryId))
            {
                var alt = pool.Where(c => !pickedLibs.Contains(c.LibraryId))
                              .OrderByDescending(c => picked.Min(s => vecs.ContainsKey(s.Id) ? CosineDistance(vecs[s.Id], vecs[c.Id]) : 0f))
                              .FirstOrDefault();
                if (alt != null) best = alt;
            }
            pool.Remove(best);
            picked.Add(best); pickedIds.Add(best.Id); pickedLibs.Add(best.LibraryId);
        }

        // ⑥ 为每个入选项取一批同库同类
        foreach (var seed in picked)
        {
            var batch = GetAudioClipsMatching(seed.LibraryId, perBatch, query);
            if (batch.Count == 0) batch = new List<AudioClip> { seed };
            if (!batch.Any(b => b.Id == seed.Id)) batch.Insert(0, seed);
            float div = picked.Count <= 1 ? 1f
                : (vecs.ContainsKey(seed.Id)
                    ? picked.Where(s => s.Id != seed.Id && vecs.ContainsKey(s.Id))
                            .Select(s => CosineDistance(vecs[seed.Id], vecs[s.Id])).DefaultIfEmpty(1f).Min()
                    : 0f);
            result.Add((seed, batch.Take(perBatch).ToList(), Strong(seed) ? "name" : "path", div));
        }
        // 🔴 **方案 B：精确词凑不够时用同族词继续补库**（2026-09-27，用户同意）——
        //   实测 `cymbal` 只有 4 个可用库、`crash` 只有 1 个，凑不够用户要的数量。
        //   用户原话：「我这么大库的体量找五个目标采样应该问题不大」
        //   ⇒ 用同族词（仍属同一类乐器）补【还没用过的库】，而不是同库重复充数。
        //   ⚠️ 扩充进来的项 matchedBy 写成 "name:<词>"，回答里要说明是靠哪个词补的。
        // ⚠️ **必须按【不同库数】判断，不是总组数**（2026-09-27 修）——
        //   实测 bug：精确搜已凑够 5 组、但其中两库重复 ⇒ result.Count == 5
        //   ⇒ 本块根本不触发、方案 B 形同虚设。用户要的是【库不重复】。
        if (result.Select(x => x.Item1.LibraryId).Distinct().Count() < count)
        {
            var usedLibs = new System.Collections.Generic.HashSet<long>(result.Select(x => x.Item1.LibraryId));
            foreach (var term in AudioQuality.RelatedTermsFor(query))
            {
                if (result.Select(x => x.Item1.LibraryId).Distinct().Count() >= count) break;
                var extraCand = AudioQuality.ApplyMicPolicy(SearchAudioClips(term, 200, 0));
                foreach (var grp in extraCand.GroupBy(c => c.LibraryId))
                {
                    if (result.Select(x => x.Item1.LibraryId).Distinct().Count() >= count) break;
                    if (usedLibs.Contains(grp.Key)) continue;
                    var seed = grp.First();
                    var batch = GetAudioClipsMatching(seed.LibraryId, perBatch, term);
                    if (batch.Count == 0) batch = new System.Collections.Generic.List<AudioClip> { seed };
                    if (!batch.Any(b => b.Id == seed.Id)) batch.Insert(0, seed);
                    // 🔴 **优先【替换】掉重复库的那一项**，而不是无脑追加（2026-09-27）——
                    //   否则组数超出 count、而用户看到的仍是「库重复」。
                    int dupIdx = result.FindIndex(x => x.Item1.LibraryId == grp.Key &&
                        result.Count(y => y.Item1.LibraryId == grp.Key) > 1);
                    var entry = (seed, batch.Take(perBatch).ToList(), "name:" + term, 0f);
                    if (dupIdx >= 0) result[dupIdx] = entry;
                    else result.Add(entry);
                    usedLibs.Add(grp.Key);
                }
            }
        }

        return result;
    }

    /// <summary>批量读嵌入向量（BLOB = float32 小端数组）。</summary>
    private Dictionary<long, float[]> LoadVectors(string table, List<long> ids)
    {
        var map = new Dictionary<long, float[]>();
        if (ids.Count == 0) return map;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 分批 IN 查询（SQLite 参数上限）
        foreach (var chunk in ids.Chunk(300))
        {
            var ph = string.Join(",", chunk.Select((_, i) => "$p" + i));
            cmd.CommandText = $"SELECT clip_id, vec FROM {table} WHERE clip_id IN ({ph})";
            cmd.Parameters.Clear();
            for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                long cid = rd.GetInt64(0);
                var blob = (byte[])rd.GetValue(1);
                var v = new float[blob.Length / 4];
                Buffer.BlockCopy(blob, 0, v, 0, blob.Length);
                map[cid] = v;
            }
        }
        return map;
    }

    /// <summary>余弦距离（1 - 余弦相似度）。向量已归一化时等于 1 - 点积。</summary>
    private static float CosineDistance(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na <= 0 || nb <= 0) return 1f;
        return (float)(1.0 - dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    public List<AudioClip> GetAudioClips(long libraryId, int limit = 8, string preferName = "")
    {
        var list = new List<AudioClip>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 排序优先级：
        //   ① 名称/路径命中当前乐器名（库内 .previews/<乐器>.nki.ogg 这类专属试听）—— 最相关
        //   ② 演示音频优先于散装采样
        //   ③ 同优先级内随机，保证「换一批」每次不同
        cmd.CommandText = """
            SELECT a.id, a.library_id, l.name, a.rel_path, a.name, a.ext, a.size_bytes, a.kind
            FROM audio_clips a JOIN libraries l ON l.id = a.library_id
            WHERE a.library_id = $lib
            ORDER BY
                CASE WHEN $prefer <> '' AND (a.name LIKE '%' || $prefer || '%' OR a.rel_path LIKE '%' || $prefer || '%')
                     THEN 0 ELSE 1 END,
                CASE a.kind WHEN 'demo' THEN 0 ELSE 1 END,
                RANDOM()
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        // 🔴 多查 6 倍 —— 质量筛选（去预览 / 去组合名 / 去远麦位）会淘汰一部分（2026-09-27）
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit * 6, 1, 300));
        cmd.Parameters.AddWithValue("$prefer", (preferName ?? "").Trim());
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new AudioClip
            {
                Id = rd.GetInt64(0),
                LibraryId = rd.GetInt64(1),
                LibraryName = rd.GetString(2),
                RelPath = rd.GetString(3),
                Name = rd.GetString(4),
                Ext = rd.GetString(5),
                SizeBytes = rd.GetInt64(6),
                Kind = rd.GetString(7),
            });
        }
        return list;
    }

    /// <summary>某库可试听片段的数量统计（用于界面提示）。</summary>
    public (int demo, int sample, int ncw, int nkx) CountAudioClips(long libraryId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              SUM(CASE WHEN kind = 'demo' THEN 1 ELSE 0 END),
              SUM(CASE WHEN kind = 'sample' THEN 1 ELSE 0 END),
              SUM(CASE WHEN kind = 'ncw' THEN 1 ELSE 0 END),
              SUM(CASE WHEN kind = 'nkx' THEN 1 ELSE 0 END)
            FROM audio_clips WHERE library_id = $lib;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        using var rd = cmd.ExecuteReader();
        if (rd.Read() && !rd.IsDBNull(0))
            return (rd.GetInt32(0),
                    rd.IsDBNull(1) ? 0 : rd.GetInt32(1),
                    rd.IsDBNull(2) ? 0 : rd.GetInt32(2),
                    rd.IsDBNull(3) ? 0 : rd.GetInt32(3));
        return (0, 0, 0, 0);
    }

    /// <summary>杂质文件清单（健康检查用）。</summary>
    public List<JunkFileRecord> GetJunkFiles(long libraryId = 0)
    {
        var list = new List<JunkFileRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT j.id, j.library_id, l.name, j.rel_path, j.kind, j.size_bytes, j.suggested_keep, l.path
            FROM junk_files j JOIN libraries l ON l.id = j.library_id
            WHERE ($lib = 0 OR j.library_id = $lib)
            ORDER BY j.size_bytes DESC;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new JunkFileRecord
            {
                Id = rd.GetInt64(0),
                LibraryId = rd.GetInt64(1),
                LibraryName = rd.GetString(2),
                RelPath = rd.GetString(3),
                Kind = rd.GetString(4),
                SizeBytes = rd.GetInt64(5),
                SuggestedKeep = rd.GetInt64(6) != 0,
                FullPath = Path.Combine(rd.GetString(7), rd.GetString(3)),
            });
        }
        return list;
    }

    /// <summary>删除指定杂质文件的索引记录（文件本身由调用方处理）。</summary>
    public int RemoveJunkRecords(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int n = 0;
        foreach (var id in list)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM junk_files WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    // ══════════════════ 收藏与自定义标签 ══════════════════
    //
    // 表结构早已存在于 schema（favorites / tags / tag_map），但一直没接线。
    // tag_map 用的是通用的 (entity_type, entity_id) 设计，所以同一套 API
    // 既能给「音色库」打标签，也能给「单个乐器」打标签。

    /// <summary>取所有已收藏的乐器 id。</summary>
    public HashSet<long> GetFavoriteIds()
    {
        var set = new HashSet<long>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instrument_id FROM favorites;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }

    /// <summary>切换收藏状态，返回切换后的状态（true=已收藏）。</summary>
    public bool ToggleFavorite(long instrumentId)
    {
        using var conn = Open();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM favorites WHERE instrument_id = $id;";
            q.Parameters.AddWithValue("$id", instrumentId);
            bool has = Convert.ToInt64(q.ExecuteScalar()) > 0;
            using var d = conn.CreateCommand();
            d.Parameters.AddWithValue("$id", instrumentId);
            if (has)
            {
                d.CommandText = "DELETE FROM favorites WHERE instrument_id = $id;";
                d.ExecuteNonQuery();
                return false;
            }
            d.CommandText = "INSERT INTO favorites(instrument_id, created_at) VALUES($id, $t);";
            d.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            d.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>取全部标签名（含各自的使用计数），按计数降序。</summary>
    public List<(string Name, int Count)> GetAllTags()
    {
        var list = new List<(string, int)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name, COUNT(m.tag_id) AS c
              FROM tags t LEFT JOIN tag_map m ON m.tag_id = t.id
             GROUP BY t.id ORDER BY c DESC, t.name;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>取某个实体（库/乐器）的标签名列表。</summary>
    public List<string> GetEntityTags(string entityType, long entityId)
    {
        var list = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name FROM tag_map m JOIN tags t ON t.id = m.tag_id
             WHERE m.entity_type = $et AND m.entity_id = $id ORDER BY t.name;
            """;
        cmd.Parameters.AddWithValue("$et", entityType);
        cmd.Parameters.AddWithValue("$id", entityId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>批量取一批实体的标签（entity_id → 标签名列表），用于列表渲染避免 N+1。</summary>
    public Dictionary<long, List<string>> GetTagsForAll(string entityType)
    {
        var map = new Dictionary<long, List<string>>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.entity_id, t.name FROM tag_map m JOIN tags t ON t.id = m.tag_id
             WHERE m.entity_type = $et ORDER BY m.entity_id, t.name;
            """;
        cmd.Parameters.AddWithValue("$et", entityType);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long id = r.GetInt64(0);
            if (!map.TryGetValue(id, out var lst)) { lst = new List<string>(); map[id] = lst; }
            lst.Add(r.GetString(1));
        }
        return map;
    }

    /// <summary>
    /// **整表覆盖**某个实体的标签集合。
    /// 传入的标签名若不存在会自动创建；传空列表则清空该实体的所有标签。
    /// 用「覆盖」而不是「增量」是为了让界面上的编辑框语义简单（所见即所得）。
    /// </summary>
    public int SetEntityTags(string entityType, long entityId, IEnumerable<string> tagNames)
    {
        var wanted = (tagNames ?? Enumerable.Empty<string>())
            .Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0 && t.Length <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tag_map WHERE entity_type = $et AND entity_id = $id;";
            del.Parameters.AddWithValue("$et", entityType);
            del.Parameters.AddWithValue("$id", entityId);
            del.ExecuteNonQuery();
        }

        int added = 0;
        foreach (var name in wanted)
        {
            long tagId;
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO tags(name) VALUES($n);";
                ins.Parameters.AddWithValue("$n", name);
                ins.ExecuteNonQuery();
            }
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT id FROM tags WHERE name = $n COLLATE NOCASE LIMIT 1;";
                q.Parameters.AddWithValue("$n", name);
                var v = q.ExecuteScalar();
                if (v == null) continue;
                tagId = Convert.ToInt64(v);
            }
            using (var m = conn.CreateCommand())
            {
                m.Transaction = tx;
                m.CommandText = "INSERT OR IGNORE INTO tag_map(entity_type, entity_id, tag_id) VALUES($et, $id, $t);";
                m.Parameters.AddWithValue("$et", entityType);
                m.Parameters.AddWithValue("$id", entityId);
                m.Parameters.AddWithValue("$t", tagId);
                added += m.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return added;
    }

    /// <summary>删除一个标签（连同它的所有关联）。</summary>
    public int DeleteTag(string name)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        long id = 0;
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT id FROM tags WHERE name = $n COLLATE NOCASE LIMIT 1;";
            q.Parameters.AddWithValue("$n", name);
            var v = q.ExecuteScalar();
            if (v == null) return 0;
            id = Convert.ToInt64(v);
        }
        foreach (var sql in new[] { "DELETE FROM tag_map WHERE tag_id = $id;", "DELETE FROM tags WHERE id = $id;" })
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
        tx.Commit();
        return 1;
    }

    
/// <summary>重命名标签。若新名已存在则合并（把关联改到已有标签上，删除旧标签）。</summary>
    
public (bool ok, string message) RenameTag(string from, string to)
    
{
    
    from = (from ?? "").Trim(); to = (to ?? "").Trim();
    
    if (from.Length == 0 || to.Length == 0) return (false, "标签名不能为空");
    
    if (to.Length > 32) return (false, "标签名不能超过 32 个字符");
    
    if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return (true, "名称没有变化");

    
    using var conn = Open();
    
    using var tx = conn.BeginTransaction();
    
    long oldId = 0, newId = 0;
    
    using (var q = conn.CreateCommand())
    
    {
    
        q.Transaction = tx;
    
        q.CommandText = "SELECT id FROM tags WHERE name = $n COLLATE NOCASE LIMIT 1;";
    
        q.Parameters.AddWithValue("$n", from);
    
        var v = q.ExecuteScalar();
    
        if (v == null) return (false, "未找到该标签");
    
        oldId = Convert.ToInt64(v);
    
    }
    
    using (var q2 = conn.CreateCommand())
    
    {
    
        q2.Transaction = tx;
    
        q2.CommandText = "SELECT id FROM tags WHERE name = $n COLLATE NOCASE LIMIT 1;";
    
        q2.Parameters.AddWithValue("$n", to);
    
        var v2 = q2.ExecuteScalar();
    
        if (v2 != null) newId = Convert.ToInt64(v2);
    
    }

    
    if (newId != 0)
    
    {
    
        // 目标已存在 → 合并：把关联改过去（忽略冲突），再删旧标签
    
        using (var m = conn.CreateCommand())
    
        {
    
            m.Transaction = tx;
    
            m.CommandText = "INSERT OR IGNORE INTO tag_map(entity_type, entity_id, tag_id) SELECT entity_type, entity_id, $new FROM tag_map WHERE tag_id = $old;";
    
            m.Parameters.AddWithValue("$new", newId);
    
            m.Parameters.AddWithValue("$old", oldId);
    
            m.ExecuteNonQuery();
    
        }
    
        using (var d = conn.CreateCommand())
    
        {
    
            d.Transaction = tx;
    
            d.CommandText = "DELETE FROM tag_map WHERE tag_id = $old; DELETE FROM tags WHERE id = $old;";
    
            d.Parameters.AddWithValue("$old", oldId);
    
            d.ExecuteNonQuery();
    
        }
    
        tx.Commit();
    
        return (true, $"「{from}」与已有的「{to}」合并了");
    
    }

    
    using (var u = conn.CreateCommand())
    
    {
    
        u.Transaction = tx;
    
        u.CommandText = "UPDATE tags SET name = $to WHERE id = $id;";
    
        u.Parameters.AddWithValue("$to", to);
    
        u.Parameters.AddWithValue("$id", oldId);
    
        u.ExecuteNonQuery();
    
    }
    
    tx.Commit();
    
    return (true, $"已把「{from}」重命名为「{to}」");
    
}
    // ══════════════════ Agent Trace（工具调用轨迹）══════════════════

    /// <summary>
    /// 记一条 Agent 工具调用轨迹。
    /// 用途（调研报告第二优先）：没有它，后面所有优化都是盲改；
    /// 有了它，失败归因（哪个工具老出错）与成本控制（哪一步最贵）都变成查表。
    /// </summary>
    public void AddTrace(long sessionId, int step, string toolName, string argsHash,
                         string argsPreview, bool ok, long elapsedMs, int resultChars, string error,
                         int promptTokens = 0, int completionTokens = 0, int reasoningTokens = 0)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_trace
                    (session_id, step, tool_name, args_hash, args_preview, ok, elapsed_ms, result_chars, error, created_at, prompt_tokens, completion_tokens, reasoning_tokens)
                VALUES ($s, $st, $t, $h, $p, $ok, $ms, $rc, $e, $at, $pt, $ct, $rt);
                """;
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$st", step);
            cmd.Parameters.AddWithValue("$t", toolName ?? "");
            cmd.Parameters.AddWithValue("$h", argsHash ?? "");
            cmd.Parameters.AddWithValue("$p", (argsPreview ?? "").Length > 300 ? argsPreview[..300] : (argsPreview ?? ""));
            cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
            cmd.Parameters.AddWithValue("$ms", elapsedMs);
            cmd.Parameters.AddWithValue("$rc", resultChars);
            cmd.Parameters.AddWithValue("$e", error ?? "");
            cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("$pt", promptTokens);
            cmd.Parameters.AddWithValue("$ct", completionTokens);
            cmd.Parameters.AddWithValue("$rt", reasoningTokens);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // 诊断：把失败原因写进文件（轨迹记录失败不该影响主流程，但必须可查）
            try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "trace-error.log"), $"{DateTime.Now:HH:mm:ss} {ex.GetType().Name}: {ex.Message}`r`n"); } catch { }
        }
    }

    /// <summary>取最近的工具调用轨迹（新的在前）。</summary>
    /// <summary>
    /// **老库迁移**：给 `agent_trace` 补上 token 三列（已存在则跳过）。
    /// 建表语句里的新列只对新库生效，已存在的库必须走 ALTER TABLE。
    /// </summary>
    /// <summary>
    /// **替换某库的说明书清单**（定向重扫用）：先删旧的再插新的。
    /// 返回 (删除数, 新增数) 供界面显示效果。
    /// </summary>
    public (int removed, int added) ReplaceManuals(long libraryId, List<ManualRecord> records)
    {
        int removed = 0, added = 0;
        try
        {
            using var conn = Open();
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM manuals WHERE library_id = $id;";
                del.Parameters.AddWithValue("$id", libraryId);
                removed = del.ExecuteNonQuery();
            }
            using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO manuals (library_id, rel_path, name, ext, size_bytes, is_primary)
                VALUES ($lib, $rel, $name, $ext, $size, $primary);
                """;
            ins.Parameters.AddWithValue("$lib", libraryId);
            var pRel = ins.CreateParameter(); pRel.ParameterName = "$rel"; ins.Parameters.Add(pRel);
            var pName = ins.CreateParameter(); pName.ParameterName = "$name"; ins.Parameters.Add(pName);
            var pExt = ins.CreateParameter(); pExt.ParameterName = "$ext"; ins.Parameters.Add(pExt);
            var pSize = ins.CreateParameter(); pSize.ParameterName = "$size"; ins.Parameters.Add(pSize);
            var pPrim = ins.CreateParameter(); pPrim.ParameterName = "$primary"; ins.Parameters.Add(pPrim);
            foreach (var m in records)
            {
                pRel.Value = m.RelPath; pName.Value = m.Name; pExt.Value = m.Ext;
                pSize.Value = m.SizeBytes; pPrim.Value = m.IsPrimary ? 1 : 0;
                ins.ExecuteNonQuery();
                added++;
            }
        }
        catch { }
        return (removed, added);
    }
    private static void MigrateTraceTokens(SqliteConnection conn)
    {
        foreach (var (col, ddl) in new[]
        {
            ("prompt_tokens",     "ALTER TABLE agent_trace ADD COLUMN prompt_tokens INTEGER NOT NULL DEFAULT 0"),
            ("completion_tokens", "ALTER TABLE agent_trace ADD COLUMN completion_tokens INTEGER NOT NULL DEFAULT 0"),
            ("reasoning_tokens",  "ALTER TABLE agent_trace ADD COLUMN reasoning_tokens INTEGER NOT NULL DEFAULT 0"),
        })
        {
            try
            {
                using var chk = conn.CreateCommand();
                chk.CommandText = "SELECT COUNT(*) FROM pragma_table_info('agent_trace') WHERE name = $c";
                chk.Parameters.AddWithValue("$c", col);
                if (Convert.ToInt64(chk.ExecuteScalar()) > 0) continue;
                using var alt = conn.CreateCommand();
                alt.CommandText = ddl;
                alt.ExecuteNonQuery();
            }
            catch { }
        }
    }
    public List<(long Id, long SessionId, int Step, string Tool, string ArgsPreview, bool Ok,
                 long ElapsedMs, int ResultChars, string Error, string CreatedAt,
                 int PromptTokens, int CompletionTokens, int ReasoningTokens)> GetTraces(int limit = 200)
    {
        var list = new List<(long, long, int, string, string, bool, long, int, string, string, int, int, int)>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, step, tool_name, args_preview, ok, elapsed_ms, result_chars, error, created_at,
                       prompt_tokens, completion_tokens, reasoning_tokens
                  FROM agent_trace ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 2000));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
                          r.GetInt64(5) != 0, r.GetInt64(6), r.GetInt32(7), r.GetString(8), r.GetString(9),
                          r.GetInt32(10), r.GetInt32(11), r.GetInt32(12)));
        }
        catch { }
        return list;
    }

    /// <summary>按工具聚合的统计（次数 / 失败数 / 平均耗时 / 平均结果大小）—— 用来找「哪个工具最常出错」。</summary>
    public List<(string Tool, int Calls, int Fails, double AvgMs, double AvgChars)> GetTraceStats()
    {
        var list = new List<(string, int, int, double, double)>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT tool_name, COUNT(*) c,
                       SUM(CASE WHEN ok = 0 THEN 1 ELSE 0 END) f,
                       AVG(elapsed_ms), AVG(result_chars)
                  FROM agent_trace GROUP BY tool_name ORDER BY c DESC;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetDouble(3), r.GetDouble(4)));
        }
        catch { }
        return list;
    }

    /// <summary>清空轨迹。</summary>
    public int ClearTraces()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agent_trace;";
            return cmd.ExecuteNonQuery();
        }
        catch { return 0; }
    }
    /// <summary>健康检查汇总：各库的杂质、缺说明书、版本未知等情况。</summary>
    public List<HealthIssue> GetHealthIssues()
    {
        var list = new List<HealthIssue>();
        using var conn = Open();

        // ① 杂质文件
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT l.id, l.name, COUNT(*), SUM(j.size_bytes)
                FROM junk_files j JOIN libraries l ON l.id = j.library_id
                GROUP BY l.id HAVING COUNT(*) > 0 ORDER BY SUM(j.size_bytes) DESC;
                """;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new HealthIssue
                {
                    LibraryId = rd.GetInt64(0), LibraryName = rd.GetString(1),
                    Kind = "junk", Severity = "info",
                    Detail = $"{rd.GetInt32(2)} 个杂质文件，可安全删除",
                    SizeBytes = rd.IsDBNull(3) ? 0 : rd.GetInt64(3),
                });
        }

        // ② 无说明书
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT l.id, l.name FROM libraries l
                WHERE NOT EXISTS (SELECT 1 FROM manuals m WHERE m.library_id = l.id)
                ORDER BY l.name;
                """;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new HealthIssue
                {
                    LibraryId = rd.GetInt64(0), LibraryName = rd.GetString(1),
                    Kind = "no-manual", Severity = "info",
                    Detail = "没有说明书文档（无法建立知识库）", SizeBytes = 0,
                });
        }

        // ③ 版本未知（无法判定兼容性）
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT l.id, l.name, COUNT(i.id) FROM libraries l
                LEFT JOIN instruments i ON i.library_id = l.id AND i.kind = 'nki'
                WHERE l.required_kontakt = ''
                GROUP BY l.id ORDER BY l.name;
                """;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new HealthIssue
                {
                    LibraryId = rd.GetInt64(0), LibraryName = rd.GetString(1),
                    Kind = "unknown-version", Severity = "info",
                    Detail = $"未能提取引擎版本（{rd.GetInt32(2)} 个 NKI）", SizeBytes = 0,
                });
        }

        // ④ 无封面
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name FROM libraries WHERE cover_file = '' ORDER BY name;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new HealthIssue
                {
                    LibraryId = rd.GetInt64(0), LibraryName = rd.GetString(1),
                    Kind = "no-cover", Severity = "info",
                    Detail = "没有封面图（无 .nicnt 或未提取到）", SizeBytes = 0,
                });
        }

        return list;
    }
    /// <summary>取某库内第一个 .nki 的完整路径（用于「调用 Kontakt 测试」）。</summary>
    public string? GetFirstInstrumentPath(long libraryId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.rel_path, l.path
            FROM instruments i JOIN libraries l ON l.id = i.library_id
            WHERE i.library_id = $id AND i.kind = 'nki'
            ORDER BY i.rel_path LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", libraryId);
        using var rd = cmd.ExecuteReader();
        if (rd.Read())
            return System.IO.Path.Combine(rd.GetString(1), rd.GetString(0));
        return null;
    }

    /// <summary>取某库内全部 .nki 相对路径（M2 乐器浏览用）。</summary>
    public List<InstrumentRecord> GetInstruments(long? libraryId = null, string search = "", int limit = 500)
    {
        var list = new List<InstrumentRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT i.id, i.library_id, l.name, i.rel_path, i.name, i.kind, i.size_bytes, i.mtime,
                   i.name_source, i.file_format, i.engine_version
            FROM instruments i JOIN libraries l ON l.id = i.library_id
            WHERE ($lib = 0 OR i.library_id = $lib)
              AND ($q = '' OR i.name LIKE '%' || $q || '%' OR i.rel_path LIKE '%' || $q || '%')
            ORDER BY i.name COLLATE NOCASE
            LIMIT {limit};
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId ?? 0);
        cmd.Parameters.AddWithValue("$q", search ?? "");
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new InstrumentRecord
            {
                Id = rd.GetInt64(0),
                LibraryId = rd.GetInt64(1),
                LibraryName = rd.GetString(2),
                RelPath = rd.GetString(3),
                Name = rd.GetString(4),
                Kind = rd.GetString(5),
                SizeBytes = rd.GetInt64(6),
                Mtime = rd.GetInt64(7),
                Source = rd.GetString(8),
                Format = rd.GetString(9),
                EngineVersion = rd.GetString(10),
            });
        }
        return list;
    }

    public List<LibraryRecord> GetLibraries(string orderBy = "name COLLATE NOCASE ASC", int limit = 0)    {
        var list = new List<LibraryRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT l.id, l.root_id, COALESCE(r.path,''), l.name, l.path, l.category, l.size_bytes, l.file_count,
                   l.nki_count, l.nkm_count, l.nkc_count, l.has_nicnt, l.junk_count, l.junk_bytes,
                   l.product_key, l.required_kontakt, l.notes, l.last_scanned_at, l.cover_file,
                   (SELECT COUNT(*) FROM manuals m WHERE m.library_id = l.id)
            FROM libraries l LEFT JOIN roots r ON r.id = l.root_id
            ORDER BY {orderBy} {(limit > 0 ? "LIMIT " + limit : "")};
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new LibraryRecord
            {
                Id = rd.GetInt64(0),
                RootId = rd.GetInt64(1),
                RootPath = rd.GetString(2),
                Name = rd.GetString(3),
                Path = rd.GetString(4),
                Category = rd.GetString(5),
                SizeBytes = rd.GetInt64(6),
                FileCount = rd.GetInt32(7),
                NkiCount = rd.GetInt32(8),
                NkmCount = rd.GetInt32(9),
                NkcCount = rd.GetInt32(10),
                HasNicnt = rd.GetInt32(11) != 0,
                JunkCount = rd.GetInt32(12),
                JunkBytes = rd.GetInt64(13),
                ProductKey = rd.GetString(14),
                RequiredKontakt = rd.GetString(15),
                Notes = rd.GetString(16),
                LastScannedAt = rd.GetString(17),
                CoverFile = rd.GetString(18),
                ManualCount = rd.GetInt32(19),
            });
        }
        return list;
    }

    /// <summary>
    /// 某库内乐器的「目录分组摘要」（顶层相对路径 + 数量），用于给 Agent 提供库结构上下文，
    /// 例如"这个库包含哪些乐器"。返回形如 "Solo Cello (4)" 的字符串列表。
    /// </summary>
    public List<string> GetInstrumentGroups(long libraryId, int maxGroups = 30)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rel_path FROM instruments WHERE library_id = $id AND kind = 'nki';";
        cmd.Parameters.AddWithValue("$id", libraryId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string rel = rd.GetString(0);
            // 取前两层目录作为分组名（多数库的结构是 <分类>/<乐器>/xxx.nki）
            var parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string group;
            if (parts.Length >= 3) group = parts[0] + "/" + parts[1];
            else if (parts.Length == 2) group = parts[0];
            else group = "(根目录)";
            map[group] = map.TryGetValue(group, out int c) ? c + 1 : 1;
        }

        return map.OrderByDescending(kv => kv.Value)
                  .Take(maxGroups)
                  .Select(kv => $"{kv.Key} ({kv.Value})")
                  .ToList();
    }

    /// <summary>
    /// 更新音色库的路径（音色库移动后调用）。
    /// 乐器的 rel_path 是相对路径，无需变动；若新路径属于另一个根目录，一并更新 root_id。
    /// </summary>
    public (bool ok, string message) UpdateLibraryPath(long libraryId, string newPath)
    {
        newPath = (newPath ?? "").Trim().TrimEnd('\\', '/');
        if (newPath.Length == 0) return (false, "路径为空");

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE libraries
               SET path = $p,
                   root_id = COALESCE(
                       (SELECT id FROM roots
                         WHERE $p = path OR $p LIKE path || '\' || '%'
                         ORDER BY LENGTH(path) DESC LIMIT 1),
                       root_id)
             WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$p", newPath);
        cmd.Parameters.AddWithValue("$id", libraryId);
        int n = cmd.ExecuteNonQuery();
        return (n > 0, n > 0 ? "已更新库路径" : "未找到该音色库");
    }

    /// <summary>
    /// 乐器检索（乐器中心用）：支持关键词、库、分类、类型过滤与分页。
    /// 关键词按空格拆分做 AND 匹配（名称或相对路径任一命中即可）。
    /// </summary>
    public (List<InstrumentRecord> items, int total) SearchInstruments(
        string query = "", long libraryId = 0, string category = "", string kind = "",
        int limit = 200, int offset = 0, string sort = "name", string articulation = "", bool favoriteOnly = false)
    {
        var terms = (query ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(6).ToList();

        var where = new StringBuilder("WHERE 1=1");
        if (libraryId > 0) where.Append(" AND i.library_id = $lib");
        // 只看收藏（用户要求：乐器中心要能筛收藏）
        if (favoriteOnly) where.Append(" AND i.id IN (SELECT instrument_id FROM favorites)");
        if (category.Length > 0) where.Append(" AND l.category = $cat");
        if (kind.Length > 0) where.Append(" AND i.kind = $kind");
        if (articulation.Length > 0) where.Append(" AND i.articulation = $art");
        for (int t = 0; t < terms.Count; t++)
            where.Append($" AND (i.name LIKE $q{t} OR i.rel_path LIKE $q{t})");

        string order = sort switch
        {
            "size" => "i.size_bytes DESC",
            "library" => "l.name COLLATE NOCASE, i.name COLLATE NOCASE",
            "page" => "i.rel_path COLLATE NOCASE",
            _ => "i.name COLLATE NOCASE",
        };

        var list = new List<InstrumentRecord>();
        using var conn = Open();

        int total;
        using (var cnt = conn.CreateCommand())
        {
            cnt.CommandText = $"SELECT COUNT(*) FROM instruments i JOIN libraries l ON l.id = i.library_id {where};";
            BindFilters(cnt, libraryId, category, kind, terms, articulation);
            total = Convert.ToInt32(cnt.ExecuteScalar() ?? 0);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT i.id, i.library_id, l.name, i.rel_path, i.name, i.kind, i.size_bytes, i.mtime,
                       i.name_source, i.file_format, i.engine_version, l.category, l.path, i.articulation
                FROM instruments i JOIN libraries l ON l.id = i.library_id
                {where}
                ORDER BY {order}
                LIMIT {Math.Clamp(limit, 1, 2000)} OFFSET {Math.Max(0, offset)};
                """;
            BindFilters(cmd, libraryId, category, kind, terms, articulation);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                list.Add(new InstrumentRecord
                {
                    Id = rd.GetInt64(0),
                    LibraryId = rd.GetInt64(1),
                    LibraryName = rd.GetString(2),
                    RelPath = rd.GetString(3),
                    Name = rd.GetString(4),
                    Kind = rd.GetString(5),
                    SizeBytes = rd.GetInt64(6),
                    Mtime = rd.GetInt64(7),
                    Source = rd.GetString(8),
                    Format = rd.GetString(9),
                    EngineVersion = rd.GetString(10),
                    LibraryPath = rd.GetString(12),
                    Articulation = rd.GetString(13),
                });
            }
        }
        return (list, total);
    }

    private static void BindFilters(SqliteCommand cmd, long libraryId, string category, string kind, List<string> terms,
                                    string articulation = "")
    {
        if (libraryId > 0) cmd.Parameters.AddWithValue("$lib", libraryId);
        if (category.Length > 0) cmd.Parameters.AddWithValue("$cat", category);
        if (kind.Length > 0) cmd.Parameters.AddWithValue("$kind", kind);
        if (articulation.Length > 0) cmd.Parameters.AddWithValue("$art", articulation);
        for (int t = 0; t < terms.Count; t++)
            cmd.Parameters.AddWithValue($"$q{t}", "%" + terms[t] + "%");
    }

    /// <summary>乐器中心左侧树：分类 → 库 → 乐器数。</summary>
    public List<(string Category, string Library, long LibraryId, int Count)> GetInstrumentTree()
    {
        var list = new List<(string, string, long, int)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.category, l.name, l.id, COUNT(i.id)
            FROM libraries l LEFT JOIN instruments i ON i.library_id = l.id AND i.kind = 'nki'
            GROUP BY l.id
            ORDER BY l.category COLLATE NOCASE, l.name COLLATE NOCASE;
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add((rd.GetString(0), rd.GetString(1), rd.GetInt64(2), rd.GetInt32(3)));
        return list;
    }

    /// <summary>更新某库的封面文件名。</summary>
    public void SetLibraryCover(long libraryId, string coverFile)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE libraries SET cover_file = $c WHERE id = $id;";
        cmd.Parameters.AddWithValue("$c", coverFile);
        cmd.Parameters.AddWithValue("$id", libraryId);
        cmd.ExecuteNonQuery();
    }
    /// <summary>取某库的说明书清单（主要手册排在前面）。</summary>
    public List<ManualRecord> GetManuals(long? libraryId = null)
    {
        var list = new List<ManualRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.library_id, l.name, l.path, m.rel_path, m.name, m.ext, m.size_bytes, m.is_primary
            FROM manuals m JOIN libraries l ON l.id = m.library_id
            WHERE ($lib = 0 OR m.library_id = $lib)
            ORDER BY m.is_primary DESC, m.size_bytes DESC;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId ?? 0);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new ManualRecord
            {
                Id = rd.GetInt64(0),
                LibraryId = rd.GetInt64(1),
                LibraryName = rd.GetString(2),
                LibraryPath = rd.GetString(3),
                RelPath = rd.GetString(4),
                Name = rd.GetString(5),
                Ext = rd.GetString(6),
                SizeBytes = rd.GetInt64(7),
                IsPrimary = rd.GetInt32(8) != 0,
            });
        }
        return list;
    }
}
