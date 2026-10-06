using XkDesk.Model;

namespace XkDesk.Services;

public sealed class ReconcileResult
{
    /// <summary>本次新分配位置的 key。</summary>
    public List<string> Placed { get; } = new();
}

/// <summary>把当前桌面项集合与持久化布局对账（纯逻辑）。</summary>
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
        var occupied = new Dictionary<string, HashSet<(int, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in monitors) occupied[m.Name] = new();

        // 1. 在场且合法的 slot 保留
        var needPlace = new List<string>();
        foreach (var key in currentKeys)
        {
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

    /// <summary>改名：把旧 key 的位置转给新 key。</summary>
    public static void Rename(LayoutState s, string oldKey, string newKey)
    {
        if (!s.FreeIcons.Remove(oldKey, out var slot)) return;
        slot.LastSeenUtc = null;
        s.FreeIcons[newKey] = slot;
    }

    public static void Move(LayoutState s, string key, string monitor, int col, int row)
    {
        s.FreeIcons[key] = new IconSlot { Monitor = monitor, Col = col, Row = row };
    }
}
