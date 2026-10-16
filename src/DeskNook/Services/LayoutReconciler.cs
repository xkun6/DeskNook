using DeskNook.Model;

namespace DeskNook.Services;

public sealed class ReconcileResult
{
    /// <summary>本次新分配位置的 key。</summary>
    public List<string> Placed { get; } = new();
    /// <summary>本次从临时位置回到原显示器原位置的 key。</summary>
    public List<string> Returned { get; } = new();
}

/// <summary>把当前桌面项集合与持久化布局对账（纯逻辑）：自由区位置 + 普通格子成员，key 全局唯一归属。</summary>
public static class LayoutReconciler
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    public static ReconcileResult Reconcile(LayoutState state, IReadOnlyList<string> currentKeys,
        IReadOnlyList<MonitorGrid> monitors, DateTime nowUtc, TimeSpan? retention = null)
    {
        var result = new ReconcileResult();
        var keep = retention ?? DefaultRetention;
        var grids = monitors.ToDictionary(m => m.Name, m => m.Size, StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(currentKeys, StringComparer.OrdinalIgnoreCase);

        // 0. 格子成员：key 全局唯一（先到先得，格子内去重）；在格子里的 key 不能再有自由区位置；消失项按 7 天规则清理
        var inBox = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in state.Boxes)
        {
            box.ItemKeys ??= new();
            box.Gone = box.Gone == null ? new(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DateTime>(box.Gone, StringComparer.OrdinalIgnoreCase);
            if (box.Kind == BoxKind.Mapped) { box.ItemKeys.Clear(); box.Gone.Clear(); continue; }
            var keys = new List<string>();
            foreach (var k in box.ItemKeys)
            {
                if (!inBox.Add(k)) continue; // 已属于更靠前的格子
                if (present.Contains(k))
                {
                    box.Gone.Remove(k);
                }
                else
                {
                    if (!box.Gone.TryGetValue(k, out var since)) box.Gone[k] = since = nowUtc;
                    if (nowUtc - since > keep) { box.Gone.Remove(k); continue; }
                }
                keys.Add(k);
            }
            box.ItemKeys = keys;
            foreach (var g in box.Gone.Keys.ToList())
                if (!inBox.Contains(g)) box.Gone.Remove(g);
        }
        foreach (var k in inBox) state.FreeIcons.Remove(k);

        var occupied = new Dictionary<string, HashSet<(int, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in monitors) occupied[m.Name] = BoxGeometry.CoveredCells(state, monitors, m.Name); // 格子覆盖的格子不可放自由图标

        // 自身显示器不在场、因而临时落到别的屏上的格子所覆盖的单元：被它们盖住的图标只是暂时让位，要记下原位（Home）
        var tempCovered = new Dictionary<string, HashSet<(int, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in state.Boxes)
        {
            if (grids.ContainsKey(b.Monitor)) continue;
            var eff = BoxGeometry.Effective(b, monitors);
            if (eff == null) continue;
            if (!tempCovered.TryGetValue(eff.Value.Grid.Name, out var tc)) tempCovered[eff.Value.Grid.Name] = tc = new();
            BoxGeometry.AddCovered(tc, eff.Value.Rect, eff.Value.Grid.CellW, eff.Value.Grid.CellH);
        }

        // slot 位置合法（显示器存在、行列在网格内、未被格子覆盖、未被同屏其他图标占用）则占位并返回 true
        bool TryKeep(IconSlot s)
        {
            if (!grids.TryGetValue(s.Monitor, out var g)
                || s.Col < 0 || s.Col >= g.Cols || s.Row < 0 || s.Row >= g.Rows
                || !occupied[s.Monitor].Add((s.Col, s.Row))) return false;
            s.LastSeenUtc = null;
            return true;
        }

        // 1. 在场且合法的 slot 保留；原显示器已回来的临时位置 slot 先放进待回位列表
        var needPlace = new List<string>();
        var toReturn = new List<string>();
        foreach (var key in currentKeys)
        {
            if (inBox.Contains(key)) continue;
            state.FreeIcons.TryGetValue(key, out var s);
            if (s?.Home != null && grids.ContainsKey(s.Home.Monitor)) toReturn.Add(key);
            else if (s == null || !TryKeep(s)) needPlace.Add(key);
        }

        // 回位：原位还空着就回去；原位仍被临时落到该屏的格子盖住则继续等；被别的图标占了就放弃回位，按当前位置照旧校验
        foreach (var key in toReturn)
        {
            var s = state.FreeIcons[key];
            var h = s.Home!;
            var g = grids[h.Monitor];
            var inGrid = h.Col >= 0 && h.Col < g.Cols && h.Row >= 0 && h.Row < g.Rows;
            if (inGrid && occupied[h.Monitor].Add((h.Col, h.Row)))
            {
                s.Monitor = h.Monitor; s.Col = h.Col; s.Row = h.Row;
                s.Home = null;
                s.LastSeenUtc = null;
                result.Returned.Add(key);
                continue;
            }
            if (!(inGrid && tempCovered.TryGetValue(h.Monitor, out var tc) && tc.Contains((h.Col, h.Row)))) s.Home = null;
            if (!TryKeep(s)) needPlace.Add(key);
        }

        // 2. 新项/非法项
        foreach (var key in needPlace)
        {
            if (monitors.Count == 0) break;
            string monName; int col, row;
            state.FreeIcons.TryGetValue(key, out var old);
            IconHome? home = null;
            if (old != null && grids.ContainsKey(old.Monitor))
            {
                // 越界/冲突：在原显示器就近找空位；只有被临时落到本屏的格子盖住才算暂时让位，记下原位
                monName = monitors.First(m => string.Equals(m.Name, old.Monitor, StringComparison.OrdinalIgnoreCase)).Name;
                (col, row) = GridLayout.NearestEmpty(grids[monName], occupied[monName], old.Col, old.Row);
                home = old.Home;
                if (tempCovered.TryGetValue(monName, out var tc) && tc.Contains((old.Col, old.Row)))
                    home ??= new IconHome { Monitor = old.Monitor, Col = old.Col, Row = old.Row };
            }
            else if (old != null)
            {
                // 显示器消失：回退主显示器，按原行列就近；记下原位，显示器回来后回去
                monName = monitors[0].Name;
                (col, row) = GridLayout.NearestEmpty(grids[monName], occupied[monName], old.Col, old.Row);
                home = old.Home ?? new IconHome { Monitor = old.Monitor, Col = old.Col, Row = old.Row };
            }
            else
            {
                monName = monitors[0].Name;
                (col, row) = GridLayout.FirstEmpty(grids[monName], occupied[monName]);
            }
            occupied[monName].Add((col, row));
            state.FreeIcons[key] = new IconSlot { Monitor = monName, Col = col, Row = row, Home = home };
            result.Placed.Add(key);
        }

        // 3. 消失项：记录时间，超期清理
        foreach (var kv in state.FreeIcons.ToList())
        {
            if (present.Contains(kv.Key)) continue;
            kv.Value.LastSeenUtc ??= nowUtc;
            if (nowUtc - kv.Value.LastSeenUtc.Value > keep) state.FreeIcons.Remove(kv.Key);
        }
        return result;
    }

    /// <summary>
    /// 旧版布局用 GDI 设备名（\\.\DISPLAYn）作显示器主键：把自由图标位置（含 Home）与格子里等于当前某个设备名的 Monitor 改写为稳定 ID。
    /// 当前不在场的旧名保持原样，等它出现再迁。返回改写条数。
    /// </summary>
    public static int MigrateMonitorNames(LayoutState s, IReadOnlyDictionary<string, string> deviceNameToId)
    {
        var map = new Dictionary<string, string>(deviceNameToId, StringComparer.OrdinalIgnoreCase);
        var n = 0;
        string Map(string monitor)
        {
            if (monitor == null || !map.TryGetValue(monitor, out var id) || id == monitor) return monitor!;
            n++;
            return id;
        }
        foreach (var slot in s.FreeIcons.Values)
        {
            slot.Monitor = Map(slot.Monitor);
            if (slot.Home != null) slot.Home.Monitor = Map(slot.Home.Monitor);
        }
        foreach (var box in s.Boxes) box.Monitor = Map(box.Monitor);
        return n;
    }

    /// <summary>改名：把旧 key 的位置/格子成员关系转给新 key。</summary>
    public static void Rename(LayoutState s, string oldKey, string newKey)
    {
        foreach (var box in s.Boxes)
        {
            var i = box.ItemKeys.FindIndex(k => string.Equals(k, oldKey, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                box.ItemKeys[i] = newKey;
                box.Gone.Remove(oldKey);
            }
        }
        if (!s.FreeIcons.Remove(oldKey, out var slot)) return;
        slot.LastSeenUtc = null;
        s.FreeIcons[newKey] = slot;
    }

    /// <summary>设为自由图标位置（同时从所有格子摘除，保证唯一归属）。</summary>
    public static void Move(LayoutState s, string key, string monitor, int col, int row)
    {
        BoxOps.RemoveFromBoxes(s, new[] { key });
        s.FreeIcons[key] = new IconSlot { Monitor = monitor, Col = col, Row = row };
    }
}
