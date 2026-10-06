using System.Globalization;
using System.IO;
using System.Text;

namespace DeskNook.Services;

/// <summary>
/// 极简文件日志：data\logs\desknook-yyyy-MM-dd.log（按写入那一刻的本地日期分文件），线程安全，追加写，永不抛异常。
/// 保留天数由 <see cref="SetRetention"/> 设置；调用它之前不做任何清理（启动早期读设置前就会写日志，
/// 若用默认值清理会误删用户设了更长保留期的文件）。设置过之后，每天首次写日志时也会清理一次（程序常驻跨天）。
/// 只清理匹配 desknook-日期.log 的文件，旧的 desknook.log 与其他文件一律不碰。
/// </summary>
internal static class Log
{
    private const string FilePrefix = "desknook-";
    private const string FileSuffix = ".log";
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly object Gate = new();
    /// <summary>保留天数（含今天）；0 = 尚未设置，此时不清理。</summary>
    private static int _retentionDays;
    /// <summary>上次清理时的本地日期；用于“每天首次写日志时清理一次”。</summary>
    private static DateTime _lastCleanDate = DateTime.MinValue;

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    /// <summary>设置日志保留天数（含今天，至少 1）并立即清理一次。</summary>
    public static void SetRetention(int days)
    {
        var removed = 0;
        var kept = 0;
        try
        {
            lock (Gate)
            {
                _retentionDays = Math.Max(1, days);
                kept = _retentionDays;
                removed = CleanExpired(DateTime.Today);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
        if (removed > 0) Info($"已清理 {removed} 个超过 {kept} 天的日志文件");
    }

    /// <summary>
    /// 在给定文件名（不含目录）里找出已过期的日志：文件名须为 desknook-yyyy-MM-dd.log，
    /// 日期早于 today - (days - 1) 的过期（保留含今天在内的 days 天）；文件名解析失败的不返回，未来日期的不删。
    /// </summary>
    internal static IReadOnlyList<string> ExpiredFiles(IEnumerable<string> fileNames, DateTime today, int days)
    {
        var cutoff = today.Date.AddDays(-(Math.Max(1, days) - 1));
        var result = new List<string>();
        foreach (var name in fileNames)
        {
            if (name == null || !name.StartsWith(FilePrefix, StringComparison.Ordinal) || !name.EndsWith(FileSuffix, StringComparison.Ordinal)) continue;
            if (name.Length < FilePrefix.Length + FileSuffix.Length) continue;
            var datePart = name.Substring(FilePrefix.Length, name.Length - FilePrefix.Length - FileSuffix.Length);
            if (!DateTime.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (date < cutoff) result.Add(name);
        }
        return result;
    }

    /// <summary>清理过期日志并记下清理日期，返回删除个数；永不抛异常。调用方须已持有 Gate，且不得在此处写日志。</summary>
    private static int CleanExpired(DateTime today)
    {
        _lastCleanDate = today.Date;
        var removed = 0;
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return 0;
            var names = Directory.EnumerateFiles(AppPaths.LogsDir, FilePrefix + "*" + FileSuffix)
                .Select(p => Path.GetFileName(p)!).ToList();
            foreach (var name in ExpiredFiles(names, today, _retentionDays))
            {
                try
                {
                    File.Delete(Path.Combine(AppPaths.LogsDir, name));
                    removed++;
                }
                catch
                {
                    // 单个文件删不掉（被占用等）就跳过，下次再试
                }
            }
        }
        catch
        {
            // 枚举失败等一律忽略
        }
        return removed;
    }

    private static void Write(string level, string message)
    {
        try
        {
            var now = DateTime.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            var removed = 0;
            var days = 0;
            lock (Gate)
            {
                // 跨天常驻：已设置保留天数且今天还没清理过，先清理一次（先算后记日志，避免持锁递归）
                if (_retentionDays > 0 && now.Date != _lastCleanDate)
                {
                    removed = CleanExpired(now);
                    days = _retentionDays;
                }
                var path = Path.Combine(AppPaths.LogsDir, $"{FilePrefix}{now.ToString(DateFormat, CultureInfo.InvariantCulture)}{FileSuffix}");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            if (removed > 0) Info($"已清理 {removed} 个超过 {days} 天的日志文件");
        }
        catch
        {
            // 日志失败不能影响主流程（尤其是崩溃兜底路径）
        }
    }
}
