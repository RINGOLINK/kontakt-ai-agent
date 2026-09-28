using System.Runtime.InteropServices;
using System.Text;

namespace KontaktLibManager.Core;

/// <summary>
/// **用 IShellLink COM 接口创建 Windows 快捷方式（Unicode 原生）**。
///
/// 为什么不用 `WScript.Shell`（实测踩到）：
///   `WScript.Shell.CreateShortcut(...)` 的 **`TargetPath` 属性会把路径转成 ANSI**，
///   在中文代码页（CP936）下遇到 `ø`（U+00F8）这类不在 CP936 里的字符就抛
///   `E_INVALIDARG: Value does not fall within the expected range`，
///   导致含特殊字符路径的库**无法建立快捷方式**（实测 `Have Audio - Nørdic Cello`）。
///   （同一对象的 `WorkingDirectory` / `Description` 走 BSTR，所以不受影响。）
///
/// `IShellLinkW` 全程使用 `LPWStr`（UTF-16），没有 ANSI 转换环节，因此没有这个问题。
/// 顺带一提：`GetShortPathName` 取 8.3 短名也救不了 —— 很多卷已禁用 8.3 名称生成，
/// 会原样返回长路径（实测确认）。
/// </summary>
public static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>创建一个指向目录/文件的快捷方式。返回是否成功。</summary>
    public static bool Create(string lnkPath, string targetPath, string? description = null, string? workingDir = null)
    {
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            var sl = (IShellLinkW)link;
            sl.SetPath(targetPath);
            sl.SetWorkingDirectory(workingDir ?? targetPath);
            if (!string.IsNullOrEmpty(description)) sl.SetDescription(description);
            ((IPersistFile)link).Save(lnkPath, true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (link != null)
            {
                try { Marshal.FinalReleaseComObject(link); } catch { }
            }
        }
    }

    /// <summary>读取快捷方式的目标路径；失败返回空串。</summary>
    public static string ReadTarget(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch { return ""; }
        finally
        {
            if (link != null)
            {
                try { Marshal.FinalReleaseComObject(link); } catch { }
            }
        }
    }
}
