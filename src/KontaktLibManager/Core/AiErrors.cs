using System;

namespace KontaktLibManager.Core;

/// <summary>
/// **AI 调用失败的分类与解读**。
///
/// **为什么需要它**（用户反馈）：之前失败时只抛一句 `InvalidOperationException` 的中文消息，
/// 前端要么只弹个 toast（几秒就没了），要么**静默失败**（什么都不显示）——
/// 用户根本不知道是断网、超时、限额、还是 Key 失效。
///
/// 这里把失败归纳成**稳定错误码**，并为每个码配**中文解读 + 该怎么办**，
/// 前端据此渲染成卡片（而不是一闪而过的提示）。
/// </summary>
public static class AiErrors
{
    // ── 错误码（前端按码做展示与统计，不要依赖消息文本）──
    public const string Network = "AI_NETWORK";        // 连不上
    public const string Timeout = "AI_TIMEOUT";        // 超时
    public const string RateLimit = "AI_RATE_LIMIT";   // 429
    public const string Auth = "AI_AUTH";              // 401/403
    public const string NotFound = "AI_NOT_FOUND";     // 404
    public const string BadRequest = "AI_BAD_REQUEST"; // 400
    public const string ContextOverflow = "AI_CONTEXT_OVERFLOW"; // 上下文超限
    public const string Server = "AI_SERVER";          // 5xx
    public const string Empty = "AI_EMPTY";            // 空响应
    public const string Cancelled = "AI_CANCELLED";    // 主动取消
    public const string Tool = "AI_TOOL";              // 工具执行失败
    public const string Unknown = "AI_UNKNOWN";

    /// <summary>由 HTTP 状态码映射到错误码。</summary>
    public static string FromHttpStatus(int code) => code switch
    {
        400 => BadRequest,
        401 or 403 => Auth,
        404 => NotFound,
        408 => Timeout,
        413 => ContextOverflow,
        429 => RateLimit,
        >= 500 => Server,
        _ => Unknown,
    };

    /// <summary>错误码 → 中文解读（是什么）。</summary>
    public static string Meaning(string code) => code switch
    {
        Network => "连不上 API 端点（DNS 解析失败、网络不通、或被代理拦截）",
        Timeout => "请求超时 —— 端点长时间没有响应",
        RateLimit => "被端点限流（HTTP 429）—— 短时间内请求过多",
        Auth => "鉴权失败（HTTP 401/403）—— API Key 无效、过期或没有该模型的权限",
        NotFound => "端点不存在（HTTP 404）—— API 地址写错了",
        BadRequest => "请求被端点拒绝（HTTP 400）—— 通常是参数不合法",
        ContextOverflow => "上下文超出模型窗口（HTTP 413）—— 发给模型的资料太多了",
        Server => "端点服务端故障（HTTP 5xx）",
        Empty => "端点返回了空响应 —— 既没有正文、也没有思考、也没有工具调用",
        Cancelled => "请求已被取消",
        Tool => "工具执行失败",
        _ => "未归类的错误",
    };

    /// <summary>错误码 → 该怎么办（建议）。</summary>
    public static string Advice(string code) => code switch
    {
        Network => "检查网络连接与代理设置；到「设置 → AI」点「测试连接」确认端点可达。",
        Timeout => "推理模型本身较慢，可稍后重试；或把「思考强度」降到 低/Auto；也可能是端点过载。",
        RateLimit => "等 10~60 秒再试；若频繁出现，说明该 Key 的配额已接近上限，考虑换 Key 或降频。",
        Auth => "到「设置 → AI」重新填写 API Key，并确认该 Key 有你要用的模型权限。",
        NotFound => "到「设置 → AI」核对 API 地址（注意是否少了或多了 /v1）。",
        BadRequest => "多半是请求参数问题；若刚改过设置（上下文长度/思考强度/max_tokens），先恢复默认再试。",
        ContextOverflow => "到「设置 → AI」把「上下文长度」调小，或把「压缩阈值」调低，让历史更早被压缩。",
        Server => "这是端点侧的问题，等几分钟再试；若持续，换一个端点。",
        Empty => "可能是模型被安全策略拦截、或 max_tokens 太小导致思考吃光配额。可提高 max_tokens 或换个问法重试。",
        Cancelled => "如果这不是你主动取消的，说明流式连接中断了，重发一次即可。",
        Tool => "看上面那条工具调用的错误详情；必要时换一种工具或参数。",
        _ => "把这条错误连同错误码一起反馈，便于定位。",
    };

    /// <summary>把码 + 细节拼成给用户看的一段话。</summary>
    public static string Describe(string code, string detail)
    {
        string m = Meaning(code);
        string a = Advice(code);
        string d = string.IsNullOrWhiteSpace(detail) ? "" : $"\n\n原始信息：{detail}";
        return $"【{code}】{m}\n\n建议：{a}{d}";
    }
}

/// <summary>
/// **带错误码的 AI 异常** —— 让上层能按码分类处理，而不是只能读消息文本。
/// </summary>
public sealed class AiException : Exception
{
    public string Code { get; }
    public string Detail { get; }

    public AiException(string code, string detail, Exception? inner = null)
        : base($"{code}: {detail}", inner)
    {
        Code = code;
        Detail = detail;
    }

    /// <summary>给用户看的完整说明（码 + 解读 + 建议）。</summary>
    public string UserMessage => AiErrors.Describe(Code, Detail);
}
