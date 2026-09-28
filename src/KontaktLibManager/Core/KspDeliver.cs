using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KontaktLibManager.Core;

/// <summary>
/// **KSP 脚本交付到 Kontakt**（「KSP 能力深化」第三步）。
///
/// **为什么需要它**：前两步只到「写出来 + 编译通过」，**但脚本还在临时目录里** ——
/// 用户拿不到、Kontakt 也加载不到。这一步负责**把脚本放到正确位置并给出加载指引**。
///
/// **📌 关于「放到哪」的如实说明**：
///   KSP 脚本**不是独立文件被 Kontakt 扫描的** —— 它**存在 NKI 乐器内部**（`Script` 槽位）。
///   **没有公开的「把 .ksp 塞进 NKI」的开源方案**（NKI 是专有二进制格式）。
///   所以本类做的是**两件【确实可行】的事**：
///     ① **把脚本写到用户指定/乐器所在的目录**（作为一个 `.ksp` 文件交付，便于归档与手动粘贴）；
///     ② **给出【在 Kontakt 里加载】的分步指引**（用户复制粘贴到 Script Editor）。
///
/// **⚠️ 不假装能自动注入 NKI** —— 那需要逆向 NKI 格式，本工具不做。
/// </summary>
public static class KspDeliver
{
    public sealed class Result
    {
        public bool Ok { get; set; }
        /// <summary>脚本写到了哪里。</summary>
        public string Path { get; set; } = "";
        /// <summary>在 Kontakt 里加载的分步指引。</summary>
        public List<string> Steps { get; set; } = new();
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// **交付脚本**：写到 <paramref name="targetDir"/>（不存在则创建）。
    /// </summary>
    /// <param name="script">脚本内容。</param>
    /// <param name="targetDir">目标目录（如某个音色库目录）。空 = 默认交付目录。</param>
    /// <param name="fileName">文件名（不含扩展名）；空 = 按时间戳生成。</param>
    public static Result Deliver(string script, string targetDir, string fileName)
    {
        var r = new Result();
        if (string.IsNullOrWhiteSpace(script)) { r.Message = "脚本为空。"; return r; }

        var dir = string.IsNullOrWhiteSpace(targetDir)
            ? Path.Combine(AppPaths.DataDir, "ksp-delivered")
            : targetDir;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            r.Message = "无法创建目标目录：" + ex.Message;
            return r;
        }

        var name = string.IsNullOrWhiteSpace(fileName)
            ? "script-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
            : Sanitize(fileName);
        var path = Path.Combine(dir, name + ".ksp");

        try
        {
            // **带 BOM 的 UTF-8** —— KSP 编辑器对中文注释友好些（与 .nicnt 处理一致）
            File.WriteAllText(path, script, new System.Text.UTF8Encoding(true));
            r.Ok = true;
            r.Path = path;
            r.Steps = BuildSteps(path, script);
            r.Message = $"✅ 脚本已交付到：{path}（{script.Length} 字符）";
        }
        catch (Exception ex)
        {
            r.Message = "写入失败：" + ex.Message;
        }
        return r;
    }

    /// <summary>生成「在 Kontakt 里加载」的分步指引。</summary>
    public static List<string> BuildSteps(string kspPath, string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n').Length;
        return new List<string>
        {
            "① 在 Kontakt 里**打开目标乐器**（双击 .nki，或从 Libraries 面板加载）",
            "② 点乐器右上角的 **扳手图标（Edit Mode / 编辑模式）**",
            "③ 在左侧栏选 **Script Editor（脚本编辑器）**",
            $"④ 选一个空的 **Script 槽位**（Slot 1–5）",
            $"⑤ 打开刚交付的脚本文件（{lines} 行）：**{kspPath}**",
            "   用记事本/VS Code 打开 → 全选 → 复制",
            "⑥ 回到 Kontakt 的 Script Editor → 粘贴进编辑框（**先清空原有内容**）",
            "⑦ 点 **Apply（应用）** —— 若脚本有语法错，Kontakt 会在下方报错",
            "⑧ **保存乐器**（File → Save As，或 Ctrl+S）—— **不保存则脚本不会留在乐器里**",
            "",
            "⚠️ **注意**：",
            "   · KSP 脚本**存在 NKI 内部**，不是独立文件被扫描的 —— 所以必须手动粘贴 + 保存乐器",
            "   · 本工具**不会自动注入 NKI**（那是专有二进制格式，没有可靠的开源方案）",
            "   · 建议**先备份原 .nki**，再改脚本",
        };
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }
}
