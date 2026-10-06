using System.IO;
using System.Text.Json;
using DeskNook.Model;

namespace DeskNook.Services;

/// <summary>整理计划中的一个格子：复用已有格子（ExistingBoxId 非空）或新建（Rect 已定位）。</summary>
public sealed class BoxPlan
{
    public string Name { get; init; } = "";
    public string? ExistingBoxId { get; init; }
    public string Monitor { get; init; } = "";
    public BoxRect? Rect { get; init; }
    public List<string> Keys { get; init; } = new();
}

public sealed class OrganizePlan
{
    public List<BoxPlan> Boxes { get; } = new();
    public int MovedCount => Boxes.Sum(b => b.Keys.Count);
}

/// <summary>一次整理的撤销记录：只记录这次整理新建的格子与移入的 key（及其整理前的自由区位置）。</summary>
public sealed class OrganizeUndo
{
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public List<string> CreatedBoxIds { get; set; } = new();
    public List<UndoMove> Moves { get; set; } = new();
}

public sealed class UndoMove
{
    public string Key { get; set; } = "";
    public string Monitor { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
}

/// <summary>一键整理（纯逻辑）：按规则把自由区的桌面项归入同名格子（没有则新建并排布），可撤销。</summary>
public static class AutoOrganizer
{
    public const int MinBoxCols = 3;
    public const int MaxBoxCols = 5;
    public const int MinBoxRows = 1;
    public const int MaxBoxRows = 4;

    // ------------------------------------------------------------ 分类

    private static string Norm(string ext) => ext.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();

    /// <summary>项所属分类名；虚拟项、无匹配规则返回 null。</summary>
    public static string? Categorize(DesktopItem item, IReadOnlyList<OrganizeRule> rules)
    {
        if (item.IsVirtual) return null;
        var isDir = item.IsFolder && item.Extension.Length == 0;
        var ext = Norm(item.Extension);
        foreach (var r in rules)
        {
            foreach (var raw in r.Extensions)
            {
                var t = raw.Trim();
                if (t == OrganizeRule.AnyToken) return r.Name;
                if (t.Equals(OrganizeRule.FolderToken, StringComparison.OrdinalIgnoreCase)) { if (isDir) return r.Name; continue; }
                if (!isDir && ext.Length > 0 && Norm(t) == ext) return r.Name;
            }
        }
        return null;
    }

    // ------------------------------------------------------------ 计划

    /// <summary>
    /// items = 全部桌面项。只处理自由区（不在任何普通格子里、非虚拟）的项。
    /// 新格子从主屏（monitors[0]）右侧自上而下、再向左排布，避开已有格子和不参与整理的自由图标。
    /// </summary>
    public static OrganizePlan Plan(LayoutState state, IReadOnlyList<DesktopItem> items,
        IReadOnlyList<OrganizeRule> rules, IReadOnlyList<MonitorGrid> monitors)
    {
        var plan = new OrganizePlan();
        if (monitors.Count == 0) return plan;

        var groups = new Dictionary<string, List<DesktopItem>>();
        var moving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (it.Container.Length != 0) continue;
            if (BoxOps.BoxOfKey(state, it.Key) != null) continue;
            var cat = Categorize(it, rules);
            if (cat == null) continue;
            if (!groups.TryGetValue(cat, out var list)) groups[cat] = list = new List<DesktopItem>();
            list.Add(it);
            moving.Add(it.Key);
        }
        var order = rules.Select(r => r.Name).Distinct().ToList();

        // 占用：现有格子覆盖 + 留在自由区的图标
        var blocked = monitors.ToDictionary(m => m.Name, m => BoxGeometry.CoveredCells(state, monitors, m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (moving.Contains(it.Key) || !state.FreeIcons.TryGetValue(it.Key, out var slot)) continue;
            if (blocked.TryGetValue(slot.Monitor, out var set)) set.Add((slot.Col, slot.Row));
        }

        foreach (var name in groups.Keys.OrderBy(n => order.IndexOf(n)))
        {
            var keys = groups[name].OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).Select(i => i.Key).ToList();
            var existing = state.Boxes.FirstOrDefault(b => b.Kind == BoxKind.Normal && b.Name == name);
            if (existing != null)
            {
                plan.Boxes.Add(new BoxPlan { Name = name, ExistingBoxId = existing.Id, Keys = keys });
                continue;
            }

            foreach (var grid in monitors)
            {
                var (cols, rows) = SizeFor(keys.Count, grid);
                var (wCells, hCells) = BoxGeometry.CellsFor(cols, rows, grid.CellH);
                var spot = FindRightSpot(grid.Size, blocked[grid.Name], wCells, hCells);
                if (spot == null) continue;
                var (c, r) = spot.Value;
                for (var i = 0; i < wCells; i++)
                    for (var j = 0; j < hCells; j++) blocked[grid.Name].Add((c + i, r + j));
                plan.Boxes.Add(new BoxPlan
                {
                    Name = name, Monitor = grid.Name, Keys = keys,
                    Rect = new BoxRect(c * grid.CellW, r * grid.CellH, BoxGeometry.WidthFor(cols, grid.CellW), BoxGeometry.HeightFor(rows, grid.CellH)),
                });
                break;
            }
            // 所有显示器都放不下：这一类留在自由区
        }
        return plan;
    }

    /// <summary>按内容数估算列数与行数，受最小/最大尺寸及显示器大小约束。</summary>
    public static (int Cols, int Rows) SizeFor(int count, MonitorGrid grid)
    {
        var cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Max(1, count))), MinBoxCols, MaxBoxCols);
        cols = Math.Min(cols, grid.Cols);
        var rows = Math.Clamp((count + cols - 1) / cols, MinBoxRows, MaxBoxRows);
        var (_, hCells) = BoxGeometry.CellsFor(cols, rows, grid.CellH);
        while (hCells > grid.Rows && rows > 1)
        {
            rows--;
            (_, hCells) = BoxGeometry.CellsFor(cols, rows, grid.CellH);
        }
        return (cols, rows);
    }

    /// <summary>从最右侧的列开始，每列自上而下找第一块放得下的空地。</summary>
    public static (int Col, int Row)? FindRightSpot(GridSize grid, ISet<(int, int)> blocked, int wCells, int hCells)
    {
        for (var c = grid.Cols - wCells; c >= 0; c--)
            for (var r = 0; r + hCells <= grid.Rows; r++)
            {
                var free = true;
                for (var i = 0; i < wCells && free; i++)
                    for (var j = 0; j < hCells; j++)
                        if (blocked.Contains((c + i, r + j))) { free = false; break; }
                if (free) return (c, r);
            }
        return null;
    }

    // ------------------------------------------------------------ 执行 / 撤销

    /// <summary>把计划应用到布局，返回撤销记录（计划为空返回 null）。</summary>
    public static OrganizeUndo? Apply(LayoutState state, OrganizePlan plan, DateTime nowUtc)
    {
        if (plan.MovedCount == 0) return null;
        var undo = new OrganizeUndo { CreatedUtc = nowUtc };
        foreach (var bp in plan.Boxes)
        {
            BoxState? box;
            if (bp.ExistingBoxId != null) box = BoxOps.Find(state, bp.ExistingBoxId);
            else
            {
                box = new BoxState { Id = BoxOps.NewId(), Name = bp.Name, Kind = BoxKind.Normal, Monitor = bp.Monitor, Rect = bp.Rect!.Clone() };
                state.Boxes.Add(box);
                undo.CreatedBoxIds.Add(box.Id);
            }
            if (box == null) continue;
            foreach (var k in bp.Keys)
            {
                var slot = state.FreeIcons.GetValueOrDefault(k);
                undo.Moves.Add(new UndoMove { Key = k, Monitor = slot?.Monitor ?? bp.Monitor, Col = slot?.Col ?? 0, Row = slot?.Row ?? 0 });
            }
            BoxOps.MoveToBox(state, bp.Keys, box, int.MaxValue);
        }
        return undo;
    }

    /// <summary>
    /// 撤销：只回滚这次整理移入的 key（回到整理前的自由区位置，该位置被别的自由图标占用时把对方挪开；
    /// 被格子覆盖则就近找空位）并删除变空的新建格子；用户之后新加的内容保留。presentKeys = 当前仍存在的桌面项。
    /// 返回回到自由区的 key 数。
    /// </summary>
    public static int ApplyUndo(LayoutState state, OrganizeUndo undo, IReadOnlyCollection<string> presentKeys, IReadOnlyList<MonitorGrid> monitors)
    {
        var present = new HashSet<string>(presentKeys, StringComparer.OrdinalIgnoreCase);
        var moves = undo.Moves.Where(m => present.Contains(m.Key)).ToList();
        var returning = new HashSet<string>(moves.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
        BoxOps.RemoveFromBoxes(state, returning);

        foreach (var id in undo.CreatedBoxIds)
        {
            var box = BoxOps.Find(state, id);
            if (box != null && box.ItemKeys.Count == 0) state.Boxes.Remove(box);
        }
        if (monitors.Count == 0) return 0;

        var grids = monitors.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var occupied = new Dictionary<string, HashSet<(int, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in monitors)
        {
            var set = BoxGeometry.CoveredCells(state, monitors, m.Name);
            foreach (var kv in state.FreeIcons)
                if (!returning.Contains(kv.Key) && present.Contains(kv.Key) && string.Equals(kv.Value.Monitor, m.Name, StringComparison.OrdinalIgnoreCase))
                    set.Add((kv.Value.Col, kv.Value.Row));
            occupied[m.Name] = set;
        }

        var restored = 0;
        foreach (var mv in moves)
        {
            var mon = grids.ContainsKey(mv.Monitor) ? grids[mv.Monitor].Name : monitors[0].Name;
            var grid = grids[mon];
            var covered = BoxGeometry.CoveredCells(state, monitors, mon);
            var (c, r) = (mv.Col, mv.Row);
            if (c >= grid.Cols || r >= grid.Rows || covered.Contains((c, r)))
            {
                (c, r) = GridLayout.NearestEmpty(grid.Size, occupied[mon], c, r);
            }
            else
            {
                // 原位置被别的自由图标占用：以回归的 key 为准，把对方挪到最近的空位
                var other = state.FreeIcons.FirstOrDefault(kv => !returning.Contains(kv.Key) && present.Contains(kv.Key)
                    && string.Equals(kv.Value.Monitor, mon, StringComparison.OrdinalIgnoreCase) && kv.Value.Col == c && kv.Value.Row == r);
                if (other.Key != null)
                {
                    var tmp = new HashSet<(int, int)>(occupied[mon]) { (c, r) };
                    var (oc, orow) = GridLayout.NearestEmpty(grid.Size, tmp, c, r);
                    other.Value.Col = oc; other.Value.Row = orow;
                    occupied[mon].Add((oc, orow));
                }
                else if (occupied[mon].Contains((c, r)))
                {
                    (c, r) = GridLayout.NearestEmpty(grid.Size, occupied[mon], c, r);
                }
            }
            occupied[mon].Add((c, r));
            state.FreeIcons[mv.Key] = new IconSlot { Monitor = mon, Col = c, Row = r };
            restored++;
        }
        return restored;
    }
}

/// <summary>撤销记录持久化：data\organize-undo.json（程序重启后仍可撤销）。</summary>
public sealed class OrganizeUndoStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; }

    public OrganizeUndoStore(string? path = null)
    {
        FilePath = path ?? AppPaths.OrganizeUndo;
    }

    public OrganizeUndo? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var u = JsonSerializer.Deserialize<OrganizeUndo>(File.ReadAllText(FilePath), Options);
            if (u == null) return null;
            u.CreatedBoxIds ??= new();
            u.Moves = (u.Moves ?? new()).Where(m => m != null && !string.IsNullOrEmpty(m.Key)).ToList();
            return u.Moves.Count == 0 ? null : u;
        }
        catch (Exception ex)
        {
            Log.Error($"organize-undo.json 损坏，已忽略：{FilePath}", ex);
            return null;
        }
    }

    public bool Save(OrganizeUndo undo)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(undo, Options));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("保存 organize-undo.json 失败", ex);
            return false;
        }
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) { Log.Error("删除 organize-undo.json 失败", ex); }
    }
}
