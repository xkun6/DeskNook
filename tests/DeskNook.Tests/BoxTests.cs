using DeskNook.Model;
using DeskNook.Services;

namespace DeskNook.Tests;

public class BoxLayoutTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // 显示器 1000x800，格子 75x100 → 13 列 8 行
    private static List<MonitorGrid> Mons() => new()
    {
        new("M1", 1.0, 0, 0, 1000, 800, 75, 100),
        new("M2", 1.0, 1000, 0, 600, 800, 75, 100),
    };

    private static BoxState NewBox(string id, double x, double y, int cols = 4, int rows = 2, string monitor = "M1") => new()
    {
        Id = id, Name = id, Monitor = monitor,
        Rect = new BoxRect(x, y, cols * 75, BoxGeometry.HeightFor(rows, 100)),
    };

    // ---------------------------------------------------------------- 布局合并

    [Fact]
    public void 同一key只归属一处_格子优先且去重()
    {
        var s = new LayoutState();
        var a = NewBox("A", 0, 0); a.ItemKeys.AddRange(new[] { "k1", "k2", "k1" });
        var b = NewBox("B", 400, 0); b.ItemKeys.AddRange(new[] { "K2", "k3" });
        s.Boxes.AddRange(new[] { a, b });
        s.FreeIcons["k1"] = new IconSlot { Monitor = "M1", Col = 9, Row = 0 };

        LayoutReconciler.Reconcile(s, new[] { "k1", "k2", "k3", "k4" }, Mons(), Now);

        Assert.Equal(new[] { "k1", "k2" }, a.ItemKeys);
        Assert.Equal(new[] { "k3" }, b.ItemKeys); // k2 已属于 A
        Assert.False(s.FreeIcons.ContainsKey("k1"));
        Assert.False(s.FreeIcons.ContainsKey("k2"));
        Assert.False(s.FreeIcons.ContainsKey("k3"));
        Assert.True(s.FreeIcons.ContainsKey("k4"));
    }

    [Fact]
    public void 新项和原位置被格子覆盖的项都避开格子()
    {
        var s = new LayoutState();
        s.Boxes.Add(NewBox("A", 0, 0, cols: 2, rows: 1)); // 宽 150、高 136 → 覆盖 (0..1, 0..1)
        s.FreeIcons["old"] = new IconSlot { Monitor = "M1", Col = 1, Row = 1 };

        var r = LayoutReconciler.Reconcile(s, new[] { "old", "n" }, Mons(), Now);

        var covered = BoxGeometry.CoveredCells(s, Mons(), "M1");
        Assert.Equal(4, covered.Count);
        foreach (var k in new[] { "old", "n" })
            Assert.DoesNotContain((s.FreeIcons[k].Col, s.FreeIcons[k].Row), covered);
        Assert.Contains("old", r.Placed);
    }

    [Fact]
    public void 格子内消失项保留七天后清理()
    {
        var s = new LayoutState();
        var a = NewBox("A", 0, 0); a.ItemKeys.AddRange(new[] { "x", "gone" });
        s.Boxes.Add(a);
        LayoutReconciler.Reconcile(s, new[] { "x" }, Mons(), Now);
        Assert.Contains("gone", a.ItemKeys);
        Assert.Equal(Now, a.Gone["gone"]);

        LayoutReconciler.Reconcile(s, new[] { "x" }, Mons(), Now.AddDays(6));
        Assert.Contains("gone", a.ItemKeys);

        LayoutReconciler.Reconcile(s, new[] { "x", "gone" }, Mons(), Now.AddDays(7)); // 回来：清除记录
        Assert.Empty(a.Gone);

        LayoutReconciler.Reconcile(s, new[] { "x" }, Mons(), Now.AddDays(8));
        LayoutReconciler.Reconcile(s, new[] { "x" }, Mons(), Now.AddDays(16));
        Assert.DoesNotContain("gone", a.ItemKeys);
    }

    [Fact]
    public void 改名同步格子成员()
    {
        var s = new LayoutState();
        var a = NewBox("A", 0, 0); a.ItemKeys.AddRange(new[] { "p", "old", "q" });
        s.Boxes.Add(a);
        LayoutReconciler.Rename(s, "old", "new");
        Assert.Equal(new[] { "p", "new", "q" }, a.ItemKeys);
    }

    [Fact]
    public void 显示器不存在时格子暂落主屏且不改写原Monitor()
    {
        var s = new LayoutState();
        var b = NewBox("A", 300, 0, monitor: "M3");
        s.Boxes.Add(b);
        var eff = BoxGeometry.Effective(b, Mons())!.Value;
        Assert.Equal("M1", eff.Grid.Name);
        LayoutReconciler.Reconcile(s, new[] { "z" }, Mons(), Now);
        Assert.Equal("M3", b.Monitor);
        // 自由图标避开它在主屏上的覆盖区
        Assert.DoesNotContain((s.FreeIcons["z"].Col, s.FreeIcons["z"].Row), BoxGeometry.CoveredCells(s, Mons(), "M1"));
        // 显示器回来：回到原屏
        var back = Mons(); back.Add(new("M3", 1.0, 1600, 0, 800, 600, 75, 100));
        Assert.Equal("M3", BoxGeometry.Effective(b, back)!.Value.Grid.Name);
    }

    [Fact]
    public void 有效矩形夹入工作区且折叠只剩标题栏()
    {
        var b = NewBox("A", 900, 700, cols: 4, rows: 2);
        var (_, r) = BoxGeometry.Effective(b, Mons())!.Value;
        Assert.Equal(1000 - 300, r.X);
        Assert.Equal(800 - r.H, r.Y);
        b.Collapsed = true;
        Assert.Equal(BoxGeometry.TitleH, BoxGeometry.Effective(b, Mons())!.Value.Rect.H);
    }

    // ---------------------------------------------------------------- 解散

    [Fact]
    public void 解散普通格子_图标回到原位置附近的空位且归属唯一()
    {
        var s = new LayoutState();
        var box = NewBox("A", 150, 100); // 左上 (2,1)
        box.ItemKeys.AddRange(new[] { "a", "b", "c", "gone" });
        s.Boxes.Add(box);
        s.FreeIcons["f"] = new IconSlot { Monitor = "M1", Col = 2, Row = 1 }; // 占着格子左上角

        var placed = BoxOps.Dissolve(s, "A", new[] { "a", "b", "c", "f" }, Mons());

        Assert.Empty(s.Boxes);
        Assert.Equal(new[] { "a", "b", "c" }, placed);
        var cells = placed.Select(k => (s.FreeIcons[k].Col, s.FreeIcons[k].Row)).ToList();
        Assert.Equal(3, cells.Distinct().Count());
        Assert.DoesNotContain((2, 1), cells);
        Assert.All(cells, c => Assert.True(Math.Abs(c.Col - 2) + Math.Abs(c.Row - 1) <= 2));
        Assert.All(placed, k => Assert.Equal("M1", s.FreeIcons[k].Monitor));
        Assert.False(s.FreeIcons.ContainsKey("gone"));
    }

    [Fact]
    public void 解散映射格子_不产生自由图标()
    {
        var s = new LayoutState();
        s.Boxes.Add(new BoxState { Id = "M", Kind = BoxKind.Mapped, MappedPath = @"C:\x", Monitor = "M1", Rect = new BoxRect(0, 0, 300, 236) });
        var placed = BoxOps.Dissolve(s, "M", new[] { "a" }, Mons());
        Assert.Empty(placed);
        Assert.Empty(s.Boxes);
        Assert.Empty(s.FreeIcons);
    }

    [Fact]
    public void 解散时避开其他格子覆盖的格子()
    {
        var s = new LayoutState();
        var a = NewBox("A", 0, 0, cols: 2, rows: 1); a.ItemKeys.Add("x");
        var other = NewBox("B", 150, 0, cols: 2, rows: 1);
        s.Boxes.AddRange(new[] { a, other });
        BoxOps.Dissolve(s, "A", new[] { "x" }, Mons());
        var covered = BoxGeometry.CoveredCells(s, Mons(), "M1");
        Assert.DoesNotContain((s.FreeIcons["x"].Col, s.FreeIcons["x"].Row), covered);
    }

    // ---------------------------------------------------------------- 成员移动 / 排序

    [Fact]
    public void 移入格子_从自由区和其他格子摘除并插入指定位置()
    {
        var s = new LayoutState();
        var a = NewBox("A", 0, 0); a.ItemKeys.AddRange(new[] { "1", "2", "3" });
        var b = NewBox("B", 400, 0); b.ItemKeys.AddRange(new[] { "x", "y" });
        s.Boxes.AddRange(new[] { a, b });
        s.FreeIcons["f"] = new IconSlot { Monitor = "M1", Col = 9, Row = 5 };

        BoxOps.MoveToBox(s, new[] { "f", "x" }, a, 1);

        Assert.Equal(new[] { "1", "f", "x", "2", "3" }, a.ItemKeys);
        Assert.Equal(new[] { "y" }, b.ItemKeys);
        Assert.False(s.FreeIcons.ContainsKey("f"));
    }

    [Fact]
    public void 格子内调整顺序_索引按移动前列表计算()
    {
        var s = new LayoutState();
        var a = NewBox("A", 0, 0); a.ItemKeys.AddRange(new[] { "1", "2", "3", "4" });
        s.Boxes.Add(a);
        BoxOps.MoveToBox(s, new[] { "1" }, a, 3);   // 放到原 4 的前面
        Assert.Equal(new[] { "2", "3", "1", "4" }, a.ItemKeys);
        BoxOps.MoveToBox(s, new[] { "4" }, a, 0);
        Assert.Equal(new[] { "4", "2", "3", "1" }, a.ItemKeys);
        BoxOps.MoveToBox(s, new[] { "2", "1" }, a, 4); // 到末尾
        Assert.Equal(new[] { "4", "3", "2", "1" }, a.ItemKeys);
    }

    [Fact]
    public void 设为自由位置时同时摘除格子成员()
    {
        var s = new LayoutState();
        var a = NewBox("A", 0, 0); a.ItemKeys.Add("k");
        s.Boxes.Add(a);
        LayoutReconciler.Move(s, "k", "M1", 7, 3);
        Assert.Empty(a.ItemKeys);
        Assert.Equal((7, 3), (s.FreeIcons["k"].Col, s.FreeIcons["k"].Row));
    }

    private static DesktopItem Item(string name, long size = 0, string ext = "", bool folder = false, DateTime? mod = null, bool virt = false) => new()
    {
        Key = name, DisplayName = name, Size = size, Extension = ext, Modified = mod ?? DateTime.MinValue,
        FilePath = virt ? null : name, Attributes = folder ? 0x20000000u : 0,
    };

    [Fact]
    public void 排序_名称_文件夹优先自然排序()
    {
        var items = new[] { Item("b10.txt"), Item("b2.txt"), Item("zdir", folder: true), Item("a.txt") };
        Assert.Equal(new[] { "zdir", "a.txt", "b2.txt", "b10.txt" }, ItemSorter.Sort(items, "name").Select(i => i.Key));
    }

    [Fact]
    public void 排序_大小_类型_修改时间_虚拟项在前()
    {
        var items = new[]
        {
            Item("big.zip", 900, ".zip"), Item("small.txt", 5, ".txt"), Item("mid.png", 50, ".png", mod: new DateTime(2026, 1, 3)),
            Item("dir", folder: true), Item("thispc", virt: true),
        };
        Assert.Equal(new[] { "thispc", "dir", "small.txt", "mid.png", "big.zip" }, ItemSorter.Sort(items, "size").Select(i => i.Key));
        Assert.Equal(new[] { "thispc", "dir", "mid.png", "small.txt", "big.zip" }, ItemSorter.Sort(items, "type").Select(i => i.Key));
        var byDate = ItemSorter.Sort(items, "date").Select(i => i.Key).ToList();
        Assert.Equal("thispc", byDate[0]);
        Assert.Equal("mid.png", byDate[1]); // 最新的在前
    }

    // ---------------------------------------------------------------- 内容排布

    [Fact]
    public void 内容排布_列数与插入位置()
    {
        Assert.Equal(4, BoxGeometry.Cols(300, 75));
        Assert.Equal(1, BoxGeometry.Cols(30, 75));
        Assert.Equal(3, BoxGeometry.ContentRows(9, 3));
        Assert.Equal(4, BoxGeometry.ContentRows(10, 3));
        Assert.Equal((1, 2), BoxGeometry.CellOfIndex(7, 3));
        Assert.Equal(0, BoxGeometry.InsertIndexAt(10, 10, 4, 75, 100, 6));
        Assert.Equal(1, BoxGeometry.InsertIndexAt(50, 10, 4, 75, 100, 6));   // 后半格 → 之后
        Assert.Equal(4, BoxGeometry.InsertIndexAt(10, 150, 4, 75, 100, 6));  // 第二行第 1 格前半
        Assert.Equal(6, BoxGeometry.InsertIndexAt(290, 150, 4, 75, 100, 6)); // 夹到 count
    }

    // ---------------------------------------------------------------- 视图（查看）

    [Fact]
    public void 视图参数_跟随桌面与传入完全相同()
    {
        foreach (var mode in new string?[] { "", null, "bogus", "Large" })
            Assert.Equal(new BoxView(48, 75, 100, false, false), BoxGeometry.ViewFor(mode, 48, 75, 100));
    }

    [Fact]
    public void 视图参数_大中图标_单元等于图标加桌面额外间距()
    {
        Assert.Equal(new BoxView(96, 123, 148, false, false), BoxGeometry.ViewFor("large", 48, 75, 100));
        Assert.Equal(new BoxView(48, 76, 86, false, false), BoxGeometry.ViewFor("medium", 32, 60, 70));
    }

    [Fact]
    public void 视图参数_小图标与列表_尺寸与方向标志()
    {
        Assert.Equal(new BoxView(16, 200, 24, true, false), BoxGeometry.ViewFor("small", 48, 75, 100));
        Assert.Equal(new BoxView(16, 200, 24, true, true), BoxGeometry.ViewFor("list", 48, 75, 100));
        Assert.Equal("list", BoxGeometry.NormalizeViewMode("list"));
        Assert.Equal("", BoxGeometry.NormalizeViewMode("detail"));
    }

    [Fact]
    public void 视图单元位置_行优先与列优先()
    {
        var small = BoxGeometry.ViewFor("small", 48, 75, 100);
        var list = BoxGeometry.ViewFor("list", 48, 75, 100);
        Assert.Equal((1, 1), BoxGeometry.ViewCellOf(small, 4, 7, 3));
        Assert.Equal((1, 2), BoxGeometry.ViewCellOf(BoxGeometry.ViewFor("", 48, 75, 100), 7, 9, 3));
        // 列表：count=7、cols=3 → rows=3，按列排
        Assert.Equal((0, 0), BoxGeometry.ViewCellOf(list, 0, 7, 3));
        Assert.Equal((0, 2), BoxGeometry.ViewCellOf(list, 2, 7, 3));
        Assert.Equal((1, 1), BoxGeometry.ViewCellOf(list, 4, 7, 3));
        Assert.Equal((2, 0), BoxGeometry.ViewCellOf(list, 6, 7, 3));
        Assert.Equal((2, 1), BoxGeometry.ViewCellOf(list, 7, 7, 3));    // index == count：末尾之后
        Assert.Equal((2, 2), BoxGeometry.ViewCellOf(list, 6, 6, 3));    // 列已满：最后一列下方
        Assert.Equal((2, 1), BoxGeometry.ViewCellOf(small, 5, 5, 3));   // 行优先 index == count
    }

    [Fact]
    public void 视图内容行数()
    {
        var small = BoxGeometry.ViewFor("small", 48, 75, 100);
        var list = BoxGeometry.ViewFor("list", 48, 75, 100);
        Assert.Equal(4, BoxGeometry.ViewContentRows(small, 10, 3));
        Assert.Equal(3, BoxGeometry.ViewContentRows(list, 7, 3));
        Assert.Equal(0, BoxGeometry.ViewContentRows(list, 0, 3));
    }

    [Fact]
    public void 视图列数与单元宽度_横排拉伸_竖排居中()
    {
        var small = BoxGeometry.ViewFor("small", 48, 75, 100);
        Assert.Equal(2, BoxGeometry.ViewCols(small, 450));
        Assert.Equal(225, BoxGeometry.ItemWidth(small, 450));
        Assert.Equal(1, BoxGeometry.ViewCols(small, 150));
        Assert.Equal(150, BoxGeometry.ItemWidth(small, 150));
        Assert.Equal(0, BoxGeometry.ViewOffsetX(small, 450));

        var large = BoxGeometry.ViewFor("large", 48, 75, 100);
        Assert.Equal(3, BoxGeometry.ViewCols(large, 450));
        Assert.Equal(123, BoxGeometry.ItemWidth(large, 450));
        Assert.Equal(40.5, BoxGeometry.ViewOffsetX(large, 450));
    }

    [Fact]
    public void 视图插入位置_竖排与旧函数一致()
    {
        var v = BoxGeometry.ViewFor("", 48, 75, 100);
        foreach (var (x, y) in new[] { (10.0, 10.0), (50.0, 10.0), (10.0, 150.0), (290.0, 150.0), (-5.0, 10.0), (310.0, 150.0) })
            Assert.Equal(BoxGeometry.InsertIndexAt(x, y, 4, 75, 100, 6), BoxGeometry.ViewInsertIndexAt(v, x, y, 4, 75, 6));
    }

    [Fact]
    public void 视图插入位置_横排按y半格判前后()
    {
        var small = BoxGeometry.ViewFor("small", 48, 75, 100);   // 单元高 24，cols=2，itemW=225
        Assert.Equal(0, BoxGeometry.ViewInsertIndexAt(small, 10, 5, 2, 225, 5));    // 第 0 格上半
        Assert.Equal(1, BoxGeometry.ViewInsertIndexAt(small, 10, 15, 2, 225, 5));   // 第 0 格下半 → 之后
        Assert.Equal(1, BoxGeometry.ViewInsertIndexAt(small, 240, 5, 2, 225, 5));   // 第 1 列上半
        Assert.Equal(2, BoxGeometry.ViewInsertIndexAt(small, 240, 15, 2, 225, 5));
        Assert.Equal(2, BoxGeometry.ViewInsertIndexAt(small, 10, 30, 2, 225, 5));   // 第 1 行上半
        Assert.Equal(5, BoxGeometry.ViewInsertIndexAt(small, 10, 500, 2, 225, 5));  // 夹到 count
        Assert.Equal(0, BoxGeometry.ViewInsertIndexAt(small, -10, -5, 2, 225, 5));  // 夹到 0
    }

    [Fact]
    public void 视图插入位置_列表换算成列乘行数加行()
    {
        var list = BoxGeometry.ViewFor("list", 48, 75, 100);     // count=7、cols=3 → rows=3
        Assert.Equal(3, BoxGeometry.ViewInsertIndexAt(list, 210, 5, 3, 200, 7));    // 第 1 列第 0 行上半
        Assert.Equal(4, BoxGeometry.ViewInsertIndexAt(list, 210, 30, 3, 200, 7));   // 第 1 列第 1 行上半
        Assert.Equal(5, BoxGeometry.ViewInsertIndexAt(list, 210, 40, 3, 200, 7));   // 第 1 列第 1 行下半
        Assert.Equal(6, BoxGeometry.ViewInsertIndexAt(list, 610, 5, 3, 200, 7));    // 列夹到最后一列
        Assert.Equal(7, BoxGeometry.ViewInsertIndexAt(list, 410, 500, 3, 200, 7));  // 夹到 count
        Assert.Equal(3, BoxGeometry.ViewInsertIndexAt(list, 10, 500, 3, 200, 7));   // 行夹到最后一行，下半 → 之后
    }

    // ---------------------------------------------------------------- 吸附 / 对齐

    [Fact]
    public void 移动_无吸附时逐像素跟手()
    {
        var raw = new BoxRect(123.4, 230.6, 300, 236);
        var r = BoxGeometry.SnapMove(raw, Array.Empty<BoxRect>(), 1000, 800).Rect;
        Assert.Equal((123, 231), (r.X, r.Y)); // 只对齐到物理像素，不是 75/100 的网格倍数
        var free = BoxGeometry.SnapMove(raw, Array.Empty<BoxRect>(), 1000, 800, 0).Rect;
        Assert.Equal((123.4, 230.6), (free.X, free.Y));
        var r2 = BoxGeometry.SnapMove(raw, Array.Empty<BoxRect>(), 1000, 800, 1.25).Rect; // 取整到物理像素
        Assert.Equal(Math.Round(123.4 * 1.25) / 1.25, r2.X, 9);
        Assert.Equal(Math.Round(230.6 * 1.25) / 1.25, r2.Y, 9);
        Assert.Equal((300, 236), (r.W, r.H));
    }

    [Fact]
    public void 移动_靠近屏幕边缘吸附并出辅助线()
    {
        var res = BoxGeometry.SnapMove(new BoxRect(4, 3, 300, 236), Array.Empty<BoxRect>(), 1000, 800);
        Assert.Equal((0, 0), (res.Rect.X, res.Rect.Y));
        Assert.Contains(res.Guides, g => g.Vertical && g.Pos == 0);
        Assert.Contains(res.Guides, g => !g.Vertical && g.Pos == 0);
        // 超过阈值不吸附
        var far = BoxGeometry.SnapMove(new BoxRect(9, 20, 300, 236), Array.Empty<BoxRect>(), 1000, 800);
        Assert.Equal((9, 20), (far.Rect.X, far.Rect.Y));
        Assert.Empty(far.Guides);
    }

    [Fact]
    public void 移动_靠近其他格子边缘吸附_阈值内外()
    {
        var other = new BoxRect(203, 400, 300, 236);
        var res = BoxGeometry.SnapMove(new BoxRect(207, 100, 300, 236), new[] { other }, 1000, 800);
        Assert.Equal(203, res.Rect.X);
        Assert.Contains(res.Guides, g => g.Vertical && Math.Abs(g.Pos - 203) < 0.5);
        Assert.Equal(100, res.Rect.Y); // y 方向离 other 很远，保持原值

        var out10 = BoxGeometry.SnapMove(new BoxRect(213, 100, 300, 236), new[] { other }, 1000, 800);
        Assert.Equal(213, out10.Rect.X);
    }

    [Fact]
    public void 移动_夹在工作区内()
    {
        var res = BoxGeometry.SnapMove(new BoxRect(990, 790, 300, 236), Array.Empty<BoxRect>(), 1000, 800).Rect;
        Assert.Equal(700, res.X);
        Assert.Equal(800 - 236, res.Y);
    }

    [Fact]
    public void 跨屏移动_按目标显示器工作区与缩放换算并夹住尺寸()
    {
        // 左侧副屏：工作区原点 (-1920, 0)，150% 缩放，工作区 1920x1040 物理像素 → 1280x693.33 DIP
        var grid = new MonitorGrid("M2", 1.5, -1920, 0, 1920, 1040, 75, 100);
        var r = BoxGeometry.RawRectOnMonitor(-1000, 300, 50, 10, 300, 236, grid);
        Assert.Equal(920 / 1.5 - 50, r.X, 9);
        Assert.Equal(300 / 1.5 - 10, r.Y, 9);
        Assert.Equal((300, 236), (r.W, r.H));
        var big = BoxGeometry.RawRectOnMonitor(-1000, 300, 0, 0, 2000, 900, grid);
        Assert.Equal(1280, big.W, 9);
        Assert.Equal(1040 / 1.5, big.H, 9);
    }

    [Fact]
    public void 缩放_逐像素_右下角()
    {
        var start = new BoxRect(75, 100, 300, BoxGeometry.HeightFor(2, 100));
        var r = BoxGeometry.SnapResize(start, ResizeEdge.Right | ResizeEdge.Bottom, 7, 19, Array.Empty<BoxRect>(), 1000, 800, 75, 100).Rect;
        Assert.Equal((75, 100), (r.X, r.Y));
        Assert.Equal(307, r.W);
        Assert.Equal(BoxGeometry.HeightFor(2, 100) + 19, r.H);
    }

    [Fact]
    public void 缩放_左上角保持对侧不动并有最小尺寸()
    {
        var start = new BoxRect(150, 200, 300, BoxGeometry.HeightFor(2, 100));
        var right = start.Right; var bottom = start.Bottom;
        var r = BoxGeometry.SnapResize(start, ResizeEdge.Left | ResizeEdge.Top, -33, -41, Array.Empty<BoxRect>(), 1000, 800, 75, 100).Rect;
        Assert.Equal(right, r.Right);
        Assert.Equal(bottom, r.Bottom);
        Assert.Equal(117, r.X);
        Assert.Equal(159, r.Y);
        Assert.Equal(333, r.W);

        var small = BoxGeometry.SnapResize(start, ResizeEdge.Right | ResizeEdge.Bottom, -9999, -9999, Array.Empty<BoxRect>(), 1000, 800, 75, 100).Rect;
        Assert.Equal(BoxGeometry.MinCols * 75, small.W);
        Assert.Equal(BoxGeometry.HeightFor(BoxGeometry.MinRows, 100), small.H);

        var small2 = BoxGeometry.SnapResize(start, ResizeEdge.Left, 9999, 0, Array.Empty<BoxRect>(), 1000, 800, 75, 100).Rect;
        Assert.Equal(BoxGeometry.MinCols * 75, small2.W);
        Assert.Equal(start.Right, small2.Right);
    }

    [Fact]
    public void 缩放_不超出工作区()
    {
        var start = new BoxRect(675, 100, 300, BoxGeometry.HeightFor(2, 100));
        var r = BoxGeometry.SnapResize(start, ResizeEdge.Right, 9999, 0, Array.Empty<BoxRect>(), 1000, 800, 75, 100).Rect;
        Assert.Equal(1000, r.Right);
        var l = BoxGeometry.SnapResize(start, ResizeEdge.Left | ResizeEdge.Top, -9999, -9999, Array.Empty<BoxRect>(), 1000, 800, 75, 100).Rect;
        Assert.Equal((0, 0), (l.X, l.Y));
    }

    [Fact]
    public void 缩放_拖动边靠近其他格子边缘吸附()
    {
        var start = new BoxRect(100, 100, 300, BoxGeometry.HeightFor(2, 100));
        var other = new BoxRect(520, 50, 200, 200);
        var snap = BoxGeometry.SnapResize(start, ResizeEdge.Right, 124, 0, new[] { other }, 1000, 800, 75, 100);
        Assert.Equal(520, snap.Rect.Right);
        Assert.Contains(snap.Guides, g => g.Vertical && Math.Abs(g.Pos - 520) < 0.5);
        var free = BoxGeometry.SnapResize(start, ResizeEdge.Right, 111, 0, new[] { other }, 1000, 800, 75, 100);
        Assert.Equal(511, free.Rect.Right);
    }

    [Fact]
    public void 非整格宽度_列数与插入位置()
    {
        Assert.Equal(4, BoxGeometry.Cols(307, 75));
        Assert.Equal(4, BoxGeometry.Cols(374, 75));
        Assert.Equal(5, BoxGeometry.Cols(375, 75));
        // 内容区居中偏移后的内容坐标：x 小于 0 夹到第 0 列，超出夹到最后一列
        Assert.Equal(0, BoxGeometry.InsertIndexAt(-5, 10, 4, 75, 100, 6));
        Assert.Equal(6, BoxGeometry.InsertIndexAt(310, 150, 4, 75, 100, 6));
        // 非整格矩形覆盖的格子：x 100..407 -> 列 1..5
        var set = new HashSet<(int, int)>();
        BoxGeometry.AddCovered(set, new BoxRect(100, 0, 307, 136), 75, 100);
        Assert.Contains((1, 0), set); Assert.Contains((5, 1), set); Assert.DoesNotContain((6, 0), set);
    }

    [Fact]
    public void 新建选位_最近的空地且避开占用()
    {
        var g = new GridSize(10, 8);
        var blocked = new HashSet<(int, int)>();
        Assert.Equal((3, 2), BoxGeometry.FindSpot(g, blocked, 4, 3, 3, 2));
        BoxGeometry.AddCovered(blocked, new BoxRect(225, 200, 300, 236), 75, 100); // 占 (3..6, 2..4)
        var spot = BoxGeometry.FindSpot(g, blocked, 4, 3, 3, 2)!.Value;
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 3; j++) Assert.DoesNotContain((spot.Col + i, spot.Row + j), blocked);
        Assert.Null(BoxGeometry.FindSpot(new GridSize(3, 8), blocked, 4, 3, 0, 0)); // 放不下
    }
}

public class BoxPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "desknook-test-" + Guid.NewGuid().ToString("N"));
    public BoxPersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void 读取阶段1的layout_json_无Boxes字段()
    {
        var p = Path.Combine(_dir, "layout.json");
        File.WriteAllText(p, "{\"Version\":1,\"FreeIcons\":{\"C:\\\\a.txt\":{\"Monitor\":\"M1\",\"Col\":1,\"Row\":2,\"LastSeenUtc\":null}}," +
                             "\"View\":{\"IconSize\":48,\"SortKey\":\"name\"},\"SystemPositionsImported\":true}");
        var s = new LayoutStore(p).Load();
        Assert.Empty(s.Boxes);
        Assert.Equal(2, s.FreeIcons["C:\\a.txt"].Row);
        Assert.True(s.SystemPositionsImported);
    }

    [Fact]
    public void 读取阶段1占位格子_缺新字段时补默认值()
    {
        var p = Path.Combine(_dir, "layout.json");
        File.WriteAllText(p, "{\"Boxes\":[{\"Name\":\"旧\",\"X\":1,\"Y\":2,\"W\":3,\"H\":4,\"ItemKeys\":[\"a\"]}]}");
        var b = Assert.Single(new LayoutStore(p).Load().Boxes);
        Assert.False(string.IsNullOrEmpty(b.Id));
        Assert.NotNull(b.Rect);
        Assert.False(b.Locked);
        Assert.Equal("", b.SortMode);
        Assert.Equal("", b.ViewMode);
        Assert.Equal(new[] { "a" }, b.ItemKeys);
    }

    [Fact]
    public void 视图模式往返_未知值规整为空()
    {
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        var s = new LayoutState();
        s.Boxes.Add(new BoxState { Id = "b1", Name = "a", ViewMode = "list" });
        s.Boxes.Add(new BoxState { Id = "b2", Name = "b", ViewMode = "detail" });
        Assert.True(store.Save(s));
        var l = store.Load();
        Assert.Equal("list", l.Boxes[0].ViewMode);
        Assert.Equal("", l.Boxes[1].ViewMode);
    }

    [Fact]
    public void 格子完整往返()
    {
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        var s = new LayoutState();
        s.Boxes.Add(new BoxState
        {
            Id = "b1", Name = "工作", Kind = BoxKind.Normal, Monitor = "\\\\.\\DISPLAY2", Rect = new BoxRect(75, 100, 300, 236),
            Collapsed = true, Locked = true, SortMode = "date", ItemKeys = { "k1", "k2" },
            Gone = { ["k2"] = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
        });
        s.Boxes.Add(new BoxState { Id = "b2", Name = "映射", Kind = BoxKind.Mapped, MappedPath = @"D:\x", Rect = new BoxRect(0, 0, 150, 136) });
        Assert.True(store.Save(s));
        var l = store.Load();
        Assert.Equal(2, l.Boxes.Count);
        var b = l.Boxes[0];
        Assert.Equal((75, 100, 300, 236), (b.Rect.X, b.Rect.Y, b.Rect.W, b.Rect.H));
        Assert.True(b.Collapsed);
        Assert.True(b.Locked);
        Assert.Equal("date", b.SortMode);
        Assert.Equal(new[] { "k1", "k2" }, b.ItemKeys);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), b.Gone["K2"]);
        Assert.Equal(BoxKind.Mapped, l.Boxes[1].Kind);
        Assert.Equal(@"D:\x", l.Boxes[1].MappedPath);
    }
}
