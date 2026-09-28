using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KontaktLibManager.Core;

/// <summary>
/// **档 1 声学特征的后台提取任务**（32 维可解释特征）。
///
/// **🔴 为什么需要它**：原实现**在 RPC handler 里直接跑双重循环**，
/// **阻塞 UI 线程** —— 实测表现为「点击按钮后界面无响应一段时间」（用户反馈）。
///
/// **与 <see cref="AudioMapTask"/> 的关系**：
///   · 本类跑的是**档 1**（32 维手工声学特征，**不用模型、快**）；
///   · `AudioMapTask` 跑的是**档 2**（MERT 768 维，要跑模型、慢）。
///   **两者共用同一套「可暂停 / 可续跑 / 增量落盘」的机制**（复用已踩过的教训）。
///
/// **📌 增量落盘**：每提取完一个就存库 ⇒ **任何时刻中断都不丢进度**；
/// **📌 续跑**：启动时读已有的 clip_id 集合跳过它们。
/// </summary>
public static class AudioFeatureTask
{
    public sealed class Status
    {
        public bool Running { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.MinValue;
        public string Message { get; set; } = "";

        public string ElapsedText => StartedAt == DateTime.MinValue ? "" :
            (DateTime.Now - StartedAt).TotalHours >= 1
                ? $"{(DateTime.Now - StartedAt).TotalHours:F1} 小时"
                : $"{(int)(DateTime.Now - StartedAt).TotalMinutes} 分 {(DateTime.Now - StartedAt).Seconds} 秒";

        public string EtaText
        {
            get
            {
                if (!Running || Done <= 0 || Total <= 0 || Done >= Total) return "";
                var per = (DateTime.Now - StartedAt).TotalSeconds / Done;
                var remain = (Total - Done) * per;
                return remain >= 3600 ? $"{remain / 3600:F1} 小时" : $"{(int)(remain / 60)} 分钟";
            }
        }
    }

    private static readonly object _lock = new();
    private static Status _status = new();
    private static volatile bool _cancel;
    private static readonly object _dbLock = new();   // **SQLite 写入串行化**（多线程写会 database is locked）

    public static Status Snapshot()
    {
        lock (_lock)
        {
            return new Status
            {
                Running = _status.Running, Done = _status.Done, Total = _status.Total,
                Failed = _status.Failed, Skipped = _status.Skipped,
                StartedAt = _status.StartedAt, Message = _status.Message,
            };
        }
    }

    public static void RequestCancel() { _cancel = true; }
    public static bool IsRunning { get { lock (_lock) return _status.Running; } }

    /// <summary>进度回调：`(已完成, 总数, 当前库名, 是否刚结束)`。**调用方负责节流**。</summary>
    public delegate void ProgressHandler(int done, int total, string current, bool finished);

    /// <summary>
    /// **启动/继续后台提取档 1 特征**。
    /// </summary>
    /// <param name="libId">0 = 全部库；&gt;0 = 只做这个库。</param>
    /// <param name="maxClips">每库上限；&lt;=0 = 不限。</param>
    /// <param name="threads">并行线程数；&lt;=0 = 自动（留 2 个核给系统）。**实测 20 核可拿 4–8 倍**。</param>
    /// <param name="force">true = **重新提取（覆盖）** —— 忽略「已提取」集合，全部重跑。
    /// **用途**：特征提取逻辑改过（如量纲修正）后，旧数据是坏的、必须重提。
    /// **⚠️ 不删旧行** —— 靠 SaveAudioFeatures 按 clip_id 覆盖，这样中途中断也不丢数据。</param>
    public static void Start(Database db, long libId, int maxClips, int threads, ProgressHandler onProgress, bool force = false)
    {
        lock (_lock)
        {
            if (_status.Running) return;
            // 🔴 **任务互斥**：MERT 任务与声学特征提取【同时只能跑一个】。
            //   理由：两者都吃 CPU/IO（MERT 还可能吃 GPU），并发会把机器压满、且进度互相干扰。
            if (AudioMapTask.IsRunning)
            {
                _status.Running = false;
                _status.Message = "MERT 任务（音色地图）正在运行 —— 两个任务不能同时跑，请等它结束或先暂停它。";
                return;
            }
            _cancel = false;
            _status = new Status
            {
                Running = true, StartedAt = DateTime.Now,
                Message = "正在提取声学特征（32 维）…",
            };
        }

        int threadsArg = threads;
        Task.Run(() =>
        {
            try
            {
                db.EnsureAudioFeaturesTable();

                // ① 收集清单（**必须用 GetAnalyzableClips** —— GetAudioClips 带随机 + 每库上限，
                //    拿来做批量提取会导致「每次清单都不同、续跑永远完不成」，已在音色地图那边踩过）
                var jobs = new List<(long ClipId, long LibId, string Full, string Ext, string LibName)>();
                var libs = db.GetLibraries().ToList();
                if (libId > 0) libs = libs.Where(l => l.Id == libId).ToList();

                foreach (var lib in libs)
                {
                    List<AudioClip> clips;
                    try { clips = db.GetAnalyzableClips(lib.Id); } catch { continue; }
                    int taken = 0;
                    foreach (var c in clips)
                    {
                        // **用 CanAnalyze（含 .ncw）**；.nkx 是容器，档 1 不做解包
                        if (!AudioFeatures.CanAnalyze(c.Ext) ||
                            c.Ext.Equals("nkx", StringComparison.OrdinalIgnoreCase)) continue;
                        if (maxClips > 0 && taken >= maxClips) break;
                        jobs.Add((c.Id, lib.Id, Path.Combine(lib.Path, c.RelPath), c.Ext, lib.Name));
                        taken++;
                    }
                }

                // ② 续跑：跳过已提取的
                // **force=true 时忽略「已提取」集合** —— 全部重跑（用于特征逻辑改版后重提）。
                // 注：不删旧行，靠 SaveAudioFeatures 按 clip_id 覆盖 ⇒ 中途中断也不丢数据。
                var doneSet = force ? new HashSet<long>() : db.AudioFeatureClipIds();
                var todo = jobs.Where(j => !doneSet.Contains(j.ClipId)).ToList();

                lock (_lock)
                {
                    _status.Total = jobs.Count;
                    _status.Done = jobs.Count - todo.Count;
                    _status.Message = todo.Count == 0
                        ? "所有可分析片段都已提取完成。"
                        : $"待提取 {todo.Count} 个（已跳过已完成的 {jobs.Count - todo.Count} 个）…";
                }
                onProgress(_status.Done, _status.Total, "", false);

                if (todo.Count == 0)
                {
                    lock (_lock) { _status.Running = false; _status.Message = "提取已完成。"; }
                    onProgress(_status.Done, _status.Total, "", true);
                    return;
                }

                // ③ **并行提取**（每完成一个立刻落盘）
                //   **为什么能并行**：AudioFeatures.Extract 是【纯 CPU 计算、无共享状态】，
                //   实测单核 90 ms/个；20 核可拿 4–8 倍（受 IO 与内存带宽限制，不会线性）。
                //   **SQLite 写入必须加锁** —— 多线程同时写会 "database is locked"。
                int threads = threadsArg;
                if (threads <= 0)
                {
                    int cores = Environment.ProcessorCount;
                    threads = Math.Max(1, cores - 2);          // **默认留 2 个核给系统**
                }
                threads = Math.Min(threads, Math.Max(1, todo.Count));

                var po = new ParallelOptions { MaxDegreeOfParallelism = threads };
                int doneCount = 0;
                try
                {
                    Parallel.ForEach(todo, po, (j, state) =>
                    {
                        if (_cancel) { state.Stop(); return; }
                        bool ok = false;
                        try
                        {
                            // 🔴 **必须用 AudioFeatures.ExtractAny（32 维）** —— 不能再用 MertFeatures.ExtractAny，
                            // 那个跑的是 MERT 模型（768 维）⇒ 又慢又存错维度（实测污染过 4,011 行）。
                            var v = AudioFeatures.ExtractAny(j.Full, j.Ext);
                            if (v != null)
                            {
                                lock (_dbLock) { db.SaveAudioFeatures(j.ClipId, j.LibId, v); }   // **写入串行化**
                                ok = true;
                            }
                        }
                        catch { }

                        int cur;
                        lock (_lock) { _status.Done++; if (!ok) _status.Failed++; cur = _status.Done; }
                        int c2 = Interlocked.Increment(ref doneCount);
                        if (c2 % 5 == 0) onProgress(cur, _status.Total, j.LibName, false);
                    });
                }
                catch (OperationCanceledException) { }

                bool cancelled = _cancel;
                lock (_lock)
                {
                    _status.Running = false;
                    _status.Message = cancelled
                        ? $"已暂停：{_status.Done} / {_status.Total}（已完成的都已保存，再点一次接着跑）"
                        : $"提取完成：{_status.Done} / {_status.Total}（失败 {_status.Failed} 个）";
                }
                onProgress(_status.Done, _status.Total, "", true);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _status.Running = false;
                    _status.Message = "提取失败：" + ex.Message;
                }
                onProgress(_status.Done, _status.Total, "", true);
            }
        });
    }
}
