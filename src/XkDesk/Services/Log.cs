using System.IO;
using System.Text;

namespace XkDesk.Services;

/// <summary>极简文件日志：%AppData%\XkDesk\logs\xkdesk.log，线程安全，追加写，永不抛异常。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XkDesk", "logs", "xkdesk.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程（尤其是崩溃兜底路径）
        }
    }
}
