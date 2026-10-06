using DeskNook.Desktop;

namespace DeskNook.Tests;

/// <summary>ExePath（未运行时 Shell 扩展启动用）是否需要重写的纯函数判断。</summary>
public class ShellExtRegistrarTests
{
    private const string Exe = @"C:\Apps\DeskNook\DeskNook.exe";

    [Theory]
    [InlineData(null, true)]                                  // 没有值：要写
    [InlineData(@"C:\Old\DeskNook.exe", true)]                // 路径不同：要写
    [InlineData(@"c:\apps\desknook\desknook.exe", true)]      // 仅大小写不同按字符串精确比较，重写成当前真实路径
    [InlineData(Exe, false)]                                  // 已是最新：不重写
    public void NeedsExePathUpdate_按当前值判断(string? current, bool expected) =>
        Assert.Equal(expected, ShellExtRegistrar.NeedsExePathUpdate(current, Exe));

    [Fact]
    public void NeedsExePathUpdate_值类型不是字符串时要写() =>
        Assert.True(ShellExtRegistrar.NeedsExePathUpdate(123, Exe));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NeedsExePathUpdate_exe为空不写(string? exe) =>
        Assert.False(ShellExtRegistrar.NeedsExePathUpdate("x", exe));
}
