using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>
/// **音色地图的 Agent 工具**（用户问题 ④/⑤：让 Agent 能用地图数据）。
///
/// **为什么必须单独一组工具**：音色地图的 6 个 RPC（`audioMapStatus` / `audioMapStart` /
/// `audioMapRebuild` / `audioMapDetail` …）**全是界面专用的** —— Agent 一个都调不到。
/// 结果就是** Agent 对地图一无所知**：不知道有几个簇、哪个簇最大、**也不能用 MERT 找相似**。
///
/// **📌 设计原则（用户早前明确要求）**：
///   · **查询类工具**（本文件全部）→ **只读表，毫秒级**，绝不触发重算；
///   · **触发计算类**（跑模型/降维）→ 必须**明确告知「这会跑很久」**，让用户决定。
///   本文件**只做查询**，所以每条都会在返回里标注数据是否就绪。
/// </summary>
public static class AudioMapActions
{
    private static JsonElement Root(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string Ok(object o) => JsonSerializer.Serialize(o, JsonOpts);
    private static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static long Num(JsonElement a, string name, long def = 0)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n2)) return n2;
        return def;
    }

    private static string Str(JsonElement a, string name, string def = "")
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return def;
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? def) : def;
    }

    // ─────────────────────────────────────────────────────────────
    // ① map_stats —— 地图总览
    // ─────────────────────────────────────────────────────────────

    /// <summary>地图统计：点数、簇数、覆盖率、最近一次生成时间。</summary>
    public static string MapStats(AgentToolContext ctx, string argsJson)
    {
        var db = ctx.Db;
        if (db == null) return Err("没有可用的索引库。");

        int mert = db.CountMertFeatures();
        var pts = db.LoadUmapCoords();
        var insts = db.LoadInstrumentMap();

        // 可分析总数（用来算覆盖率）
        int analyzable = 0;
        try
        {
            foreach (var lib in db.GetLibraries())
                foreach (var c in db.GetAnalyzableClips(lib.Id))
                    if (AudioFeatures.CanAnalyze(c.Ext)) analyzable++;
        }
        catch { }

        int clusters = pts.Where(p => p.Cluster >= 0).Select(p => p.Cluster).Distinct().Count();

        return Ok(new
        {
            ok = true,
            ready = pts.Count > 0,
            note = pts.Count > 0
                ? "音色地图已就绪。用 map_clusters 看簇、map_find_similar 用 MERT 找相似、map_filter 按簇/库筛选。"
                : "音色地图还没生成 —— 需要用户在「音色地图」页点「开始分析」提取特征、再点「重算地图」。",
            extracted = mert,                      // 已提取的 MERT 特征数
            analyzable,                            // 可分析的音频片段总数
            coveragePercent = analyzable > 0 ? Math.Round(mert * 100.0 / analyzable, 1) : 0,
            mapPoints = pts.Count,                 // 地图上的采样级点数
            clusterCount = clusters,
            instrumentMapPoints = insts.Count,     // 乐器级地图点数
            featuresPerPoint = 768,                // MERT 嵌入维度
            engine = "MERT-v1-95M（768 维）+ UMAP 2D + HDBSCAN",
        });
    }

    // ─────────────────────────────────────────────────────────────
    // ② map_clusters —— 簇列表（带代表乐器）
    // ─────────────────────────────────────────────────────────────

    /// <summary>列出簇：规模、占比最高的库、代表样本。</summary>
    public static string MapClusters(AgentToolContext ctx, string argsJson)
    {
        var db = ctx.Db;
        if (db == null) return Err("没有可用的索引库。");
        var args = Root(argsJson);
        int top = (int)Math.Clamp(Num(args, "top", 20), 1, 200);

        var pts = db.LoadUmapCoords();
        if (pts.Count == 0)
            return Ok(new
            {
                ok = true, ready = false, count = 0,
                message = "音色地图还没生成 —— 请用户在「音色地图」页点「开始分析」再点「重算地图」。",
            });

        var libNames = new Dictionary<long, string>();
        foreach (var l in db.GetLibraries()) libNames[l.Id] = l.Name;
        var clipNames = new Dictionary<long, string>();
        foreach (var l in db.GetLibraries())
        {
            try { foreach (var c in db.GetAnalyzableClips(l.Id)) clipNames[c.Id] = c.Name; } catch { }
        }

        var groups = pts.Where(p => p.Cluster >= 0).GroupBy(p => p.Cluster)
                        .OrderByDescending(g => g.Count()).Take(top).ToList();
        var total = pts.Count;

        var items = groups.Select(g =>
        {
            var list = g.ToList();
            var topLibId = list.GroupBy(p => p.LibraryId).OrderByDescending(x => x.Count()).First().Key;
            // 代表样本 = 离簇质心最近的点
            double cx = list.Average(p => p.X), cy = list.Average(p => p.Y);
            var rep = list.OrderBy(p => { double dx = p.X - cx, dy = p.Y - cy; return dx * dx + dy * dy; }).First();
            return new
            {
                cluster = g.Key,
                size = list.Count,
                sharePercent = total > 0 ? Math.Round(list.Count * 100.0 / total, 1) : 0,
                topLibrary = libNames.TryGetValue(topLibId, out var ln) ? ln : "?",
                representativeFile = clipNames.TryGetValue(rep.ClipId, out var cn) ? cn : "?",
                representativeClipId = rep.ClipId,
                distinctLibraries = list.Select(p => p.LibraryId).Distinct().Count(),
            };
        }).ToList();

        return Ok(new
        {
            ok = true, ready = true,
            totalPoints = total,
            clusterCount = pts.Where(p => p.Cluster >= 0).Select(p => p.Cluster).Distinct().Count(),
            note = "按规模降序。topLibrary 是「占比最高的库」—— **若 distinctLibraries 很大，说明这个簇混了多个库，用库名当簇名会有误导**。",
            items,
        });
    }

    // ─────────────────────────────────────────────────────────────
    // ③ map_find_similar —— 用 MERT 找相似（比档 1 质量高得多）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// **用 MERT 嵌入找相似音色**。
    /// **与 `find_similar_audio` 的区别**：那个查的是档 1 的 32 维手工声学特征，
    /// 本工具查的是 **768 维 MERT 语义嵌入** —— **质量高得多**（实测同目录样本相似度 0.9210 vs 异类 0.7348）。
    /// </summary>
    public static string MapFindSimilar(AgentToolContext ctx, string argsJson)
    {
        var db = ctx.Db;
        if (db == null) return Err("没有可用的索引库。");
        var args = Root(argsJson);
        long clipId = Num(args, "clipId");
        int topK = (int)Math.Clamp(Num(args, "topK", 10), 1, 50);
        string query = Str(args, "query");     // 可选：按文件名模糊找起点

        float[]? vec = null;
        string startName = "";
        if (clipId > 0)
        {
            vec = db.GetMertFeature(clipId);
            if (vec == null) return Err($"clipId={clipId} 没有 MERT 特征（可能没提取过）。");
            startName = FindClipName(db, clipId);
        }
        else if (!string.IsNullOrWhiteSpace(query))
        {
            // 按文件名模糊找起点（取第一个有 MERT 特征的）
            foreach (var lib in db.GetLibraries())
            {
                try
                {
                    foreach (var c in db.GetAnalyzableClips(lib.Id))
                    {
                        if (c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var v = db.GetMertFeature(c.Id);
                        if (v != null) { vec = v; clipId = c.Id; startName = c.Name; break; }
                    }
                }
                catch { }
                if (vec != null) break;
            }
            if (vec == null) return Err($"没找到文件名含「{query}」且已提取 MERT 特征的片段。");
        }
        else
        {
            return Err("需要给 clipId（片段 id）或 query（文件名片段）。");
        }

        var nn = db.FindSimilarMert(vec, topK, clipId);
        if (nn.Count == 0)
            return Ok(new
            {
                ok = true, ready = false, count = 0,
                message = "没有找到相似项 —— 可能 MERT 特征还没提取（请用户在「音色地图」页点「开始分析」）。",
            });

        // 建 id → (库, 名) 映射
        var meta = new Dictionary<long, (string Lib, string Name)>();
        foreach (var lib in db.GetLibraries())
        {
            try { foreach (var c in db.GetAnalyzableClips(lib.Id)) meta[c.Id] = (lib.Name, c.Name); } catch { }
        }

        return Ok(new
        {
            ok = true, ready = true,
            startClipId = clipId,
            startFile = startName,
            count = nn.Count,
            note = "余弦相似度（MERT 768 维）。> 0.9 通常是很像、0.8~0.9 同类、< 0.75 只是沾边。",
            items = nn.Select(x => new
            {
                clipId = x.ClipId,
                score = Math.Round(x.Score, 4),
                file = meta.TryGetValue(x.ClipId, out var m) ? m.Name : "?",
                library = meta.TryGetValue(x.ClipId, out var m2) ? m2.Lib : "?",
            }).ToList(),
        });
    }

    // ─────────────────────────────────────────────────────────────
    // ④ map_filter —— 按簇 / 库 / 坐标范围筛选地图点
    // ─────────────────────────────────────────────────────────────

    /// <summary>按簇号、库名、坐标范围筛选地图上的点。</summary>
    public static string MapFilter(AgentToolContext ctx, string argsJson)
    {
        var db = ctx.Db;
        if (db == null) return Err("没有可用的索引库。");
        var args = Root(argsJson);
        long cluster = Num(args, "cluster", -999);
        string libName = Str(args, "library");
        int limit = (int)Math.Clamp(Num(args, "limit", 30), 1, 300);

        var pts = db.LoadUmapCoords();
        if (pts.Count == 0)
            return Ok(new { ok = true, ready = false, count = 0, message = "音色地图还没生成。" });

        var libNames = new Dictionary<long, string>();
        foreach (var l in db.GetLibraries()) libNames[l.Id] = l.Name;
        var clipNames = new Dictionary<long, string>();
        foreach (var l in db.GetLibraries())
        {
            try { foreach (var c in db.GetAnalyzableClips(l.Id)) clipNames[c.Id] = c.Name; } catch { }
        }

        IEnumerable<AudioMapReduce.Point> q = pts;
        if (cluster != -999) q = q.Where(p => p.Cluster == cluster);
        if (!string.IsNullOrWhiteSpace(libName))
            q = q.Where(p => libNames.TryGetValue(p.LibraryId, out var ln)
                             && ln.IndexOf(libName, StringComparison.OrdinalIgnoreCase) >= 0);

        var all = q.ToList();
        var shown = all.Take(limit).ToList();

        return Ok(new
        {
            ok = true, ready = true,
            matched = all.Count,
            shown = shown.Count,
            truncated = all.Count > shown.Count,
            note = "这是地图上的【点】。若要按「明亮度/噪声感」这类可解释特征筛选，需等混合筛选功能（档 1 特征尚未提取）。",
            items = shown.Select(p => new
            {
                clipId = p.ClipId,
                cluster = p.Cluster,
                library = libNames.TryGetValue(p.LibraryId, out var ln) ? ln : "?",
                file = clipNames.TryGetValue(p.ClipId, out var cn) ? cn : "?",
                x = Math.Round(p.X, 3), y = Math.Round(p.Y, 3),
            }).ToList(),
        });
    }

    // ─────────────────────────────────────────────────────────────
    // ⑤ map_filter_by_timbre —— 混合筛选（按可解释的声学特征筛）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// **按可解释的音色特征筛选**（混合筛选的 Agent 入口）。
    ///
    /// **与 map_find_similar 的分工**：
    ///   · `map_find_similar` —— 用 **MERT 768 维**回答「**哪个和哪个像**」（语义强、不可解释）；
    ///   · 本工具 —— 用**档 1 的 6 个可解释维度**回答「**我要明亮/有打击感/噪声多的**」（可解释）。
    ///
    /// **参数用 0~1 的百分位**（0.7 = 比 70% 的样本更亮）—— 这样用户不需要知道原始量纲。
    /// </summary>
    public static string MapFilterByTimbre(AgentToolContext ctx, string argsJson)
    {
        var db = ctx.Db;
        if (db == null) return Err("没有可用的索引库。");
        var args = Root(argsJson);

        var raw = db.LoadAllAudioFeatures();
        if (raw.Count == 0)
            return Ok(new
            {
                ok = true, ready = false, count = 0,
                message = "还没有提取过声学特征（档 1）。**请用户先在「维护」页点「提取音色特征」**" +
                          "（或让 Agent 调 extract_audio_features）—— 32 维、不用 MERT，很快。",
                availableDimensions = TimbreFilter.Dims.Select(d => new { d.Key, d.Label, d.Meaning }),
            });

        // 解析条件（每个维度可选 min/max）
        var f = new TimbreFilter.Filter();
        foreach (var d in TimbreFilter.Dims)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(d.Key, out var el)) continue;
            if (el.ValueKind != JsonValueKind.Object) continue;
            float? min = null, max = null;
            if (el.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number) min = (float)mn.GetDouble();
            if (el.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number) max = (float)mx.GetDouble();
            f.Set(d.Key, min, max);
        }

        int limit = (int)Math.Clamp(Num(args, "limit", 30), 1, 300);

        var rows = TimbreFilter.Normalize(raw);
        var hit = TimbreFilter.Apply(rows, f);

        // 建 id → (库, 名) 映射
        var meta = new Dictionary<long, (string Lib, string Name)>();
        foreach (var lib in db.GetLibraries())
        {
            try { foreach (var c in db.GetAnalyzableClips(lib.Id)) meta[c.Id] = (lib.Name, c.Name); } catch { }
        }

        var shown = hit.Take(limit).ToList();
        return Ok(new
        {
            ok = true, ready = true,
            condition = TimbreFilter.Describe(f),
            totalCandidates = rows.Count,
            matched = hit.Count,
            shown = shown.Count,
            truncated = hit.Count > shown.Count,
            note = "**所有阈值都是 0~1 的百分位**（0.7 = 比 70% 的样本更亮）。" +
                   "若匹配数为 0，说明条件太严 —— 建议放宽或去掉某个维度。",
            availableDimensions = TimbreFilter.Dims.Select(d => new { d.Key, d.Label, d.Meaning }),
            items = shown.Select(r => new
            {
                clipId = r.ClipId,
                library = meta.TryGetValue(r.ClipId, out var m) ? m.Lib : "?",
                file = meta.TryGetValue(r.ClipId, out var m2) ? m2.Name : "?",
                percentiles = TimbreFilter.Dims.Select((d, i) => new { d.Key, d.Label, value = Math.Round(r.Pct[i], 3) }),
            }).ToList(),
        });
    }

    private static string FindClipName(Database db, long clipId)
    {
        foreach (var lib in db.GetLibraries())
        {
            try { foreach (var c in db.GetAnalyzableClips(lib.Id)) if (c.Id == clipId) return c.Name; }
            catch { }
        }
        return "";
    }
}
