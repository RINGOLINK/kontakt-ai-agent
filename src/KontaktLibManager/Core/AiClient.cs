using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KontaktLibManager.Core;

public sealed class AiSettings
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
        /// <summary>最大输出 token。**0 = 不限制**（请求里不发送 max_tokens，由模型自行决定上限）。</summary>
        public int MaxTokens { get; set; } = 0;

        /// <summary>模型的上下文窗口长度（token）。用于上下文预算管理与压缩触发。</summary>
        public int ContextLength { get; set; } = 262144;

        /// <summary>上下文压缩阈值（百分比）。已用 token 超过 ContextLength × 该比例时触发压缩，默认 70%。</summary>
        public int CompressPercent { get; set; } = 70;

        /// <summary>该模型是否具备多模态（视觉）能力。纯语言模型应关掉，避免发送图片导致报错。</summary>
        public bool Multimodal { get; set; } = true;
        
        /// <summary>是否允许 Agent 联网（搜索/抓网页）。默认关闭。</summary>
        public bool AllowNetwork { get; set; } = false;

        /// <summary>是否允许 Agent 执行 Shell 命令（默认关闭）。</summary>
        public bool AllowShell { get; set; } = false;
        /// <summary>🔴 权限档位（2026-09-26）：readonly / readwrite / full。</summary>
        public string PermissionTier { get; set; } = "readonly";

        /// <summary>
        /// 思考强度：auto（默认，不发送该字段，由模型自控）/ low / medium / high。
        /// 对应 OpenAI 兼容的 reasoning_effort 字段。
        /// </summary>
        public string ThinkingEffort { get; set; } = "auto";

        
// ── 语言设置（用户要求三项独立）──
        
/// <summary>界面语言：zh（默认）/ en。仅影响前端文案。</summary>
        
public string UiLanguage { get; set; } = "zh";
        
/// <summary>AI 思考语言：zh / en / auto（默认，不干预）。强制模型用指定语言推理。</summary>
        
public string ThinkLanguage { get; set; } = "auto";
        
/// <summary>AI 回复语言：zh / en / auto（默认，不干预）。强制模型用指定语言作答。</summary>
        
public string ReplyLanguage { get; set; } = "auto";

    /// <summary>是否允许 Agent 操作外部程序界面（点击/输入/按键）。默认关，且每次操作都要确认。</summary>
    public bool AllowUiControl { get; set; }

        /// <summary>压缩触发阈值（token）。</summary>
        public int CompressAtTokens => (int)(ContextLength * Math.Clamp(CompressPercent, 10, 95) / 100.0);
    public bool Configured => BaseUrl.Length > 0 && ApiKey.Length > 0 && Model.Length > 0;

    public static AiSettings Load(Database db)
    {
        // 🔴 权限档位（2026-09-26，用户要求放右上角）：
        //   readonly  = 只能查（不给 shell / UI 控制）
        //   readwrite = 可执行【只读命令】（自动放行）+ 写操作逐条确认 + 不开 UI 控制
        //   full      = 全部放行（含写操作与 UI 控制）
        string tier = db.GetMeta("ai_permission_tier", "readonly");
        if (tier != "readonly" && tier != "readwrite" && tier != "full") tier = "readonly";
        return new AiSettings
        {
            BaseUrl = db.GetMeta("ai_base_url", ""),
            ApiKey = db.GetMeta("ai_api_key", ""),
            Model = db.GetMeta("ai_model", ""),
            Temperature = double.TryParse(db.GetMeta("ai_temperature", "0.3"), out double t) ? t : 0.3,
            MaxTokens = int.TryParse(db.GetMeta("ai_max_tokens", "0"), out int m) ? m : 0,
            ContextLength = int.TryParse(db.GetMeta("ai_context_length", "262144"), out int cl) && cl > 0 ? cl : 262144,
            CompressPercent = int.TryParse(db.GetMeta("ai_compress_percent", "70"), out int cp) && cp is >= 10 and <= 95 ? cp : 70,
            Multimodal = db.GetMeta("ai_multimodal", "1") != "0",
            AllowNetwork = db.GetMeta("ai_allow_network", "0") == "1",
            // 下面三个由【档位】统一推导（旧的独立开关已废弃）
            PermissionTier = tier,
            AllowShell = tier != "readonly",
            AllowUiControl = tier == "full",
            ThinkingEffort = db.GetMeta("ai_thinking_effort", "auto"),
            UiLanguage = db.GetMeta("ui_language", "zh"),
            ThinkLanguage = db.GetMeta("ai_think_language", "auto"),
            ReplyLanguage = db.GetMeta("ai_reply_language", "auto"),
        };
    }

    public void Save(Database db)
    {
        db.SetMeta("ai_base_url", BaseUrl.Trim().TrimEnd('/'));
        db.SetMeta("ai_api_key", ApiKey.Trim());
        db.SetMeta("ai_model", Model.Trim());
        db.SetMeta("ai_temperature", Temperature.ToString("F2"));
        db.SetMeta("ai_max_tokens", MaxTokens.ToString());
        db.SetMeta("ai_context_length", ContextLength.ToString());
        db.SetMeta("ai_compress_percent", CompressPercent.ToString());
        db.SetMeta("ai_multimodal", Multimodal ? "1" : "0");
        db.SetMeta("ai_allow_network", AllowNetwork ? "1" : "0");
        db.SetMeta("ai_allow_shell", AllowShell ? "1" : "0");
        db.SetMeta("ai_permission_tier", PermissionTier);
        db.SetMeta("ai_thinking_effort", ThinkingEffort);
        db.SetMeta("ui_language", UiLanguage);
        db.SetMeta("ai_think_language", ThinkLanguage);
        db.SetMeta("ai_reply_language", ReplyLanguage);
        db.SetMeta("ai_allow_ui_control", AllowUiControl ? "1" : "0");
    }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";      // system | user | assistant | tool
    public string Content { get; set; } = "";
    /// <summary>可选的附图（PNG 字节）——多模态模型会一并理解。</summary>
    public byte[]? ImagePng { get; set; }
    /// <summary>assistant 消息携带的工具调用。</summary>
    public List<ToolCall>? ToolCalls { get; set; }
    /// <summary>tool 消息对应的调用 id。</summary>
    public string ToolCallId { get; set; } = "";
}

/// <summary>
/// OpenAI 兼容的对话客户端（支持多模态）。
/// 用户可配置 Base URL / API Key / 模型，兼容 DeepSeek、OpenAI、SenseNova、Ollama、LM Studio 等。
/// </summary>
public static class AiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// 全局节流：实测端点按 tpm/rpm 限流（"inference exceeds tpm/rpm limit"），
    /// 连续快速请求会 429。这里保证请求之间至少间隔一定时间，并串行化发送。
    /// </summary>
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static DateTime _lastSendUtc = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.5);

    private static async Task DelayForThrottleAsync(CancellationToken ct)
    {
        var wait = MinInterval - (DateTime.UtcNow - _lastSendUtc);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }

    public static async Task<(bool ok, string message, string reply)> TestAsync(AiSettings s, CancellationToken ct = default)
    {
        if (!s.Configured) return (false, "尚未配置 Base URL / API Key / 模型", "");
        try
        {
            var reply = await ChatAsync(s, new List<ChatMessage>
            {
                new() { Role = "user", Content = "回复两个字：正常" }
            }, ct, maxTokensOverride: 600);   // 推理模型需要足够 token 才能产出 content（否则只有 reasoning）
            return (true, $"连接成功（模型 {s.Model}）：{reply.Trim()}", reply);
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}", "");
        }
    }

    public static async Task<string> ChatAsync(AiSettings s, List<ChatMessage> messages,
        CancellationToken ct = default, int? maxTokensOverride = null)
    {
        var payload = BuildPayload(s, messages, maxTokensOverride);
        string json = JsonSerializer.Serialize(payload);
        return await PostAsync(s, json, ct);
    }

    /// <summary>让多模态模型「看」一张图并回答（用于解析纯图片页的说明书）。</summary>
    public static async Task<string> DescribeImageAsync(AiSettings s, byte[] png, string prompt,
        CancellationToken ct = default, int maxTokens = 1200)
    {
        var msg = new ChatMessage { Role = "user", Content = prompt, ImagePng = png };
        var payload = BuildPayload(s, new List<ChatMessage> { msg }, maxTokens);
        string json = JsonSerializer.Serialize(payload);
        return await PostAsync(s, json, ct);
    }

    /// <summary>
    /// 按魔数判断图片格式并拼成 data URL。
    /// 之前硬编码成 image/png，但截图现在是 JPEG —— 贴错 MIME 有些服务端会拒。
    /// </summary>
    private static string SniffDataUrl(byte[] b)
    {
        string mime = "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) mime = "image/jpeg";
        else if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                 b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) mime = "image/webp";
        else if (b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) mime = "image/gif";
        return "data:" + mime + ";base64," + Convert.ToBase64String(b);
    }

    private static object BuildPayload(AiSettings s, List<ChatMessage> messages, int? maxTokens)
    {
        var msgs = messages.Select(m =>
        {
            if (m.ImagePng == null || m.ImagePng.Length == 0)
                return (object)new { role = m.Role, content = m.Content };

            // 多模态：content 为数组
            return new
            {
                role = m.Role,
                content = new object[]
                {
                    new { type = "text", text = m.Content },
                    new { type = "image_url", image_url = new { url = SniffDataUrl(m.ImagePng) } },
                },
            };
        }).ToArray();

        return new
        {
            model = s.Model,
            messages = msgs,
            temperature = s.Temperature,
            // MaxTokens == 0 表示「不限制」：不发送该字段，由模型自行决定上限
            max_tokens = maxTokens ?? (s.MaxTokens > 0 ? s.MaxTokens : (int?)null),
            // 思考强度：auto 表示不发送该字段（由模型自控）
            reasoning_effort = string.IsNullOrEmpty(s.ThinkingEffort) || s.ThinkingEffort == "auto" ? null : s.ThinkingEffort,
            stream = false,
        };
    }

    private static async Task<string> PostAsync(AiSettings s, string json, CancellationToken ct)
    {
        string url = s.BaseUrl.TrimEnd('/') + "/chat/completions";

        // 官方端点高峰期较慢/偶发失败 → 失败多试几次（退避约 150 秒）
        int[] delaysMs = { 2000, 4000, 8000, 15000, 25000, 40000, 60000 };
        var jitter = new Random();
        Exception? last = null;

        for (int attempt = 0; attempt <= delaysMs.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + s.ApiKey);

            HttpResponseMessage resp;
            await Throttle.WaitAsync(ct);
            try
            {
                await DelayForThrottleAsync(ct);
                resp = await Http.SendAsync(req, ct);
                _lastSendUtc = DateTime.UtcNow;
            }
            catch (HttpRequestException ex)
            {
                Throttle.Release();
                last = new InvalidOperationException($"网络错误：{ex.Message}", ex);
                if (attempt < delaysMs.Length) { await Task.Delay(delaysMs[attempt] + jitter.Next(0, 800), ct); continue; }
                throw last;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Throttle.Release();
                last = new InvalidOperationException("请求超时（端点繁忙）");
                if (attempt < delaysMs.Length) { await Task.Delay(delaysMs[attempt] + jitter.Next(0, 800), ct); continue; }
                throw last;
            }
            catch
            {
                Throttle.Release();
                throw;
            }
            Throttle.Release();

            using (resp)
            {
                string body = await resp.Content.ReadAsStringAsync(ct);
                int code = (int)resp.StatusCode;

                if (code == 429 || code >= 500)
                {
                    last = new InvalidOperationException(
                        code == 429 ? "端点限流（HTTP 429）"
                                    : $"服务端错误 HTTP {code}：{(body.Length > 200 ? body[..200] : body)}");
                    if (attempt < delaysMs.Length) { await Task.Delay(delaysMs[attempt] + jitter.Next(0, 800), ct); continue; }
                    throw new InvalidOperationException(
                        $"{last.Message}；已自动重试 {delaysMs.Length} 次仍失败，请稍后再试");
                }

                if (!resp.IsSuccessStatusCode)
                {
                    string brief = body.Length > 400 ? body[..400] : body;
                    throw new InvalidOperationException($"HTTP {code}：{brief}");
                }

                return ParseReply(body);
            }
        }
        throw last ?? new InvalidOperationException("请求失败");
    }

    private static string ParseReply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var msg = choices[0].GetProperty("message");
            string text = ExtractText(msg);
            if (text.Length > 0) return text;

            var keys = string.Join(", ", msg.EnumerateObject().Select(p => p.Name));
            throw new InvalidOperationException($"响应 message 中无可提取文本（字段：{keys}）");
        }
        throw new InvalidOperationException("响应中没有 choices：" + (body.Length > 300 ? body[..300] : body));
    }

    /// <summary>
    /// 从 message 中提取正文。兼容各家的字段差异：
    ///   content（标准，字符串或分块数组）/ reasoning_content（DeepSeek）/ reasoning（SenseNova）。
    /// </summary>
    private static string ExtractText(JsonElement msg)
    {
        if (msg.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                var s = content.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s!;
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                if (sb.Length > 0) return sb.ToString();
            }
        }

        foreach (var key in new[] { "reasoning_content", "reasoning" })
        {
            if (msg.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.String)
            {
                var v = r.GetString();
                if (!string.IsNullOrWhiteSpace(v)) return v!;
            }
        }
        return "";
    }
}
