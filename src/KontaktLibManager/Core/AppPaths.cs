using System.IO;

namespace KontaktLibManager.Core;

/// <summary>应用路径约定：便携优先，数据跟随应用目录。</summary>
public static class AppPaths
{
    /// <summary>默认音色库根目录（用户环境）。</summary>
    public const string DefaultRoot = @"D:\Kontakt Libraries";

    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>
    /// 应用数据目录（索引库 / 封面缓存 / 日志）。
    /// 默认跟随应用目录（便携优先）；可用环境变量 KLM_DATA_DIR 覆盖
    /// （自检/播种等工具进程需要指向应用目录时使用）。
    /// </summary>
    public static string DataDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("KLM_DATA_DIR");
            var dir = !string.IsNullOrWhiteSpace(custom) ? custom! : Path.Combine(BaseDir, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DbPath => Path.Combine(DataDir, "index.db");

    public static string WebViewDataDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "webview2");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string WwwRoot => Path.Combine(BaseDir, "wwwroot");
}
