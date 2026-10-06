using XkDesk.Model;

namespace XkDesk.Services;

[Flags]
public enum ResizeEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>对齐辅助线：Vertical=竖线（Pos 为 x，From~To 为 y 范围），否则横线。DIP，相对工作区。</summary>
public sealed record Guide(bool Vertical, double Pos, double From, double To);

public sealed record SnapResult(BoxRect Rect, IReadOnlyList<Guide> Guides);

/// <summary>格子几何（纯逻辑）：有效矩形、占用格、内容排布、移动/缩放吸附、新建选位。</summary>
public static class BoxGeometry
{
    public const double TitleH = 32;
    public const double PadBottom = 4;
    public const double ChromeH = TitleH + PadBottom;
    public const int MinCols = 2;
    public const int MinRows = 1;
    public const int DefaultCols = 4;
    public const int DefaultRows = 2;
    public const double SnapThreshold = 8;
    private const double Eps = 1e-6;

    public static double WidthFor(int cols, double cellW) => cols * cellW;
    public static double HeightFor(int rows, double cellH) => ChromeH + rows * cellH;

    // ------------------------------------------------------------ 内容排布

    public static int Cols(double width, double cellW) => Math.Max(1, (int)Math.Floor(width / cellW + Eps));

    public static int ContentRows(int count, int cols) => count <= 0 ? 0 : (count + cols - 1) / cols;

    /// <summary>内容可视高度（展开时）。</summary>
    public static double ViewportHeight(double boxHeight) => Math.Max(0, boxHeight - ChromeH);

    public static (int Col, int Row) CellOfIndex(int index, int cols) => (index % cols, index / cols);

    /// <summary>内容坐标（已含滚动偏移，原点为内容区左上角）→ 插入位置（0..count）。</summary>
    public static int InsertIndexAt(double x, double y, int cols, double cellW, double cellH, int count)
    {
        var c = Math.Clamp((int)Math.Floor(x / cellW), 0, cols - 1);
        var frac = x / cellW - Math.Floor(x / cellW);
        var row = Math.Max(0, (int)Math.Floor(y / cellH));
        var idx = row * cols + c + (frac >= 0.5 && x >= 0 ? 1 : 0);
        return Math.Clamp(idx, 0, count);
    }

    // ------------------------------------------------------------ 有效矩形 / 占用格

    /// <summary>
    /// 格子实际显示的矩形：所在显示器不存在时落到主屏（monitors[0]），并夹到工作区内；不改写状态。
    /// 折叠时只有标题栏高度。
    /// </summary>
    public static (MonitorGrid Grid, BoxRect Rect)? Effective(BoxState b, IReadOnlyList<MonitorGrid> monitors, bool ignoreCollapsed = false)
    {
        if (monitors.Count == 0) return null;
        var grid = monitors.FirstOrDefault(m => string.Equals(m.Name, b.Monitor, StringComparison.OrdinalIgnoreCase)) ?? monitors[0];
        var workW = grid.WorkWidth / grid.Scale;
        var workH = grid.WorkHeight / grid.Scale;
        var w = Math.Min(Math.Max(b.Rect.W, 1), workW);
        var h = b.Collapsed && !ignoreCollapsed ? TitleH : Math.Min(Math.Max(b.Rect.H, TitleH), workH);
        var x = Math.Clamp(b.Rect.X, 0, Math.Max(0, workW - w));
        var y = Math.Clamp(b.Rect.Y, 0, Math.Max(0, workH - h));
        return (grid, new BoxRect(x, y, w, h));
    }

    /// <summary>矩形覆盖的网格格子集合（含部分覆盖）。</summary>
    public static void AddCovered(ISet<(int, int)> set, BoxRect r, double cellW, double cellH)
    {
        var (c0, c1, r0, r1) = Span(r, cellW, cellH);
        for (var c = c0; c <= c1; c++)
            for (var row = r0; row <= r1; row++) set.Add((c, row));
    }

    public static (int C0, int C1, int R0, int R1) Span(BoxRect r, double cellW, double cellH) =>
        ((int)Math.Floor(r.X / cellW + Eps), (int)Math.Ceiling((r.X + r.W) / cellW - Eps) - 1,
         (int)Math.Floor(r.Y / cellH + Eps), (int)Math.Ceiling((r.Y + r.H) / cellH - Eps) - 1);

    /// <summary>某显示器上所有格子（含 excludeId 以外）覆盖的格子集合。</summary>
    public static HashSet<(int, int)> CoveredCells(LayoutState s, IReadOnlyList<MonitorGrid> monitors, string monitorName, string? excludeId = null)
    {
        var set = new HashSet<(int, int)>();
        foreach (var b in s.Boxes)
        {
            if (excludeId != null && b.Id == excludeId) continue;
            var eff = Effective(b, monitors);
            if (eff == null || !string.Equals(eff.Value.Grid.Name, monitorName, StringComparison.OrdinalIgnoreCase)) continue;
            AddCovered(set, eff.Value.Rect, eff.Value.Grid.CellW, eff.Value.Grid.CellH);
        }
        return set;
    }

    // ------------------------------------------------------------ 移动吸附

    /// <summary>把 DIP 值取整到整数物理像素（scale 为 DPI 缩放；&lt;=0 时不取整）。</summary>
    public static double PixelSnap(double v, double scale) => scale > 0 ? Math.Round(v * scale, MidpointRounding.AwayFromZero) / scale : v;

    /// <summary>在 raw 附近 SnapThreshold 内找最近的候选位置；没有则返回 raw。</summary>
    private static double SnapNear(double raw, IEnumerable<double> candidates)
    {
        var best = raw;
        var bestD = double.MaxValue;
        foreach (var cand in candidates)
        {
            var d = Math.Abs(cand - raw);
            if (d <= SnapThreshold && d < bestD) { bestD = d; best = cand; }
        }
        return best;
    }

    private static double SnapAxis(double raw, double size, double workSize, IReadOnlyList<(double Lo, double Hi)> others, double scale)
    {
        IEnumerable<double> Cands()
        {
            yield return 0;
            yield return workSize - size;
            foreach (var (lo, hi) in others)
            {
                yield return lo;          // 左边对左边
                yield return hi;          // 左边对右边
                yield return lo - size;   // 右边对左边
                yield return hi - size;   // 右边对右边
            }
        }
        var best = SnapNear(raw, Cands());
        return PixelSnap(Math.Clamp(best, 0, Math.Max(0, workSize - size)), scale);
    }

    /// <summary>移动：自由跟手（按设备像素取整）；靠近其他格子/屏幕边缘（阈值内）则吸附到边缘。</summary>
    public static SnapResult SnapMove(BoxRect raw, IReadOnlyList<BoxRect> others, double workW, double workH, double scale = 1)
    {
        var xs = others.Select(o => (o.X, o.Right)).ToList();
        var ys = others.Select(o => (o.Y, o.Bottom)).ToList();
        var x = SnapAxis(raw.X, raw.W, workW, xs, scale);
        var y = SnapAxis(raw.Y, raw.H, workH, ys, scale);
        var r = new BoxRect(x, y, raw.W, raw.H);
        return new SnapResult(r, FindGuides(r, others, workW, workH));
    }

    /// <summary>
    /// 缩放：被拖动的边逐像素跟手，阈值内吸附到屏幕/其他格子的边；Left/Top 拖动时对侧边不动；
    /// 有最小尺寸（MinCols×cellW、HeightFor(MinRows)）并夹到工作区内。
    /// </summary>
    public static SnapResult SnapResize(BoxRect start, ResizeEdge edge, double dx, double dy,
        IReadOnlyList<BoxRect> others, double workW, double workH, double cellW, double cellH, double scale = 1)
    {
        var minW = MinCols * cellW;
        var minH = HeightFor(MinRows, cellH);
        double left = start.X, right = start.Right, top = start.Y, bottom = start.Bottom;

        IEnumerable<double> XCands() { yield return 0; yield return workW; foreach (var o in others) { yield return o.X; yield return o.Right; } }
        IEnumerable<double> YCands() { yield return 0; yield return workH; foreach (var o in others) { yield return o.Y; yield return o.Bottom; } }

        if (edge.HasFlag(ResizeEdge.Right))
        {
            right = SnapNear(start.Right + dx, XCands());
            right = Math.Max(Math.Min(right, workW), left + minW);
        }
        if (edge.HasFlag(ResizeEdge.Left))
        {
            left = SnapNear(start.X + dx, XCands());
            left = Math.Max(0, Math.Min(left, right - minW));
        }
        if (edge.HasFlag(ResizeEdge.Bottom))
        {
            bottom = SnapNear(start.Bottom + dy, YCands());
            bottom = Math.Max(Math.Min(bottom, workH), top + minH);
        }
        if (edge.HasFlag(ResizeEdge.Top))
        {
            top = SnapNear(start.Y + dy, YCands());
            top = Math.Max(0, Math.Min(top, bottom - minH));
        }

        left = PixelSnap(left, scale); right = PixelSnap(right, scale);
        top = PixelSnap(top, scale); bottom = PixelSnap(bottom, scale);
        var r = new BoxRect(left, top, right - left, bottom - top);
        return new SnapResult(r, FindGuides(r, others, workW, workH));
    }

    /// <summary>当前矩形与其他格子/屏幕边缘重合时的辅助线。</summary>
    public static IReadOnlyList<Guide> FindGuides(BoxRect r, IReadOnlyList<BoxRect> others, double workW, double workH)
    {
        var guides = new List<Guide>();
        void AddV(double pos, double from, double to)
        {
            if (!guides.Any(g => g.Vertical && Math.Abs(g.Pos - pos) < 0.5)) guides.Add(new Guide(true, pos, from, to));
        }
        void AddH(double pos, double from, double to)
        {
            if (!guides.Any(g => !g.Vertical && Math.Abs(g.Pos - pos) < 0.5)) guides.Add(new Guide(false, pos, from, to));
        }

        foreach (var mx in new[] { r.X, r.Right })
        {
            if (Math.Abs(mx) < 0.5 || Math.Abs(mx - workW) < 0.5) AddV(mx, r.Y, r.Bottom);
            foreach (var o in others)
                foreach (var ox in new[] { o.X, o.Right })
                    if (Math.Abs(mx - ox) < 0.5) AddV(ox, Math.Min(r.Y, o.Y), Math.Max(r.Bottom, o.Bottom));
        }
        foreach (var my in new[] { r.Y, r.Bottom })
        {
            if (Math.Abs(my) < 0.5 || Math.Abs(my - workH) < 0.5) AddH(my, r.X, r.Right);
            foreach (var o in others)
                foreach (var oy in new[] { o.Y, o.Bottom })
                    if (Math.Abs(my - oy) < 0.5) AddH(oy, Math.Min(r.X, o.X), Math.Max(r.Right, o.Right));
        }
        return guides;
    }

    // ------------------------------------------------------------ 新建选位

    /// <summary>
    /// 在网格里找一块 wCells×hCells 的空地（不含 blocked 格子），取左上角离 (wantCol,wantRow) 最近的；
    /// 平局列小优先再行小；没有则返回 null。
    /// </summary>
    public static (int Col, int Row)? FindSpot(GridSize grid, ISet<(int, int)> blocked, int wCells, int hCells, int wantCol, int wantRow)
    {
        (int, int)? best = null;
        long bestD = long.MaxValue;
        for (var c = 0; c + wCells <= grid.Cols; c++)
            for (var r = 0; r + hCells <= grid.Rows; r++)
            {
                long dc = c - wantCol, dr = r - wantRow;
                var d = dc * dc + dr * dr;
                if (d >= bestD) continue;
                var free = true;
                for (var i = 0; i < wCells && free; i++)
                    for (var j = 0; j < hCells; j++)
                        if (blocked.Contains((c + i, r + j))) { free = false; break; }
                if (free) { bestD = d; best = (c, r); }
            }
        return best;
    }

    /// <summary>默认尺寸（按列/内容行数）占用的格子数。</summary>
    public static (int WCells, int HCells) CellsFor(int cols, int rows, double cellH) =>
        (cols, (int)Math.Ceiling(HeightFor(rows, cellH) / cellH - Eps));
}
