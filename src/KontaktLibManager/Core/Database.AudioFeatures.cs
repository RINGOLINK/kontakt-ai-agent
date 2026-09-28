using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace KontaktLibManager.Core;

/// <summary>
/// **`Database` 的分部类：音色声学特征的存取**（调研报告「远期」第 7 项 · 档 1）。
///
/// **为什么单独一个文件**：`Database.cs` 里的 schema 是一整段 C# 原始字符串字面量
/// （`"""`），在其内部插入代码极易破坏缩进与字符串边界（实测反复踩过）。
/// 把新增的存取方法放进这个**独立的分部类文件**，就完全不必碰那段 schema，
/// 既零风险、也让「音频特征」这块能力自成一个可读的单元。
///
/// 对应的建表语句（`audio_features`）仍在 `Database.cs` 的 schema 里 ——
/// 它随 schema 版本一起演进，属于契约的一部分。
/// </summary>
public sealed partial class Database
{
    /// <summary>
    /// **保证 `audio_features` 表存在**（自愈式建表）。
    ///
    /// **为什么不用 `Database.cs` 里那段 schema**：那段是一整块 C# 原始字符串字面量（`"""`），
    /// 在里面插建表语句极易破坏缩进与字符串边界（实测反复踩过，还把已写好的 DDL 弄丢过）。
    /// 放在这里用 `CREATE TABLE IF NOT EXISTS` 幂等建表，**既零风险又不怕 schema 版本演进**。
    /// </summary>
    public void EnsureAudioFeaturesTable()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS audio_features (
                clip_id    INTEGER PRIMARY KEY,
                library_id INTEGER NOT NULL,
                dim        INTEGER NOT NULL,
                vec        BLOB    NOT NULL,
                created_at TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_af_library ON audio_features(library_id);
            """;
        cmd.ExecuteNonQuery();
    }
    /// <summary>保存/更新一个音频片段的声学特征（按 clip_id 覆盖）。</summary>
    public void SaveAudioFeatures(long clipId, long libraryId, float[] vec)
    {
        EnsureAudioFeaturesTable();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audio_features(clip_id, library_id, dim, vec, created_at)
            VALUES($c, $l, $d, $v, $t)
            ON CONFLICT(clip_id) DO UPDATE SET dim=$d, vec=$v, created_at=$t
            """;
        cmd.Parameters.AddWithValue("$c", clipId);
        cmd.Parameters.AddWithValue("$l", libraryId);
        cmd.Parameters.AddWithValue("$d", vec.Length);
        cmd.Parameters.AddWithValue("$v", AudioFeatures.Pack(vec));
        cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>读出一条音频片段的特征向量（没有则 null）。</summary>
    public float[]? GetAudioFeatures(long clipId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT vec FROM audio_features WHERE clip_id = $c";
        cmd.Parameters.AddWithValue("$c", clipId);
        var o = cmd.ExecuteScalar();
        return o is byte[] b ? AudioFeatures.Unpack(b) : null;
    }

    /// <summary>已提取特征的数量（供界面/工具报告覆盖率）。</summary>
    public int CountAudioFeatures()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audio_features";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// **加载全部特征到内存做余弦 KNN**。
    /// 本机规模约万级 × 32 维 ≈ 数 MB，一次性载入可接受；
    /// 若将来特征量级到百万，应改为分块或加 ANN 索引。
    /// </summary>
    public List<(long ClipId, long LibraryId, float[] Vec)> LoadAllAudioFeatures()
    {
        var list = new List<(long, long, float[])>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT clip_id, library_id, vec FROM audio_features";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var v = AudioFeatures.Unpack(r.IsDBNull(2) ? null : (byte[])r.GetValue(2));
            if (v != null) list.Add((r.GetInt64(0), r.GetInt64(1), v));
        }
        return list;
    }

    /// <summary>
    /// **余弦 KNN：找与给定向量最相似的音频片段**。
    /// </summary>
    /// <param name="query">查询向量（32 维）。</param>
    /// <param name="topK">返回条数。</param>
    /// <param name="excludeClipId">排除的片段 id（查「与它相似的其它片段」时传它自己）。</param>
    /// <param name="sameLibraryOnly">是否只在同一库内找（默认 false = 全库找）。</param>
    public List<(long ClipId, long LibraryId, double Score)> FindSimilarAudio(
        float[] query, int topK = 20, long excludeClipId = 0, bool sameLibraryOnly = false, long libraryId = 0)
    {
        var all = LoadAllAudioFeatures();
        var scored = new List<(long, long, double)>();
        foreach (var (cid, lid, vec) in all)
        {
            if (cid == excludeClipId) continue;
            if (sameLibraryOnly && lid != libraryId) continue;
            scored.Add((cid, lid, AudioFeatures.Cosine(query, vec)));
        }
        return scored.OrderByDescending(x => x.Item3).Take(topK).ToList();
    }
}
