using XkDesk.Model;
using XkDesk.Native;

namespace XkDesk.Services;

/// <summary>桌面项排序（纯逻辑）：name | size | type | date，名称使用 Explorer 的自然排序。</summary>
public static class ItemSorter
{
    private static readonly Comparer<string> Natural = Comparer<string>.Create(Win32.StrCmpLogicalW);

    public static IEnumerable<DesktopItem> Sort(IEnumerable<DesktopItem> items, string key)
    {
        var list = items.ToList();
        // 虚拟项（此电脑、回收站…）始终在最前，保持枚举顺序
        var virtuals = list.Where(i => i.IsVirtual);
        var real = list.Where(i => !i.IsVirtual);
        IEnumerable<DesktopItem> sorted = key switch
        {
            "size" => real.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Size).ThenBy(i => i.DisplayName, Natural),
            "type" => real.OrderBy(i => i.IsFolder ? "" : i.Extension).ThenBy(i => i.DisplayName, Natural),
            "date" => real.OrderByDescending(i => i.Modified).ThenBy(i => i.DisplayName, Natural),
            _ => real.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.DisplayName, Natural),
        };
        return virtuals.Concat(sorted);
    }
}
