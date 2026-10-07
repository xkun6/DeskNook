using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskNook.Model;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>
/// 图标缓存，两阶段加载（参照腾讯桌面整理：系统图像列表按图标索引取图，缩略图另取）：
/// 阶段 1（高优先级）SHGetFileInfo 取系统图标索引 → IImageList.GetIcon，按 (图标索引, 列表Id) 共享；
/// 阶段 2（低优先级）仅文件系统文件取缩略图，成功则替换缓存并触发 <see cref="Upgraded"/>。
/// 结果都是冻结的 BitmapSource，按 Key+像素尺寸缓存；缓存字典只在 UI 线程访问。
/// </summary>
internal sealed class ShellIconCache : IDisposable
{
    private const uint SIIGBF_THUMBNAILONLY = 0x8;
    private const long Stage1SlowMs = 100, Stage2SlowMs = 500;

    private sealed record Request(string Key, byte[] Pidl, int Px, bool IsLink, bool IsFolder, bool IsFileSystem, int Stage, int Gen);

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<(string, int), BitmapSource> _cache = new();
    private readonly Dictionary<(string, int), List<Action<BitmapSource?>>> _pending = new();
    /// <summary>按 (系统图标索引, 列表Id) 共享的位图（不含快捷方式箭头）；工作线程读写，整体失效时清空。</summary>
    private readonly ConcurrentDictionary<(int, int), BitmapSource> _indexCache = new();
    private readonly BlockingCollection<Request> _high = new();
    private readonly BlockingCollection<Request> _low = new();
    private readonly List<Thread> _threads = new();
    private int _generation; // 失效时自增，丢弃过期结果

    /// <summary>阶段 2 把缓存升级为缩略图后触发（UI 线程）：key、px。</summary>
    public event Action<string, int>? Upgraded;

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

    /// <summary>取图标：缓存命中则立即回调（同步），否则排队加载后在 UI 线程回调（只回调一次：阶段 1 结果或回退结果）。</summary>
    public void Get(DesktopItem item, int px, Action<BitmapSource?> done)
    {
        var k = (item.Key, px);
        if (_cache.TryGetValue(k, out var hit)) { done(hit); return; }
        if (_pending.TryGetValue(k, out var waiters)) { waiters.Add(done); return; }
        _pending[k] = new List<Action<BitmapSource?>> { done };
        _high.Add(new Request(item.Key, item.Pidl, px, item.IsLink, item.IsFolder, !item.IsVirtual, 1, _generation));
    }

    /// <summary>使缓存失效：key 为 null 表示全部。</summary>
    public void Invalidate(string? key)
    {
        if (key == null)
        {
            _cache.Clear();
            _indexCache.Clear();
            _generation++;
            return;
        }
        foreach (var k in _cache.Keys.Where(k => string.Equals(k.Item1, key, StringComparison.OrdinalIgnoreCase)).ToList())
            _cache.Remove(k);
        _generation++;
    }

    private void Worker()
    {
        using var lists = new ImageListSet();
        var queues = new[] { _high, _low };
        while (true)
        {
            Request req;
            // 高优先级队列在数组前面，TakeFromAny 优先取它；Dispose 后两个队列都完成，TakeFromAny 抛异常，线程退出
            try { BlockingCollection<Request>.TakeFromAny(queues, out req!); }
            catch (InvalidOperationException) { break; }
            catch (ArgumentException) { break; }

            try
            {
                if (req.Stage == 1) RunStage1(req, lists);
                else RunStage2(req);
            }
            catch (Exception ex) { Log.Error($"加载图标失败 {req.Key}", ex); }
        }
    }

    private void RunStage1(Request req, ImageListSet lists)
    {
        var started = Stopwatch.GetTimestamp();
        BitmapSource? bmp = null;
        var viaList = false;
        try
        {
            bmp = LoadFromImageList(req, lists);
            viaList = bmp != null;
        }
        catch (Exception ex) { Log.Error($"系统图像列表取图标失败 {req.Key}", ex); }
        if (bmp == null)
        {
            try { bmp = LoadImage(req.Pidl, req.Px, req.IsLink, 0); }
            catch (Exception ex) { Log.Error($"加载图标失败 {req.Key}", ex); }
        }
        var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (ms > Stage1SlowMs) Log.Info($"图标阶段1耗时 {ms} ms：{req.Key}");

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var k = (req.Key, req.Px);
            if (bmp != null && req.Gen == _generation) _cache[k] = bmp;
            if (_pending.Remove(k, out var waiters))
                foreach (var w in waiters)
                    try { w(bmp); } catch (Exception ex) { Log.Error("图标回调异常", ex); }
        });

        // 阶段 2：只有“文件系统里的普通文件”才可能有缩略图；走了回退路径（GetImage 已含缩略图）的不再升级
        if (viaList && !req.IsFolder && req.IsFileSystem && !req.IsLink)
        {
            try { _low.Add(req with { Stage = 2 }); }
            catch (InvalidOperationException) { } // 已 Dispose
        }
    }

    private void RunStage2(Request req)
    {
        var started = Stopwatch.GetTimestamp();
        var bmp = LoadImage(req.Pidl, req.Px, false, SIIGBF_THUMBNAILONLY); // 无缩略图（常见）返回 null，静默忽略
        var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (ms > Stage2SlowMs) Log.Info($"图标阶段2耗时 {ms} ms：{req.Key}");
        if (bmp == null) return;

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (req.Gen != _generation) return; // 期间发生过失效，丢弃
            _cache[(req.Key, req.Px)] = bmp;
            try { Upgraded?.Invoke(req.Key, req.Px); }
            catch (Exception ex) { Log.Error("图标升级回调异常", ex); }
        });
    }

    /// <summary>阶段 1：SHGetFileInfo 取系统图标索引 → 选图像列表 → IImageList.GetIcon；失败返回 null（调用方退回 GetImage）。</summary>
    private BitmapSource? LoadFromImageList(Request req, ImageListSet lists)
    {
        int index;
        var pidl = ShellApi.PidlFromBytes(req.Pidl);
        try
        {
            var fi = new Win32.SHFILEINFO();
            if (Win32.SHGetFileInfoW(pidl, 0, ref fi, (uint)Marshal.SizeOf<Win32.SHFILEINFO>(), Win32.SHGFI_PIDL | Win32.SHGFI_SYSICONINDEX) == IntPtr.Zero)
                return null;
            index = fi.iIcon;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }

        var listId = ShellIconSelect.SelectList(lists.Sizes(), req.Px);
        var key = (index, listId);
        if (!_indexCache.TryGetValue(key, out var shared))
        {
            shared = ReadListIcon(lists, listId, index, checkJumbo: listId == ShellIconSelect.Jumbo);
            // JUMBO 里只是左上角小图（或全透明）：改用 EXTRALARGE 的同索引图标
            if (shared == null && listId == ShellIconSelect.Jumbo)
                shared = ReadListIcon(lists, ShellIconSelect.ExtraLarge, index, checkJumbo: false);
            if (shared == null) return null;
            if (req.Gen == Volatile.Read(ref _generation)) _indexCache[key] = shared;
        }
        return req.IsLink ? AddLinkArrow(shared) : shared;
    }

    /// <summary>
    /// 从指定列表取图标并转成冻结的 Pbgra32 位图。checkJumbo 时若内容只占左上小块（或全透明）返回 null。
    /// 位图完全没有颜色信息也返回 null（交给回退路径）。
    /// </summary>
    private static BitmapSource? ReadListIcon(ImageListSet lists, int listId, int index, bool checkJumbo)
    {
        var list = lists.Get(listId);
        if (list == null) return null;
        if (list.GetIcon(index, Win32.ILD_TRANSPARENT, out var hicon) < 0 || hicon == IntPtr.Zero) return null;
        BitmapSource src;
        try
        {
            src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            Win32.DestroyIcon(hicon);
        }
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        var pixels = new byte[w * h * 4];
        src.CopyPixels(pixels, w * 4, 0);

        if (checkJumbo && ShellIconSelect.JumboContentTooSmall(pixels, w, h)) return null;

        // 旧式图标没有 alpha：有颜色就补成不透明，全空则视为失败
        var hasAlpha = false;
        var hasColor = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] != 0) { hasAlpha = true; break; }
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) hasColor = true;
        }
        if (!hasAlpha)
        {
            if (!hasColor) return null;
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var bgra = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        var converted = new FormatConvertedBitmap(bgra, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>每个工作线程自己持有一份系统图像列表 COM 对象（STA 线程各取各的，不跨线程用 RCW）；各列表尺寸进程内只取一次。</summary>
    private sealed class ImageListSet : IDisposable
    {
        private static readonly object SizesLock = new();
        private static (int Id, int Size)[]? s_sizes;
        private readonly IImageList?[] _lists = new IImageList?[8];
        private readonly bool[] _tried = new bool[8];

        public IImageList? Get(int id)
        {
            if (!_tried[id])
            {
                _tried[id] = true;
                var iid = ShellApi.IID_IImageList;
                // 系统图像列表是进程级单例，按指针身份复用的 RCW 绑定在第一个取到它的 STA 线程上，
                // 另一线程再取会 QueryInterface 失败（E_NOINTERFACE）；用 Unique 让每个线程有自己的 RCW
                if (Win32.SHGetImageList(id, ref iid, out var ppv) >= 0 && ppv != IntPtr.Zero)
                {
                    try { _lists[id] = (IImageList)Marshal.GetUniqueObjectForIUnknown(ppv); }
                    finally { Marshal.Release(ppv); }
                }
            }
            return _lists[id];
        }

        /// <summary>候选列表的实际尺寸（随系统 DPI 变）；取不到的列表尺寸记 0。</summary>
        public IReadOnlyList<(int Id, int Size)> Sizes()
        {
            lock (SizesLock)
            {
                if (s_sizes != null) return s_sizes;
                var sizes = new (int Id, int Size)[ShellIconSelect.CandidateLists.Length];
                for (var i = 0; i < sizes.Length; i++)
                {
                    var id = ShellIconSelect.CandidateLists[i];
                    var size = 0;
                    var list = Get(id);
                    if (list != null && list.GetIconSize(out var cx, out var cy) >= 0) size = Math.Max(cx, cy);
                    sizes[i] = (id, size);
                }
                if (sizes.Any(x => x.Size > 0)) s_sizes = sizes;
                return sizes;
            }
        }

        public void Dispose()
        {
            for (var i = 0; i < _lists.Length; i++)
            {
                var l = _lists[i];
                _lists[i] = null;
                if (l != null) try { Marshal.ReleaseComObject(l); } catch { }
            }
        }
    }

    private static BitmapSource? LoadImage(byte[] pidlBytes, int px, bool isLink, uint flags)
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
                if (factory.GetImage(new Win32.SIZE { cx = px, cy = px }, flags, out var hbmp) < 0 || hbmp == IntPtr.Zero)
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

    public void Dispose()
    {
        _high.CompleteAdding();
        _low.CompleteAdding();
    }
}
