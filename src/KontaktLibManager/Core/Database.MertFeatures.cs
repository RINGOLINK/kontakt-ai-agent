using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace KontaktLibManager.Core;

/// <summary>
/// **`Database` 的分部类：MERT 嵌入的存取**（调研报告「远期」第 7 项 · 档 2）。
///
/// **为什么单独一张表、而不是复用 `audio_features`**：
///   `audio_features` 存的是**档 1 的 32 维手工声学特征**，
///   这张表存的是**档 2 的 768 维 MERT 语义嵌入** —— **两套完全不同来源的特征**。
///   **分开存的价值**：可以**并存、可对比**（将来能实际比较「MERT 是否真比手工特征好」），
///   而不是把旧数据冲掉。
///
/// **架构原则（用户明确要求）**：**绝不让 Agent 在查询时实时跑模型** ——
///   全部**入库/接管时后台批量预计算**、落这张表；**Agent 查询时只读表算余弦（毫秒级）**。
/// </summary>
public sealed partial class Database
{
    /// <summary>保证 `mert_features` 表存在（自愈式建表，避免动 `Database.cs` 里那段 schema 原始字符串）。</summary>
    public void EnsureMertTable()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS mert_features (
                clip_id    INTEGER PRIMARY KEY,
                library_id INTEGER NOT NULL,
                dim        INTEGER NOT NULL,
                vec        BLOB    NOT NULL,
                created_at TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_mert_library ON mert_features(library_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>保存/更新一个片段的 MERT 嵌入（按 clip_id 覆盖）。</summary>
    public void SaveMertFeature(long clipId, long libraryId, float[] vec)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mert_features(clip_id, library_id, dim, vec, created_at)
            VALUES($c, $l, $d, $v, $t)
            ON CONFLICT(clip_id) DO UPDATE SET dim=$d, vec=$v, created_at=$t
            """;
        cmd.Parameters.AddWithValue("$c", clipId);
        cmd.Parameters.AddWithValue("$l", libraryId);
        cmd.Parameters.AddWithValue("$d", vec.Length);
        cmd.Parameters.AddWithValue("$v", AudioFeatures.Pack(vec));   // 同一个 float[]→BLOB 打包器
        cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>已提取的 MERT 嵌入数量。</summary>
    public int CountMertFeatures()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM mert_features";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch { return 0; }   // 表不存在时当作 0（首次运行）
    }

    /// <summary>已提取的 clip_id 集合（**续跑时用来跳过已处理的**）。</summary>
    public HashSet<long> MertDoneClipIds()
    {
        var set = new HashSet<long>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT clip_id FROM mert_features";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetInt64(0));
        }
        catch { }
        return set;
    }

    /// <summary>读一个片段的 MERT 嵌入。</summary>
    public float[]? GetMertFeature(long clipId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT vec FROM mert_features WHERE clip_id = $c";
            cmd.Parameters.AddWithValue("$c", clipId);
            var o = cmd.ExecuteScalar();
            return o is byte[] b ? AudioFeatures.Unpack(b) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// **在 MERT 嵌入里做余弦 KNN**（音色地图详情用）。
    ///
    /// **为什么不能复用 FindSimilarAudio**：那个查的是档 1 的 `audio_features`（32 维手工声学特征），
    /// **而地图是档 2 的 `mert_features`（768 维）建的** —— 两套表完全不同源。
    /// **实测踩到**：`audio_features` 是空表（档 1 从没跑过）⇒ 详情页永远返回「没有特征」，
    /// 表现为「所有簇都无法试听、随机点一个也显示没有可播放音频」。
    /// </summary>
    public List<(long ClipId, double Score)> FindSimilarMert(float[] query, int topK = 12, long excludeClipId = 0)
    {
        var outp = new List<(long, double)>();
        if (query == null || query.Length == 0) return outp;
        try
        {
            foreach (var (clipId, _libId, vec) in LoadAllMertFeatures())
            {
                if (clipId == excludeClipId) continue;
                if (vec.Length != query.Length) continue;
                double dot = 0, na = 0, nb = 0;
                for (int i = 0; i < vec.Length; i++)
                {
                    dot += (double)query[i] * vec[i];
                    na += (double)query[i] * query[i];
                    nb += (double)vec[i] * vec[i];
                }
                double den = Math.Sqrt(na) * Math.Sqrt(nb);
                if (den <= 0) continue;
                outp.Add((clipId, dot / den));
            }
        }
        catch { }
        return outp.OrderByDescending(x => x.Item2).Take(topK).ToList();
    }

    /// <summary>已提取档 1 特征的 clip_id 集合（**续跑时用来跳过已处理的**）。</summary>
    public HashSet<long> AudioFeatureClipIds()
    {
        var set = new HashSet<long>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT clip_id FROM audio_features";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetInt64(0));
        }
        catch { }
        return set;
    }

    // （LoadAllAudioFeatures 已在别处定义，这里不重复）

    /// <summary>**加载全部 MERT 嵌入到内存做余弦 KNN**（供地图查询用；毫秒级，不跑模型）。</summary>
    public List<(long ClipId, long LibraryId, float[] Vec)> LoadAllMertFeatures()
    {
        var list = new List<(long, long, float[])>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT clip_id, library_id, vec FROM mert_features";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var v = AudioFeatures.Unpack(r.IsDBNull(2) ? null : (byte[])r.GetValue(2));
                if (v != null) list.Add((r.GetInt64(0), r.GetInt64(1), v));
            }
        }
        catch { }
        return list;
    }
    /// <summary>保证 umap_coords 表存在（自愈式建表）。</summary>
    public void EnsureUmapTable()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS umap_coords (
                clip_id    INTEGER PRIMARY KEY,
                library_id INTEGER NOT NULL,
                x          REAL    NOT NULL,
                y          REAL    NOT NULL,
                cluster    INTEGER NOT NULL DEFAULT -1,
                created_at TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_umap_cluster ON umap_coords(cluster);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>清空并写入一批 2D 坐标（整张地图重算时用）。</summary>
    public void ReplaceUmapCoords(List<AudioMapReduce.Point> pts)
    {
        EnsureUmapTable();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM umap_coords";
            del.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO umap_coords(clip_id, library_id, x, y, cluster, created_at) VALUES($c,$l,$x,$y,$k,$t)";
            var pc = ins.CreateParameter(); pc.ParameterName = "$c"; ins.Parameters.Add(pc);
            var pl = ins.CreateParameter(); pl.ParameterName = "$l"; ins.Parameters.Add(pl);
            var px = ins.CreateParameter(); px.ParameterName = "$x"; ins.Parameters.Add(px);
            var py = ins.CreateParameter(); py.ParameterName = "$y"; ins.Parameters.Add(py);
            var pk = ins.CreateParameter(); pk.ParameterName = "$k"; ins.Parameters.Add(pk);
            var pt = ins.CreateParameter(); pt.ParameterName = "$t"; ins.Parameters.Add(pt);
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var q in pts)
            {
                pc.Value = q.ClipId; pl.Value = q.LibraryId;
                px.Value = (double)q.X; py.Value = (double)q.Y; pk.Value = q.Cluster; pt.Value = now;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>读全部地图点（供界面渲染）。</summary>
    public List<AudioMapReduce.Point> LoadUmapCoords()
    {
        var list = new List<AudioMapReduce.Point>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT clip_id, library_id, x, y, cluster FROM umap_coords";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AudioMapReduce.Point
                {
                    ClipId = r.GetInt64(0), LibraryId = r.GetInt64(1),
                    X = (float)r.GetDouble(2), Y = (float)r.GetDouble(3), Cluster = r.GetInt32(4),
                });
        }
        catch { }
        return list;
    }

    /// <summary>保证 instrument_map 表存在（**乐器级地图**，音色地图三期）。</summary>
    public void EnsureInstrumentMapTable()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS instrument_map (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id   INTEGER NOT NULL,
                rel_dir      TEXT    NOT NULL,
                name         TEXT    NOT NULL DEFAULT '',
                sample_count INTEGER NOT NULL DEFAULT 0,
                x            REAL    NOT NULL,
                y            REAL    NOT NULL,
                cluster      INTEGER NOT NULL DEFAULT -1,
                created_at   TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_imap_library ON instrument_map(library_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>整批替换乐器级地图（重算时用）。</summary>
    public void ReplaceInstrumentMap(List<(long LibId, string RelDir, string Name, int Count, float X, float Y, int Cluster)> rows)
    {
        EnsureInstrumentMapTable();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM instrument_map";
            del.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO instrument_map(library_id, rel_dir, name, sample_count, x, y, cluster, created_at) VALUES($l,$d,$n,$c,$x,$y,$k,$t)";
            foreach (var n2 in new[] { "$l", "$d", "$n", "$c", "$x", "$y", "$k", "$t" })
                ins.Parameters.Add(ins.CreateParameter()).ParameterName = n2;
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var r in rows)
            {
                ins.Parameters["$l"].Value = r.LibId;
                ins.Parameters["$d"].Value = r.RelDir;
                ins.Parameters["$n"].Value = r.Name;
                ins.Parameters["$c"].Value = r.Count;
                ins.Parameters["$x"].Value = (double)r.X;
                ins.Parameters["$y"].Value = (double)r.Y;
                ins.Parameters["$k"].Value = r.Cluster;
                ins.Parameters["$t"].Value = now;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>读乐器级地图（供界面渲染）。</summary>
    public List<(long LibId, string RelDir, string Name, int Count, float X, float Y, int Cluster)> LoadInstrumentMap()
    {
        var list = new List<(long, string, string, int, float, float, int)>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT library_id, rel_dir, name, sample_count, x, y, cluster FROM instrument_map";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                          (float)r.GetDouble(4), (float)r.GetDouble(5), r.GetInt32(6)));
        }
        catch { }
        return list;
    }

}
