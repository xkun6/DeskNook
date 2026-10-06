# 桌面层接管

对应代码：`src/DeskNook/Desktop/DesktopShell.cs`、`src/DeskNook/Desktop/DesktopHostWindow.xaml.cs`、`src/DeskNook/App.xaml.cs`（重建与重挂）、`src/DeskNook/app.manifest`。

目标：在不改变 Explorer 原生窗口结构的前提下，让 DeskNook 的画布出现在壁纸之上、所有普通窗口之下，并能接收鼠标/键盘。

## 窗口层级与定位

Explorer 桌面窗口的典型结构：

```
Progman                          (class "Progman")
  SHELLDLL_DefView               <- Win11 24H2 等情形：一直在 Progman 下
    SysListView32 "FolderView"   <- 系统桌面图标
WorkerW                          <- 其他情形：DefView 挂在某个含它的 WorkerW 下
  SHELLDLL_DefView
    SysListView32 "FolderView"
```

`DesktopShell.FindDesktop()` 的逻辑：

1. `FindWindow("Progman")`，没有则返回 `default`（`IsValid=false`）。
2. 先 `FindWindowEx(progman, "SHELLDLL_DefView")`；没找到再 `EnumWindows` 找类名为 `WorkerW` 且含 `SHELLDLL_DefView` 子窗口的那个。
3. 在 DefView 下找 `SysListView32`（标题 `FolderView`）。
4. 返回 `DesktopInfo(Progman, DefView, DefViewParent, ListView)`；`DefViewParent` 就是“承载 DefView 的顶层窗口”（Progman 或 WorkerW）。

`FindDesktop` 不发送任何消息。历史上曾向 Progman 发 `0x052C`（“生成 WorkerW”）以获得一个位于图标之后的窗口，提交 `45d4dcb` 起彻底移除，原因：

- 代码注释记载的原因：不改变 Explorer 原生窗口结构（该消息会让 Explorer 新增 WorkerW），也不去打扰尚未就绪的新 Explorer（重挂轮询期间曾因此改为“仅轮询”）；
- 设计上的推断：DeskNook 不需要“在图标之后”的窗口——系统图标层本身已被 `SW_HIDE`，宿主窗口只要在壁纸之上即可；少依赖一个非公开消息，对 Win11 24H2 的结构变化也更稳（后者未实测）。

不要再加回 `0x052C`。

## 隐藏/恢复系统图标

- `DesktopShell.HideIcons(info)`：`ShowWindow(ListView, SW_HIDE)`，记录 `_lastListView` 与 `_hidden`。**不写注册表**（不是“显示桌面图标”那个注册表开关），所以状态随 Explorer 进程生命周期消亡，崩溃后重启 Explorer 即恢复。
- `DesktopShell.RestoreIcons()`：幂等；重新 `FindDesktop` 取最新 ListView，找不到再用 `IsWindow(_lastListView)` 的旧句柄。
- DeskNook 运行期间系统 ListView 始终保持隐藏。“查看 ▸ 显示桌面图标”由代理拦截后只切换 DeskNook 自己画的图标，见 [menu.md](menu.md) 的动词拦截一节。
- 为什么还要保留隐藏的系统 DefView/ListView：右键菜单“查看/排序方式/刷新/粘贴/撤销”等 DefView 自带项需要真实 DefView 作站点；系统视图也是首次导入图标位置、图标大小与间距的来源（`src/DeskNook/Desktop/SystemDesktopView.cs`）。

## 宿主窗口

每个显示器一个 `DesktopHostWindow`（`Window`，`WindowStyle=None`、`ShowInTaskbar=False`、`ShowActivated=False`，背景 `#01000000`），内容是 `DesktopSurface`。

关键点：

- 背景用 alpha=1 的近乎全透明色而不是 `Transparent`：整块区域都能命中鼠标，空白处的双击、框选、右键才能收到。
- 样式追加 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`：不进 Alt+Tab、不抢焦点（键盘焦点见下）。
- 位置大小**不用 WPF 的 Left/Top**，而是 `ApplyBounds()` 用 `SetWindowPos` 按显示器物理像素矩形设置，避免 DPI 换算误差；`OnDpiChanged` 里强制恢复一次。
- 窗口关闭/重建时释放 `WinEventHook`、`DesktopDropTarget`，`DesktopSurface.Detach()` 取消对控制器事件的订阅（否则重建后事件会叠加）。

### 挂载方式：owner（默认）与 child

由命令行 `--attach=owner|child` 选择，默认 `Owner`（`App._attach`）。

| | owner（默认） | child |
|---|---|---|
| 做法 | `SetWindowLongPtr(GWLP_HWNDPARENT, DefViewParent)`：把承载 DefView 的顶层窗口设为 owner，再 `HWND_BOTTOM` | `SetParent(host, DefViewParent)` + 去掉 `WS_POPUP/CAPTION/THICKFRAME` 加 `WS_CHILD`，`HWND_TOP`（位于 DefView 之上） |
| 层级 | owned 窗口始终在 owner 之上；owner 是桌面层窗口，所以宿主窗口紧贴桌面层、在普通窗口之下 | 成为桌面窗口的子窗口，坐标相对父窗口（需 `ScreenToClient` 换算，虚拟屏幕原点可能为负） |
| 风险 | Z 序可能被 Explorer 改动 → 需要维护（见下） | 对 Explorer 窗口结构依赖更强，Win11 24H2 上更脆弱 |

选定 owner 的依据是阶段 0 的实测（`App.xaml.cs` 注释“阶段 0 实测选定 owner + dwm”）；`--attach=child` 仅作对照/备用。

### 透明方案：dwm（默认）与 layered

`--transparency=dwm|layered`，默认 `Dwm`。

- `layered`：`AllowsTransparency=true` 的分层窗口，软件合成，4K 屏上 CPU 占用高。
- `dwm`：窗口非分层，`SourceInitialized` 里 `CompositionTarget.BackgroundColor=Transparent` + `DwmExtendFrameIntoClientArea(-1,-1,-1,-1)`，由 DWM 合成透明，性能更好。方案文档把“性能更好”作为选择理由，实测对比结论落在默认值上。

`AllowsTransparency` 必须在窗口显示前设置，所以在构造函数里按 `transparency` 决定。

## Z 序维护

owner 模式下，宿主窗口应一直在“最底部”（仅高于桌面层）。会把它顶上去的场景：Win+D、点击桌面使 Progman/WorkerW 成为前台、Explorer 重排窗口。两层防线：

1. **WinEventHook**：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)`；回调 `OnForegroundChanged` 里用 `DesktopShell.IsDesktopWindow(hwnd)`（是 Progman，或含 DefView 的 WorkerW）判断，是则立刻 `ReassertBottom`（`SetWindowPos(HWND_BOTTOM, NOACTIVATE|NOMOVE|NOSIZE)`），并启动 150ms 的 `_zTimer` 延迟复查一次（Explorer 可能在事件之后才调整 Z 序）。委托保存在字段 `_winEventProc`，防止被 GC 回收导致回调崩溃。
2. **`WM_WINDOWPOSCHANGING`**：宿主窗口的 `WndProc` 里，若有人要改 Z 序（没有 `SWP_NOZORDER`）且 `hwndInsertAfter != HWND_BOTTOM`，直接把结构体里的 `hwndInsertAfter` 改写为 `HWND_BOTTOM`——只改结构体，不再调 `SetWindowPos`，所以没有递归。日志里每个宿主最多记 20 次（`_zFixLogCount`）。

child 模式没有这两层（不需要 `HWND_BOTTOM`）。

## 键盘焦点

宿主窗口是 `NOACTIVATE`，点击不会自动激活。`DesktopSurface.BringToForeground()` 在鼠标按下/右键时显式 `SetForegroundWindow(hwnd)` + `Focus()`。键盘事件在宿主窗口的 `PreviewKeyDown` 里转发给 `DesktopSurface.HandleKey`。`Activated/Deactivated` 驱动选中项在失焦时变灰（`IconItemControl.WindowActive`）。

菜单期间（v1 回退路径）宿主窗口的 `WndProc` 先把 `WM_INITMENUPOPUP/DRAWITEM/MEASUREITEM/MENUCHAR/MENUSELECT` 交给 `ShellContextMenu.TryHandleMessage` 转发给 `IContextMenu2/3`。代理路径下这些消息在 Explorer 里的代理窗口过程里处理。

弹菜单前代理需要前台权限，所以 DeskNook 在发请求前 `AllowSetForegroundWindow(代理所在 PID)`；代理在回传事件前再 `AllowSetForegroundWindow(DeskNook PID)`，以便原位重命名框/对话框能抢到焦点。

## DPI 与多显示器

- `app.manifest` 声明 `PerMonitorV2`；`DesktopShell.GetMonitors()` 用 `EnumDisplayMonitors` + `GetMonitorInfo` + `GetDpiForMonitor` 得到每个显示器的 `Bounds`（物理像素，虚拟屏幕坐标）、`Work`（工作区）、`Scale`（DPI/96），主显示器排第一（新项优先分配到主屏）。
- 布局里的坐标一律是**逻辑坐标**：图标是 `(显示器设备名, 列, 行)`，格子矩形是相对工作区左上角的 DIP。`MonitorGrid` 把物理工作区除以 `Scale` 得到 DIP 工作区，再按格子尺寸 `CellW/CellH` 得到列数/行数。
- `DesktopSurface.Origin`：工作区原点相对宿主窗口左上角的 DIP 偏移（工作区不含任务栏）。
- 宿主窗口覆盖**整个显示器**（`Bounds`，含任务栏区域），图标/格子只在工作区内排布。
- 拖放/菜单坐标是屏幕物理像素，`DesktopSurface.ToLocal` 用 `(screen - Bounds.Left) / Scale` 换算为 DIP。
- 显示器设备名（`\\.\DISPLAY1`）是布局里的主键；显示器缺失时项临时落到主屏但不改写原值，详见 [boxes.md](boxes.md)。
- 新增/移除显示器、改分辨率/缩放：`WM_DISPLAYCHANGE` → 去抖 600ms → 重建全部宿主窗口（[architecture.md](architecture.md)）。

## Win10 与 Win11 24H2

- Win10（开发机 19045）：DefView 通常挂在某个 `WorkerW` 下，走 `FindDesktop` 的 `EnumWindows` 分支。
- Win11 24H2：DefView 可能一直在 `Progman` 下，走第一个分支；owner 取 `DefViewParent`，对两种情形都成立，这也是不再用 `0x052C` 和 `WorkerW` 的原因之一。
- **Win11 24H2 的实机验证没有做过**（仅在 Win10 上实测），改动桌面层代码后建议在 24H2 上手测：Win+D、重启 Explorer、多显示器。见 [testing.md](testing.md) 的手测清单。

## 修改本层时的注意事项

- 任何新增的“向 Explorer 窗口发消息/改窗口样式”的行为都要评估 Explorer 崩溃/重启后的恢复；DeskNook 现在对 Explorer 窗口只做三件事：`FindWindow*`、`ShowWindow(ListView)`、把它设为 owner。
- 重建宿主窗口（`RebuildHosts`）必须先 `CloseHosts()`；窗口可能已被系统销毁，关闭时异常要吞掉。
- 宿主窗口的 `OnClosed` 必须 `UnhookWinEvent`，否则重建后旧回调还会触发。
