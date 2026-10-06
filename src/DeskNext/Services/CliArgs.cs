namespace DeskNext.Services;

/// <summary>命令行参数解析（纯函数，便于单测）。</summary>
internal static class CliArgs
{
    /// <summary>解析 --autostart=on|off（不区分大小写）；没有该参数或值非法返回 null。多个时取最后一个合法值。</summary>
    public static bool? ParseAutostart(IEnumerable<string> args)
    {
        bool? result = null;
        foreach (var arg in args)
        {
            var a = arg.Trim().ToLowerInvariant();
            if (a == "--autostart=on") result = true;
            else if (a == "--autostart=off") result = false;
        }
        return result;
    }
}
