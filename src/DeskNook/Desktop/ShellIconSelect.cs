namespace DeskNook.Desktop;

/// <summary>系统图像列表的选择与 JUMBO 小内容判定（纯函数，可单测）。</summary>
internal static class ShellIconSelect
{
    /// <summary>候选列表 Id：SHIL_SMALL=1、SHIL_LARGE=0、SHIL_EXTRALARGE=2、SHIL_JUMBO=4（按尺寸从小到大的常见顺序）。</summary>
    public static readonly int[] CandidateLists = { 1, 0, 2, 4 };

    public const int Jumbo = 4;
    public const int ExtraLarge = 2;

    /// <summary>
    /// 选尺寸 ≥ px 的最小列表；都不够选 JUMBO。尺寸 ≤ 0 的列表视为不可用（获取失败）。
    /// 尺寸相同时取先出现的。
    /// </summary>
    public static int SelectList(IReadOnlyList<(int Id, int Size)> sizes, int px)
    {
        var best = -1;
        var bestSize = int.MaxValue;
        foreach (var (id, size) in sizes)
            if (size > 0 && size >= px && size < bestSize) { best = id; bestSize = size; }
        return best >= 0 ? best : Jumbo;
    }

    /// <summary>
    /// JUMBO 图标是否“内容太小不可用”：只有 32/48 图标的程序，JUMBO 列表里是 256 画布左上角一个小图。
    /// 取 alpha&gt;0 像素包围盒，右下界都不超过画布的 1/4（内容只占左上 1/4×1/4）返回 true；全透明也按不可用处理。
    /// 输入为 BGRA 字节（每像素 4 字节，自上而下，stride = width*4）。
    /// </summary>
    public static bool JumboContentTooSmall(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return true;
        int maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = width - 1; x >= 0; x--)
            {
                if (bgra[row + x * 4 + 3] == 0) continue;
                if (x > maxX) maxX = x;
                maxY = y; // 行自上而下，最后一个有内容的行即下界
                break;
            }
        }
        if (maxX < 0) return true; // 全透明
        return maxX + 1 <= width / 4 && maxY + 1 <= height / 4;
    }
}
