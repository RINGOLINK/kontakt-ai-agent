using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KontaktLibManager.Core;

/// <summary>命令的风险等级。</summary>
public enum ShellRisk
{
    /// <summary>只读白名单命中 —— 直接执行，不打扰用户。</summary>
    Safe,
    /// <summary>可能改动系统 —— 必须经用户确认（验权）后才能执行。</summary>
    NeedsConfirm,
    /// <summary>明确危险 —— 一律拒绝，不给确认入口。</summary>
    Blocked,
}

public sealed class ShellDecision
{
    public ShellRisk Risk { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>命中的只读白名单命令（Safe 时有值）。</summary>
    public string MatchedRule { get; set; } = "";
}

public sealed class ShellResult
{
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = "";
    public string StdErr { get; set; } = "";
    public double Seconds { get; set; }
    public bool TimedOut { get; set; }
}

/// <summary>
/// Agent 的 Shell 工具（PowerShell）。
///
/// **权限模型（用户拍板）**：
///   · **只读白名单直通** —— 命中白名单的命令（Get-ChildItem / Select-String / Measure-Object …）
///     且不含任何危险算子时直接执行，不打断用户；
///   · **风险操作走验权+提权** —— 其余命令一律返回 <see cref="ShellRisk.NeedsConfirm"/>，
///     由界面弹出「即将执行的命令原文」让用户确认，确认后才真正执行；
///   · **明确危险一律拒绝** —— 格式化磁盘、关机、删注册表等不给确认入口。
///
/// 设计原则：**默认不信任**。只有明确匹配白名单的才自动放行，其余全部要求确认。
/// </summary>
public static class ShellTools
{
    /// <summary>只读白名单：命令必须以其中之一开头（大小写不敏感）。</summary>
    private static readonly string[] ReadOnlyCommands =
    {
        "get-childitem", "gci", "ls", "dir",
        "get-item", "gi", "get-itemproperty", "gp",
        "get-content", "gc", "cat", "type",
        "select-string", "sls",
        "measure-object", "measure",
        "get-filehash",
        "test-path",
        "resolve-path",
        "get-process", "gps",
        "get-service", "gsv",
        "get-volume", "get-psdrive",
        "get-date", "get-command", "get-help", "get-member",
        "where-object", "sort-object", "select-object", "group-object",
        "format-table", "format-list", "format-wide",
        "out-string", "out-host",
        "convertto-json", "convertfrom-json", "convertto-csv",
        "compare-object", "get-acl",
        "ffprobe",
    };

    /// <summary>危险算子/命令：出现即判定为风险（或直接拒绝）。</summary>
    private static readonly string[] DangerTokens =
    {
        "remove-", "rm ", "rmdir", "del ", "erase ", "rd ",
        "set-", "new-", "add-", "clear-", "move-", "move ", "copy-", "copy ", "ren ", "rename-",
        "out-file", "set-content", "add-content", "export-", "import-",
        "start-process", "start-", "stop-", "invoke-", "iwr", "curl", "wget",
        "reg add", "reg delete", "reg import", "reg export",
        "sc ", "net ", "schtasks", "taskkill", "wmic", "bcdedit",
        ">", ">>", "|", ";",
        "cmd ", "cmd.exe", "bash", "wsl", "python", "node ", "npm ", "pip ", "git ",
        "new-item", "mkdir", "md ", "touch",
    };

    /// <summary>一律拒绝（连确认都不给）。</summary>
    private static readonly string[] BlockedTokens =
    {
        "format-volume", "format ", "diskpart", "shutdown", "restart-computer",
        "stop-computer", "clear-disk", "initialize-disk", "remove-partition",
        "rm -rf /", "del /f /s /q c:", "cipher /w",
        "set-executionpolicy", "disable-windowsoptionalfeature",
    };

    /// <summary>判定一条命令的风险等级。</summary>
    public static ShellDecision Classify(string command)
    {
        var d = new ShellDecision();
        string c = (command ?? "").Trim();
        if (c.Length == 0) { d.Risk = ShellRisk.Blocked; d.Reason = "命令为空"; return d; }

        string low = c.ToLowerInvariant();

        // ① 明确危险 → 直接拒绝
        foreach (var t in BlockedTokens)
        {
            if (low.Contains(t))
            {
                d.Risk = ShellRisk.Blocked;
                d.Reason = $"命令包含被禁止的操作「{t.Trim()}」，出于安全考虑不允许执行";
                return d;
            }
        }

        // ②-0 🔴 **「纯只读管道」直接放行（2026-09-26 修）** ——
        //   起因（用户实测）：`Get-ChildItem ... -Recurse -File | Select-Object -First 5`
        //   因为 `DangerTokens` 里有 `"|"` ⇒ 被判 NeedsConfirm ⇒ **Agent 一轮里弹了十几次授权**。
        //   但**「只读命令 | 只读命令」是安全的** —— 管道本身不产生副作用。
        //   判据（严格，只放宽管道/分号）：
        //     · 不含输出重定向 `>`、`>>`、后台 `&`、子表达式 `$(`、反引号；
        //     · 按 `|` 和 `;` 切分后，**每一段的首个命令都在只读白名单里**。
        //   ⚠️ 只要有一段的首命令不是只读（如 `| Remove-Item`）⇒ 落回 ② 走确认。
        bool hasRedirect = low.Contains(">") || low.Contains("&") ||
                           low.Contains("$(") || low.Contains("`");
        if (!hasRedirect)
        {
            var segs = low.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries);
            bool allReadOnly = segs.Length > 0;
            string? badSeg = null;
            foreach (var seg in segs)
            {
                string head = seg.Trim().Split(new[] { ' ', '\t', '\r', '\n' },
                                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                head = head.TrimStart('&', '.', '\\', '"', '\'');
                bool ok = ReadOnlyCommands.Any(r => head == r || head == r + ".exe");
                if (!ok) { allReadOnly = false; badSeg = seg.Trim(); break; }
            }
            if (allReadOnly)
            {
                d.Risk = ShellRisk.Safe;
                d.MatchedRule = "pure-readonly-pipeline";
                d.Reason = "命令只由只读命令与管道组成，无副作用";
                return d;
            }
        }
        // ②-1 其余含危险算子 → 需要确认
        foreach (var t in DangerTokens)
        {
            if (low.Contains(t))
            {
                d.Risk = ShellRisk.NeedsConfirm;
                d.Reason = $"命令包含可能产生副作用的操作「{t.Trim()}」";
                return d;
            }
        }

        // ③ 命中只读白名单 → 直接放行
        string first = low.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault() ?? "";
        // 允许 & / .\ 前缀
        first = first.TrimStart('&', '.', '\\', '"', '\'');
        foreach (var r in ReadOnlyCommands)
        {
            if (first == r || first == r + ".exe")
            {
                d.Risk = ShellRisk.Safe;
                d.MatchedRule = r;
                d.Reason = $"命中只读白名单「{r}」";
                return d;
            }
        }

        // ④ 其余一律要求确认（默认不信任）
        d.Risk = ShellRisk.NeedsConfirm;
        d.Reason = "该命令不在只读白名单内，需要你确认后才能执行";
        return d;
    }

    /// <summary>执行 PowerShell 命令（调用方必须先完成风险判定与用户确认）。</summary>
    public static async Task<ShellResult> RunAsync(string command, string workDir, int timeoutSec, CancellationToken ct)
    {
        var r = new ShellResult();
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveShell(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            using var p = new Process { StartInfo = psi };
            var so = new StringBuilder();
            var se = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 300)));

            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { r.TimedOut = true; try { p.Kill(true); } catch { } }

            r.ExitCode = r.TimedOut ? -1 : p.ExitCode;
            r.StdOut = Trim(so.ToString(), 12000);
            r.StdErr = Trim(se.ToString(), 4000);
        }
        catch (Exception ex)
        {
            r.ExitCode = -1;
            r.StdErr = "执行失败：" + ex.Message;
        }
        sw.Stop();
        r.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
        return r;
    }

    private static string? _shellExe;

    /// <summary>优先用 PowerShell 7（pwsh），没有则回退到 Windows PowerShell 5.1（powershell.exe）。</summary>
    private static string ResolveShell()
    {
        if (_shellExe != null) return _shellExe;
        foreach (var exe in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("exit 0");
                using var p = Process.Start(psi);
                if (p != null) { p.WaitForExit(5000); _shellExe = exe; return exe; }
            }
            catch { }
        }
        _shellExe = "powershell";
        return _shellExe;
    }

    private static string Trim(string s, int n)
    {
        s = (s ?? "").TrimEnd();
        return s.Length <= n ? s : s[..n] + "\n…（输出已截断）";
    }

    /// <summary>把判定结果 + 执行结果序列化成工具返回值。</summary>
    public static string ToJson(ShellDecision d, ShellResult? r, string command)
    {
        if (d.Risk == ShellRisk.Blocked)
            return ToolJson.S(new { blocked = true, reason = d.Reason, command });
        if (r == null)
            return ToolJson.S(new { needsConfirm = true, reason = d.Reason, command });
        return ToolJson.S(new
        {
            exitCode = r.ExitCode,
            seconds = r.Seconds,
            timedOut = r.TimedOut,
            stdout = r.StdOut,
            stderr = r.StdErr,
            risk = d.Risk.ToString(),
        });
    }
}
