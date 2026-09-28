using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UMAP;

namespace KontaktLibManager.Core;

/// <summary>
/// **音色地图的降维与聚类**（调研报告「远期」第 8 项：UMAP 音色图谱 + HDBSCAN 自动标签）。
///
/// **架构原则（用户明确要求）**：**UMAP 是【全局降维】—— 必须拿到【全部】特征才能算**，
/// 所以**只能在离线/后台批量算好、落盘**；**Agent 查询时只读坐标（毫秒级）**，
/// **绝不能在查询时实时跑**（那样根本算不出来）。
///
/// **实现要点**：
///   · UMAP 包（`UMAP` v1.0.62830，**纯 C#、MIT、支持 net9.0**）的用法是
///     `InitializeFit()` 返回轮数 → **逐轮 `Step()`** → `GetEmbedding()`；
///     **逐轮调用的好处**：可以**每轮报进度**、**中途取消**（长任务不卡 UI 的关键）。
///   · 我们的向量已 **L2 归一化** ⇒ 用 `CosineForNormalizedVectors`（**比通用余弦快**）。
///   · 距离度量选择会显著影响地图形态：**余弦**适合「音色相似」，**欧氏**适合「数值接近」。
/// </summary>
public static class AudioMapReduce
{
    /// <summary>
    /// **自带的随机数发生器**（实现 UMAP 的 IProvideRandomValues）。
    /// **为什么不用包里的 DefaultRandomGenerator**：它没有公开的无参构造函数（实测编译不过）。
    /// 自己实现一个确定性 PRNG（固定种子）反而更好 —— **地图每次重算结果稳定，便于对比**。
    /// </summary>
    private sealed class SimpleRng : IProvideRandomValues
    {
        private uint _s = 0x9E3779B9;
        /// <summary>单线程使用（我们的降维在单线程里跑）—— 如实声明为 false。</summary>
        public bool IsThreadSafe => false;
        public int Next(int minValue, int maxValue)
        {
            if (maxValue <= minValue) return minValue;
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return minValue + (int)(_s % (uint)(maxValue - minValue));
        }
        public float NextFloat()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return (_s & 0xFFFFFF) / (float)0x1000000;
        }
        public void NextFloats(Span<float> buffer)
        {
            for (int i = 0; i < buffer.Length; i++) buffer[i] = NextFloat();
        }
    }

    /// <summary>降维结果：每个 clip 一个 2D 坐标 + 所属簇。</summary>
    public sealed class Point
    {
        public long ClipId { get; set; }
        public long LibraryId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public int Cluster { get; set; } = -1;   // -1 = 未聚类
    }

    /// <summary>降维进度回调：`(已完成轮数, 总轮数)`。</summary>
    public delegate void EpochHandler(int epoch, int total);

    /// <summary>
    /// **跑 UMAP 把 N 个 768 维向量降到 2D**。
    /// **同步阻塞** —— 调用方必须放在后台线程（`Task.Run`）。
    /// </summary>
    /// <param name="vectors">已 L2 归一化的特征向量（同一维度）。</param>
    /// <param name="neighbors">近邻数；越大越看重全局结构（默认 15）。</param>
    /// <param name="epochs">自定义轮数；null = 由包按数据量自动决定。</param>
    /// <param name="onEpoch">每轮回调（可用来报进度）。</param>
    /// <param name="isCancelled">返回 true 时中止（**已算出的坐标不可用，调用方应丢弃**）。</param>
    /// <param name="metric">距离度量：\"cosine\"（默认，适合音色相似）或 \"euclidean\"。</param>
    public static float[][]? Reduce(
        float[][] vectors, int neighbors = 15, int? epochs = null,
        EpochHandler? onEpoch = null, Func<bool>? isCancelled = null, string metric = "cosine")
    {
        if (vectors.Length < 3) return null;      // 少于 3 个点没意义

        // 维度必须一致
        int dim = vectors[0].Length;
        if (vectors.Any(v => v.Length != dim)) return null;

        // 邻居数不能超过点数
        int k = Math.Max(2, Math.Min(neighbors, vectors.Length - 1));

        // 距离度量可选：**余弦**适合「音色相似」（我们的向量已 L2 归一化，用专版更快）；
        // **欧氏**适合「数值接近」，地图形态会明显不同 —— 让用户能对比。
        // 注：DistanceCalculation 是 UMAP 命名空间下的【顶层委托】（不是 Umap 的嵌套类型，实测 CS0426）
        DistanceCalculation dist = string.Equals(metric, "euclidean", StringComparison.OrdinalIgnoreCase)
            ? Umap.DistanceFunctions.Euclidean
            : Umap.DistanceFunctions.CosineForNormalizedVectors;
        var umap = new Umap(
            dist,
            new SimpleRng(),
            dimensions: 2,
            numberOfNeighbors: k,
            customNumberOfEpochs: epochs,
            progressReporter: null);

        int total = umap.InitializeFit(vectors);
        if (total <= 0) return null;

        for (int i = 0; i < total; i++)
        {
            if (isCancelled != null && isCancelled()) return null;   // 取消 → 丢弃结果
            umap.Step();
            onEpoch?.Invoke(i + 1, total);
        }
        return umap.GetEmbedding();
    }

    /// <summary>
    /// **HDBSCAN 聚类**（正式方案，替换临时的网格聚类）。
    ///
    /// **为什么比网格聚类好**：
    ///   · **能识别噪声点** —— 稀疏离群点标为 `-1`，而不是被硬塞进某个簇；
    ///   · **簇大小自适应** —— 不需要预设簇数（网格聚类要靠 `targetPerCell` 这个魔法数）；
    ///   · **基于密度连通性** —— 更符合「音色聚在一起」的直觉。
    ///
    /// **⚠️ 但要说清预期**：**它不会把【连续渐变】的数据「变出」分离的簇** ——
    /// 音色是连续的这个事实不会变。预期是「**簇更干净、离群点被识别**」，
    /// 而不是「突然分出十几个漂亮的簇」。
    ///
    /// 用 2D 地图坐标做欧氏距离（地图本身已经是 2D 投影，标准做法）。
    /// </summary>
    /// <param name="points">地图点（就地写入 <c>Cluster</c>）。</param>
    /// <param name="minPoints">核心点的邻域最小点数（越大越保守、噪声越多）。</param>
    /// <param name="minClusterSize">簇的最小规模（小于它的会被当噪声）。</param>
    public static bool ClusterByHdbscan(List<Point> points, int minPoints = 5, int minClusterSize = 8)
    {
        if (points.Count < minClusterSize * 2) return false;   // 点太少，交给调用方回退
        try
        {
            // Run 是【静态】方法（实测：用实例引用会报 CS0176）
            var result = HdbscanSharp.Runner.HdbscanRunner.Run(
                points.Count,
                minPoints,
                minClusterSize,
                (int i, int j) =>
                {
                    // 2D 地图上的欧氏距离（委托只收两个索引，坐标从闭包里的 points 取）
                    double dx = points[i].X - points[j].X;
                    double dy = points[i].Y - points[j].Y;
                    return Math.Sqrt(dx * dx + dy * dy);
                },
                null);
            var labels = result?.Labels;
            if (labels == null || labels.Length != points.Count) return false;

            // 🔴 **标签约定修正（2026-09-24，重要）** ——
            //   实测 `HdbscanSharp` 用 **`0` 表示噪声**、簇号从 `1` 开始（最大到 3000）；
            //   它【不】使用惯例的 `-1`。
            //   而下游（`ClusterAuto` 统计、地图着色、簇图例）都按「`< 0` 是噪声」判断 ⇒
            //   **噪声被当成一个正常簇** ⇒ 实测出现「一个簇聚了 8,041 个采样（21.5%）」，
            //   且该簇里是毫无关系的音色（世界打击乐 + 弦乐合奏 + 音效）——
            //   因为**噪声点本来就是「无法归类」的离群点**。
            //   ⇒ **这里把 `0` 统一重映射为 `-1`**，下游全部自动正确。
            for (int i = 0; i < points.Count; i++)
                points[i].Cluster = labels[i] == 0 ? -1 : labels[i];
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// **HDBSCAN，失败则回退网格聚类** —— 保证「永远有簇可看」，不会因为算法异常导致地图空白。
    /// </summary>
    public static string ClusterAuto(List<Point> points, int targetPerCell = 5)
    {
        if (ClusterByHdbscan(points))
        {
            int clusters = points.Where(p => p.Cluster >= 0).Select(p => p.Cluster).Distinct().Count();
            int noise = points.Count(p => p.Cluster < 0);
            return $"HDBSCAN：{clusters} 个簇、{noise} 个噪声点";
        }
        ClusterByGrid(points, targetPerCell);
        return $"网格聚类（HDBSCAN 回退）：{points.Select(p => p.Cluster).Distinct().Count()} 个簇";
    }

    /// <summary>
    /// **极简的网格聚类**（HDBSCAN 的轻量替代）。
    ///
    /// **为什么先用它**：HDBSCAN 需要额外引入 `HdbscanSharp` 包；
    /// 而「把 2D 散点按密度切成若干簇」用**自适应网格**就能得到一个**够用的初版**，
    /// 让地图先能按簇着色、看清结构。**后续若发现簇划分不够好，再换 HDBSCAN。**
    ///
    /// 做法：按点密度自适应决定网格边长（目标每格 ~N 个点），
    /// 再把**相邻的非空格**并成一个簇（四邻域连通）。
    /// </summary>
    public static void ClusterByGrid(List<Point> points, int targetPerCell = 12)
    {
        if (points.Count < 3) return;
        float minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        float minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        float w = Math.Max(1e-6f, maxX - minX), h = Math.Max(1e-6f, maxY - minY);

        // 网格数 ≈ sqrt(N / targetPerCell)，至少 4×4
        int g = Math.Max(4, (int)Math.Sqrt(points.Count / (double)Math.Max(1, targetPerCell)));
        g = Math.Min(g, 120);                     // 上限，避免格太多导致碎片化
        float cw = w / g, ch = h / g;

        // 每个点落到哪个格
        var cellOf = new int[points.Count];
        var occupied = new HashSet<(int, int)>();
        for (int i = 0; i < points.Count; i++)
        {
            int cx = Math.Min(g - 1, Math.Max(0, (int)((points[i].X - minX) / cw)));
            int cy = Math.Min(g - 1, Math.Max(0, (int)((points[i].Y - minY) / ch)));
            cellOf[i] = cy * g + cx;
            occupied.Add((cx, cy));
        }

        // 四邻域连通 → 簇号
        var cellCluster = new Dictionary<(int, int), int>();
        int next = 0;
        foreach (var start in occupied)
        {
            if (cellCluster.ContainsKey(start)) continue;
            int id = next++;
            var stack = new Stack<(int, int)>();
            stack.Push(start);
            cellCluster[start] = id;
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                foreach (var nb in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    if (occupied.Contains(nb) && !cellCluster.ContainsKey(nb))
                    { cellCluster[nb] = id; stack.Push(nb); }
            }
        }

        for (int i = 0; i < points.Count; i++)
        {
            int cx = Math.Min(g - 1, Math.Max(0, (int)((points[i].X - minX) / cw)));
            int cy = Math.Min(g - 1, Math.Max(0, (int)((points[i].Y - minY) / ch)));
            points[i].Cluster = cellCluster.TryGetValue((cx, cy), out int c) ? c : -1;
        }
    }
}
