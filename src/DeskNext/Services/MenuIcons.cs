using System.IO;
using System.Reflection;

namespace DeskNext.Services;

/// <summary>
/// 自定义菜单项的线条图标：嵌入资源里的多尺寸 ICO 释放到 data\icons\，
/// 路径交给 Shell 扩展（它按菜单当前 DPI 选最接近的帧，转成 32bpp PARGB 位图）。
/// </summary>
internal static class MenuIcons
{
    public static string Dir { get; } = AppPaths.IconsDir;

    /// <summary>图标文件路径（纯字符串计算，不做 IO；文件由 <see cref="EnsureExtracted"/> 在启动时释放）。</summary>
    public static string PathOf(string name) => System.IO.Path.Combine(Dir, name + ".ico");

    /// <summary>释放全部嵌入的菜单图标（长度不一致时覆盖）。幂等，失败只记日志。</summary>
    public static void EnsureExtracted()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith("Assets.", StringComparison.Ordinal) || !res.EndsWith(".ico", StringComparison.Ordinal)) continue;
                var file = System.IO.Path.Combine(Dir, res["Assets.".Length..]);
                using var src = asm.GetManifestResourceStream(res)!;
                if (File.Exists(file) && new FileInfo(file).Length == src.Length) continue;
                using var dst = File.Create(file);
                src.CopyTo(dst);
            }
        }
        catch (Exception ex)
        {
            Log.Error("释放菜单图标失败", ex);
        }
    }

    /// <summary>读取嵌入的 ICO 原始字节（托盘图标用）；找不到返回 null。</summary>
    public static byte[]? ReadResource(string fileName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Assets." + fileName);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
