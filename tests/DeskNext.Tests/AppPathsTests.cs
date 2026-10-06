using DeskNext.Services;

namespace DeskNext.Tests;

/// <summary>数据根判断：纯函数，路径全部注入，不碰真实环境。</summary>
public class AppPathsTests
{
    private const string Pf = @"C:\Program Files";
    private const string Pf86 = @"C:\Program Files (x86)";
    private const string Roaming = @"C:\Users\u\AppData\Roaming";

    private static string Resolve(string baseDir) => AppPaths.ResolveDataDir(baseDir, Pf, Pf86, Roaming);

    [Theory]
    [InlineData(@"C:\Program Files\DeskNext\")]
    [InlineData(@"C:\Program Files\DeskNext")]
    [InlineData(@"c:\program files\DeskNext\bin\")]
    [InlineData(@"C:\Program Files\")]
    [InlineData(@"C:\Program Files\..\Program Files\DeskNext\")]
    public void ProgramFiles下使用AppData(string baseDir) =>
        Assert.Equal(Path.Combine(Roaming, "DeskNext"), Resolve(baseDir));

    [Theory]
    [InlineData(@"C:\Program Files (x86)\DeskNext\")]
    [InlineData(@"C:\PROGRAM FILES (X86)\DeskNext")]
    public void ProgramFilesX86下使用AppData(string baseDir) =>
        Assert.Equal(Path.Combine(Roaming, "DeskNext"), Resolve(baseDir));

    [Theory]
    [InlineData(@"D:\Tools\DeskNext\")]
    [InlineData(@"J:\Program\APP\xk-desk\bin\")]
    [InlineData(@"C:\Users\u\Desktop\xk\")]
    public void 普通目录使用exe目录下的data(string baseDir) =>
        Assert.Equal(Path.Combine(baseDir, "data"), Resolve(baseDir));

    [Theory]
    [InlineData(@"C:\Program FilesX\DeskNext\")]
    [InlineData(@"C:\Program Files (x86)X\DeskNext\")]
    [InlineData(@"C:\Program Files2\")]
    [InlineData(@"D:\Program Files\DeskNext\")]
    public void 前缀相似但不在其下使用data(string baseDir) =>
        Assert.Equal(Path.Combine(baseDir, "data"), Resolve(baseDir));

    [Fact]
    public void ProgramFiles路径为空时不误判() =>
        Assert.Equal(Path.Combine(@"D:\x\", "data"), AppPaths.ResolveDataDir(@"D:\x\", "", "", Roaming));

    [Fact]
    public void 各文件在数据根下()
    {
        Assert.Equal(Path.Combine(AppPaths.DataDir, "layout.json"), AppPaths.Layout);
        Assert.Equal(Path.Combine(AppPaths.DataDir, "settings.json"), AppPaths.Settings);
        Assert.Equal(Path.Combine(AppPaths.DataDir, "organize-undo.json"), AppPaths.OrganizeUndo);
        Assert.Equal(Path.Combine(AppPaths.DataDir, "logs"), AppPaths.LogsDir);
        Assert.Equal(Path.Combine(AppPaths.DataDir, "running.flag"), AppPaths.RunningFlag);
        Assert.Equal(Path.Combine(AppPaths.DataDir, "shellext"), AppPaths.ShellExtDir);
        Assert.Equal(Path.Combine(AppPaths.DataDir, "icons"), AppPaths.IconsDir);
    }
}
