using DeskNook.Desktop;

namespace DeskNook.Tests;

/// <summary>系统图像列表选择与 JUMBO 小内容判定（纯函数）。</summary>
public class ShellIconSelectTests
{
    // 候选顺序：SMALL(1) LARGE(0) EXTRALARGE(2) JUMBO(4)
    private static (int Id, int Size)[] Sizes(int small, int large, int extra, int jumbo) =>
        new[] { (1, small), (0, large), (2, extra), (4, jumbo) };

    [Theory]
    // 100% DPI：16/32/48/256
    [InlineData(16, 16, 32, 48, 256, 1)]
    [InlineData(32, 16, 32, 48, 256, 0)]
    [InlineData(40, 16, 32, 48, 256, 2)]
    [InlineData(48, 16, 32, 48, 256, 2)]
    [InlineData(49, 16, 32, 48, 256, 4)]
    [InlineData(96, 16, 32, 48, 256, 4)]
    [InlineData(300, 16, 32, 48, 256, 4)] // 都不够选 JUMBO
    // 150% DPI：24/48/72/256
    [InlineData(36, 24, 48, 72, 256, 0)]
    [InlineData(48, 24, 48, 72, 256, 0)]
    [InlineData(72, 24, 48, 72, 256, 2)]
    [InlineData(73, 24, 48, 72, 256, 4)]
    [InlineData(24, 24, 48, 72, 256, 1)]
    public void SelectList_PicksSmallestNotSmallerThanPx(int px, int small, int large, int extra, int jumbo, int expected) =>
        Assert.Equal(expected, ShellIconSelect.SelectList(Sizes(small, large, extra, jumbo), px));

    [Fact]
    public void SelectList_SkipsUnavailableLists()
    {
        // JUMBO 取不到（尺寸 0）、px 超过其余列表：仍返回 JUMBO（由调用方处理取图失败）
        Assert.Equal(ShellIconSelect.Jumbo, ShellIconSelect.SelectList(Sizes(16, 32, 48, 0), 64));
        // 中间的列表取不到时跳过
        Assert.Equal(4, ShellIconSelect.SelectList(Sizes(16, 32, 0, 256), 40));
    }

    private static byte[] Canvas(int size, Action<byte[], int> fill)
    {
        var px = new byte[size * size * 4];
        fill(px, size);
        return px;
    }

    private static void Block(byte[] px, int size, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var o = (y * size + x) * 4;
                px[o] = 10; px[o + 1] = 20; px[o + 2] = 30; px[o + 3] = 255;
            }
    }

    [Fact]
    public void JumboContentTooSmall_TopLeft48_True() =>
        Assert.True(ShellIconSelect.JumboContentTooSmall(Canvas(256, (p, s) => Block(p, s, 0, 0, 48, 48)), 256, 256));

    [Fact]
    public void JumboContentTooSmall_TopLeft64Boundary_True() =>
        Assert.True(ShellIconSelect.JumboContentTooSmall(Canvas(256, (p, s) => Block(p, s, 0, 0, 64, 64)), 256, 256));

    [Fact]
    public void JumboContentTooSmall_65Wide_False() =>
        Assert.False(ShellIconSelect.JumboContentTooSmall(Canvas(256, (p, s) => Block(p, s, 0, 0, 65, 48)), 256, 256));

    [Fact]
    public void JumboContentTooSmall_Full_False() =>
        Assert.False(ShellIconSelect.JumboContentTooSmall(Canvas(256, (p, s) => Block(p, s, 0, 0, s, s)), 256, 256));

    [Fact]
    public void JumboContentTooSmall_CenteredContent_False() =>
        Assert.False(ShellIconSelect.JumboContentTooSmall(Canvas(256, (p, s) => Block(p, s, 64, 64, 192, 192)), 256, 256));

    [Fact]
    public void JumboContentTooSmall_FullyTransparent_TreatedAsUnusable() =>
        Assert.True(ShellIconSelect.JumboContentTooSmall(new byte[256 * 256 * 4], 256, 256));

    [Fact]
    public void JumboContentTooSmall_InvalidInput_TreatedAsUnusable()
    {
        Assert.True(ShellIconSelect.JumboContentTooSmall(Array.Empty<byte>(), 256, 256));
        Assert.True(ShellIconSelect.JumboContentTooSmall(new byte[16], 0, 0));
    }
}
