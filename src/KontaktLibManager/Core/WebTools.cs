using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KontaktLibManager.Core;

/// <summary>
/// Agent 的联网工具：网页搜索 + 网页正文抓取。
///
/// 设计取舍：
///   · 不依赖任何需要 API Key 的搜索服务（用户没配就要能用），
///     用 DuckDuckGo Lite 的 HTML 端点做搜索 —— 无需 key、无需登录。
///   · 结果只返回「标题 + URL + 摘要」，正文由 <see cref="WebFetch"/> 按需抓取，
///     避免一次把大量网页塞进上下文。
///   · 网络访问受 <see cref="AgentToolContext.AllowNetwork"/> 控制，默认关闭，
///     由界面层显式开启（与「只读免打扰、写操作要确认」的权限模型一致）。
/// </summary>
public static class WebTools
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return c;
    }

    /// <summary>网页搜索：返回标题 + URL + 摘要。</summary>
    public static async Task<string> SearchAsync(AgentToolContext ctx, string argsJson, CancellationToken ct)
    {
        if (!ctx.AllowNetwork) return "{\"error\":\"联网能力未开启（请在设置中允许 Agent 联网）\"}";
        var args = Parse(argsJson);
        string q = GetStr(args, "query");
        int max = (int)Math.Clamp(GetLong(args, "max_results") is > 0 and <= 10 ? GetLong(args, "max_results") : 5, 1, 10);
        if (q.Length == 0) return "{\"error\":\"缺少 query\"}";

        try
        {
            string url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(q);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await Http.SendAsync(req, ct);
            string html = await res.Content.ReadAsStringAsync(ct);

            var items = new List<object>();
            // DDG HTML 版：<a class="result__a" href="...">标题</a> + <a class="result__snippet">摘要</a>
            var titleRe = new Regex("<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            var snipRe = new Regex("<a[^>]*class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            var snippets = snipRe.Matches(html).Select(m => Clean(m.Groups[1].Value)).ToList();

            int i = 0;
            foreach (Match m in titleRe.Matches(html))
            {
                if (items.Count >= max) break;
                string u = DecodeDdgUrl(m.Groups[1].Value);
                string title = Clean(m.Groups[2].Value);
                if (title.Length < 2 || u.Length == 0) continue;
                items.Add(new
                {
                    title,
                    url = u,
                    snippet = i < snippets.Count ? Truncate(snippets[i], 300) : "",
                });
                i++;
            }

            return ToolJson.S(new
            {
                query = q,
                count = items.Count,
                items,
                note = items.Count == 0 ? "没有解析到结果，可尝试换关键词，或直接用 web_fetch 打开已知网址" : "",
            });
            }
            catch (Exception ex) { return $"{{\"error\":\"搜索失败：{Escape(ex.Message)}\"}}"; }
    }

    /// <summary>抓取网页正文（去标签，截断）。</summary>
    public static async Task<string> FetchAsync(AgentToolContext ctx, string argsJson, CancellationToken ct)
    {
        if (!ctx.AllowNetwork) return "{\"error\":\"联网能力未开启（请在设置中允许 Agent 联网）\"}";
        var args = Parse(argsJson);
        string u = GetStr(args, "url");
        int maxChars = (int)Math.Clamp(GetLong(args, "max_chars") is > 0 and <= 20000 ? GetLong(args, "max_chars") : 6000, 500, 20000);
        if (u.Length == 0) return "{\"error\":\"缺少 url\"}";
        if (!u.StartsWith("http://") && !u.StartsWith("https://")) return "{\"error\":\"只支持 http/https\"}";

        try
        {
            using var res = await Http.GetAsync(u, ct);
            string ctype = res.Content.Headers.ContentType?.MediaType ?? "";
            string body = await res.Content.ReadAsStringAsync(ct);

            string text = ctype.Contains("html") ? HtmlToText(body) : body;
            bool truncated = text.Length > maxChars;
            if (truncated) text = text[..maxChars];

            return ToolJson.S(new
            {
                url = u,
                status = (int)res.StatusCode,
                contentType = ctype,
                chars = text.Length,
                truncated,
                content = text,
            });
        }
        catch (Exception ex) { return $"{{\"error\":\"抓取失败：{Escape(ex.Message)}\"}}"; }
    }

    /// <summary>
    /// 音频试听联动：给出某个音色库可试听的片段（演示音频 / 已解码采样 / .nkx 容器）。
    /// 让 Agent 能在回答「这个库的连奏怎么样」时直接给出可播放的链接。
    /// </summary>
    public static string FindAudition(AgentToolContext ctx, string argsJson)
    {
        if (ctx.Db == null) return "{\"error\":\"索引不可用\"}";
        var args = Parse(argsJson);
        string libName = GetStr(args, "library");
        string kw = GetStr(args, "keyword");
        int limit = (int)Math.Clamp(GetLong(args, "limit") is > 0 and <= 30 ? GetLong(args, "limit") : 8, 1, 30);

        long libId = 0;
        string resolvedName = "";
        if (libName.Length > 0)
        {
            var lib = ctx.Db.GetLibraries()
                .FirstOrDefault(l => l.Name.Contains(libName, StringComparison.OrdinalIgnoreCase));
            if (lib == null) return $"{{\"error\":\"未找到名称含「{libName}」的音色库\"}}";
            libId = lib.Id;
            resolvedName = lib.Name;
        }

        // 🔴 **跨库检索 + 真过滤（2026-09-26 修）** ——
        //   旧实现 `GetAudioClips(libId, limit, kw)` 有两个 bug 叠加：
        //     · `kw` 传给的是 `preferName`（**只排序、不过滤**）；
        //     · 不传 library 时 `libId = 0` ⇒ SQL 变成 `WHERE library_id = 0` ⇒ **必然 0 条**。
        //   ⇒ 实测：Agent 想「全局找 cymbal 采样」得到 0 结果、只好逐库试 audition、
        //     又因不传 match 拿到随机采样 ⇒ **推荐出 kick / hammer / shaker 等完全无关的东西**。
        //   ⇒ 现在：**不传 library 就跨全部库检索**；**keyword 非空就真过滤**。
        var clips = ctx.Db.SearchAudioClips(kw, limit, libId);
        var (demo, sample, ncw, nkx) = ctx.Db.CountAudioClips(libId);

        return ToolJson.S(new
        {
            library = resolvedName.Length > 0 ? resolvedName : "（全部库）",
            keyword = kw.Length > 0 ? kw : null,
            playable = clips.Count,
            breakdown = new { demo, sample, ncw, nkx },
            items = clips.Select(c => new
            {
                name = c.Name,
                // **带上库名与库 id** —— 跨库检索时这是必需信息（否则模型不知道去哪找）
                library = c.LibraryName,
                libraryId = c.LibraryId,
                // 🔴 **命中方式（2026-09-26 新增）—— 让模型知道【证据强度】** ——
                //   实测问题：`Gongs with FX` / `Gong Scrapes And Rattles` 名字里【没有】cymbal，
                //   只因为它们位于 `06 Cymbals and Gongs\.previews\` 目录而被命中 ⇒ 混进推荐里，
                //   而模型还断言「这 5 个都是名字里就带 cymbal 的采样」（错）。
                //   ⇒ 明确区分：`name`=强证据（名字真的含关键词）/ `path`=弱证据（只是所在目录含关键词）。
                //   **保底名额应优先用 `name`；`path` 命中的要说明「同类但名字未含关键词」。**
                matchedBy = MatchField(c, kw),
                kind = c.Kind,
                sizeBytes = c.SizeBytes,
                relPath = c.RelPath,
                hint = c.Kind switch
                {
                    "demo" => "**NKS 快照预览**（乐器/预设的试听片段，不是单个采样）",
                    "ncw" => "NI 压缩采样，本工具可解码后试听",
                    "nkx" => "加密单块容器，需解包解密后试听",
                    _ => "采样文件",
                },
            }),
            // **给下一步的明确指引** —— 拿到清单后用 `audition { library, match }` 才能真正试听
            nextStep = clips.Count > 0
                ? "要试听其中的片段，请对【它所属的库】调用 audition（带 match 参数）。" +
                  "⚠️ 挑「保底」项时请优先选 matchedBy=name 的（名字真含关键词）；" +
                  "matchedBy=path 的只是【所在目录名】含关键词，可能不是你要的乐器（例如「Gongs」目录下的锣）。"
                : "没有匹配到片段。可换更短的关键词（如 crash / ride / hat），或先用 query_instruments 看哪些库有对应乐器。",
        });
    }

    // ── 辅助 ──

    /// <summary>DDG 的结果链接是跳转形式（//duckduckgo.com/l/?uddg=<编码后的真实URL>），这里解回真实地址。</summary>
    /// <summary>
    /// 🔴 判断关键词命中在【名字】还是【路径】（2026-09-26）。
    ///   `name` = 强证据（文件名真含关键词）；`path` = 弱证据（只是所在目录名含关键词）。
    ///   为什么需要：实测 `Gongs with FX`（锣）只因位于 `06 Cymbals and Gongs\` 目录而被命中，
    ///   混进了镲片推荐、而模型还断言「这些名字里都带 cymbal」（错）。
    /// </summary>
    private static string MatchField(AudioClip c, string keywords)
    {
        var terms = (keywords ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return "none";
        string nm = (c.Name ?? "").ToLowerInvariant();
        string rp = (c.RelPath ?? "").ToLowerInvariant();
        foreach (var t in terms)
        {
            string tl = t.ToLowerInvariant();
            bool inName = nm.Contains(tl);
            bool inPath = rp.Contains(tl);
            if (!inName && !inPath) return "none";
            if (!inName) return "path";   // 只要有任意一个词只命中路径 ⇒ 算弱证据
        }
        return "name";
    }

    private static string DecodeDdgUrl(string href)
    {
        try
        {
            if (href.StartsWith("//")) href = "https:" + href;
            int q2 = href.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
            if (q2 >= 0)
            {
                string enc = href[(q2 + 5)..];
                int amp = enc.IndexOf('&');
                if (amp >= 0) enc = enc[..amp];
                return Uri.UnescapeDataString(enc);
            }
            return href.StartsWith("http") ? href : "";
        }
        catch { return ""; }
    }

    private static string HtmlToText(string html)
    {
        string s = Regex.Replace(html, "(?s)<script.*?</script>", " ");
        s = Regex.Replace(s, "(?s)<style.*?</style>", " ");
        s = Regex.Replace(s, "(?s)<!--.*?-->", " ");
        s = Regex.Replace(s, "(?i)<br\\s*/?>", "\n");
        s = Regex.Replace(s, "(?i)</(p|div|li|h[1-6]|tr)>", "\n");
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, "[ \\t\\u00A0]+", " ");
        s = Regex.Replace(s, "\\n{3,}", "\n\n");
        return s.Trim();
    }

    private static string Clean(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")).Trim();

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static JsonElement Parse(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return default; }
    }

    private static string GetStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";

    private static long GetLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long n) ? n : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out long m) ? m : 0,
            _ => 0,
        };
    }

    private static string Escape(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
}
