using DeskNook.Model;
using DeskNook.Services;

namespace DeskNook.Tests;

public class OrganizerTests
{
    // 主屏 1000x800，格子 75x100 → 13 列 8 行
    private static List<MonitorGrid> Mons() => new()
    {
        new("M1", 1.0, 0, 0, 1000, 800, 75, 100),
        new("M2", 1.0, 1000, 0, 600, 800, 75, 100),
    };

    private const uint FolderAttr = 0x20000000;

    private static DesktopItem File(string name) => new()
    {
        Key = @"C:\Desktop\" + name, DisplayName = name, FilePath = @"C:\Desktop\" + name,
        Extension = Path.GetExtension(name).ToLowerInvariant(),
    };

    private static DesktopItem Folder(string name) => new()
    {
        Key = @"C:\Desktop\" + name, DisplayName = name, FilePath = @"C:\Desktop\" + name, Attributes = FolderAttr,
    };

    private static DesktopItem Virtual(string name) => new()
    {
        Key = "::{" + name + "}", DisplayName = name, FilePath = null, Attributes = FolderAttr,
    };

    private static readonly List<OrganizeRule> Rules = AppSettings.DefaultRules();

    private static LayoutState Place(LayoutState s, IEnumerable<DesktopItem> items)
    {
        var i = 0;
        foreach (var it in items) s.FreeIcons[it.Key] = new IconSlot { Monitor = "M1", Col = i++ % 3, Row = i / 3 };
        return s;
    }

    private static (LayoutState State, List<DesktopItem> Items, OrganizeUndo? Undo) Run(IEnumerable<DesktopItem> items, LayoutState? s = null)
    {
        var list = items.ToList();
        s ??= new LayoutState();
        Place(s, list.Where(i => !s.FreeIcons.ContainsKey(i.Key) && BoxOps.BoxOfKey(s, i.Key) == null));
        var plan = AutoOrganizer.Plan(s, list, Rules, Mons());
        return (s, list, AutoOrganizer.Apply(s, plan, DateTime.UtcNow));
    }

    private static BoxState B(LayoutState s, string name) => s.Boxes.Single(b => b.Name == name);

    // ---------------------------------------------------------------- 分类

    [Theory]
    [InlineData("a.lnk", "快捷方式与程序")]
    [InlineData("a.url", "快捷方式与程序")]
    [InlineData("a.exe", "快捷方式与程序")]
    [InlineData("a.msi", "快捷方式与程序")]
    [InlineData("a.bat", "快捷方式与程序")]
    [InlineData("a.cmd", "快捷方式与程序")]
    [InlineData("a.appref-ms", "快捷方式与程序")]
    [InlineData("a.docx", "文档")]
    [InlineData("a.xlsx", "文档")]
    [InlineData("a.pdf", "文档")]
    [InlineData("a.txt", "文档")]
    [InlineData("a.md", "文档")]
    [InlineData("a.wps", "文档")]
    [InlineData("a.csv", "文档")]
    [InlineData("a.PNG", "图片")]
    [InlineData("a.jpeg", "图片")]
    [InlineData("a.mp4", "视频")]
    [InlineData("a.MKV", "视频")]
    [InlineData("a.mp3", "音频")]
    [InlineData("a.flac", "音频")]
    [InlineData("a.zip", "压缩包")]
    [InlineData("a.7z", "压缩包")]
    [InlineData("a.xyz", "其他")]
    [InlineData("noext", "其他")]
    public void 各类扩展名归类(string name, string expect)
    {
        Assert.Equal(expect, AutoOrganizer.Categorize(File(name), Rules));
    }

    [Fact]
    public void 文件夹归文件夹_虚拟项不参与()
    {
        Assert.Equal("文件夹", AutoOrganizer.Categorize(Folder("dir"), Rules));
        Assert.Null(AutoOrganizer.Categorize(Virtual("此电脑"), Rules));
        // 带扩展名的“文件夹”（如 zip）不算文件夹
        var zip = new DesktopItem { Key = "z", DisplayName = "z.zip", FilePath = "z.zip", Attributes = FolderAttr, Extension = ".zip" };
        Assert.Equal("压缩包", AutoOrganizer.Categorize(zip, Rules));
    }

    [Fact]
    public void 自定义规则按顺序优先()
    {
        var rules = new List<OrganizeRule>
        {
            new("笔记", "txt"),
            new("文本", "txt", "md"),
            new("杂项", "*"),
        };
        Assert.Equal("笔记", AutoOrganizer.Categorize(File("a.txt"), rules));
        Assert.Equal("文本", AutoOrganizer.Categorize(File("a.md"), rules));
        Assert.Equal("杂项", AutoOrganizer.Categorize(File("a.png"), rules));
        // 没有兜底规则 → 不整理
        Assert.Null(AutoOrganizer.Categorize(File("a.png"), new List<OrganizeRule> { new("x", "txt") }));
        // 扩展名写法容错：带点/通配/大小写
        Assert.Equal("x", AutoOrganizer.Categorize(File("a.txt"), new List<OrganizeRule> { new("x", " .TXT ") }));
        Assert.Equal("x", AutoOrganizer.Categorize(File("a.txt"), new List<OrganizeRule> { new("x", "*.txt") }));
    }

    // ---------------------------------------------------------------- 计划与执行

    [Fact]
    public void 整理建格子_虚拟项留在自由区()
    {
        var items = new[] { File("a.txt"), File("b.docx"), File("c.png"), Folder("dir"), Virtual("此电脑"), File("noext") };
        var (s, _, undo) = Run(items);
        Assert.NotNull(undo);
        Assert.Equal(new[] { "dir" }, B(s, "文件夹").ItemKeys.Select(Path.GetFileName));
        Assert.Equal(new[] { "a.txt", "b.docx" }, B(s, "文档").ItemKeys.Select(Path.GetFileName));
        Assert.Equal(new[] { "c.png" }, B(s, "图片").ItemKeys.Select(Path.GetFileName));
        Assert.Equal(new[] { "noext" }, B(s, "其他").ItemKeys.Select(Path.GetFileName));
        Assert.Equal(4, s.Boxes.Count);
        Assert.True(s.FreeIcons.ContainsKey("::{此电脑}"));
        Assert.False(s.FreeIcons.ContainsKey(@"C:\Desktop\a.txt"));
    }

    [Fact]
    public void 新格子靠右从上到下再向左_互不重叠()
    {
        var items = new List<DesktopItem>();
        for (var i = 0; i < 12; i++) items.Add(File($"d{i}.txt"));
        for (var i = 0; i < 12; i++) items.Add(File($"p{i}.png"));
        for (var i = 0; i < 9; i++) items.Add(File($"v{i}.mp4"));
        for (var i = 0; i < 9; i++) items.Add(File($"m{i}.mp3"));
        for (var i = 0; i < 9; i++) items.Add(File($"z{i}.zip"));
        for (var i = 0; i < 9; i++) items.Add(Folder($"f{i}"));
        items.Add(Virtual("回收站"));
        var (s, _, _) = Run(items);

        Assert.Equal(6, s.Boxes.Count);
        // 第一个（文件夹）在最右列顶部
        var first = B(s, "文件夹");
        Assert.Equal(0, first.Rect.Y);
        Assert.True(first.Rect.Right >= 13 * 75 - 1e-6 - 0.001 && first.Rect.Right <= 13 * 75 + 1e-6);
        // 全部在工作区内、互不重叠、不压住留下的虚拟项
        var cells = new List<HashSet<(int, int)>>();
        foreach (var b in s.Boxes)
        {
            Assert.True(b.Rect.X >= 0 && b.Rect.Y >= 0 && b.Rect.Right <= 1000 + 1e-6 && b.Rect.Bottom <= 800 + 1e-6, b.Name);
            Assert.True(b.Rect.W >= 3 * 75 && b.Rect.W <= 5 * 75);
            var set = new HashSet<(int, int)>();
            BoxGeometry.AddCovered(set, b.Rect, 75, 100);
            foreach (var other in cells) Assert.False(other.Overlaps(set), b.Name);
            cells.Add(set);
        }
        var vs = s.FreeIcons["::{回收站}"];
        foreach (var set in cells) Assert.DoesNotContain((vs.Col, vs.Row), set);
        // 自上而下：第二个格子位于第一个下方（同列区）或其左侧
        var second = s.Boxes[1];
        Assert.True(second.Rect.Y >= first.Rect.Bottom - 1e-6 || second.Rect.Right <= first.Rect.X + 1e-6);
    }

    [Fact]
    public void 已有同名格子复用追加_已在格子内的项不动()
    {
        var s = new LayoutState();
        var docs = new BoxState { Id = "D", Name = "文档", Monitor = "M1", Rect = new BoxRect(300, 0, 300, 236) };
        docs.ItemKeys.Add(@"C:\Desktop\old.txt");
        var other = new BoxState { Id = "O", Name = "随便", Monitor = "M1", Rect = new BoxRect(0, 400, 300, 236) };
        other.ItemKeys.Add(@"C:\Desktop\inbox.png");
        s.Boxes.AddRange(new[] { docs, other });

        var items = new[] { File("old.txt"), File("inbox.png"), File("new.txt") };
        var (_, _, undo) = Run(items, s);

        Assert.NotNull(undo);
        Assert.Equal(2, s.Boxes.Count); // 没有新建
        Assert.Equal(new[] { @"C:\Desktop\old.txt", @"C:\Desktop\new.txt" }, docs.ItemKeys);
        Assert.Equal(new[] { @"C:\Desktop\inbox.png" }, other.ItemKeys); // 格子内的不动
        Assert.Empty(undo!.CreatedBoxIds);
        Assert.Single(undo.Moves);
    }

    [Fact]
    public void 映射格子与映射项不参与_同名映射格子不被复用()
    {
        var s = new LayoutState();
        s.Boxes.Add(new BoxState { Id = "M", Name = "文档", Kind = BoxKind.Mapped, MappedPath = @"C:\x", Monitor = "M1", Rect = new BoxRect(0, 0, 300, 236) });
        var mapped = new DesktopItem { Key = "M\u0001a.txt", Container = "M", DisplayName = "a.txt", FilePath = @"C:\x\a.txt", Extension = ".txt" };
        var (_, _, undo) = Run(new[] { mapped, File("b.txt") }, s);
        Assert.Equal(2, s.Boxes.Count);
        Assert.Equal(new[] { @"C:\Desktop\b.txt" }, s.Boxes.Single(b => b.Kind == BoxKind.Normal).ItemKeys);
        Assert.DoesNotContain(undo!.Moves, m => m.Key == mapped.Key);
    }

    [Fact]
    public void 新格子避开已有格子和不整理的自由图标()
    {
        var s = new LayoutState();
        s.Boxes.Add(new BoxState { Id = "X", Name = "占位", Monitor = "M1", Rect = new BoxRect(9 * 75, 0, 4 * 75, BoxGeometry.HeightFor(2, 100)) });
        var v = Virtual("此电脑");
        s.FreeIcons[v.Key] = new IconSlot { Monitor = "M1", Col = 9, Row = 5 };
        var (_, _, _) = Run(new[] { v, File("a.txt") }, s);
        var docs = B(s, "文档");
        var set = new HashSet<(int, int)>();
        BoxGeometry.AddCovered(set, docs.Rect, 75, 100);
        Assert.DoesNotContain((9, 5), set);
        var x = new HashSet<(int, int)>();
        BoxGeometry.AddCovered(x, s.Boxes[0].Rect, 75, 100);
        Assert.False(x.Overlaps(set));
    }

    [Fact]
    public void 没有可整理的项_返回空()
    {
        var (_, _, undo) = Run(new[] { Virtual("此电脑") });
        Assert.Null(undo);
    }

    // ---------------------------------------------------------------- 撤销

    [Fact]
    public void 撤销_恢复整理前布局()
    {
        var items = new[] { File("a.txt"), File("c.png"), Folder("dir"), Virtual("此电脑") };
        var s = new LayoutState();
        Place(s, items);
        var before = s.FreeIcons.ToDictionary(kv => kv.Key, kv => (kv.Value.Monitor, kv.Value.Col, kv.Value.Row));

        var (_, _, undo) = Run(items, s);
        Assert.Equal(3, s.Boxes.Count);

        var n = AutoOrganizer.ApplyUndo(s, undo!, items.Select(i => i.Key).ToList(), Mons());
        Assert.Equal(3, n);
        Assert.Empty(s.Boxes);
        var after = s.FreeIcons.ToDictionary(kv => kv.Key, kv => (kv.Value.Monitor, kv.Value.Col, kv.Value.Row));
        Assert.Equal(before, after);
    }

    [Fact]
    public void 撤销_整理后用户又改动_只回滚本次的内容()
    {
        var s = new LayoutState();
        var docs = new BoxState { Id = "D", Name = "文档", Monitor = "M1", Rect = new BoxRect(300, 0, 300, 236) };
        docs.ItemKeys.Add(@"C:\Desktop\old.txt");
        s.Boxes.Add(docs);
        var items = new List<DesktopItem> { File("old.txt"), File("new.txt"), File("p.png"), File("q.mp4") };
        Place(s, items.Where(i => i.Key != @"C:\Desktop\old.txt"));
        var origNew = s.FreeIcons[@"C:\Desktop\new.txt"];
        var (oc, orow) = (origNew.Col, origNew.Row);

        var (_, _, undo) = Run(items, s);
        var pics = B(s, "图片");
        var vids = B(s, "视频");

        // 用户之后：把一个新文件放进图片格子、把 q.mp4 手动拖出到别处、手动改了图片格子名字、新增了 user.txt 进文档格子
        var user = File("user.png");
        items.Add(user);
        BoxOps.MoveToBox(s, new[] { user.Key }, pics, int.MaxValue);
        BoxOps.RemoveFromBoxes(s, new[] { @"C:\Desktop\q.mp4" });
        s.FreeIcons[@"C:\Desktop\q.mp4"] = new IconSlot { Monitor = "M1", Col = 7, Row = 7 };
        BoxOps.MoveToBox(s, new[] { @"C:\Desktop\p.png" }, docs, int.MaxValue); // 用户把 p.png 拖进了别的格子
        // 另一个自由图标占用了 new.txt 原来的格子
        var squatter = File("squat.xyz");
        items.Add(squatter);
        s.FreeIcons[squatter.Key] = new IconSlot { Monitor = "M1", Col = oc, Row = orow };

        AutoOrganizer.ApplyUndo(s, undo!, items.Select(i => i.Key).ToList(), Mons());

        // 整理前就在格子里的 old.txt 不动；用户新放进图片格子的保留，图片格子因此保留
        Assert.Contains(@"C:\Desktop\old.txt", docs.ItemKeys);
        Assert.DoesNotContain(@"C:\Desktop\new.txt", docs.ItemKeys);
        Assert.Contains(s.Boxes, b => b.Id == pics.Id);
        Assert.Equal(new[] { user.Key }, pics.ItemKeys);
        // 视频格子变空 → 删除
        Assert.DoesNotContain(s.Boxes, b => b.Id == vids.Id);
        // key 回自由区原位置为准（q.mp4 / p.png / new.txt 都回原位置），占位者被挪开
        Assert.DoesNotContain(@"C:\Desktop\p.png", docs.ItemKeys);
        var back = s.FreeIcons[@"C:\Desktop\new.txt"];
        Assert.Equal((oc, orow), (back.Col, back.Row));
        var sq = s.FreeIcons[squatter.Key];
        Assert.NotEqual((oc, orow), (sq.Col, sq.Row));
        // 所有自由图标位置互不相同
        var all = s.FreeIcons.Values.Select(v => (v.Monitor, v.Col, v.Row)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void 撤销_已消失的项被忽略()
    {
        var items = new[] { File("a.txt"), File("b.txt") };
        var (s, _, undo) = Run(items);
        var n = AutoOrganizer.ApplyUndo(s, undo!, new[] { items[0].Key }, Mons());
        Assert.Equal(1, n);
        Assert.False(s.FreeIcons.ContainsKey(items[1].Key));
    }

    [Fact]
    public void 撤销记录_读写与损坏()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xk-undo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "organize-undo.json");
            var store = new OrganizeUndoStore(path);
            Assert.Null(store.Load());
            var u = new OrganizeUndo { CreatedBoxIds = { "b1" }, Moves = { new UndoMove { Key = "k", Monitor = "M1", Col = 2, Row = 3 } } };
            Assert.True(store.Save(u));
            var r = store.Load()!;
            Assert.Equal("b1", r.CreatedBoxIds.Single());
            Assert.Equal((2, 3), (r.Moves[0].Col, r.Moves[0].Row));
            File2.WriteAllText(path, "{坏的");
            Assert.Null(store.Load());
            store.Clear();
            Assert.DoesNotContain(path, Directory.GetFiles(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class SettingsStoreTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "xk-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 不存在时给默认规则()
    {
        var s = new SettingsStore(Path.Combine(Temp(), "settings.json")).Load();
        Assert.Equal(AppSettings.DefaultRules().Select(r => r.Name), s.OrganizeRules.Select(r => r.Name));
        Assert.Equal("其他", s.OrganizeRules[^1].Name);
    }

    [Fact]
    public void 保存后读回一致_且无临时文件残留()
    {
        var dir = Temp();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var s = new AppSettings { OrganizeRules = { new OrganizeRule("自定义", "abc", "def") } };
            s.OrganizeRules.Clear();
            s.OrganizeRules.Add(new OrganizeRule("自定义", "abc", "def"));
            Assert.True(store.Save(s));
            Assert.True(store.Save(s)); // 覆盖保存（File.Replace 路径）
            var r = store.Load();
            Assert.Equal("自定义", r.OrganizeRules.Single().Name);
            Assert.Equal(new[] { "abc", "def" }, r.OrganizeRules.Single().Extensions);
            Assert.DoesNotContain(store.FilePath + ".tmp", Directory.GetFiles(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void 损坏时备份并回退默认()
    {
        var dir = Temp();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File2.WriteAllText(path, "{ not json");
            var r = new SettingsStore(path).Load();
            Assert.Equal(AppSettings.DefaultRules().Count, r.OrganizeRules.Count);
            Assert.False(File2.Exists(path));
            Assert.Single(Directory.GetFiles(dir, "settings.json.bad-*"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void 规则里的空名字与空值被清理()
    {
        var dir = Temp();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File2.WriteAllText(path, "{\"OrganizeRules\":[{\"Name\":\"  \",\"Extensions\":[\"a\"]},{\"Name\":\"ok\",\"Extensions\":null},null]}");
            var r = new SettingsStore(path).Load();
            Assert.Equal("ok", r.OrganizeRules.Single().Name);
            Assert.Empty(r.OrganizeRules.Single().Extensions);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

internal static class File2
{
    public static void WriteAllText(string p, string c) => System.IO.File.WriteAllText(p, c);
    public static bool Exists(string p) => System.IO.File.Exists(p);
}
