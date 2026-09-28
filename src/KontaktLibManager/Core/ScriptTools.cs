using System.Diagnostics;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// Agent 的**代码执行**工具：让模型把一次性数据处理写成小脚本再运行
/// （统计占用、批量解析文件名、生成报告…），比用一堆 Shell 命令拼装更可靠。
///
/// **权限模型（用户拍板）**：代码执行属于「写 + 执行」类操作，因此
/// **一律走「预览改动 → 用户确认 → 执行 → 可回滚」**：
///   ① 预览：把脚本正文完整展示给用户（不是只给一句「要运行脚本」）；
///   ② 确认：用户点「允许执行」才真正运行；
///   ③ 可回滚：脚本写在**专用临时目录**，执行完立即删除；
///      脚本只能通过 stdout 返回结果，不主动改用户文件（要改文件请用 Shell 工具逐条确认）。
/// </summary>
public static class ScriptTools
{
    public const string PowerShell = "powershell";
    public const string JavaScript = "javascript";

    /// <summary>脚本临时目录（每次执行后清理）。</summary>
    public static string TempDir => Path.Combine(Path.GetTempPath(), "klm-agent-scripts");

    public sealed class Prepared
    {
        public string Path { get; set; } = "";
        public string Language { get; set; } = "";
        public string Code { get; set; } = "";
        public string Error { get; set; } = "";
        public bool Ok => Error.Length == 0;
    }

    /// <summary>把脚本写到临时目录（返回可预览的路径与正文）。</summary>
    public static Prepared Prepare(string language, string code)
    {
        var p = new Prepared { Language = language, Code = code ?? "" };
        string lang = (language ?? "").Trim().ToLowerInvariant();
        if (lang.Length == 0) lang = PowerShell;
        p.Language = lang;

        if (p.Code.Trim().Length == 0) { p.Error = "脚本内容为空"; return p; }
        if (p.Code.Length > 20000) { p.Error = "脚本过长（上限 20000 字符）"; return p; }

        string ext = lang switch
        {
            PowerShell => ".ps1",
            JavaScript => ".mjs",
            _ => "",
        };
        if (ext.Length == 0) { p.Error = $"不支持的脚本语言：{lang}（支持 powershell / javascript）"; return p; }

        try
        {
            Directory.CreateDirectory(TempDir);
            string name = "klm-" + Guid.NewGuid().ToString("N")[..8] + ext;
            p.Path = Path.Combine(TempDir, name);
            // 无 BOM 的 UTF-8：PowerShell 5.1 对带 BOM 的脚本才会按 UTF-8 解析中文
            File.WriteAllText(p.Path, p.Code, new UTF8Encoding(true));
        }
        catch (Exception ex) { p.Error = "写脚本失败：" + ex.Message; }
        return p;
    }

    /// <summary>执行已准备的脚本。</summary>
    public static async Task<ShellResult> RunAsync(Prepared p, string workDir, int timeoutSec, CancellationToken ct)
    {
        var r = new ShellResult();
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir,
            };

            if (p.Language == JavaScript)
            {
                psi.FileName = "node";
                psi.ArgumentList.Add(p.Path);
            }
            else
            {
                psi.FileName = "powershell";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(p.Path);
            }

            using var proc = new Process { StartInfo = psi };
            var so = new StringBuilder();
            var se = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 300)));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { r.TimedOut = true; try { proc.Kill(true); } catch { } }

            r.ExitCode = r.TimedOut ? -1 : proc.ExitCode;
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

    /// <summary>删除临时脚本（回滚）。</summary>
    public static void Cleanup(Prepared p)
    {
        try { if (p.Path.Length > 0 && File.Exists(p.Path)) File.Delete(p.Path); } catch { }
    }

    /// <summary>清理整个临时目录（启动时可调一次，清掉上次异常残留）。</summary>
    public static void CleanupAll()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            foreach (var f in Directory.GetFiles(TempDir)) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    private static string Trim(string s, int n)
    {
        s = (s ?? "").TrimEnd();
        return s.Length <= n ? s : s[..n] + "\n…（输出已截断）";
    }
}
