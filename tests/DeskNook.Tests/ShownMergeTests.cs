using DeskNook.Desktop;

namespace DeskNook.Tests;

/// <summary>后台复核结果并入“虚拟项是否显示”缓存的纯函数。</summary>
public class ShownMergeTests
{
    private static Dictionary<string, bool> Cache() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["::{A}"] = true,
        ["::{B}"] = false,
    };

    [Fact]
    public void NoChange_ReturnsFalse()
    {
        var cache = Cache();
        var results = new Dictionary<string, bool> { ["::{a}"] = true, ["::{B}"] = false };
        Assert.False(DesktopItemSource.MergeShown(cache, results));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void FlippedValue_ReturnsTrueAndUpdates()
    {
        var cache = Cache();
        var results = new Dictionary<string, bool> { ["::{B}"] = true };
        Assert.True(DesktopItemSource.MergeShown(cache, results));
        Assert.True(cache["::{B}"]);
    }

    [Fact]
    public void NewKey_ReturnsTrueAndAdds()
    {
        var cache = Cache();
        var results = new Dictionary<string, bool> { ["::{C}"] = false };
        Assert.True(DesktopItemSource.MergeShown(cache, results));
        Assert.False(cache["::{C}"]);
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void EmptyResults_ReturnsFalse() =>
        Assert.False(DesktopItemSource.MergeShown(Cache(), new Dictionary<string, bool>()));
}
