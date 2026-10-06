using DeskNook.Model;
using DeskNook.Services;

namespace DeskNook.Tests;

public class GridLayoutTests
{
    private static readonly GridSize G = new(3, 2);

    [Fact]
    public void FirstEmpty_按列向下填充()
    {
        var occ = new HashSet<(int, int)>();
        var got = new List<(int, int)>();
        for (var i = 0; i < 4; i++) { var p = GridLayout.FirstEmpty(G, occ); got.Add(p); occ.Add(p); }
        Assert.Equal(new[] { (0, 0), (0, 1), (1, 0), (1, 1) }, got);
    }

    [Fact]
    public void FirstEmpty_跳过占用()
    {
        var occ = new HashSet<(int, int)> { (0, 0), (0, 1), (1, 1) };
        Assert.Equal((1, 0), GridLayout.FirstEmpty(G, occ));
    }

    [Fact]
    public void FirstEmpty_网格满向右扩展()
    {
        var occ = new HashSet<(int, int)>();
        for (var c = 0; c < 3; c++) for (var r = 0; r < 2; r++) occ.Add((c, r));
        Assert.Equal((3, 0), GridLayout.FirstEmpty(G, occ));
    }

    [Fact]
    public void NearestEmpty_目标空则原地()
    {
        Assert.Equal((1, 1), GridLayout.NearestEmpty(G, new HashSet<(int, int)>(), 1, 1));
    }

    [Fact]
    public void NearestEmpty_平局列小优先再行小()
    {
        var g = new GridSize(3, 3);
        // 目标 (1,1) 被占，四邻距离相同 → 列小优先：(0,1)
        var occ = new HashSet<(int, int)> { (1, 1) };
        Assert.Equal((0, 1), GridLayout.NearestEmpty(g, occ, 1, 1));
        // (0,1) 也被占 → 剩 (1,0)/(2,1)/(1,2) 距离相同，列小优先 → (1,0)
        occ.Add((0, 1));
        Assert.Equal((1, 0), GridLayout.NearestEmpty(g, occ, 1, 1));
    }

    [Fact]
    public void NearestEmpty_clamp与满格()
    {
        Assert.Equal((2, 1), GridLayout.NearestEmpty(G, new HashSet<(int, int)>(), 99, 99));
        var occ = new HashSet<(int, int)>();
        for (var c = 0; c < 3; c++) for (var r = 0; r < 2; r++) occ.Add((c, r));
        Assert.Equal((3, 0), GridLayout.NearestEmpty(G, occ, 0, 0));
    }

    [Fact]
    public void FromPixels_四舍五入与clamp()
    {
        var g = new GridSize(10, 5);
        Assert.Equal((2, 1), GridLayout.FromPixels(160, 80, 75, 75, g)); // 2.13, 1.07
        Assert.Equal((0, 0), GridLayout.FromPixels(-50, -10, 75, 75, g));
        Assert.Equal((9, 4), GridLayout.FromPixels(5000, 5000, 75, 75, g));
        Assert.Equal((1, 0), GridLayout.FromPixels(40, 30, 75, 75, g)); // 0.53 → 1, 0.4 → 0
    }

    private static List<MonitorGrid> Monitors() => new()
    {
        new("\\\\.\\DISPLAY1", 1.0, 0, 0, 2560, 1400, 75, 75),
        new("\\\\.\\DISPLAY2", 1.5, 2560, 0, 1920, 1040, 75, 75),
    };

    [Fact]
    public void FromScreenPixel_主屏100()
    {
        var r = GridLayout.FromScreenPixel(150, 75, Monitors())!.Value;
        Assert.Equal(("\\\\.\\DISPLAY1", 2, 1), r);
    }

    [Fact]
    public void FromScreenPixel_副屏150缩放()
    {
        // 格子物理 112.5px；偏移 (225,112.5)→(2,1)
        var r = GridLayout.FromScreenPixel(2560 + 225, 113, Monitors())!.Value;
        Assert.Equal(("\\\\.\\DISPLAY2", 2, 1), r);
    }

    [Fact]
    public void FromScreenPixel_负数与越界取最近显示器()
    {
        var m = Monitors();
        var a = GridLayout.FromScreenPixel(-100, -100, m)!.Value;
        Assert.Equal(("\\\\.\\DISPLAY1", 0, 0), a);
        var b = GridLayout.FromScreenPixel(99999, 99999, m)!.Value;
        Assert.Equal("\\\\.\\DISPLAY2", b.Monitor);
        Assert.Equal(m[1].Cols - 1, b.Col);
        Assert.Equal(m[1].Rows - 1, b.Row);
        Assert.Null(GridLayout.FromScreenPixel(0, 0, new List<MonitorGrid>()));
    }

    [Fact]
    public void MonitorGrid_行列数()
    {
        var m = Monitors();
        Assert.Equal(34, m[0].Cols); // 2560/75
        Assert.Equal(18, m[0].Rows); // 1400/75
        Assert.Equal(17, m[1].Cols); // 1920/1.5/75=17.07
        Assert.Equal(9, m[1].Rows);  // 1040/1.5/75=9.24
    }
}

public class ReconcilerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static List<MonitorGrid> Mons() => new()
    {
        new("M1", 1.0, 0, 0, 300, 150, 75, 75),   // 4x2
        new("M2", 1.0, 300, 0, 300, 150, 75, 75), // 4x2
    };

    [Fact]
    public void 新项放首空格()
    {
        var s = new LayoutState();
        var r = LayoutReconciler.Reconcile(s, new[] { "a", "b", "c" }, Mons(), Now);
        Assert.Equal(new[] { "a", "b", "c" }, r.Placed);
        Assert.Equal((0, 0), (s.FreeIcons["a"].Col, s.FreeIcons["a"].Row));
        Assert.Equal((0, 1), (s.FreeIcons["b"].Col, s.FreeIcons["b"].Row));
        Assert.Equal((1, 0), (s.FreeIcons["c"].Col, s.FreeIcons["c"].Row));
        Assert.Equal("M1", s.FreeIcons["c"].Monitor);
    }

    [Fact]
    public void 已有位置保留且新项避开()
    {
        var s = new LayoutState();
        LayoutReconciler.Move(s, "a", "M1", 0, 0);
        var r = LayoutReconciler.Reconcile(s, new[] { "n", "a" }, Mons(), Now);
        Assert.Equal(new[] { "n" }, r.Placed);
        Assert.Equal((0, 1), (s.FreeIcons["n"].Col, s.FreeIcons["n"].Row));
        Assert.Equal((0, 0), (s.FreeIcons["a"].Col, s.FreeIcons["a"].Row));
    }

    [Fact]
    public void 消失项保留七天后清理且不阻塞新项()
    {
        var s = new LayoutState();
        LayoutReconciler.Move(s, "gone", "M1", 0, 0);
        LayoutReconciler.Reconcile(s, new string[0], Mons(), Now);
        Assert.Equal(Now, s.FreeIcons["gone"].LastSeenUtc);

        // 6 天后仍在；期间新项可占用其格子？不阻塞新项
        var r = LayoutReconciler.Reconcile(s, new[] { "x" }, Mons(), Now.AddDays(6));
        Assert.Equal((0, 0), (s.FreeIcons["x"].Col, s.FreeIcons["x"].Row));
        Assert.True(s.FreeIcons.ContainsKey("gone"));
        Assert.Equal(Now, s.FreeIcons["gone"].LastSeenUtc);

        LayoutReconciler.Reconcile(s, new[] { "x" }, Mons(), Now.AddDays(8));
        Assert.False(s.FreeIcons.ContainsKey("gone"));
    }

    [Fact]
    public void 消失项回来清除LastSeen并保位()
    {
        var s = new LayoutState();
        LayoutReconciler.Move(s, "k", "M1", 2, 1);
        LayoutReconciler.Reconcile(s, new string[0], Mons(), Now);
        var r = LayoutReconciler.Reconcile(s, new[] { "k" }, Mons(), Now.AddDays(1));
        Assert.Empty(r.Placed);
        Assert.Null(s.FreeIcons["k"].LastSeenUtc);
        Assert.Equal((2, 1), (s.FreeIcons["k"].Col, s.FreeIcons["k"].Row));
    }

    [Fact]
    public void 改名保留位置()
    {
        var s = new LayoutState();
        LayoutReconciler.Move(s, "old", "M1", 3, 1);
        LayoutReconciler.Rename(s, "old", "new");
        var r = LayoutReconciler.Reconcile(s, new[] { "new" }, Mons(), Now);
        Assert.Empty(r.Placed);
        Assert.Equal((3, 1), (s.FreeIcons["new"].Col, s.FreeIcons["new"].Row));
        Assert.False(s.FreeIcons.ContainsKey("old"));
    }

    [Fact]
    public void 显示器消失回退主屏()
    {
        var s = new LayoutState();
        LayoutReconciler.Move(s, "k", "M2", 1, 1);
        var r = LayoutReconciler.Reconcile(s, new[] { "k" }, new List<MonitorGrid> { Mons()[0] }, Now);
        Assert.Equal(new[] { "k" }, r.Placed);
        Assert.Equal(("M1", 1, 1), (s.FreeIcons["k"].Monitor, s.FreeIcons["k"].Col, s.FreeIcons["k"].Row));
    }

    [Fact]
    public void 冲突与越界就近安置()
    {
        var s = new LayoutState();
        LayoutReconciler.Move(s, "a", "M1", 1, 1);
        LayoutReconciler.Move(s, "b", "M1", 1, 1);   // 冲突
        LayoutReconciler.Move(s, "c", "M1", 99, 0);  // 越界
        LayoutReconciler.Reconcile(s, new[] { "a", "b", "c" }, Mons(), Now);
        var cells = new[] { "a", "b", "c" }.Select(k => (s.FreeIcons[k].Col, s.FreeIcons[k].Row)).ToList();
        Assert.Equal(3, cells.Distinct().Count());
        Assert.Equal((1, 1), cells[0]);
        Assert.All(cells, c => Assert.InRange(c.Col, 0, 3));
        Assert.Equal((3, 0), cells[2]); // 越界项靠近原位置（最右列）
    }
}

public class ItemDiffTests
{
    private static DesktopItem Item(string key, string name = "", long size = 0) =>
        new() { Key = key, DisplayName = name == "" ? key : name, Size = size, FilePath = key };

    [Fact]
    public void 增删改()
    {
        var o = new[] { Item("a"), Item("b"), Item("c", size: 1) };
        var n = new[] { Item("A"), Item("c", size: 2), Item("d") };
        var d = ItemDiff.Compute(o, n);
        Assert.Equal(new[] { "d" }, d.Added.Select(i => i.Key));
        Assert.Equal(new[] { "b" }, d.Removed.Select(i => i.Key));
        Assert.Equal(new[] { "A", "c" }, d.Updated.Select(i => i.Key)); // A 仅大小写不同的 Key 视为同一项，显示名变化 → 更新
        Assert.Empty(d.Renamed);
    }

    [Fact]
    public void 无变化为空()
    {
        var o = new[] { Item("a") };
        Assert.True(ItemDiff.Compute(o, new[] { Item("a") }).IsEmpty);
    }

    [Fact]
    public void 无提示时改名按增删处理()
    {
        var d = ItemDiff.Compute(new[] { Item("a") }, new[] { Item("b") });
        Assert.Single(d.Added);
        Assert.Single(d.Removed);
        Assert.Empty(d.Renamed);
    }

    [Fact]
    public void 改名提示命中并保留布局位置()
    {
        var o = new[] { Item("a"), Item("x") };
        var n = new[] { Item("b"), Item("x") };
        var d = ItemDiff.Compute(o, n, new Dictionary<string, string> { ["a"] = "b" });
        Assert.Empty(d.Added);
        Assert.Empty(d.Removed);
        var (oi, ni) = Assert.Single(d.Renamed);
        Assert.Equal(("a", "b"), (oi.Key, ni.Key));

        var s = new LayoutState();
        LayoutReconciler.Move(s, "a", "M1", 2, 1);
        foreach (var (ro, rn) in d.Renamed) LayoutReconciler.Rename(s, ro.Key, rn.Key);
        Assert.Equal((2, 1), (s.FreeIcons["b"].Col, s.FreeIcons["b"].Row));
    }

    [Fact]
    public void 改名提示不成立时忽略()
    {
        // 新集中没有目标 → 不算改名
        var d = ItemDiff.Compute(new[] { Item("a") }, new[] { Item("z") }, new Dictionary<string, string> { ["a"] = "b" });
        Assert.Empty(d.Renamed);
        Assert.Single(d.Added);
        Assert.Single(d.Removed);
    }
}

public class LayoutStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "desknook-test-" + Guid.NewGuid().ToString("N"));
    public LayoutStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void 文件不存在返回默认()
    {
        var s = new LayoutStore(Path.Combine(_dir, "nope.json")).Load();
        Assert.Empty(s.FreeIcons);
        Assert.Equal(48, s.View.IconSize);
    }

    [Fact]
    public void 往返()
    {
        var store = new LayoutStore(Path.Combine(_dir, "sub", "layout.json"));
        var s = new LayoutState { SystemPositionsImported = true };
        s.View.IconSize = 96; s.View.SortKey = "name";
        s.FreeIcons["C:\\a.txt"] = new IconSlot { Monitor = "M1", Col = 3, Row = 4, LastSeenUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
        s.Boxes.Add(new BoxState { Id = "b1", Name = "盒", Kind = BoxKind.Mapped, MappedPath = "D:\\x", ItemKeys = { "k" } });
        Assert.True(store.Save(s));
        var l = store.Load();
        Assert.True(l.SystemPositionsImported);
        Assert.Equal(96, l.View.IconSize);
        Assert.Equal("name", l.View.SortKey);
        Assert.Equal(3, l.FreeIcons["c:\\A.TXT"].Col); // 忽略大小写
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), l.FreeIcons["C:\\a.txt"].LastSeenUtc);
        Assert.Equal(BoxKind.Mapped, l.Boxes[0].Kind);
        Assert.Equal("盒", l.Boxes[0].Name);
        Assert.Contains("\"Mapped\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void 损坏文件回退并备份()
    {
        var p = Path.Combine(_dir, "layout.json");
        File.WriteAllText(p, "{ this is not json");
        var s = new LayoutStore(p).Load();
        Assert.Empty(s.FreeIcons);
        Assert.False(File.Exists(p));
        Assert.Single(Directory.GetFiles(_dir, "layout.json.bad-*"));
    }

    [Fact]
    public void null字段被修正()
    {
        var p = Path.Combine(_dir, "layout.json");
        File.WriteAllText(p, "{\"FreeIcons\":null,\"Boxes\":null,\"View\":null}");
        var s = new LayoutStore(p).Load();
        Assert.NotNull(s.FreeIcons);
        Assert.NotNull(s.Boxes);
        Assert.NotNull(s.View);
    }

    [Fact]
    public void 覆盖写两次且无残留临时文件()
    {
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        var s = new LayoutState();
        Assert.True(store.Save(s));
        s.View.IconSize = 32;
        Assert.True(store.Save(s));
        Assert.Equal(32, store.Load().View.IconSize);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }
}
