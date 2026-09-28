using System.Text.Encodings.Web;
using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>
/// 工具返回值的统一 JSON 序列化。
///
/// **为什么必须关掉默认转义**：<c>JsonSerializer</c> 默认把非 ASCII 转成 <c>\uXXXX</c>，
/// 一个中文字符会变成 6 个字符 —— 对 Agent 来说就是 token 暴涨约 6 倍。
/// 工具返回值里中文很多（库名、演奏法、记忆内容），必须用
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> 保留原字符。
/// </summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string S(object value) => JsonSerializer.Serialize(value, Opts);
}
