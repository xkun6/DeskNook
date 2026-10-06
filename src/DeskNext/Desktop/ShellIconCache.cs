using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskNext.Model;
using DeskNext.Native;
using DeskNext.Services;

namespace DeskNext.Desktop;

/// <summary>
/// 图标缓存：IShellItemImageFactory.GetImage 在后台 STA 线程加载，
/// 转成冻结的 BitmapSource，按 Key+像素尺寸缓存。缓存本身只在 UI 线程访问。
/// </summary>
internal sealed class ShellIconCache : IDisposable
{
    private sealed record Request(string Key, byte[] Pidl, int Px, bool IsLink);

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<(string, int), BitmapSource> _cache = new();
    private readonly Dictionary<(string, int), List<Action<BitmapSource?>>> _pending = new();
    private readonly BlockingCollection<Request> _queue = new();
    private readonly List<Thread> _threads = new();
    private int _generation; // 失效时自增，丢弃过期结果

    public ShellIconCache(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        for (var i = 0; i < 2; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = $"ShellIconWorker{i}" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _threads.Add(t);
        }
    }

    public BitmapSource? TryGet(string key, int px) => _cache.GetValueOrDefault((key, px));

    /// <summary>取图标：缓存命中则立即回调（同步），否则排队加载后在 UI 线程回调。</summary>
    public void Get(DesktopItem item, int px, Action<BitmapSource?> done)
    {
        var k = (item.Key, px);
        if (_cache.TryGetValue(k, out var hit)) { done(hit); return; }
        if (_pending.TryGetValue(k, out var waiters)) { waiters.Add(done); return; }
        _pending[k] = new List<Action<BitmapSource?>> { done };
        _queue.Add(new Request(item.Key, item.Pidl, px, item.IsLink));
    }

    /// <summary>使缓存失效：key 为 null 表示全部。</summary>
    public void Invalidate(string? key)
    {
        if (key == null)
        {
            _cache.Clear();
            _generation++;
            return;
        }
        foreach (var k in _cache.Keys.Where(k => string.Equals(k.Item1, key, StringComparison.OrdinalIgnoreCase)).ToList())
            _cache.Remove(k);
        _generation++;
    }

    private void Worker()
    {
        foreach (var req in _queue.GetConsumingEnumerable())
        {
            var gen = _generation;
            BitmapSource? bmp = null;
            try { bmp = LoadImage(req.Pidl, req.Px, req.IsLink); }
            catch (Exception ex) { Log.Error($"加载图标失败 {req.Key}", ex); }

            _dispatcher.BeginInvoke(() =>
            {
                var k = (req.Key, req.Px);
                if (bmp != null && gen == _generation) _cache[k] = bmp;
                if (_pending.Remove(k, out var waiters))
                    foreach (var w in waiters)
                        try { w(bmp); } catch (Exception ex) { Log.Error("图标回调异常", ex); }
            });
        }
    }

    private static BitmapSource? LoadImage(byte[] pidlBytes, int px, bool isLink)
    {
        var pidl = ShellApi.PidlFromBytes(pidlBytes);
        try
        {
            if (Win32.SHCreateItemFromIDList(pidl, ShellApi.IID_IShellItemImageFactory, out var ppv) < 0 || ppv == IntPtr.Zero)
                return null;
            var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
            Marshal.Release(ppv);
            try
            {
                if (factory.GetImage(new Win32.SIZE { cx = px, cy = px }, 0, out var hbmp) < 0 || hbmp == IntPtr.Zero)
                    return null;
                try { var bs = ToBitmapSource(hbmp); return isLink && bs != null ? AddLinkArrow(bs) : bs; }
                finally { Win32.DeleteObject(hbmp); }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>GetImage 不带快捷方式箭头：用系统“链接”图标叠加到左下角（大小约为图标的 1/3，与系统桌面一致）。</summary>
    private static BitmapSource AddLinkArrow(BitmapSource icon)
    {
        var info = new Win32.SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<Win32.SHSTOCKICONINFO>() };
        // SIID_LINK = 29；SHGSI_ICON | SHGSI_LARGEICON
        if (Win32.SHGetStockIconInfo(29, 0x100 | 0x0, ref info) != 0 || info.hIcon == IntPtr.Zero) return icon;
        try
        {
            var arrow = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var w = icon.PixelWidth;
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(icon, new Rect(0, 0, w, icon.PixelHeight));
                dc.DrawImage(arrow, new Rect(0, icon.PixelHeight - w * 0.65, w * 0.65, w * 0.65));
            }
            var rtb = new RenderTargetBitmap(w, icon.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        finally
        {
            Win32.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>HBITMAP(32bpp，带 alpha) → 冻结的 Pbgra32 BitmapSource。</summary>
    private static BitmapSource? ToBitmapSource(IntPtr hbmp)
    {
        if (Win32.GetObject(hbmp, Marshal.SizeOf<Win32.BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        if (w <= 0 || h <= 0) return null;

        var bih = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // 自上而下
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        var hdc = Win32.GetDC(IntPtr.Zero);
        try
        {
            if (Win32.GetDIBits(hdc, hbmp, 0, (uint)h, pixels, ref bih, 0) == 0) return null;
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, hdc);
        }

        // 位图本身不带 alpha 时（旧式图标）把 alpha 补成不透明，避免整张透明
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        var converted = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    public void Dispose() => _queue.CompleteAdding();
}
