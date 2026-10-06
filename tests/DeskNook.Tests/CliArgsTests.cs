using DeskNook.Services;

namespace DeskNook.Tests;

/// <summary>命令行 --autostart=on|off 解析。</summary>
public class CliArgsTests
{
    [Theory]
    [InlineData("--autostart=on", true)]
    [InlineData("--autostart=off", false)]
    [InlineData("--AutoStart=ON", true)]
    [InlineData("--AUTOSTART=Off", false)]
    public void 合法值(string arg, bool expected) => Assert.Equal(expected, CliArgs.ParseAutostart(new[] { arg }));

    [Theory]
    [InlineData("--autostart")]
    [InlineData("--autostart=")]
    [InlineData("--autostart=1")]
    [InlineData("--autostart=on1")]
    [InlineData("--exit")]
    public void 非法或无关参数返回null(string arg) => Assert.Null(CliArgs.ParseAutostart(new[] { arg }));

    [Fact]
    public void 无参数返回null() => Assert.Null(CliArgs.ParseAutostart(Array.Empty<string>()));

    [Fact]
    public void 多个时取最后一个合法值() =>
        Assert.False(CliArgs.ParseAutostart(new[] { "--autostart=on", "--no-proxy", "--autostart=off", "--autostart=bad" }));

    [Fact]
    public void 与其他参数混用() => Assert.True(CliArgs.ParseAutostart(new[] { "--attach=owner", "--autostart=on" }));
}
