using System.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **音色地图的状态与任务**（调研报告「远期」第 7 项档 2 + 第 8 项）。
///
/// **架构原则（用户明确提出、也是必须的）**：
///   **绝不让 Agent 在查询时实时跑模型** —— 那样每次查询要几十分钟、UMAP 更是根本算不出来
///   （它是**全局降维**，必须拿到**全部**特征才能算）。
///   ⇒ 正确做法：**入库/接管时后台批量预计算，全部落 SQLite；Agent 查询时只查表（毫秒级）**。
///
/// **三个阶段**（顺序有依赖）：
///   ① **提取特征** —— 批量跑 MERT（768 维），**可暂停 / 可续跑**（复用知识库建库的增量持久化思路）
///   ② **降维** —— UMAP 到 2D（**必须等 ① 全部完成**）
///   ③ **聚类** —— HDBSCAN 出簇标签（依赖 ② 的坐标）
///
/// **进度推送**：走**回调 + 节流**（不是每个片段都推），避免把 UI 线程压死。
/// </summary>
public static class AudioMapTask
{
    /// <summary>任务阶段。</summary>
    public enum Stage { Idle, Extracting, Reducing, Clustering, Done, Failed }

    /// <summary>当前状态快照（供 RPC 返回给界面）。</summary>
    public sealed class Status
    {
        public Stage Stage { get; set; } = Stage.Idle;
        public bool Running { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public double Percent => Total > 0 ? Done * 100.0 / Total : 0;
        public DateTime StartedAt { get; set; } = DateTime.MinValue;
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";

        // 三个阶段各自的状态（界面用）
        public string ExtractState { get; set; } = "";
        public string UmapState { get; set; } = "";
        public string ClusterState { get; set; } = "";

        public int MapPointCount { get; set; }

        /// <summary>提取失败的片段数（**如实报告，不藏**）。</summary>
        public int Failed { get; set; }

        public string ElapsedText => StartedAt == DateTime.MinValue ? "" :
            (DateTime.Now - StartedAt).TotalHours >= 1
                ? $"{(DateTime.Now - StartedAt).TotalHours:F1} 小时"
                : $"{(int)(DateTime.Now - StartedAt).TotalMinutes} 分 {(DateTime.Now - StartedAt).Seconds} 秒";

        /// <summary>预计剩余（按当前速率线性外推）。</summary>
        public string EtaText
        {
            get
            {
                if (!Running || Done <= 0 || Total <= 0 || Done >= Total) return "";
                var elapsed = DateTime.Now - StartedAt;
                var perItem = elapsed.TotalSeconds / Done;
                var remain = (Total - Done) * perItem;
                return remain >= 3600 ? $"{remain / 3600:F1} 小时" : $"{(int)(remain / 60)} 分钟";
            }
        }
    }

    private static readonly object _lock = new();
    private static Status _status = new();
    private static volatile bool _cancel;
    private static readonly object _dbLock = new();   // **SQLite 写入串行化**（多线程写会 database is locked）

    /// <summary>读当前状态（线程安全快照）。</summary>
    public static Status Snapshot()
    {
        lock (_lock)
        {
            return new Status
            {
                Stage = _status.Stage, Running = _status.Running,
                Done = _status.Done, Total = _status.Total,
                StartedAt = _status.StartedAt, Message = _status.Message, Error = _status.Error,
                ExtractState = _status.ExtractState, UmapState = _status.UmapState,
                ClusterState = _status.ClusterState, MapPointCount = _status.MapPointCount, Failed = _status.Failed,
            };
        }
    }

    /// <summary>请求取消（暂停）。</summary>
    public static void RequestCancel() { _cancel = true; }

    /// <summary>进度回调：(已完成, 总数, 当前库名, 是否刚结束)。**调用方负责节流**。</summary>
    public delegate void ProgressHandler(int done, int total, string current, bool finished);

    private static Task? _task;

    /// <summary>
    /// **启动/继续后台提取 MERT 嵌入**（第 2 步核心）。
    /// <param name="useGpu">true = 用 GPU（DirectML）跑；false = CPU。</param>
    /// <param name="threads">CPU 模式的线程数（0 = 自动，留 2 核给系统）。**GPU 模式忽略此项。**</param>
    ///
    /// **三个关键设计**：
    ///   ① **可暂停** —— 调 RequestCancel 后，当前片段跑完就停，**已提取的都已落盘**；
    ///   ② **可续跑** —— 启动时先读 mert_features 表里已有的 clip_id 集合，**跳过它们**（不重复跑模型）；
    ///   ③ **增量持久化** —— **每提取完一个就立刻存库**，所以任何时刻中断都不丢已完成的进度
    ///      （这比「攒一批再存」更稳 —— 复用知识库建库踩过的教训）。
    ///
    /// **为什么用后台线程**：全量约 86 分钟，**绝不能占 UI 线程**（用户明确要求防卡死）。
    /// </summary>
    public static void StartExtraction(Database db, string? modelPath, ProgressHandler onProgress, bool useGpu = false, int threads = 0, bool force = false)
    {
        lock (_lock)
        {
            if (_status.Running) return;
            // 🔴 **任务互斥**：声学特征提取正在跑时不能启动 MERT 任务
            if (AudioFeatureTask.IsRunning) return;
            _cancel = false;
            _status = new Status
            {
                Stage = Stage.Extracting, Running = true, StartedAt = DateTime.Now,
                Message = "正在提取 MERT 音色嵌入…",
                ExtractState = "running", UmapState = "", ClusterState = "",
            };
        }

        _task = Task.Run(() =>
        {
            try
            {
                db.EnsureMertTable();

                // ① 收集待处理清单（只收可解码的）
                var jobs = new List<(long ClipId, long LibId, string Full, string LibName, string Ext, string RegKey)>();
                foreach (var lib in db.GetLibraries().ToList())
                {
                    List<AudioClip> clips;
                    try { clips = db.GetAnalyzableClips(lib.Id); } catch { continue; }
                    foreach (var c in clips)
                    {
                        // **一期：CanAnalyze = 原生可解码 + 需解码（.ncw）** —— 扩大可分析范围
                        if (!AudioFeatures.CanAnalyze(c.Ext)) continue;
                        jobs.Add((c.Id, lib.Id, Path.Combine(lib.Path, c.RelPath), lib.Name, c.Ext, lib.ProductKey ?? ""));
                    }
                }

                // ② 续跑：跳过已提取的
                var doneSet = db.MertDoneClipIds();
            // **force=true 时忽略「已提取」集合** —— 全部重跑（MERT 提取逻辑改版后重提）
            if (force) doneSet.Clear();
                var todo = jobs.Where(j => !doneSet.Contains(j.ClipId)).ToList();

                lock (_lock)
                {
                    _status.Total = jobs.Count;
                    _status.Done = jobs.Count - todo.Count;
                    _status.Message = todo.Count == 0
                        ? "所有可解码片段都已提取完成。"
                        : $"待提取 {todo.Count} 个（已跳过已完成的 {doneSet.Count} 个）…";
                }
                onProgress(Snapshot().Done, Snapshot().Total, "", false);

                if (todo.Count == 0)
                {
                    lock (_lock)
                    {
                        _status.Running = false; _status.Stage = Stage.Done;
                        _status.ExtractState = "done"; _status.Message = "提取已完成。";
                    }
                    onProgress(Snapshot().Done, Snapshot().Total, "", true);
                    return;
                }

                // ③ **多片段并行提取**（每完成一个立刻落盘）
                //   **为什么能并行**：ONNX Runtime 的 InferenceSession.Run() 是【线程安全】的，
                //   可被多个线程并发调用；且 MERT 每个片段是独立计算、无共享状态。
                //   **配套调整**：GetSession 里会把 IntraOpNumThreads 降到 1-2 ——
                //   否则 th 个片段 x th 个算子线程 = th^2 个线程抢 CPU（线程超订反而更慢）。
                //   **SQLite 写入必须加锁**（多线程写会 database is locked）。
                int parThreads = threads <= 0 ? Math.Max(1, Environment.ProcessorCount - 2) : threads;
                parThreads = Math.Min(parThreads, Math.Max(1, todo.Count));
                var po = new ParallelOptions { MaxDegreeOfParallelism = parThreads };
                int doneCnt = 0;
                try
                {
                    Parallel.ForEach(todo, po, (j, state) =>
                    {
                        if (_cancel) { state.Stop(); return; }
                        bool okOne = false;
                        try
                        {
                            float[]? v;
                            if (j.Ext.Equals("nkx", StringComparison.OrdinalIgnoreCase))
                            {
                                // **二期：.nkx 是容器** —— 按容器取代表采样聚合（不能每个采样重新解包）
                                var nr = NkxFeatures.Extract(j.Full, j.RegKey, NkxFeatures.DefaultSamplesPerContainer, modelPath, 10.0, useGpu, parThreads);
                                v = nr.Vec;
                            }
                            else
                            {
                                v = MertFeatures.ExtractAny(j.Full, j.Ext, 10.0, modelPath, useGpu, parThreads);
                            }
                            if (v != null)
                            {
                                lock (_dbLock) { db.SaveMertFeature(j.ClipId, j.LibId, v); }   // **写入串行化**
                                okOne = true;
                            }
                        }
                        catch { /* 单个失败不中断全局 */ }
                
                        lock (_lock) { _status.Done++; if (!okOne) _status.Failed++; }
                        int c2 = Interlocked.Increment(ref doneCnt);
                        if (c2 % 5 == 0) onProgress(Snapshot().Done, Snapshot().Total, j.LibName, false);
                    });
                }
                catch (OperationCanceledException) { }

                bool cancelled = _cancel;
                lock (_lock)
                {
                    _status.Running = false;
                    _status.Stage = cancelled ? Stage.Idle : Stage.Done;
                    _status.ExtractState = cancelled ? "" : "done";
                    _status.Message = cancelled
                        ? $"已暂停：{_status.Done} / {_status.Total}（已完成的都已保存，点「继续分析」接着跑）"
                        : $"提取完成：{_status.Done} / {_status.Total}";
                }
                onProgress(Snapshot().Done, Snapshot().Total, "", true);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _status.Running = false; _status.Stage = Stage.Failed;
                    _status.ExtractState = "fail"; _status.Error = ex.Message;
                    _status.Message = "提取失败：" + ex.Message;
                }
                onProgress(Snapshot().Done, Snapshot().Total, "", true);
            }
        });
    }

    /// <summary>
    /// **启动后台降维 + 聚类**（第 3 步核心）。
    ///
    /// **为什么必须后台**：UMAP 是【全局优化】，几千个点要跑很多轮，**绝不能占 UI 线程**。
    /// **可取消**：逐轮检查取消标志，**取消则丢弃结果**（半成品坐标没有意义）。
    /// </summary>
    public static void StartReduce(Database db, ProgressHandler onProgress, int neighbors = 15, string metric = "cosine")
    {
        lock (_lock)
        {
            if (_status.Running) return;
            if (AudioFeatureTask.IsRunning) return;   // 任务互斥
            _cancel = false;
            _status.Running = true; _status.Stage = Stage.Reducing;
            _status.StartedAt = DateTime.Now;
            _status.ExtractState = "done"; _status.UmapState = "running";
            _status.Message = "正在降维（UMAP）…";
        }

        Task.Run(() =>
        {
            try
            {
                // ① 载入全部 MERT 嵌入
                var all = db.LoadAllMertFeatures();
                if (all.Count < 3)
                {
                    lock (_lock) { _status.Running = false; _status.Stage = Stage.Failed; _status.UmapState = "fail"; }
                    _status.Message = $"特征太少（{all.Count} 个），无法降维。请先完成特征提取。";
                    onProgress(0, 0, "", true);
                    return;
                }

                // ② 维度必须一致（取众数维度，剔除异类）
                int dim = all.GroupBy(a => a.Vec.Length).OrderByDescending(g => g.Count()).First().Key;
                var use = all.Where(a => a.Vec.Length == dim).ToList();

                lock (_lock) { _status.Total = use.Count; _status.Done = 0; _status.Message = $"正在降维：{use.Count} 个点 × {dim} 维（n_neighbors={neighbors}, 度量={metric}）…"; }
                onProgress(0, use.Count, "", false);

                // ③ 跑 UMAP（逐轮报进度 + 可取消）
                var vecs = use.Select(a => a.Vec).ToArray();
                var coords = AudioMapReduce.Reduce(vecs, neighbors, null,
                    (ep, totalEp) =>
                    {
                        lock (_lock) { _status.Done = ep; }
                        if (ep % 5 == 0 || ep == totalEp) onProgress(ep, totalEp, $"UMAP 第 {ep}/{totalEp} 轮", false);
                    },
                    () => _cancel, metric);

                if (coords == null)
                {
                    lock (_lock) { _status.Running = false; _status.Stage = Stage.Idle; _status.UmapState = ""; }
                    _status.Message = _cancel ? "降维已取消。" : "降维失败（数据不足或维度不一致）。";
                    onProgress(0, 0, "", true);
                    return;
                }

                // ④ 组装点 + 聚类
                var pts = new List<AudioMapReduce.Point>();
                for (int i = 0; i < use.Count && i < coords.Length; i++)
                    pts.Add(new AudioMapReduce.Point
                    {
                        ClipId = use[i].ClipId, LibraryId = use[i].LibraryId,
                        X = coords[i][0], Y = coords[i][1],
                    });
                var cluInfo = AudioMapReduce.ClusterAuto(pts, 5);   // HDBSCAN，失败回退网格

                // ⑤ 落盘
                db.ReplaceUmapCoords(pts);

                int clusters = pts.Select(p => p.Cluster).Distinct().Count();
                lock (_lock)
                {
                    _status.Running = false; _status.Stage = Stage.Done;
                    _status.UmapState = "done"; _status.ClusterState = "done";
                    _status.MapPointCount = pts.Count;
                    _status.Message = $"地图已生成：{pts.Count} 个点 —— {cluInfo}";
                }
                onProgress(pts.Count, pts.Count, "", true);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _status.Running = false; _status.Stage = Stage.Failed;
                    _status.UmapState = "fail"; _status.Error = ex.Message;
                    _status.Message = "降维失败：" + ex.Message;
                }
                onProgress(0, 0, "", true);
            }
        });
    }

    /// <summary>
    /// **构建乐器级地图**（音色地图三期）。
    ///
    /// **零额外推理成本**：不重新跑 MERT，而是**聚合已有的采样级特征**（按乐器目录求均值）。
    /// 然后对这批量（比采样级少得多）跑一次 UMAP + 聚类。
    /// </summary>
    public static void StartInstrumentMap(Database db, ProgressHandler onProgress)
    {
        lock (_lock)
        {
            if (_status.Running) return;
            if (AudioFeatureTask.IsRunning) return;   // 任务互斥
            _cancel = false;
            _status.Running = true; _status.Stage = Stage.Reducing;
            _status.StartedAt = DateTime.Now;
            _status.UmapState = "running";
            _status.Message = "正在聚合乐器级特征…";
        }

        Task.Run(() =>
        {
            try
            {
                db.EnsureInstrumentMapTable();

                // ① 聚合（纯向量平均，不跑模型）
                var insts = InstrumentAggregate.Aggregate(db, (d, t2) =>
                {
                    lock (_lock) { _status.Done = d; _status.Total = t2; }
                    if (d % 20 == 0) onProgress(d, t2, "聚合乐器", false);
                }, () => _cancel);

                if (insts == null || insts.Count < 3)
                {
                    lock (_lock) { _status.Running = false; _status.Stage = Stage.Failed; _status.UmapState = "fail"; }
                    _status.Message = "乐器数太少，无法建图。请先完成采样级特征提取。";
                    onProgress(0, 0, "", true);
                    return;
                }

                // ② 对乐器向量跑 UMAP（比采样级少得多，很快）
                lock (_lock) { _status.Message = $"正在降维：{insts.Count} 个乐器…"; }
                var vecs = insts.Select(x => x.Vec).ToArray();
                var coords = AudioMapReduce.Reduce(vecs, 8, null,
                    (ep, totalEp) =>
                    {
                        lock (_lock) { _status.Done = ep; _status.Total = totalEp; }
                        if (ep % 5 == 0 || ep == totalEp) onProgress(ep, totalEp, "UMAP", false);
                    },
                    () => _cancel);

                if (coords == null)
                {
                    lock (_lock) { _status.Running = false; _status.Stage = Stage.Idle; _status.UmapState = ""; }
                    _status.Message = _cancel ? "已取消。" : "乐器级降维失败。";
                    onProgress(0, 0, "", true);
                    return;
                }

                // ③ 聚类 + 落盘
                var pts = new List<AudioMapReduce.Point>();
                for (int i = 0; i < insts.Count && i < coords.Length; i++)
                    pts.Add(new AudioMapReduce.Point { ClipId = i, LibraryId = insts[i].LibraryId, X = coords[i][0], Y = coords[i][1] });
                var cluInfo = AudioMapReduce.ClusterAuto(pts, 5);   // HDBSCAN，失败回退网格

                var rows = new List<(long, string, string, int, float, float, int)>();
                for (int i = 0; i < insts.Count && i < pts.Count; i++)
                    rows.Add((insts[i].LibraryId, insts[i].RelDir, insts[i].Name, insts[i].SampleCount, pts[i].X, pts[i].Y, pts[i].Cluster));
                db.ReplaceInstrumentMap(rows);

                int cl = rows.Select(r => r.Item7).Distinct().Count();
                lock (_lock)
                {
                    _status.Running = false; _status.Stage = Stage.Done;
                    _status.UmapState = "done"; _status.ClusterState = "done";
                    _status.Message = $"乐器级地图已生成：{rows.Count} 个乐器 —— {cluInfo}";
                }
                onProgress(rows.Count, rows.Count, "", true);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _status.Running = false; _status.Stage = Stage.Failed;
                    _status.UmapState = "fail"; _status.Error = ex.Message;
                    _status.Message = "乐器级建图失败：" + ex.Message;
                }
                onProgress(0, 0, "", true);
            }
        });
    }

    /// <summary>任务是否在跑。</summary>
    public static bool IsRunning { get { lock (_lock) return _status.Running; } }

    /// <summary>
    /// **统计可分析范围**（如实反映硬边界：Kontakt 专有格式解不了）。
    /// </summary>
    public static (int Total, int Decodable, int Extracted, string CoverageText) Survey(Database db)
    {
        int total = 0, decodable = 0;
        try
        {
            foreach (var lib in db.GetLibraries())
            {
                List<AudioClip> clips;
                try { clips = db.GetAnalyzableClips(lib.Id); } catch { continue; }
                foreach (var c in clips)
                {
                    total++;
                    if (AudioFeatures.CanAnalyze(c.Ext)) decodable++;   // 一期含 .ncw、二期含 .nkx
                }
            }
        }
        catch { }

        int extracted = 0;
        try { extracted = db.CountAudioFeatures(); } catch { }

        var text = decodable == 0
            ? "⚠ 没找到可分析的音频片段"
            : $"可分析 {decodable} / 采样到 {total} 个片段" +
              $"（已含 .ncw 解码 + .nkx 容器解包 + .ogg/.mp3/.aif 解码）" +
              (extracted > 0 ? $"　·　已提取特征 {extracted} 个" : "");

        return (decodable, decodable, extracted, text);
    }
}
