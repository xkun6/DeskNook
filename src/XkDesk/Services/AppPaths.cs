using System.IO;

namespace XkDesk.Services;

/// <summary>
/// 数据位置的唯一来源。数据根：程序位于 Program Files / Program Files (x86) 之下时用 %AppData%\XkDesk
/// （该处程序目录通常不可写），否则一律用 &lt;exe 目录&gt;\data。首次访问时尝试创建数据根。
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    static AppPaths()
    {
        try { Directory.CreateDirectory(DataDir); } catch { /* 不可写：各处读写自行容错 */ }
    }

    public static string Layout => Path.Combine(DataDir, "layout.json");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string OrganizeUndo => Path.Combine(DataDir, "organize-undo.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string RunningFlag => Path.Combine(DataDir, "running.flag");
    public static string ShellExtDir => Path.Combine(DataDir, "shellext");
    public static string IconsDir => Path.Combine(DataDir, "icons");

    /// <summary>纯函数（可注入路径供单测）：按程序目录决定数据根。</summary>
    public static string ResolveDataDir(string baseDir, string programFiles, string programFilesX86, string appData)
    {
        if (IsUnder(baseDir, programFiles) || IsUnder(baseDir, programFilesX86))
            return Path.Combine(appData, "XkDesk");
        return Path.Combine(baseDir, "data");
    }

    /// <summary>path 是否在 root 之下（或等于 root）；不区分大小写，必须以目录分隔符为边界。</summary>
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
