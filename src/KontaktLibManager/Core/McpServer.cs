using System.Text.Json;
using System.Text.Json.Nodes;

namespace KontaktLibManager.Core;

/// <summary>
/// 把 Agent 的工具层暴露成 **MCP（Model Context Protocol）服务器**。
///
/// 目的：工具定义与执行**解耦**。同一个工具既能被内置 Agent 调用，也能通过 MCP
/// 被外部客户端（Claude Desktop / Cursor / 其他 Agent 框架）调用；未来若社区出现
/// Kontakt 相关 MCP，也能反向挂进来。
///
/// 实现范围（最小可用集）：
///   · `initialize`        握手，声明 tools 能力
///   · `tools/list`        列出全部工具（JSON Schema 与内置 Agent 完全一致）
///   · `tools/call`        调用工具，返回 MCP 规范的 content 数组
///   · `notifications/initialized`  通知（忽略）
///
/// 传输：stdio，每行一个 JSON-RPC 2.0 消息（MCP 的 stdio 传输即换行分隔的 JSON）。
///
/// **权限模型一致性**：MCP 侧与内置 Agent 走**同一套判定** —— 只读工具直通，
/// Shell 风险命令返回 `needsConfirm` 而**不自行执行**（外部客户端需自己向用户确认）。
/// </summary>
public static class McpServer
{
    public const string ProtocolVersion = "2024-11-05";
    public const string ServerName = "kontakt-lib-manager";
    public const string ServerVersion = "1.0.0";

    /// <summary>工具定义（与 AiAssistant.BuildTools 保持同一份语义）。</summary>
    public static JsonArray BuildToolList()
    {
        var arr = new JsonArray();
        void Add(string name, string desc, object props, string[] required)
        {
            arr.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = JsonNode.Parse(JsonSerializer.Serialize(props))!,
                    ["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray()),
                },
            });
        }

        Add("query_libraries", "按条件查询本地音色库索引（分类/名称关键词/体积/所需 Kontakt 版本），返回匹配库与分类汇总。",
            new { category = new { type = "string", description = "分类关键词" }, keyword = new { type = "string", description = "库名关键词" },
                  min_kontakt = new { type = "string" }, min_size_gb = new { type = "integer" }, max_size_gb = new { type = "integer" },
                  limit = new { type = "integer" } }, Array.Empty<string>());

        Add("query_instruments", "查询乐器（NKI）索引：按所属库、名称关键词、演奏法、类型筛选，返回演奏法分布。",
            new { library = new { type = "string" }, keyword = new { type = "string" }, articulation = new { type = "string" },
                  kind = new { type = "string" }, limit = new { type = "integer" } }, Array.Empty<string>());

        Add("list_directory", "列出音色库目录下的子目录与文件（只读，限定在音色库目录内）。",
            new { path = new { type = "string" } }, new[] { "path" });

        Add("read_text_file", "读取音色库目录下的文本文件（.txt/.md/.nki/.ksp 等；PDF 请用知识库）。",
            new { path = new { type = "string" }, max_chars = new { type = "integer" } }, new[] { "path" });

        Add("find_audition", "查出某个音色库可试听的音频片段（演示音频/采样/.ncw/.nkx 容器）。",
            new { library = new { type = "string" }, keyword = new { type = "string" }, limit = new { type = "integer" } }, Array.Empty<string>());

        Add("web_search", "联网搜索，返回标题/链接/摘要。需在设置中开启「允许 Agent 联网」。",
            new { query = new { type = "string" }, max_results = new { type = "integer" } }, new[] { "query" });

        Add("web_fetch", "抓取指定网页的正文（去标签）。",
            new { url = new { type = "string" }, max_chars = new { type = "integer" } }, new[] { "url" });

        Add("run_command", "执行 PowerShell 命令。只读白名单直通；风险命令返回 needsConfirm 交由客户端向用户确认。",
            new { command = new { type = "string" }, timeout_sec = new { type = "integer" } }, new[] { "command" });

        Add("remember", "写入一条长期记忆（跨会话保留）。",
            new { content = new { type = "string" }, key = new { type = "string" }, scope = new { type = "string" }, importance = new { type = "integer" } },
            new[] { "content" });

        Add("recall", "检索长期记忆。",
            new { query = new { type = "string" }, limit = new { type = "integer" } }, Array.Empty<string>());

        Add("forget", "删除一条长期记忆（需先 recall 拿到 id）。",
            new { id = new { type = "integer" } }, new[] { "id" });


        // ── 界面自动化：只读 ──
        Add("ui_list_windows", "列出当前有窗口的进程（进程名/标题/PID/HWND）。",
            new { filter = new { type = "string" }, limit = new { type = "integer" } }, Array.Empty<string>());
        Add("ui_dump_tree", "枚举窗口的控件树（类型/名称/类名/支持的操作/屏幕坐标）。",
            new { window = new { type = "string" }, depth = new { type = "integer" }, types = new { type = "string" },
                  actionable_only = new { type = "boolean" }, limit = new { type = "integer" } }, new[] { "window" });
        Add("ui_find", "在窗口里按名称/类型/类名查找控件，返回坐标（含中心点）。",
            new { window = new { type = "string" }, name = new { type = "string" }, type = new { type = "string" },
                  className = new { type = "string" }, limit = new { type = "integer" } }, new[] { "window" });
        Add("ui_get_state", "读取控件状态（选中/开关/文本值/数值/展开/是否可用）。",
            new { window = new { type = "string" }, name = new { type = "string" }, type = new { type = "string" } }, new[] { "window", "name" });
        Add("ui_screenshot", "截取窗口并存成文件，返回路径与尺寸。",
            new { window = new { type = "string" } }, new[] { "window" });
        Add("ui_describe_window", "截图并用视觉模型描述窗口内容，返回**文字描述**（UIA 覆盖不到时的兜底）。",
            new { window = new { type = "string" }, question = new { type = "string" } }, new[] { "window" });

        // ── 界面自动化：写（MCP 侧不代用户确认，返回 needsConfirm）──
        Add("ui_click", "点击外部程序里的控件（需先用 ui_find 定位）。MCP 客户端需自行向用户确认后再执行。",
            new { window = new { type = "string" }, name = new { type = "string" }, type = new { type = "string" },
                  className = new { type = "string" }, doubleClick = new { type = "boolean" }, right = new { type = "boolean" } }, new[] { "window" });
        Add("ui_type", "向外部程序输入文本。MCP 客户端需自行向用户确认。",
            new { window = new { type = "string" }, text = new { type = "string" }, name = new { type = "string" },
                  className = new { type = "string" } }, new[] { "window", "text" });
        Add("ui_key", "向外部程序发送按键组合，如 Ctrl+S / Enter / F5。",
            new { window = new { type = "string" }, keys = new { type = "string" } }, new[] { "window", "keys" });
        Add("ui_scroll", "在外部程序某控件处滚动滚轮。",
            new { window = new { type = "string" }, name = new { type = "string" }, className = new { type = "string" },
                  notches = new { type = "integer" } }, new[] { "window" });

        // ── Kontakt 领域技能 ──
        Add("kontakt_ui_state", "读取 Kontakt 界面模式（新版 NewLumen / 老版 Classic）与选中标签。",
            new { window = new { type = "string" } }, Array.Empty<string>());
        Add("kontakt_classic_search", "老版界面：用 Kontakt 自带搜索框过滤音色库列表（定位库的首选手段）。",
            new { query = new { type = "string" } }, new[] { "query" });
        Add("kontakt_classic_load", "老版界面：双击库列表第 N 项以加载该库。建议先用 kontakt_classic_search 过滤。",
            new { index = new { type = "integer" } }, Array.Empty<string>());
        Add("kontakt_set_view", "新版界面：切换内容类型或筛选维度。",
            new { contentType = new { type = "string" }, filter = new { type = "string" } }, Array.Empty<string>());
        Add("kontakt_search", "新版界面：在浏览器里搜索。",
            new { query = new { type = "string" } }, new[] { "query" });
        Add("kontakt_switch_ui_mode", "切换 Kontakt 新版/老版界面（mode=classic/new）。成功率低，失败时请提示用户手动按 F10。",
            new { mode = new { type = "string" } }, new[] { "mode" });

        // ── 其他 ──
        Add("search_kontakt_doc", "检索 Kontakt 官方文档（340 页，**英文**）。",
            new { query = new { type = "string" }, top_k = new { type = "integer" } }, new[] { "query" });
        Add("run_script", "把一次性数据处理写成脚本运行（powershell / javascript）。MCP 客户端需自行确认。",
            new { language = new { type = "string" }, code = new { type = "string" }, timeout_sec = new { type = "integer" } }, new[] { "code" });

        Add("update_plan", "为多步任务建立/更新显式计划。",
            new { goal = new { type = "string" }, steps = new { type = "array" } }, new[] { "steps" });

        return arr;
    }

    /// <summary>处理一条 JSON-RPC 消息，返回响应（通知返回 null）。</summary>
    public static async Task<string?> HandleAsync(string line, AgentToolContext ctx, PlanStore plans, long branchId)
    {
        JsonNode? req;
        try { req = JsonNode.Parse(line); }
        catch { return Error(null, -32700, "Parse error"); }
        if (req is not JsonObject o) return Error(null, -32600, "Invalid Request");

        var idNode = o["id"];
        string method = o["method"]?.GetValue<string>() ?? "";

        // 通知（无 id）不需要响应
        bool isNotification = idNode == null;

        switch (method)
        {
            case "initialize":
                return Result(idNode, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
                });

            case "notifications/initialized":
                return null;

            case "tools/list":
                return Result(idNode, new JsonObject { ["tools"] = BuildToolList() });

            case "tools/call":
            {
                string name = o["params"]?["name"]?.GetValue<string>() ?? "";
                string argsJson = o["params"]?["arguments"]?.ToJsonString() ?? "{}";
                string text = await CallAsync(name, argsJson, ctx, plans, branchId);
                return Result(idNode, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = false,
                });
            }

            case "ping":
                return Result(idNode, new JsonObject());

            default:
                if (isNotification) return null;
                return Error(idNode, -32601, $"Method not found: {method}");
        }
    }

    /// <summary>工具调用分发（与内置 Agent 共用实现）。</summary>
    public static async Task<string> CallAsync(string name, string argsJson, AgentToolContext ctx, PlanStore plans, long branchId)
    {
        var ai2 = ctx.Db != null ? AiSettings.Load(ctx.Db) : new AiSettings();
        try
        {
            switch (name)
            {
// ── 界面自动化：只读 ──
                case "ui_list_windows": return UiAutomation.ListWindows(argsJson);
                case "ui_dump_tree": return UiAutomation.DumpTree(argsJson);
                case "ui_find": return UiAutomation.Find(argsJson);
                case "ui_get_state": return UiAutomation.GetState(argsJson);
                case "ui_screenshot":
                {
                    var sw = UiAutomation.ResolveWindow(GetArgStr(argsJson, "window"));
                    if (sw == null) return "{\"error\":\"未找到窗口\"}";
                    byte[]? shot = UiVision.CaptureWindow(sw.Value.Hwnd);
                    if (shot == null) return "{\"error\":\"截图失败\"}";
                    return ToolJson.S(new { ok = true, path = UiVision.SaveTemp(shot), bytes = shot.Length });
                }
                case "ui_describe_window":
                {
                    var dw = UiAutomation.ResolveWindow(GetArgStr(argsJson, "window"));
                    if (dw == null) return "{\"error\":\"未找到窗口\"}";
                    byte[]? shot2 = UiVision.CaptureWindow(dw.Value.Hwnd);
                    if (shot2 == null) return "{\"error\":\"截图失败\"}";
                    return await UiVision.DescribeAsync(ai2, shot2, GetArgStr(argsJson, "question"), CancellationToken.None);
                }

                // ── 界面自动化：写（MCP 不代用户确认，返回 needsConfirm）──
                case "ui_click": case "ui_type": case "ui_key": case "ui_scroll":
                {
                    string act = name[3..];
                    var pv = UiActions.Preview(act, argsJson);
                    if (!pv.Ok) return ToolJson.S(new { error = pv.Error });
                    return ToolJson.S(new
                    {
                        needsConfirm = true,
                        action = pv.Description,
                        message = "MCP 服务器不代替用户确认界面操作。请客户端向用户确认后再调用（或改用本机内置 Agent，它带确认弹窗）。",
                    });
                }

                // ── Kontakt 领域技能 ──
                case "kontakt_ui_state": return KontaktSkills.GetUiState(argsJson);
                case "kontakt_classic_search": case "kontakt_classic_load":
                case "kontakt_set_view": case "kontakt_search": case "kontakt_switch_ui_mode":
                {
                    string pv2 = name switch
                    {
                        "kontakt_classic_search" => KontaktSkills.PreviewClassicSearch(argsJson),
                        "kontakt_classic_load" => KontaktSkills.PreviewClassicLoadItem(argsJson),
                        "kontakt_set_view" => KontaktSkills.PreviewSetView(argsJson),
                        "kontakt_search" => KontaktSkills.PreviewSearch(argsJson),
                        "kontakt_switch_ui_mode" => KontaktSkills.PreviewSwitchUiMode(argsJson),
                        _ => "",
                    };
                    if (pv2.Length == 0) return "{\"error\":\"参数不完整\"}";
                    return ToolJson.S(new
                    {
                        needsConfirm = true,
                        action = pv2,
                        message = "MCP 服务器不代替用户确认界面操作。请客户端向用户确认后再调用。",
                    });
                }

                // ── 其他 ──
                case "search_kontakt_doc":
                {
                    var kbd = ManualKb.Load(ManualKb.KontaktDocLibraryId);
                    if (kbd == null || kbd.Chunks.Count == 0) return "{\"error\":\"Kontakt 官方文档知识库尚未建立\"}";
                    var hits = ManualKb.HybridSearch(kbd, GetArgStr(argsJson, "query"), 5);
                    return ToolJson.S(new
                    {
                        count = hits.Count,
                        results = hits.Select(c => new { page = c.Page, text = c.Text.Length > 700 ? c.Text[..700] + "…" : c.Text }),
                    });
                }
                case "run_script":
                {
                    var sd = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                    string lang = sd.RootElement.TryGetProperty("language", out var lv) ? (lv.GetString() ?? "powershell") : "powershell";
                    string code = sd.RootElement.TryGetProperty("code", out var cv) ? (cv.GetString() ?? "") : "";
                    var prep = ScriptTools.Prepare(lang, code);
                    if (!prep.Ok) return ToolJson.S(new { error = prep.Error });
                    try
                    {
                        var sr = await ScriptTools.RunAsync(prep, ctx.ShellWorkDir, 30, CancellationToken.None);
                        return ToolJson.S(new { exitCode = sr.ExitCode, seconds = sr.Seconds, stdout = sr.StdOut, stderr = sr.StdErr });
                    }
                    finally { ScriptTools.Cleanup(prep); }
                }

                case "query_libraries": return AgentTools.QueryLibraries(ctx, argsJson);
                case "query_instruments": return AgentTools.QueryInstruments(ctx, argsJson);
                case "list_directory": return AgentTools.ListDirectory(ctx, argsJson);
                case "read_text_file": return AgentTools.ReadTextFile(ctx, argsJson);
                case "find_audition": return WebTools.FindAudition(ctx, argsJson);
                case "web_search": return await WebTools.SearchAsync(ctx, argsJson, CancellationToken.None);
                case "web_fetch": return await WebTools.FetchAsync(ctx, argsJson, CancellationToken.None);
                case "remember": return MemoryTools.Remember(ctx, argsJson);
                case "recall": return MemoryTools.Recall(ctx, argsJson);
                case "forget": return MemoryTools.Forget(ctx, argsJson);
                case "update_plan": return PlanTools.Update(plans, branchId, argsJson);
                case "run_command":
                {
                    if (!ctx.AllowShell) return "{\"error\":\"Shell 能力未开启\"}";
                    var args = JsonNode.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                    string cmd = args?["command"]?.GetValue<string>() ?? "";
                    int tsec = args?["timeout_sec"]?.GetValue<int>() ?? 30;
                    var risk = ShellTools.Classify(cmd);
                    if (risk.Risk == ShellRisk.Blocked) return ShellTools.ToJson(risk, null, cmd);
                    // MCP 侧不代用户确认：风险命令一律要求客户端自行确认
                    if (risk.Risk == ShellRisk.NeedsConfirm) return ShellTools.ToJson(risk, null, cmd);
                    var r = await ShellTools.RunAsync(cmd, ctx.ShellWorkDir, tsec, CancellationToken.None);
                    return ShellTools.ToJson(risk, r, cmd);
                }
                default:
                    return $"{{\"error\":\"未知工具 {name}\"}}";
            }
        }
        catch (Exception ex)
        {
            return $"{{\"error\":\"工具执行失败：{ex.Message}\"}}";
        }
    }

    private static string GetArgStr(string json, string name)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (d.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private static string Result(JsonNode? id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();
}
