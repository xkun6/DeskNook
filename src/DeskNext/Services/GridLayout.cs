using DeskNext.Model;

namespace DeskNext.Services;

/// <summary>网格空位分配与坐标换算（纯逻辑）。</summary>
public static class GridLayout
{
    /// <summary>Explorer 规则：从左上开始按列向下填；网格满则向右扩展列。</summary>
    public static (int Col, int Row) FirstEmpty(GridSize g, ISet<(int, int)> occupied)
    {
        for (var c = 0; ; c++)
            for (var r = 0; r < g.Rows; r++)
                if (!occupied.Contains((c, r))) return (c, r);
    }

    /// <summary>网格内距目标最近的空格（距离平方，平局列小优先再行小）；无空位退化为 FirstEmpty。</summary>
    public static (int Col, int Row) NearestEmpty(GridSize g, ISet<(int, int)> occupied, int col, int row)
    {
        col = Math.Clamp(col, 0, g.Cols - 1);
        row = Math.Clamp(row, 0, g.Rows - 1);
        var best = (Col: -1, Row: -1);
        long bestD = long.MaxValue;
        for (var c = 0; c < g.Cols; c++)
            for (var r = 0; r < g.Rows; r++)
            {
                if (occupied.Contains((c, r))) continue;
                long dc = c - col, dr = r - row;
                var d = dc * dc + dr * dr;
                if (d < bestD) { bestD = d; best = (c, r); }
            }
        return bestD == long.MaxValue ? FirstEmpty(g, occupied) : best;
    }

    /// <summary>相对工作区左上角的物理像素偏移 → 格子坐标（四舍五入并 clamp）。</summary>
    public static (int Col, int Row) FromPixels(double xPx, double yPx, double cellWPx, double cellHPx, GridSize g)
    {
        var c = (int)Math.Round(xPx / cellWPx, MidpointRounding.AwayFromZero);
        var r = (int)Math.Round(yPx / cellHPx, MidpointRounding.AwayFromZero);
        return (Math.Clamp(c, 0, g.Cols - 1), Math.Clamp(r, 0, g.Rows - 1));
    }

    /// <summary>屏幕物理坐标 → (显示器, 列, 行)；点不在任何工作区时取最近的显示器。</summary>
    public static (string Monitor, int Col, int Row)? FromScreenPixel(int screenX, int screenY, IReadOnlyList<MonitorGrid> monitors)
    {
        if (monitors.Count == 0) return null;
        MonitorGrid? hit = null;
        long bestD = long.MaxValue;
        foreach (var m in monitors)
        {
            var dx = screenX < m.WorkLeft ? m.WorkLeft - screenX : screenX >= m.WorkLeft + m.WorkWidth ? screenX - (m.WorkLeft + m.WorkWidth - 1) : 0;
            var dy = screenY < m.WorkTop ? m.WorkTop - screenY : screenY >= m.WorkTop + m.WorkHeight ? screenY - (m.WorkTop + m.WorkHeight - 1) : 0;
            var d = (long)dx * dx + (long)dy * dy;
            if (d < bestD) { bestD = d; hit = m; }
        }
        var mon = hit!;
        var (c, r) = FromPixels(screenX - mon.WorkLeft, screenY - mon.WorkTop, mon.CellW * mon.Scale, mon.CellH * mon.Scale, mon.Size);
        return (mon.Name, c, r);
    }
}
