using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KontaktLibManager.Core;

/// <summary>模型请求调用工具（OpenAI function-calling 格式）。</summary>
public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>一次流式响应的最终结果。</summary>
public sealed class StreamResult
{
    public string Content { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public List<ToolCall> ToolCalls { get; set; } = new();
    public bool HasToolCalls => ToolCalls.Count > 0;
    /// <summary>提示词 token 数（来自 SSE 最后一个 chunk 的 usage）。</summary>
    public int PromptTokens { get; set; }
    /// <summary>补全 token 数（含思考）。</summary>
    public int CompletionTokens { get; set; }
    /// <summary>其中思考 token 数（completion_tokens_details.reasoning_tokens）。</summary>
    public int ReasoningTokens { get; set; }
    /// <summary>总 token 数。</summary>
    public int TotalTokens { get; set; }
}

/// <summary>流式对话 + 工具调用（OpenAI 兼容端点）。</summary>
public static class AiStream
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// 重试策略：官方端点高峰期可能较慢或偶发失败，因此失败就多试几次。
    /// 覆盖：429 限流、5xx、网络错误、以及「空响应」（既无正文也无思考也无工具调用）。
    /// 退避总时长约 150 秒。
    /// </summary>
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static DateTime _lastSendUtc = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.2);
    private static readonly Random Jitter = new();

    /// <summary>
    /// 重试策略：官方端点高峰期可能较慢或偶发失败，因此失败就多试几次。
    /// 覆盖：429 限流、5xx、网络错误、超时、以及「空响应」（既无正文也无思考也无工具调用）。
    /// 退避总时长约 150 秒。
    /// </summary>
    private static readonly int[] RetryDelaysMs = { 2000, 4000, 8000, 15000, 25000, 40000, 60000 };

    /// <summary>
    /// 流式对话。onDelta 收到正文增量，onReasoning 收到思考增量（推理模型）。
    /// tools 为 OpenAI 工具定义数组（可为 null）。
    /// </summary>
    public static async Task<StreamResult> ChatStreamAsync(
        AiSettings s, List<ChatMessage> messages, object[]? tools,
        Action<string>? onDelta = null, Action<string>? onReasoning = null,
        CancellationToken ct = default, int? maxTokens = null)
    {
        var payload = BuildPayload(s, messages, tools, maxTokens);
        string json = JsonSerializer.Serialize(payload);
        string url = s.BaseUrl.TrimEnd('/') + "/chat/completions";

        // 调试：KLM_AI_DEBUG=1 时把请求体落盘，便于比对端点期望的格式
        if (Environment.GetEnvironmentVariable("KLM_AI_DEBUG") == "1")
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.DataDir, "ai-request.json"), json);
            }
            catch { }
        }

        int[] delaysMs = RetryDelaysMs;
        Exception? last = null;

        for (int attempt = 0; attempt <= delaysMs.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + s.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

            HttpResponseMessage resp;
            await Throttle.WaitAsync(ct);
            try
            {
                var wait = MinInterval - (DateTime.UtcNow - _lastSendUtc);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                _lastSendUtc = DateTime.UtcNow;
            }
            catch (HttpRequestException ex)
            {
                Throttle.Release();
                last = new AiException(AiErrors.Network, ex.Message, ex);
                if (attempt < delaysMs.Length) { await DelayAsync(delaysMs[attempt], attempt, ct); continue; }
                throw last;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Throttle.Release();
                last = new AiException(AiErrors.Timeout, "端点长时间无响应");
                if (attempt < delaysMs.Length) { await DelayAsync(delaysMs[attempt], attempt, ct); continue; }
                throw last;
            }
            catch { Throttle.Release(); throw; }
            Throttle.Release();

            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code == 429 || code >= 500)
                {
                    string body = "";
                    try { body = await resp.Content.ReadAsStringAsync(ct); } catch { }
                    last = new AiException(AiErrors.FromHttpStatus(code),
                        code == 429 ? "端点限流（HTTP 429）" : $"HTTP {code}：{(body.Length > 200 ? body[..200] : body)}");
                    if (attempt < delaysMs.Length) { await DelayAsync(delaysMs[attempt], attempt, ct); continue; }
                    throw new AiException(((AiException)last).Code,
                        $"{last.Message}；已自动重试 {delaysMs.Length} 次仍失败，请稍后再试");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    throw new AiException(AiErrors.FromHttpStatus(code), $"HTTP {code}：{(body.Length > 400 ? body[..400] : body)}");
                }

                var streamed = await ReadSseAsync(resp, onDelta, onReasoning, ct);

                // 空响应（端点繁忙时会出现）也视为可重试失败
                bool empty = streamed.Content.Length == 0 && streamed.Reasoning.Length == 0 && !streamed.HasToolCalls;
                if (empty && attempt < delaysMs.Length)
                {
                    last = new AiException(AiErrors.Empty, "端点返回空响应（无正文/无思考/无工具调用）");
                    await DelayAsync(delaysMs[attempt], attempt, ct);
                    continue;
                }

                return streamed;
            }
        }
        throw last ?? new AiException(AiErrors.Unknown, "请求失败且无更多信息");
    }

    /// <summary>带抖动的退避等待（避免同时重试打爆端点）。</summary>
    private static async Task DelayAsync(int baseMs, int attempt, CancellationToken ct)
    {
        int jitter = Jitter.Next(0, 800);
        await Task.Delay(baseMs + jitter, ct);
    }

    private static object BuildPayload(AiSettings s, List<ChatMessage> messages, object[]? tools, int? maxTokens)
    {
        var msgs = messages.Select(BuildMessage).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = s.Model,
            ["messages"] = msgs,
            ["temperature"] = s.Temperature,
            ["max_tokens"] = maxTokens ?? (s.MaxTokens > 0 ? s.MaxTokens : (int?)null),
            ["stream"] = true,
        };
        // 思考强度：auto 表示不发送该字段（由模型自控）
        if (!string.IsNullOrEmpty(s.ThinkingEffort) && s.ThinkingEffort != "auto")
            payload["reasoning_effort"] = s.ThinkingEffort;
        if (tools is { Length: > 0 })
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }
        return payload;
    }

    private static object BuildMessage(ChatMessage m)
    {
        var d = new Dictionary<string, object?> { ["role"] = m.Role };

        if (m.ToolCalls is { Count: > 0 })
        {
            d["content"] = m.Content.Length > 0 ? m.Content : null;
            d["tool_calls"] = m.ToolCalls.Select(tc => new
            {
                id = tc.Id,
                type = "function",
                function = new { name = tc.Name, arguments = tc.Arguments },
            }).ToArray();
            return d;
        }

        if (m.Role == "tool")
        {
            d["tool_call_id"] = m.ToolCallId;
            d["content"] = m.Content;
            return d;
        }

        if (m.ImagePng is { Length: > 0 })
        {
            d["content"] = new object[]
            {
                new { type = "text", text = m.Content },
                new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(m.ImagePng) } },
            };
            return d;
        }

        d["content"] = m.Content;
        return d;
    }

    /// <summary>解析 SSE 流。</summary>
    private static async Task<StreamResult> ReadSseAsync(
        HttpResponseMessage resp, Action<string>? onDelta, Action<string>? onReasoning, CancellationToken ct)
    {
        var result = new StreamResult();
        var calls = new Dictionary<int, ToolCall>();
        var content = new StringBuilder();
        var reasoning = new StringBuilder();

        int promptTokens = 0, completionTokens = 0, reasoningTokens = 0, totalTokens = 0;
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        bool debug = Environment.GetEnvironmentVariable("KLM_AI_DEBUG") == "1";
        var rawLog = debug ? new StringBuilder() : null;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            rawLog?.AppendLine(line);
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(data);

                // **usage 解析必须在 choices 检查之前** ——
                // 实测：OpenAI 兼容端点把 `usage` 放在**最后一个 chunk**，而那个 chunk 的 `choices` 是空数组
                // （或干脆没有 choices）。原来的代码 `choices 为空就 continue` 会把 usage 直接丢掉，
                // 导致 token 计量永远是 0（这也是「没有 token 计量」的真根因）。
                if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                    usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out int ptv)) promptTokens = ptv;
                    if (usage.TryGetProperty("completion_tokens", out var ctok) && ctok.TryGetInt32(out int ctv)) completionTokens = ctv;
                    if (usage.TryGetProperty("total_tokens", out var tt) && tt.TryGetInt32(out int ttv)) totalTokens = ttv;
                    // 推理模型：思考 token 常在 completion_tokens_details.reasoning_tokens
                    if (usage.TryGetProperty("completion_tokens_details", out var det) &&
                        det.ValueKind == JsonValueKind.Object &&
                        det.TryGetProperty("reasoning_tokens", out var rt) && rt.TryGetInt32(out int rtv))
                        reasoningTokens = rtv;
                }

                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta)) continue;

                // 正文增量
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    string piece = c.GetString() ?? "";
                    if (piece.Length > 0) { content.Append(piece); onDelta?.Invoke(piece); }
                }

                // 思考增量（各家字段不同）
                foreach (var key in new[] { "reasoning_content", "reasoning" })
                {
                    if (delta.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        string piece = r.GetString() ?? "";
                        if (piece.Length > 0) { reasoning.Append(piece); onReasoning?.Invoke(piece); }
                    }
                }

                // 工具调用增量
                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        int idx = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out int v) ? v : 0;
                        if (!calls.TryGetValue(idx, out var call)) { call = new ToolCall(); calls[idx] = call; }

                        // 注意：增量帧里后续会带空字符串（id/name/type 都是 ""），
                        // 必须「非空才赋值」，否则会把先到的真实值覆盖成空。
                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                        {
                            var s = idEl.GetString();
                            if (!string.IsNullOrEmpty(s)) call.Id = s;
                        }
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                            {
                                var s = nEl.GetString();
                                if (!string.IsNullOrEmpty(s)) call.Name = s;
                            }
                            if (fn.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                                call.Arguments += aEl.GetString() ?? "";
                        }
                    }
                }
            }
            catch (JsonException) { /* 忽略无法解析的行 */ }
        }

        result.Content = content.ToString();
        // token 计量（来自 SSE 最后一个 chunk 的 usage）
        result.PromptTokens = promptTokens;
        result.CompletionTokens = completionTokens;
        result.ReasoningTokens = reasoningTokens;
        result.TotalTokens = totalTokens > 0 ? totalTokens : promptTokens + completionTokens;
        result.Reasoning = reasoning.ToString();
        result.ToolCalls = calls.OrderBy(kv => kv.Key).Select(kv => kv.Value)
                                .Where(tc => tc.Name.Length > 0).ToList();

        if (debug && rawLog != null)
        {
            try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "ai-sse.log"), rawLog.ToString()); }
            catch { }
        }
        return result;
    }
}
