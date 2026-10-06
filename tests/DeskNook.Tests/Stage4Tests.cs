using System.Text.Json;
using Microsoft.Win32;
using DeskNook.Desktop;
using DeskNook.Model;
using DeskNook.Services;

namespace DeskNook.Tests;

/// <summary>开机自启：使用测试专用注册表键，绝不触碰真实的 Run 键。</summary>
public class AutoStartTests : IDisposable
{
    private const string TestKey = @"Software\DeskNookTests\Run";
    private readonly AutoStart _a = new(TestKey, "DeskNookTest", () => @"C:\Apps\DeskNook\DeskNook.exe");

    public AutoStartTests() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\DeskNookTests", throwOnMissingSubKey: false);
    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\DeskNookTests", throwOnMissingSubKey: false);

    [Fact]
    public void 默认未启用()
    {
        Assert.False(_a.IsEnabled);
        Assert.Null(_a.CurrentValue);
    }

    [Fact]
    public void 启用写入带引号的exe路径_禁用删除值()
    {
        Assert.True(_a.SetEnabled(true));
        Assert.True(_a.IsEnabled);
        Assert.Equal("\"" + @"C:\Apps\DeskNook\DeskNook.exe" + "\"", _a.CurrentValue);
        Assert.True(_a.SetEnabled(false));
        Assert.False(_a.IsEnabled);
        Assert.Null(_a.CurrentValue);
    }

    [Fact]
    public void 禁用时值不存在也不报错_重复启用幂等()
    {
        Assert.True(_a.SetEnabled(false));
        Assert.True(_a.SetEnabled(true));
        Assert.True(_a.SetEnabled(true));
        Assert.True(_a.IsEnabled);
    }

    [Fact]
    public void 状态以注册表为准_外部修改可见()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue("DeskNookTest", "\"" + @"X:\other.exe" + "\"");
        Assert.True(_a.IsEnabled);
        Assert.True(_a.Toggle());
        Assert.False(_a.IsEnabled);
    }

    [Fact]
    public void 取不到exe路径时启用失败()
    {
        var a = new AutoStart(TestKey, "DeskNookTest", () => null);
        Assert.False(a.SetEnabled(true));
        Assert.False(a.IsEnabled);
    }
}

public class Stage4SettingsTests
{
    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "xk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 新字段默认值()
    {
        var s = new AppSettings();
        Assert.True(s.DoubleClickToggle);
        Assert.Equal("system", s.IconSizeMode);
        Assert.Equal(AppSettings.DefaultBoxOpacity, s.BoxOpacity);
        Assert.Equal(7, s.LogRetentionDays);
    }

    [Fact]
    public void 旧版settings_json缺新字段时取默认值且规则保留()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"Version\":1,\"OrganizeRules\":[{\"Name\":\"文档\",\"Extensions\":[\"txt\"]}]}");
            var s = new SettingsStore(path).Load();
            Assert.True(s.DoubleClickToggle);
            Assert.Equal("system", s.IconSizeMode);
            Assert.Equal(0.7, s.BoxOpacity);
            Assert.Equal(7, s.LogRetentionDays);
            Assert.Single(s.OrganizeRules);
            Assert.Equal("文档", s.OrganizeRules[0].Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 新字段读写往返()
    {
        var dir = Temp();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            Assert.True(store.Save(new AppSettings { DoubleClickToggle = false, IconSizeMode = "large", BoxOpacity = 0.4 }));
            var s = store.Load();
            Assert.False(s.DoubleClickToggle);
            Assert.Equal("large", s.IconSizeMode);
            Assert.Equal(0.4, s.BoxOpacity, 3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0.0, 0.2)]
    [InlineData(5.0, 1.0)]
    [InlineData(-1.0, 0.2)]
    [InlineData(0.55, 0.55)]
    public void 透明度夹到范围内(double input, double expected)
    {
        var s = new AppSettings { BoxOpacity = input };
        s.Normalize();
        Assert.Equal(expected, s.BoxOpacity, 3);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 7)]
    [InlineData(14, 14)]
    [InlineData(30, 30)]
    [InlineData(5, 7)]
    [InlineData(0, 7)]
    [InlineData(-1, 7)]
    [InlineData(365, 7)]
    public void 日志保留天数只接受可选值否则回默认(int input, int expected)
    {
        var s = new AppSettings { LogRetentionDays = input };
        s.Normalize();
        Assert.Equal(expected, s.LogRetentionDays);
    }

    [Fact]
    public void 未知图标大小模式回到跟随系统()
    {
        var s = new AppSettings { IconSizeMode = "huge" };
        s.Normalize();
        Assert.Equal("system", s.IconSizeMode);
    }

    [Theory]
    [InlineData("small", 32)]
    [InlineData("medium", 48)]
    [InlineData("large", 96)]
    public void 图标大小模式对应像素(string mode, int px) => Assert.Equal(px, AppSettings.IconSizeOf(mode));

    [Fact]
    public void 跟随系统没有固定像素() => Assert.Null(AppSettings.IconSizeOf("system"));

    [Fact]
    public void 布局隐藏状态默认显示_旧layout_json兼容_往返保持()
    {
        Assert.False(new LayoutState().View.IconsHidden);
        var old = JsonSerializer.Deserialize<LayoutState>("{\"Version\":1,\"View\":{\"IconSize\":48,\"SortKey\":\"\"}}")!;
        Assert.False(old.View.IconsHidden);
        old.View.IconsHidden = true;
        var back = JsonSerializer.Deserialize<LayoutState>(JsonSerializer.Serialize(old))!;
        Assert.True(back.View.IconsHidden);
    }
}

public class ProxyVersionTests
{
    [Fact]
    public void 标题与注册的DLL一致不算过期() =>
        Assert.False(ExplorerMenuProxy.IsOutdated("DeskNookShellExt.06c5816b.dll", "DeskNookShellExt.06C5816B.dll"));

    [Fact]
    public void 哈希不同算过期() =>
        Assert.True(ExplorerMenuProxy.IsOutdated("DeskNookShellExt.06c5816b.dll", "DeskNookShellExt.aaaaaaaa.dll"));

    [Fact]
    public void 没有期望版本或没有标题时不判断()
    {
        Assert.False(ExplorerMenuProxy.IsOutdated("DeskNookShellExt.06c5816b.dll", ""));
        Assert.False(ExplorerMenuProxy.IsOutdated("", "DeskNookShellExt.aaaaaaaa.dll"));
        Assert.False(ExplorerMenuProxy.IsOutdated(null, "DeskNookShellExt.aaaaaaaa.dll"));
    }
}

/// <summary>日志过期判定：纯函数，只处理文件名，不碰磁盘。</summary>
public class LogRetentionTests
{
    private static readonly DateTime Today = new(2026, 10, 7);

    [Fact]
    public void 保留七天含今天_更早的过期()
    {
        var r = Log.ExpiredFiles(new[] { "desknook-2026-10-01.log", "desknook-2026-09-30.log" }, Today, 7);
        Assert.Equal(new[] { "desknook-2026-09-30.log" }, r);
    }

    [Fact]
    public void 不匹配新文件名或日期非法的都不碰()
    {
        var r = Log.ExpiredFiles(new[] { "desknook.log", "desknook-abc.log", "other.txt", "desknook-2026-13-01.log", "shellext.log" }, Today, 1);
        Assert.Empty(r);
    }

    [Fact]
    public void 保留一天只留今天()
    {
        var r = Log.ExpiredFiles(new[] { "desknook-2026-10-07.log", "desknook-2026-10-06.log" }, Today, 1);
        Assert.Equal(new[] { "desknook-2026-10-06.log" }, r);
    }

    [Fact]
    public void 未来日期的文件不删()
    {
        Assert.Empty(Log.ExpiredFiles(new[] { "desknook-2026-10-08.log", "desknook-2030-01-01.log" }, Today, 1));
    }
}
