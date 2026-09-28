using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// **KSP 生成 + 编译校验闭环** —— 调研报告「远期」第 9 项。
///
/// **要解决的问题（报告原话）**：让 Agent 生成 KSP 脚本，再用开源 KSPCompiler
/// （https://github.com/r-koubou/KSPCompiler ，**C# / MIT**）编译校验，
/// **把编译错误回灌模型迭代** ——「从『管理音色库』跨到『**改造音色库**』，天花板最高」。
///
/// **为什么是外部进程而不是 NuGet 引用**：KSPCompiler 发布形态是 **CLI**（不是库），
/// 且它要求 **.NET 10**，而本项目是 **.NET 9**。把它当**独立可执行文件**调用，
/// 就**不必升级本项目**、也不必为它改依赖 —— 代价只是分发时多一个目录。
///
/// **📌 关于分发（本项目自己 publish 的 self-contained 版本）**：
///   `data\kspc\kspc.exe`（**71 MB 单文件、自包含，用户无需装任何 .NET**）
///   + **必需的符号数据** `data\kspc\Data\Symbols\{callbacks,commands,uitypes,variables}.yaml`（共 ~456 KB）。
///   来源：`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`。
///   **符号数据必须与 exe 同级目录的 `Data\Symbols\` 下**，否则编译会因缺少符号表而误报。
///
/// **🔴 关键实测结论（务必牢记）**：**KSPCompiler 的编译问题输出为 `Warning`，退出码【仍为 0】！**
///   ⇒ **判断编译成败【不能只看退出码，必须解析输出文本】。**
///   实测三种情况：
///     · 文件不存在     → 退出码 **1** + `FileNotFoundException`
///     · 有编译问题     → 退出码 **0** + `Warning 3:5 xxx: Unknown KSP command ...`
///     · 编译通过       → 退出码 **0** + 无输出
/// </summary>
public static class KspCompiler
{
    /// <summary>一条编译诊断（错误或警告）。</summary>
    public sealed class Diagnostic
    {
        /// <summary>`error` / `warning`。</summary>
        public string Severity { get; set; } = "warning";
        /// <summary>行号（1 起；未知为 0）。</summary>
        public int Line { get; set; }
        /// <summary>列号（1 起；未知为 0）。</summary>
        public int Column { get; set; }
        /// <summary>诊断消息。</summary>
        public string Message { get; set; } = "";
        /// <summary>原始行（便于回灌给模型时保留完整上下文）。</summary>
        public string Raw { get; set; } = "";
    }

    /// <summary>一次编译的结果。</summary>
    public sealed class CompileResult
    {
        public bool Ok { get; set; }
        /// <summary>进程退出码（**注意：有编译问题时它也可能是 0**）。</summary>
        public int ExitCode { get; set; }
        public List<Diagnostic> Diagnostics { get; set; } = new();
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
        /// <summary>人类可读的结论（含「为何判失败」的依据）。</summary>
        public string Summary { get; set; } = "";
        /// <summary>耗时（毫秒）。</summary>
        public long ElapsedMs { get; set; }
    }

    /// <summary>默认的 kspc 路径：项目 data 目录下的自包含版本。</summary>
    public static string DefaultPath => Path.Combine(AppPaths.DataDir, "kspc", "kspc.exe");

    /// <summary>解析出 kspc 的实际可执行文件路径（找不到返回 null）。</summary>
    public static string? Resolve(string? configured)
    {
        foreach (var p in new[] { configured, DefaultPath })
        {
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// **编译一个 .ksp 文件**。
    /// </summary>
    /// <param name="exePath">kspc.exe 路径（见 <see cref="Resolve"/>）。</param>
    /// <param name="kspPath">要编译的 .ksp 绝对路径。</param>
    /// <param name="obfuscate">是否顺带做混淆。</param>
    public static CompileResult Compile(string exePath, string kspPath, bool obfuscate = false)
    {
        var r = new CompileResult();
        if (!File.Exists(exePath)) { r.Summary = $"找不到编译器：{exePath}"; return r; }
        if (!File.Exists(kspPath)) { r.Summary = $"找不到脚本：{kspPath}"; r.ExitCode = -1; return r; }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppPaths.DataDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--input");
        psi.ArgumentList.Add(kspPath);
        if (obfuscate) psi.ArgumentList.Add("--enable-obfuscation");

        var sw = Stopwatch.StartNew();
        try
        {
            using var p = Process.Start(psi);
            if (p == null) { r.Summary = "无法启动编译器进程。"; return r; }
            r.StdOut = p.StandardOutput.ReadToEnd();
            r.StdErr = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            r.ExitCode = p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            r.Summary = "启动编译器失败：" + ex.Message;
            return r;
        }
        sw.Stop();
        r.ElapsedMs = sw.ElapsedMilliseconds;

        r.Diagnostics = ParseOutput(r.StdOut, r.StdErr);

        // 🔴 **判成败的依据是【输出文本】而非退出码**（实测：有编译问题时退出码也是 0）
        bool hasError = r.Diagnostics.Any(d => d.Severity == "error");
        bool hasWarning = r.Diagnostics.Any(d => d.Severity == "warning");
        bool crashed = r.ExitCode != 0 || r.StdErr.Contains("Exception", StringComparison.OrdinalIgnoreCase);

        if (crashed && r.Diagnostics.Count == 0)
        {
            r.Ok = false;
            r.Summary = $"编译器异常退出（退出码 {r.ExitCode}）：{FirstLine(r.StdErr)}";
        }
        else if (hasError)
        {
            r.Ok = false;
            r.Summary = $"编译失败：{r.Diagnostics.Count(d => d.Severity == "error")} 个错误。";
        }
        else if (hasWarning)
        {
            // 警告不阻断，但要如实报告（模型可能想修掉）
            r.Ok = true;
            r.Summary = $"编译通过，但有 {r.Diagnostics.Count} 条警告 —— 建议逐条核对（未知命令往往是拼写错误）。";
        }
        else
        {
            r.Ok = true;
            r.Summary = "编译通过，无错误无警告。";
        }
        return r;
    }

    /// <summary>
    /// 解析 kspc 的输出。实测格式：`Warning\t3:5\tthis_is_not_a_command: Unknown KSP command ...`
    /// 容错：也接受 `Error`、`error`、`行:列` 用空格分隔、或没有行列号的行。
    /// </summary>
    public static List<Diagnostic> ParseOutput(string stdout, string stderr)
    {
        var list = new List<Diagnostic>();
        foreach (var rawLine in (stdout + "\n" + stderr).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0) continue;
            if (line.StartsWith("Usage:") || line.StartsWith("A compiler program")) continue;

            var d = new Diagnostic { Raw = line };
            var low = line.ToLowerInvariant();
            if (low.StartsWith("error")) d.Severity = "error";
            else if (low.StartsWith("warning") || low.StartsWith("warn")) d.Severity = "warning";
            else if (low.Contains("exception") || low.Contains("error")) d.Severity = "error";
            else continue;                       // 不认识的输出行不当作诊断

            // 切掉前缀，找 `行:列` 或 `行`
            var rest = line;
            int sp = rest.IndexOfAny(new[] { '\t', ' ' });
            if (sp > 0) rest = rest[(sp + 1)..].TrimStart();

            var m = System.Text.RegularExpressions.Regex.Match(rest, @"^(\d+)(?::(\d+))?\s*(.*)$");
            if (m.Success)
            {
                d.Line = int.TryParse(m.Groups[1].Value, out int ln) ? ln : 0;
                d.Column = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int col) ? col : 0;
                d.Message = m.Groups[3].Value.Trim();
            }
            else d.Message = rest;
            list.Add(d);
        }
        return list;
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(无输出)";
        var i = s.IndexOf('\n');
        var l = i < 0 ? s : s[..i];
        return l.Length > 200 ? l[..200] + "…" : l;
    }

    /// <summary>把诊断整理成回灌给模型的一段话（用于「编译错误 → 自纠」闭环）。</summary>
    public static string BuildCorrection(CompileResult r, string scriptPath)
    {
        if (r.Ok && r.Diagnostics.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("【KSP 编译结果 —— 请据此修正脚本，不要坚持原来的写法】");
        sb.AppendLine($"脚本：{Path.GetFileName(scriptPath)}");
        sb.AppendLine($"结论：{r.Summary}");
        if (r.Diagnostics.Count > 0)
        {
            sb.AppendLine("诊断（行:列 消息）：");
            foreach (var d in r.Diagnostics.Take(30))
                sb.AppendLine($"  - [{d.Severity}] {d.Line}:{d.Column} {d.Message}");
            sb.AppendLine();
            sb.AppendLine("请**只改有问题的行**，改完重新调用 compile_ksp 验证；");
            sb.AppendLine("若某条诊断你判断是编译器误报，请明确说明理由，不要默默忽略。");
        }
        if (!r.Ok && r.StdErr.Length > 0)
            sb.AppendLine("编译器 stderr：" + FirstLine(r.StdErr));
        return sb.ToString();
    }
}
