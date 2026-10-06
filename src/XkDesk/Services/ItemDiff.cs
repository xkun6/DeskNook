using XkDesk.Model;

namespace XkDesk.Services;

public sealed class ItemDiffResult
{
    public List<DesktopItem> Added { get; } = new();
    public List<DesktopItem> Removed { get; } = new();
    public List<DesktopItem> Updated { get; } = new();
    public List<(DesktopItem Old, DesktopItem New)> Renamed { get; } = new();
    public bool IsEmpty => Added.Count + Removed.Count + Updated.Count + Renamed.Count == 0;
}

/// <summary>桌面项集合差异（纯逻辑，Key 忽略大小写）。</summary>
public static class ItemDiff
{
    public static ItemDiffResult Compute(IReadOnlyList<DesktopItem> oldItems, IReadOnlyList<DesktopItem> newItems,
        IReadOnlyDictionary<string, string>? renames = null)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var oldMap = oldItems.GroupBy(i => i.Key, cmp).ToDictionary(g => g.Key, g => g.First(), cmp);
        var newMap = newItems.GroupBy(i => i.Key, cmp).ToDictionary(g => g.Key, g => g.First(), cmp);
        var res = new ItemDiffResult();
        var renamedOld = new HashSet<string>(cmp);
        var renamedNew = new HashSet<string>(cmp);

        if (renames != null)
            foreach (var (o, n) in renames)
            {
                if (cmp.Equals(o, n) || renamedOld.Contains(o) || renamedNew.Contains(n)) continue;
                if (oldMap.TryGetValue(o, out var oi) && !newMap.ContainsKey(o) && newMap.TryGetValue(n, out var ni) && !oldMap.ContainsKey(n))
                {
                    res.Renamed.Add((oi, ni));
                    renamedOld.Add(o);
                    renamedNew.Add(n);
                }
            }

        foreach (var ni in newItems)
        {
            if (renamedNew.Contains(ni.Key)) continue;
            if (!oldMap.TryGetValue(ni.Key, out var oi)) res.Added.Add(ni);
            else if (oi.Fingerprint != ni.Fingerprint) res.Updated.Add(ni);
        }
        foreach (var oi in oldItems)
            if (!renamedOld.Contains(oi.Key) && !newMap.ContainsKey(oi.Key)) res.Removed.Add(oi);
        return res;
    }
}
