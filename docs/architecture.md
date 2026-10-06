# 总体架构

## 一句话

DeskNook 把系统桌面的 `SysListView32` 用 `ShowWindow(SW_HIDE)` 藏起来，在每个显示器上铺一个透明 WPF 宿主窗口自己画图标和格子；右键菜单不自己画，而是让 `explorer.exe` 进程里的“菜单代理”弹出真正的原生菜单，自定义项通过 Shell 扩展注入。

## 进程与线程模型

| 进程 | 线程 | 做什么 | 代码 |
|---|---|---|---|
| `DeskNook.exe` | UI 线程（STA，WPF Dispatcher） | 全部业务：桌面项集合、布局、选择、菜单评估、宿主窗口渲染、托盘、SHChangeNotify 消息、剪贴板监听 | `src/DeskNook/App.xaml.cs:App`、`src/DeskNook/Desktop/DesktopController.cs:DesktopController` |
| `DeskNook.exe` | `ShellIconWorker0/1`（后台 STA，各一条） | `IShellItemImageFactory.GetImage` 取图标，结果 `Dispatcher.BeginInvoke` 回 UI 线程 | `src/DeskNook/Desktop/ShellIconCache.cs:ShellIconCache` |
| `DeskNook.exe` | `DeskNookMenuProxyLoader`（后台 STA，按需一次性） | 确保代理已加载进 Explorer（跨进程 COM、SetWindowsHookEx、轮询等待），不能占用 UI 线程 | `src/DeskNook/Desktop/ExplorerMenuProxy.cs:EnsureLoadedAsync` |
| `DeskNook.exe` | 线程池任务（4 个监听循环 + 每连接一个处理任务） | 命名管道服务端 `\\.\pipe\DeskNook.Menu`；需要 UI 线程的工作用 `Dispatcher.InvokeAsync/BeginInvoke` | `src/DeskNook/Desktop/MenuPipeServer.cs:MenuPipeServer` |
| `DeskNook.exe` | 线程池（`RegisterWaitForSingleObject`） | 等 `--exit` 置位的 `Global\` 命名事件，再 `Dispatcher.BeginInvoke(ExitApp)` | `src/DeskNook/Services/ExitSignal.cs:ExitSignal` |
| `DeskNook.exe` | 线程池（`Task.Run`） | 向代理发 `WM_COPYDATA`（**必须**离开 UI 线程，见 [menu.md#死锁规避](menu.md#死锁规避)） | `ExplorerMenuProxy.TryShow` |
| `explorer.exe`（拥有桌面的那个） | Explorer 自己的桌面（DefView）线程 | 代理窗口 `DeskNook.MenuProxy` 建在这条线程上；`TrackPopupMenuEx`、`IContextMenu` 全在这里跑 | `src/DeskNookShellExt/proxy.cpp` |
| `explorer.exe` | 备用：专用 STA 线程 | 取不到桌面线程时的退路（取不到真实 DefView，背景菜单改走 `CreateViewObject`） | `proxy.cpp:ProxyMain` |
| 任意进程 | 调用方线程 | Shell 扩展 `DnExt`（IShellExtInit + IContextMenu）随右键菜单被加载，经管道向 DeskNook 查询/回调 | `src/DeskNookShellExt/ext.cpp` |

hidden window（`HwndSource`）清单，都在 UI 线程：`DeskNookMessageWindow`（`ShellMessageWindow`，托盘/广播/退出消息）、`DeskNookItemNotify`（每个 `DesktopItemSource` 一个，接收 SHChangeNotify）、`DeskNookClipboard`（`ClipboardWatcher`）。

## 架构图

```
 +------------------------------- DeskNook.exe (WPF, .NET 9, x64) -------------------------------+
 |                                                                                                |
 |  UI 线程                                                                                       |
 |  +-----------+    +-------------------+   +--------------------+   +----------------------+  |
 |  | App       |--->| DesktopController |-->| LayoutStore        |-->| data\layout.json     |  |
 |  | 启动/退出 |    | 桌面项/布局/选择  |   | SettingsStore      |   | data\settings.json   |  |
 |  | 重挂/重建 |    | 菜单/整理/拖放    |   | OrganizeUndoStore  |   | data\organize-undo   |  |
 |  +-----+-----+    +----+----------+---+   +--------------------+   +----------------------+  |
 |        |               |          |                                                            |
 |        |     +---------v--+   +---v--------------+   +------------------+                      |
 |        |     | DesktopItem|   | DesktopSurface   |   | MenuExtensions   |  <- 魔改入口         |
 |        |     | Source     |   | BoxControl       |   | (自定义项/动词表)|                      |
 |        |     | (SHChange- |   | IconItemControl  |   +----+-------------+                      |
 |        |     |  Notify)   |   | (每显示器一个)   |        |                                    |
 |        |     +------------+   +------+-----------+   +----v-------------+  +-----------------+ |
 |        |                             |               | MenuPipeServer   |  | ExplorerMenu-   | |
 |  +-----v------------+        +-------v--------+      | \\.\pipe\        |  | Proxy (发请求,  | |
 |  | ShellMessageWin  |        | DesktopHost    |      | DeskNook.Menu    |  |  装载代理)      | |
 |  | TaskbarCreated   |        | Window (owner  |      +----^-------------+  +--------+--------+ |
 |  | DISPLAYCHANGE    |        | +dwm透明, 底层) |           |                         |          |
 |  +------------------+        +-------+--------+           |                         |          |
 |                                      | ShowWindow(HIDE)   |                         |          |
 +--------------------------------------|--------------------|-------------------------|----------+
                                        v                    |  JSON 行(管道)          | WM_COPYDATA
                        +-------------------------+          |                         | (后台线程发送)
                        | explorer.exe            |          |                         v
                        |  Progman/WorkerW        |          |      +---------------------------------+
                        |   SHELLDLL_DefView      |          |      | explorer.exe 桌面线程           |
                        |    SysListView32(已隐藏)|          |      |  DeskNook.MenuProxy 窗口        |
                        |                         |          |      |  GetUIObjectOf/CreateViewObject |
                        +-------------------------+          |      |  TrackPopupMenuEx / InvokeCmd   |
                                                             |      +----------------+----------------+
   任意进程 (资源管理器窗口等)                               |                       | 菜单创建时由 Shell
   +--------------------------------------+                  |                       v 实例化已注册的 handler
   | DnExt: IShellExtInit + IContextMenu  |------------------+---------  DeskNookShellExt.dll (DnExt)
   | QueryContextMenu -> 管道 query(200ms)|
   +--------------------------------------+
```

要点：DeskNookShellExt.dll 既是 Shell 扩展（任何弹原生菜单的进程都会加载它），又在 `explorer.exe` 里充当菜单代理；两个角色共用同一个管道和同一份 JSON 协议。

## 模块分层与目录结构

```
src/DeskNook/                      WPF 主程序（net9.0-windows，x64，PerMonitorV2）
  App.xaml / App.xaml.cs           入口：启动顺序、CLI 参数、单实例、崩溃兜底、重挂/显示器重建、统一退出
  app.manifest                     asInvoker；PerMonitorV2；Win10/11
  Assets/*.ico                     应用图标 app.ico 与菜单线条图标（嵌入资源，运行时释放到 data\icons）
  Desktop/                         与 Shell/桌面/Explorer 打交道的一层（几乎都必须在 UI 线程）
    DesktopShell.cs                定位 Progman/WorkerW/DefView/ListView；隐藏/恢复系统图标；枚举显示器
    DesktopHostWindow.xaml(.cs)    每显示器一个宿主窗口：owner/child 挂载、dwm/layered 透明、Z 序维护
    DesktopController.cs           中枢：项集合 + 布局 + 选择 + 剪切状态 + 全部操作（打开/删除/粘贴/重命名/格子/整理/拖出）
    DesktopItemSource.cs           枚举桌面根或任意目录，SHChangeNotifyRegister 监听，300ms 去抖 diff
    DesktopDropTarget.cs           宿主窗口的 OLE IDropTarget，转发给 Shell 的 IDropTarget
    ShellIconCache.cs              图标后台加载与缓存、快捷方式箭头叠加
    ShellActions.cs                重命名（SetNameOf）、拖出（SHDoDragDrop）
    SystemDesktopView.cs           通过 IShellWindows 读取系统 DefView 的图标大小/间距/位置
    ShellMessageWindow.cs          隐藏顶层窗口：TaskbarCreated、WM_DISPLAYCHANGE、第二实例/退出消息
    TrayIcon.cs                    自写 Shell_NotifyIcon 托盘与原生菜单
    ClipboardWatcher.cs            剪贴板监听，识别“剪切”（Preferred DropEffect）
    MenuExtensions.cs              **魔改入口**：自定义菜单项 Items + 动词拦截 VerbInterceptors + MenuContext
    ExplorerMenuProxy.cs           DeskNook 端的代理管理：加载（shellview/hook）、版本检测、发请求、收事件
    MenuPipeServer.cs              命名管道服务端：query/invoke/事件
    ShellExtRegistrar.cs           HKCU 注册/卸载 Shell 扩展、按哈希复制 DLL、清理旧版注册
    ShellContextMenu.cs            v1 进程内菜单（现为回退路径 + InvokeVerb/InvokeDefault 的实现）
    BoxMenu.cs                     普通格子空白处/标题栏的纯自定义菜单（不涉及 Shell 菜单）
  Views/                           WPF 视图
    DesktopSurface.cs              每显示器的画布：渲染、命中、框选、拖动发起、键盘、原位重命名
    BoxControl.cs                  格子外观与交互（标题栏、移动/缩放、折叠、滚动条、标题编辑）
    IconItemControl.xaml(.cs)      单个图标（悬停/选中/失焦/剪切半透明）
    SettingsWindow.xaml(.cs)       设置窗口（常规页 + 整理规则页）
  Model/                           纯数据：DesktopItem、LayoutState/BoxState/IconSlot、AppSettings/OrganizeRule、MonitorGrid
  Services/                        纯逻辑与持久化（单测主要覆盖这一层）
    AppPaths.cs                    数据根的唯一来源
    LayoutStore.cs / SettingsStore.cs / AutoOrganizer.cs(含 OrganizeUndoStore)   JSON 读写（临时文件 + 替换）
    LayoutReconciler.cs / BoxOps.cs / BoxGeometry.cs / GridLayout.cs / ItemDiff.cs / ItemSorter.cs   布局纯逻辑
    MenuProtocol.cs                管道/WM_COPYDATA 的 DTO 与 JSON 编解码（C++ 侧必须与它一致）
    MenuIcons.cs                   释放菜单图标 ICO
    AutoStart.cs / CliArgs.cs / ExitSignal.cs / Log.cs
  Native/                          P/Invoke 与 COM 声明（Win32*.cs 是同一个 partial 类）

src/DeskNookShellExt/              C++ x64 DLL（静态 CRT，/EHa，无第三方依赖）
  desknook.h                       共享声明、CLSID、管道名/窗口类名常量
  main.cpp                         DllMain / DllGetClassObject / DllCanUnloadNow / DnHookProc
  ext.cpp                          DnExt（IShellExtInit + IContextMenu）与类工厂
  proxy.cpp                        菜单代理窗口、取 IContextMenu、弹出、拦截、事件回传
  util.cpp                         日志、JSON、管道客户端、WIC 菜单位图
  DeskNookShellExt.def             导出 DllGetClassObject/DllCanUnloadNow（PRIVATE）与 DnHookProc

tests/DeskNook.Tests/              xUnit，见 testing.md
installer/                         WiX v5 MSI，见 build-release.md
tools/                             publish.ps1、build-installer.ps1、gen-icons.ps1、test/（截图自动化）
.github/workflows/release.yml      CI
```

分层依赖方向：`Views → Desktop → Services/Model`，`Native` 被 `Desktop`/`Views` 使用；`Services` 与 `Model` 不依赖 WPF 视图，所以可单测。`DesktopController` 通过事件（`ItemsChanged`、`SelectionChanged`、`IconInvalidated`…）驱动 `DesktopSurface` 重建，视图不直接改布局。

代码里没有第三方 NuGet 依赖（没有 Vanara、CommunityToolkit、H.NotifyIcon）：所有 Shell/COM/托盘都是自写 P/Invoke。

## 启动流程（`App.OnStartup` 顺序）

1. `Win32.SetPreferredAppMode(1)` + `FlushMenuThemes()`：菜单深色模式 AllowDark，必须在创建任何窗口前。
2. `base.OnStartup`。`App.xaml` 为 `ShutdownMode="OnExplicitShutdown"`，退出只走 `Shutdown()`。
3. 一次性命令（不启动界面，不受单实例影响）：
   - `--autostart=on|off`：`AutoStart.Default.SetEnabled` 后 `Shutdown()`。
   - `--exit`：先 `ExitSignal.TrySignal()`（跨会话命名事件），没有事件再回退到给 `DeskNookMessageWindow` 发注册消息 `DeskNook.ExitRequest`；然后 `ExitSignal.WaitOthersExit(8000)` 等实例退净。
   - `--unregister`：`ShellExtRegistrar.Unregister()`（含删开机自启项）。
4. 单实例：`Mutex(@"Local\DeskNook.SingleInstance")`。拿不到 → `NotifyFirstInstance()`（`AllowSetForegroundWindow` + 发 `DeskNook.ShowSettings` 消息，让首实例打开设置窗口）并退出。
5. `CheckLastRunAndMarkRunning()`：`running.flag` 残留则记日志“上次异常退出”，随后写入当前 PID。
6. `ParseArgs`：`--attach=`、`--transparency=`、`--no-proxy`、`--simulate-outdated-proxy`，未知参数只记日志。
7. 注册崩溃兜底（`AppDomain.UnhandledException`、`DispatcherUnhandledException`、`TaskScheduler.UnobservedTaskException`、`ProcessExit`），都调 `DesktopShell.RestoreIcons()`。Dispatcher 异常不标记 `Handled`：恢复图标后让进程崩溃，避免半接管状态。
8. `ExitSignal.Listen`（创建 `Global\DeskNook.Exit.<用户SID>` 事件）。
9. `ShellMessageWindow` 并挂接 `TaskbarCreated`/`DisplayChanged`/`ExitRequested`/`SettingsRequested`。
10. `OleInitialize`；`new DesktopController(Dispatcher)` + `Initialize(系统 ListView 句柄)`：加载 layout/settings/undo，枚举桌面项，读系统图标大小与间距，首次导入系统图标位置，对账。
11. `MenuIcons.EnsureExtracted()`；`TrayIcon` 创建并 `Recreate()`。
12. 菜单 v2（`--no-proxy` 时跳过）：`ShellExtRegistrar.EnsureRegistered()` → 创建 `ExplorerMenuProxy` → `MenuPipeServer.Start()` → 注册成功则 `EnsureLoadedAsync("启动")` → 低优先级 `WarmUp()`（预热 JIT，避免首次查询超过扩展的 200ms 超时）。
13. `RebuildHosts("启动")`：关旧宿主 → `FindDesktop` → `HideIcons` → `UpdateMonitors` → 每个显示器 new `DesktopHostWindow`。失败（桌面窗口不在）→ `StartReattach`。

## 退出与崩溃兜底

核心事实：系统图标的隐藏只是 `ShowWindow(SW_HIDE)`，**不写注册表**，所以 Explorer 重启后图标天然恢复；DeskNook 只需保证自己退出时恢复。

`DesktopShell.RestoreIcons()` 幂等（`_hidden` 标志 + 重新查找当前 ListView，找不到再用上次句柄），以下路径都会调用它：

| 路径 | 位置 |
|---|---|
| 正常退出（托盘/菜单/`--exit`/`ExitSignal`） | `App.ExitApp`（先恢复图标，再关宿主、释放托盘/管道/控制器，最后 `Shutdown()`） |
| `Application.Exit` | `App.OnExit`（只有持有互斥量的首实例才删 `running.flag`） |
| 注销/关机 | `App.OnSessionEnding`（同时 `ClearRunningFlag`） |
| 托管未处理异常 / Dispatcher 异常 / 未观察 Task 异常 | `OnStartup` 里注册的三个处理器 |
| `ProcessExit` | `AppDomain.CurrentDomain.ProcessExit` |
| 宿主窗口一个都建不出来 | `App.RebuildHosts` 末尾 |

硬杀（`taskkill /f`、断电式崩溃）不会运行任何兜底：系统图标会一直保持隐藏，直到 **重启 Explorer**，或 **再启动一次 DeskNook 并正常退出**，或手动给 ListView 发 `SW_SHOW`（`tools/test/lib.ps1:ShowSystemIcons` 就是这个兜底）。

`running.flag`：启动时写入 PID，正常退出/注销时删除。它**只用于诊断**——残留说明上次异常退出，下次启动只记一行日志，不触发任何恢复动作。

`ExitSignal`（`Global\DeskNook.Exit.<SID>`）：安装包的自定义动作在 Windows Installer 服务（会话 0）里执行 `DeskNook.exe --exit`，`FindWindow/PostMessage` 够不到用户会话的窗口，命名内核对象则不受会话隔离影响。窗口消息通道仍保留，用于兼容旧版实例。

## Explorer 重启（TaskbarCreated）

`ShellMessageWindow` 收到 `TaskbarCreated` 广播后，`App` 做两件事：

1. `StartReattach("…")`：`CloseHosts()`（窗口可能已被系统销毁，异常全部吞掉），每 500ms `DispatcherTimer` 轮询 `FindDesktop().IsValid`，最长 30s；出现后 `RebuildHosts("Explorer 重启后重挂")`，成功再 `_menuProxy.EnsureLoadedAsync(...)`（新 Explorer 里没有代理了）。超时只记日志。
2. `_tray.Recreate()`：重建托盘图标。

宿主窗口被外部销毁（`OnHostClosed`）**不会**退出程序，等 `TaskbarCreated` 后重挂。

## 显示器变化

`WM_DISPLAYCHANGE` 会连发多条：`App.OnDisplayChanged` 去抖 600ms 后 `RebuildHosts("显示器布局变化")`（重挂流程进行中时跳过）。`RebuildHosts` 里的 `DesktopController.UpdateMonitors()` 重算网格并对账，越界/消失显示器上的项就近落位，详见 [boxes.md](boxes.md) 与 [desktop-layer.md](desktop-layer.md)。

## 日志

- `data\logs\desknook.log`：`Services/Log.cs`，追加写，带线程号，永不抛异常。不做轮转，注意长期增长。
- `data\logs\shellext.log`：C++ 侧 `dn::Log`，超 1MB 清空重写；路径由 DLL 自身位置推导（DLL 必须位于 `<数据根>\shellext\`，目录名不符则不写日志）。
- 排障时先看这两个文件，行内关键字见 [troubleshooting.md](troubleshooting.md)。
