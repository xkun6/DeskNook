using DeskNext.Model;

namespace DeskNext.Services;

public sealed class ReconcileResult
{
    /// <summary>本次新分配位置的 key。</summary>
    public List<string> Placed { get; } = new();
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

        // 1. 在场且合法的 slot 保留
        var needPlace = new List<string>();
        foreach (var key in currentKeys)
        {
            if (inBox.Contains(key)) continue;
            if (state.FreeIcons.TryGetValue(key, out var s)
                && grids.TryGetValue(s.Monitor, out var g)
                && s.Col >= 0 && s.Col < g.Cols && s.Row >= 0 && s.Row < g.Rows
                && occupied[s.Monitor].Add((s.Col, s.Row)))
            {
                s.LastSeenUtc = null;
            }
            else needPlace.Add(key);
        }

        // 2. 新项/非法项
        foreach (var key in needPlace)
        {
            if (monitors.Count == 0) break;
            string monName; int col, row;
            state.FreeIcons.TryGetValue(key, out var old);
            if (old != null && grids.ContainsKey(old.Monitor))
            {
                // 越界/冲突：在原显示器就近找空位
                monName = monitors.First(m => string.Equals(m.Name, old.Monitor, StringComparison.OrdinalIgnoreCase)).Name;
                (col, row) = GridLayout.NearestEmpty(grids[monName], occupied[monName], old.Col, old.Row);
            }
            else if (old != null)
            {
                // 显示器消失：回退主显示器，按原行列就近
                monName = monitors[0].Name;
                (col, row) = GridLayout.NearestEmpty(grids[monName], occupied[monName], old.Col, old.Row);
            }
            else
            {
                monName = monitors[0].Name;
                (col, row) = GridLayout.FirstEmpty(grids[monName], occupied[monName]);
            }
            occupied[monName].Add((col, row));
            state.FreeIcons[key] = new IconSlot { Monitor = monName, Col = col, Row = row };
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
