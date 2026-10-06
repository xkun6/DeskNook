using DeskNook.Desktop;

namespace DeskNook.Tests;

/// <summary>桌面项通知 → 是否需要清空“虚拟项是否显示”缓存的判断（纯函数）。</summary>
public class ShownCacheResetTests
{
    [Theory]
    // 新建文件批次（日志：0x2 [E:\Desktop\x.txt]、0x2000 [E:\Desktop]、0x4000000 空）
    [InlineData(0x2, @"E:\Desktop\新建文本文档 (6).txt", null, false)]
    [InlineData(0x8, @"E:\Desktop\新建文件夹", null, false)]
    [InlineData(0x2000, @"E:\Desktop", null, false)]
    [InlineData(0x4000000, null, null, false)]
    [InlineData(0x4, @"E:\Desktop\a.txt", null, false)]
    [InlineData(0x1, @"E:\Desktop\a.txt", @"E:\Desktop\b.txt", false)]
    // 失效条件
    [InlineData(0x08000000, null, null, true)]
    [InlineData(0x1000, "::{679F85CB-0220-4080-B29B-5540CC05AAB6}", null, true)]
    [InlineData(0x2, null, "::{645FF040-5081-101B-9F08-00AA002F954E}", true)]
    [InlineData(0x1000, null, null, true)]
    [InlineData(0x2000, "", "", true)]
    public void Decide(int evt, string? n1, string? n2, bool expected) =>
        Assert.Equal(expected, DesktopItemSource.NeedsShownCacheReset(evt, n1, n2));
}
