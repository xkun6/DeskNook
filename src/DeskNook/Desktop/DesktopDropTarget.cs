using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeskNook.Model;
using DeskNook.Native;
using DeskNook.Services;
using DeskNook.Views;

namespace DeskNook.Desktop;

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
    private string? _forwardTag;      // 转发目标标识：item:图标key | map:映射格子Id | bg（桌面背景）
    private BoxState? _targetBox;     // 当前指针所在的格子（用于 Drop 时落位）
    private int _targetIndex;         // 普通格子里的插入位置
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
            _surface.SetDropFeedback(null, null, 0);
            _targetBox = null;
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
            var fromMapped = internalDrag && _c.DragContainer.Length > 0;
            if (_forward != null)
            {
                if (_forwardTag == "bg" && (!internalDrag || fromMapped))
                {
                    // 外部拖入 / 从映射目录拖出到桌面：文件操作完成后新项落在释放处（自由区的格子，或普通格子里的插入位置）
                    if (_targetBox is { Kind: BoxKind.Normal } nb) _c.SetPendingDropBox(nb.Id, _targetIndex);
                    else
                    {
                        var (col, row) = _surface.CellAtScreenPoint(pt);
                        _c.SetPendingDrop(_surface.MonitorName, col, row);
                    }
                }
                var hr = _forward.Drop(pDataObj, grfKeyState, pt, ref pdwEffect);
                Log.Info($"拖放转发给 {_forwardTag}：hr=0x{hr:X} 效果={pdwEffect}");
            }
            else if (internalDrag && !fromMapped && _c.DragAnchorKey != null)
            {
                // 桌面项在桌面内拖动：只改布局（自由区位置 / 格子成员），不动文件
                Log.Info($"内部拖放落位：目标格子={_targetBox?.Name ?? "(自由区)"} 插入位置={_targetIndex}");
                if (_targetBox is { Kind: BoxKind.Normal } nb) _c.MoveKeysToBox(_c.DragKeys!, nb, _targetIndex);
                else
                {
                    var (col, row) = _surface.CellAtScreenPoint(pt);
                    _c.MoveSelection(_c.DragAnchorKey, _surface.MonitorName, col, row);
                }
                pdwEffect = ShellApi.DROPEFFECT_NONE; // 仅改布局，不要让源端把它当成“移动文件”
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
            _surface.SetDropFeedback(null, null, 0);
            _targetBox = null;
            _data = null;
        }
        return 0;
    }

    /// <summary>
    /// 决定当前该由谁处理，并设置有效效果。目标优先级：
    /// 可接收的图标（文件夹/回收站…，含映射格子内的）→ 映射格子空白处（该目录的 IDropTarget）→
    /// 来自映射格子的项拖到桌面/普通格子（桌面背景的 IDropTarget，文件操作）→ 桌面项在桌面内拖动（只改布局）→ 外部拖入桌面背景。
    /// </summary>
    private void Evaluate(uint keyState, Win32.POINT pt, ref uint effect)
    {
        var internalDrag = _c.DragKeys != null;
        var srcContainer = internalDrag ? _c.DragContainer : "";
        var box = _surface.BoxAtScreenPoint(pt);
        var hitKey = _surface.KeyAtScreenPoint(pt);
        var hit = hitKey != null ? _c.ItemOf(hitKey) : null;

        string? wantTag = null;
        Func<IDropTarget?>? make = null;
        if (hit != null && !(internalDrag && _c.DragKeys!.Contains(hit.Key, StringComparer.OrdinalIgnoreCase)) && CanDropOn(hit))
        {
            wantTag = "item:" + hit.Key;
            make = () => ShellApi.GetUIObjectOf<IDropTarget>(new[] { hit.Pidl }, ShellApi.IID_IDropTarget, _hwnd);
        }
        else if (box is { Kind: BoxKind.Mapped } mb)
        {
            if (internalDrag && srcContainer == mb.Id)
            {
                // 拖回自己所在的映射格子：没有意义，不接收
                ReleaseForward(leave: true);
                SetFeedback(null, 0);
                effect = ShellApi.DROPEFFECT_NONE;
                return;
            }
            wantTag = "map:" + mb.Id;
            make = () => _c.CreateMappedDropTarget(mb, _hwnd);
        }
        else if (!internalDrag || srcContainer.Length > 0)
        {
            wantTag = "bg"; // 外部拖入，或从映射目录拖到桌面（Explorer 语义的文件操作）
            make = () => ShellApi.CreateViewObject<IDropTarget>(ShellApi.IID_IDropTarget, _hwnd);
        }

        // 反馈：普通格子显示插入位置，映射格子整体高亮
        var normalBox = box is { Kind: BoxKind.Normal } && (wantTag == null || wantTag == "bg") ? box : null;
        _targetBox = wantTag != null && wantTag.StartsWith("item:") ? null : box;
        _targetIndex = normalBox != null ? _surface.InsertIndexAtScreenPoint(normalBox, pt) : 0;
        SetFeedback(wantTag != null && wantTag.StartsWith("map:") ? box : normalBox, _targetIndex, insert: normalBox != null);

        if (wantTag == null)
        {
            // 桌面项在桌面内拖动：只改布局
            ReleaseForward(leave: true);
            effect = ShellApi.DROPEFFECT_MOVE;
            return;
        }

        if (_forward == null || _forwardTag != wantTag)
        {
            ReleaseForward(leave: true);
            _forward = make!();
            _forwardTag = wantTag;
            if (_forward == null) { effect = ShellApi.DROPEFFECT_NONE; return; }
            _forward.DragEnter(_data!, keyState, pt, ref effect);
        }
        else
        {
            _forward.DragOver(keyState, pt, ref effect);
        }
    }

    private void SetFeedback(BoxState? box, int index, bool insert = false) =>
        _surface.SetDropFeedback(box?.Id, insert ? box?.Id : null, index);

    private static readonly HashSet<string> DropOnExt = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".bat", ".cmd", ".com", ".scr", ".lnk", ".msi", ".ps1", ".vbs", ".js", ".jar", ".py" };

    /// <summary>
    /// 与 Explorer 一致：只有文件夹、虚拟项（回收站等）、快捷方式和可执行文件接收拖放；
    /// 普通文件虽然带 SFGAO_DROPTARGET，但拖到它上面只是改位置。
    /// </summary>
    private static bool CanDropOn(DesktopItem item)
    {
        if ((ShellApi.GetAttributes(item.Pidl, ShellApi.SFGAO_DROPTARGET) & ShellApi.SFGAO_DROPTARGET) == 0) return false;
        return item.IsFolder || item.IsVirtual || item.IsLink || DropOnExt.Contains(item.Extension);
    }

    private void ReleaseForward(bool leave)
    {
        if (_forward == null) return;
        try { if (leave) _forward.DragLeave(); }
        catch (Exception ex) { Log.Error("转发 DragLeave 异常", ex); }
        Marshal.ReleaseComObject(_forward);
        _forward = null;
        _forwardTag = null;
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
