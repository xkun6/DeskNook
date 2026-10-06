using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using XkDesk.Model;
using XkDesk.Native;
using XkDesk.Services;
using XkDesk.Views;

namespace XkDesk.Desktop;

/// <summary>
/// 宿主窗口的 OLE 拖放目标。拖放行为交给 Shell 自己的 IDropTarget 处理（移动/复制/快捷方式规则、
/// 修饰键、右键拖放菜单都与 Explorer 一致）：
/// 鼠标在可接收的图标（文件夹/回收站…）上 → 转发给该项的 IDropTarget；否则
/// 外部拖入 → 转发给桌面背景的 IDropTarget；本程序自己拖的图标落在空白处 → 仅改位置。
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class DesktopDropTarget : IDropTarget, IDisposable
{
    private readonly DesktopController _c;
    private readonly DesktopSurface _surface;
    private readonly IntPtr _hwnd;
    private IDropTargetHelper? _helper;

    private IDataObject? _data;
    private IDropTarget? _forward;
    private string? _forwardKey;      // 转发目标对应的图标 key；null = 桌面背景
    private bool _registered;

    public DesktopDropTarget(DesktopController controller, DesktopSurface surface, IntPtr hwnd)
    {
        _c = controller;
        _surface = surface;
        _hwnd = hwnd;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellApi.CLSID_DragDropHelper)!;
            _helper = (IDropTargetHelper)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            Log.Error("创建 DragDropHelper 失败（拖放将没有拖拽图像）", ex);
        }
        Win32.RevokeDragDrop(hwnd); // WPF 已为窗口注册了自己的目标，先撤销
        var hr = Win32.RegisterDragDrop(hwnd, this);
        _registered = hr == 0;
        Log.Info($"RegisterDragDrop hwnd=0x{hwnd:X} hr=0x{hr:X}");
    }

    public int DragEnter(IDataObject pDataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        try
        {
            _data = pDataObj;
            _helper?.DragEnter(_hwnd, pDataObj, ref pt, pdwEffect);
            Evaluate(grfKeyState, pt, ref pdwEffect);
        }
        catch (Exception ex) { Log.Error("DragEnter 异常", ex); pdwEffect = 0; }
        return 0;
    }

    public int DragOver(uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        try
        {
            Evaluate(grfKeyState, pt, ref pdwEffect);
            _helper?.DragOver(ref pt, pdwEffect);
        }
        catch (Exception ex) { Log.Error("DragOver 异常", ex); pdwEffect = 0; }
        return 0;
    }

    public int DragLeave()
    {
        try
        {
            _helper?.DragLeave();
            ReleaseForward(leave: true);
            _data = null;
        }
        catch (Exception ex) { Log.Error("DragLeave 异常", ex); }
        return 0;
    }

    public int Drop(IDataObject pDataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        try
        {
            _data = pDataObj;
            Evaluate(grfKeyState, pt, ref pdwEffect);
            var effectForHelper = pdwEffect;
            _helper?.Drop(pDataObj, ref pt, effectForHelper);

            var internalDrag = _c.DragKeys != null;
            if (_forward != null)
            {
                if (_forwardKey == null && !internalDrag)
                {
                    // 外部拖入桌面背景：新项落在释放的格子
                    var (col, row) = _surface.CellAtScreenPoint(pt);
                    _c.SetPendingDrop(_surface.MonitorName, col, row);
                }
                var hr = _forward.Drop(pDataObj, grfKeyState, pt, ref pdwEffect);
                Log.Info($"拖放转发给{(_forwardKey == null ? "桌面背景" : "图标 " + _forwardKey)}：hr=0x{hr:X} 效果={pdwEffect}");
            }
            else if (internalDrag && _c.DragAnchorKey != null)
            {
                var (col, row) = _surface.CellAtScreenPoint(pt);
                _c.MoveSelection(_c.DragAnchorKey, _surface.MonitorName, col, row);
                pdwEffect = ShellApi.DROPEFFECT_NONE; // 仅改位置，不要让源端把它当成“移动文件”
            }
            else
            {
                pdwEffect = ShellApi.DROPEFFECT_NONE;
            }
        }
        catch (Exception ex) { Log.Error("Drop 异常", ex); pdwEffect = 0; }
        finally
        {
            ReleaseForward(leave: false);
            _data = null;
        }
        return 0;
    }

    /// <summary>决定当前该由谁处理，并设置有效效果。</summary>
    private void Evaluate(uint keyState, Win32.POINT pt, ref uint effect)
    {
        var internalDrag = _c.DragKeys != null;
        var hitKey = _surface.KeyAtScreenPoint(pt);
        var hit = hitKey != null ? _c.ItemOf(hitKey) : null;

        // 目标：图标（可接收拖放且不是被拖的项）→ 背景（外部拖入）→ 无（内部改位置）
        string? wantKey = null;
        var wantItem = false;
        if (hit != null && !(internalDrag && _c.DragKeys!.Contains(hit.Key, StringComparer.OrdinalIgnoreCase)) && CanDropOn(hit))
        {
            wantKey = hit.Key;
            wantItem = true;
        }
        var wantBackground = !wantItem && !internalDrag;

        if (!wantItem && !wantBackground)
        {
            ReleaseForward(leave: true);
            effect = ShellApi.DROPEFFECT_MOVE;
            return;
        }

        if (_forward == null || _forwardKey != wantKey)
        {
            ReleaseForward(leave: true);
            _forward = wantItem
                ? ShellApi.GetUIObjectOf<IDropTarget>(new[] { hit!.Pidl }, ShellApi.IID_IDropTarget, _hwnd)
                : ShellApi.CreateViewObject<IDropTarget>(ShellApi.IID_IDropTarget, _hwnd);
            _forwardKey = wantKey;
            if (_forward == null) { effect = ShellApi.DROPEFFECT_NONE; return; }
            _forward.DragEnter(_data!, keyState, pt, ref effect);
        }
        else
        {
            _forward.DragOver(keyState, pt, ref effect);
        }
    }

    private static bool CanDropOn(DesktopItem item)
    {
        uint attrs = ShellApi.SFGAO_DROPTARGET;
        using var arr = new ShellApi.PidlArray(new[] { item.Pidl });
        return ShellApi.Desktop.GetAttributesOf(1, arr.Ptrs, ref attrs) >= 0 && (attrs & ShellApi.SFGAO_DROPTARGET) != 0;
    }

    private void ReleaseForward(bool leave)
    {
        if (_forward == null) return;
        try { if (leave) _forward.DragLeave(); }
        catch (Exception ex) { Log.Error("转发 DragLeave 异常", ex); }
        Marshal.ReleaseComObject(_forward);
        _forward = null;
        _forwardKey = null;
    }

    public void Dispose()
    {
        if (_registered)
        {
            Win32.RevokeDragDrop(_hwnd);
            _registered = false;
        }
        ReleaseForward(leave: false);
        if (_helper != null) { Marshal.ReleaseComObject(_helper); _helper = null; }
    }
}
