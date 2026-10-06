using System.Text.Json;
using DeskNook.Desktop;
using DeskNook.Services;

namespace DeskNook.Tests;

public class MenuProtocolTests
{
    private static MenuContext Ctx(MenuSource source = MenuSource.Desktop, params string[] paths) =>
        new() { Controller = null!, Source = source, Paths = paths };

    private static List<WireMenuItem> Build(IEnumerable<CustomMenuItem> items, MenuContext ctx, Dictionary<int, CustomMenuItem>? map = null)
    {
        var next = 1;
        return MenuExtensions.BuildWire(items, ctx, map ?? new Dictionary<int, CustomMenuItem>(), ref next, topLevel: true);
    }

    [Fact]
    public void 适用条件过滤并分配唯一Id()
    {
        var a = new CustomMenuItem { Title = "A" };
        var b = new CustomMenuItem { Title = "B", Applies = _ => false };
        var c = new CustomMenuItem { Title = "C", Applies = _ => true };
        var map = new Dictionary<int, CustomMenuItem>();
        var wire = Build(new[] { a, b, c }, Ctx(), map);
        Assert.Equal(new[] { "A", "C" }, wire.Select(w => w.Title));
        Assert.Equal(2, wire.Select(w => w.Id).Distinct().Count());
        Assert.All(wire, w => Assert.Same(map[w.Id], w.Title == "A" ? a : c));
    }

    [Fact]
    public void 动态标题与状态被求值()
    {
        var it = new CustomMenuItem
        {
            Title = "x", DynamicTitle = _ => "动态标题", Enabled = _ => false, Checked = _ => true, Radio = true, Icon = @"C:\a.png",
        };
        var w = Assert.Single(Build(new[] { it }, Ctx()));
        Assert.Equal("动态标题", w.Title);
        Assert.False(w.Enabled);
        Assert.True(w.Checked);
        Assert.True(w.Radio);
        Assert.Equal(@"C:\a.png", w.Icon);
    }

    [Fact]
    public void 子菜单只保留适用子项_没有可用子项则整个子菜单消失()
    {
        var parent = new CustomMenuItem
        {
            Title = "P",
            Children = new[]
            {
                new CustomMenuItem { Title = "ok" },
                new CustomMenuItem { Title = "no", Applies = _ => false },
            },
        };
        var empty = new CustomMenuItem { Title = "E", Children = new[] { new CustomMenuItem { Title = "no", Applies = _ => false } } };
        var wire = Build(new[] { parent, empty }, Ctx());
        var p = Assert.Single(wire);
        Assert.Equal("P", p.Title);
        Assert.Equal(0, p.Id); // 子菜单本身不带命令 Id
        Assert.Equal("ok", Assert.Single(p.Children!).Title);
        Assert.True(p.Children![0].Id > 0);
    }

    [Fact]
    public void 分隔线与回退专用项()
    {
        var items = new[]
        {
            new CustomMenuItem { IsSeparator = true },
            new CustomMenuItem { Title = "只在回退里", FallbackOnly = true },
            new CustomMenuItem { Title = "正常" },
        };
        var wire = Build(items, Ctx());
        Assert.Equal(2, wire.Count);
        Assert.True(wire[0].Sep);
        Assert.Equal("正常", wire[1].Title);
    }

    [Fact]
    public void 资源管理器来源只出现标记了InExplorer的项()
    {
        var items = new[]
        {
            new CustomMenuItem { Title = "仅DeskNook" },
            new CustomMenuItem { Title = "也在资源管理器", InExplorer = true },
        };
        Assert.Equal(2, Build(items, Ctx(MenuSource.Desktop)).Count);
        Assert.Equal(new[] { "也在资源管理器" }, Build(items, Ctx(MenuSource.Explorer, @"C:\x")).Select(w => w.Title));
    }

    [Fact]
    public void 位置名称映射()
    {
        var items = new[]
        {
            new CustomMenuItem { Title = "t", Position = MenuPosition.Top },
            new CustomMenuItem { Title = "b", Position = MenuPosition.Bottom },
        };
        Assert.Equal(new[] { "top", "bottom" }, Build(items, Ctx()).Select(w => w.Pos));
    }

    [Fact]
    public void 查询应答JSON往返_中文不转义()
    {
        var items = new List<WireMenuItem>
        {
            new() { Id = 3, Title = "整理至新格子(&G)", Icon = @"C:\icons\a.png", Pos = "bottom" },
            new() { Sep = true },
            new() { Title = "子菜单", Children = new List<WireMenuItem> { new() { Id = 4, Title = "子项", Checked = true, Radio = true } } },
        };
        var json = MenuProtocol.SerializeQueryResult(42, items);
        Assert.Contains("整理至新格子", json); // UnsafeRelaxedJsonEscaping：不转成 \uXXXX
        Assert.DoesNotContain("\n", json);

        var back = MenuProtocol.ParseQueryResult(json, out var q);
        Assert.Equal(42, q);
        Assert.Equal(3, back.Count);
        Assert.Equal(3, back[0].Id);
        Assert.Equal(@"C:\icons\a.png", back[0].Icon);
        Assert.True(back[1].Sep);
        Assert.Equal("子项", back[2].Children![0].Title);
        Assert.True(back[2].Children![0].Checked);
    }

    [Fact]
    public void 查询应答字段名与C加加侧约定一致()
    {
        using var doc = JsonDocument.Parse(MenuProtocol.SerializeQueryResult(1, new[]
        {
            new WireMenuItem { Id = 7, Title = "x", Enabled = false, Checked = true, Radio = true, Pos = "top" },
        }));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("q").GetInt64());
        var it = root.GetProperty("items")[0];
        Assert.Equal(7, it.GetProperty("id").GetInt32());
        Assert.Equal("x", it.GetProperty("title").GetString());
        Assert.False(it.GetProperty("enabled").GetBoolean());
        Assert.True(it.GetProperty("checked").GetBoolean());
        Assert.True(it.GetProperty("radio").GetBoolean());
        Assert.Equal("top", it.GetProperty("pos").GetString());
    }

    [Fact]
    public void 解析Shell扩展发来的查询()
    {
        var line = "{\"t\":\"query\",\"kind\":\"item\",\"folder\":\"\",\"items\":[\"E:\\\\Desktop\\\\a.txt\",\"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\"],\"shift\":true,\"proc\":\"explorer.exe\",\"pid\":1234,\"req\":\"r5\"}";
        Assert.Equal("query", MenuProtocol.ParseMessageType(line));
        var q = MenuProtocol.ParseQuery(line)!;
        Assert.Equal("item", q.Kind);
        Assert.Equal(new[] { @"E:\Desktop\a.txt", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" }, q.Items);
        Assert.True(q.Shift);
        Assert.Equal("explorer.exe", q.Proc);
        Assert.Equal(1234, q.Pid);
        Assert.Equal("r5", q.Req);
    }

    [Fact]
    public void 解析背景查询与缺字段容错()
    {
        var q = MenuProtocol.ParseQuery("{\"t\":\"query\",\"kind\":\"background\",\"folder\":\"E:\\\\Desktop\"}")!;
        Assert.Equal("background", q.Kind);
        Assert.Equal(@"E:\Desktop", q.Folder);
        Assert.Empty(q.Items);
        Assert.False(q.Shift);
        Assert.Equal("", q.Req);
    }

    [Fact]
    public void 解析调用与事件()
    {
        Assert.Equal((42L, 3), MenuProtocol.ParseInvoke("{\"t\":\"invoke\",\"q\":42,\"id\":3,\"req\":\"\"}"));
        Assert.Null(MenuProtocol.ParseInvoke("{\"t\":\"invoke\",\"id\":3}"));

        var ev = MenuProtocol.ParseEvent("{\"t\":\"invoked\",\"req\":\"r9\",\"verb\":\"\",\"title\":\"小图标\",\"parent\":\"查看\",\"defView\":true,\"hr\":0}")!;
        Assert.Equal("invoked", ev.Type);
        Assert.Equal("r9", ev.Req);
        Assert.Equal("小图标", ev.Title);
        Assert.Equal("查看", ev.Parent);
        Assert.True(ev.DefView);

        var closed = MenuProtocol.ParseEvent("{\"t\":\"closed\",\"req\":\"r9\",\"picked\":false}")!;
        Assert.Equal("closed", closed.Type);
        Assert.False(closed.Picked);
        var err = MenuProtocol.ParseEvent("{\"t\":\"closed\",\"req\":\"r9\",\"error\":\"nocm\"}")!;
        Assert.Equal("nocm", err.Error);
    }

    [Fact]
    public void 非法JSON返回null而不是抛异常()
    {
        Assert.Null(MenuProtocol.ParseMessageType("not json"));
        Assert.Null(MenuProtocol.ParseQuery("{broken"));
        Assert.Null(MenuProtocol.ParseInvoke(""));
        Assert.Null(MenuProtocol.ParseEvent("[1,2"));
    }

    [Fact]
    public void 代理请求JSON字段名与C加加侧约定一致()
    {
        var json = MenuProtocol.SerializeRequest(new ProxyRequest
        {
            Id = "r1", Kind = "item", Folder = "::desktop", Items = new List<string> { @"E:\Desktop\a.txt" }, X = 100, Y = 200, Shift = true,
            IconsVisible = false, InterceptVerbs = new List<string> { "rename" },
        });
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal("menu", r.GetProperty("t").GetString());
        Assert.Equal("r1", r.GetProperty("id").GetString());
        Assert.Equal("item", r.GetProperty("kind").GetString());
        Assert.Equal("::desktop", r.GetProperty("folder").GetString());
        Assert.Equal(@"E:\Desktop\a.txt", r.GetProperty("items")[0].GetString());
        Assert.Equal(100, r.GetProperty("x").GetInt32());
        Assert.Equal(200, r.GetProperty("y").GetInt32());
        Assert.True(r.GetProperty("shift").GetBoolean());
        Assert.False(r.GetProperty("iconsVisible").GetBoolean());
        Assert.Equal("rename", r.GetProperty("interceptVerbs")[0].GetString());
    }

    [Theory]
    [InlineData(@"C:\a\b.txt", true)]
    [InlineData(@"\\server\share\a", true)]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false)]
    [InlineData("", false)]
    public void 判断文件系统路径(string p, bool expected) => Assert.Equal(expected, MenuContext.IsFileSystemPath(p));

    [Fact]
    public void 同一目录判断()
    {
        Assert.True(MenuExtensions.InSameFolder(new[] { @"C:\d\a.txt", @"C:\d\b.txt", @"C:\D\sub" }, out var f));
        Assert.Equal(@"C:\d", f, ignoreCase: true);
        Assert.False(MenuExtensions.InSameFolder(new[] { @"C:\d\a.txt", @"C:\e\b.txt" }, out _));
        Assert.False(MenuExtensions.InSameFolder(Array.Empty<string>(), out _));
    }
}
