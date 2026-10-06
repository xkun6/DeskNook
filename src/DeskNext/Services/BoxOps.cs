using DeskNext.Model;

namespace DeskNext.Services;

/// <summary>格子成员与布局操作（纯逻辑）：保证同一 key 只归属一处（自由区或某个普通格子）。</summary>
public static class BoxOps
{
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static BoxState? Find(LayoutState s, string id) =>
        s.Boxes.FirstOrDefault(b => b.Id == id);

    /// <summary>key 所在的普通格子；不在任何格子里返回 null。</summary>
    public static BoxState? BoxOfKey(LayoutState s, string key) =>
        s.Boxes.FirstOrDefault(b => b.Kind == BoxKind.Normal && b.ItemKeys.Contains(key, StringComparer.OrdinalIgnoreCase));

    /// <summary>从所有普通格子摘除（不改自由区）。</summary>
    public static void RemoveFromBoxes(LayoutState s, IEnumerable<string> keys)
    {
        var set = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return;
        foreach (var b in s.Boxes)
        {
            if (b.Kind != BoxKind.Normal) continue;
            b.ItemKeys.RemoveAll(set.Contains);
            foreach (var k in set) b.Gone.Remove(k);
        }
    }

    /// <summary>
    /// 把 keys 放进普通格子 target 的 index 位置（index 为移动前列表中的插入位置，同格内移动会自动扣除前移项）。
    /// 同时从自由区和其他格子摘除。
    /// </summary>
    public static void MoveToBox(LayoutState s, IReadOnlyList<string> keys, BoxState target, int index)
    {
        if (target.Kind != BoxKind.Normal) return;
        var ordered = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var set = new HashSet<string>(ordered, StringComparer.OrdinalIgnoreCase);
        index = Math.Clamp(index, 0, target.ItemKeys.Count);
        var before = target.ItemKeys.Take(index).Count(set.Contains);
        RemoveFromBoxes(s, ordered);
        foreach (var k in ordered) s.FreeIcons.Remove(k);
        index = Math.Clamp(index - before, 0, target.ItemKeys.Count);
        target.ItemKeys.InsertRange(index, ordered);
    }

    /// <summary>
    /// 删除（解散）格子。普通格子里在场的项回到所在显示器自由区：从格子左上角起就近找空位、按成员顺序依次放置；
    /// 映射格子直接消失（不涉及任何文件）。返回回到自由区的 key。
    /// </summary>
    public static List<string> Dissolve(LayoutState s, string boxId, IReadOnlyList<string> presentKeys, IReadOnlyList<MonitorGrid> monitors)
    {
        var placed = new List<string>();
        var box = Find(s, boxId);
        if (box == null) return placed;
        var eff = BoxGeometry.Effective(box, monitors, ignoreCollapsed: true);
        s.Boxes.Remove(box);
        if (box.Kind != BoxKind.Normal || eff == null) return placed;

        var (grid, rect) = eff.Value;
        var present = new HashSet<string>(presentKeys, StringComparer.OrdinalIgnoreCase);
        var occupied = BoxGeometry.CoveredCells(s, monitors, grid.Name);
        foreach (var kv in s.FreeIcons)
            if (present.Contains(kv.Key) && string.Equals(kv.Value.Monitor, grid.Name, StringComparison.OrdinalIgnoreCase))
                occupied.Add((kv.Value.Col, kv.Value.Row));

        var col0 = (int)Math.Floor(rect.X / grid.CellW + 1e-6);
        var row0 = (int)Math.Floor(rect.Y / grid.CellH + 1e-6);
        foreach (var key in box.ItemKeys)
        {
            if (!present.Contains(key)) continue;
            var (c, r) = GridLayout.NearestEmpty(grid.Size, occupied, col0, row0);
            occupied.Add((c, r));
            s.FreeIcons[key] = new IconSlot { Monitor = grid.Name, Col = c, Row = r };
            placed.Add(key);
        }
        return placed;
    }
}
