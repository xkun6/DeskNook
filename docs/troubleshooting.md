# 排障与踩过的坑

日志位置（数据根见 [data-and-config.md](data-and-config.md)）：`<数据根>\logs\desknook.log`（主程序，追加写不轮转）、`<数据根>\logs\shellext.log`（C++ 扩展与代理，超 1MB 清空重写）。日志里会出现文件路径，贴给别人前先检查。

## 桌面图标消失

DeskNook 通过 `ShowWindow(SW_HIDE)` 隐藏系统 `SysListView32`，而不是改注册表，所以恢复都很简单。

1. 先让程序正常退出：`DeskNook.exe --exit`（走 `ExitSignal`，退出时会恢复系统图标）。
2. 仍不显示：重启 Explorer（任务管理器 → 重启 `Windows 资源管理器`）。系统 ListView 属于 Explorer，重启后自动恢复。
3. 程序被强杀或崩溃：`<数据根>\running.flag` 会残留；下次启动记日志“上次异常退出”，并先恢复系统图标再接管。
4. 只是 DeskNook 自己的图标/格子不见了：可能是双击空白处进入了“隐藏”状态（`layout.json` 的 `View.IconsHidden`），再双击空白或托盘左键即可。
5. 都不行：`tools/test/lib.ps1` 的 `Stop-DeskNook` 兜底里有 `ShowSystemIcons` 的调用方式可参考。

## 右键菜单里没有“桌面整理”等自定义项

按顺序查：

1. `desknook.log` 是否有“菜单代理”加载成功/失败记录；`shellext.log` 是否有 `DnHookProc`、代理窗口创建的记录。
2. **Explorer 里还钉着旧 DLL**：升级或改了 C++ 后，旧 DLL 仍在 Explorer 进程里，新版本客户端与旧代理不匹配。主程序通过代理窗口标题检测版本，不一致时记日志并弹托盘气泡“菜单组件已更新，重启资源管理器后生效”。解决：重启 Explorer。
3. 运行时带了 `--no-proxy`：不注册、不加载代理，菜单走进程内回退路径（只有部分项）。
4. 注册表被清：检查 `HKCU\Software\Classes\*\shellex\ContextMenuHandlers\DeskNook` 等三个键（见 [data-and-config.md](data-and-config.md)）；重启 DeskNook 会 `EnsureRegistered` 重写。
5. 管道连不上：`\\.\pipe\DeskNook.Menu` 由主程序创建；主程序没运行或被安全软件拦截时，代理只能不加项（原生菜单仍正常）。
6. shellview 方式加载失败（`0x80040155`）是**预期**的：跨进程 `GetItemObject` 走不通，实际靠 `SetWindowsHookEx(WH_GETMESSAGE)` + `DnHookProc` + PIN 加载。详见 [menu.md](menu.md)。

## 升级 DLL 不生效

- 输出目录里的 `DeskNookShellExt.dll` 不被锁，Explorer 加载的是 `<数据根>\shellext\DeskNookShellExt.<hash8>.dll`（按内容哈希命名的副本）；新内容 = 新哈希 = 新文件，但旧文件仍被 Explorer 占用。只有重启 Explorer 才换到新版本。
- 编译时静默用旧 DLL：csproj 的 `BuildShellExt` 找不到 VS 但输出目录已有 DLL 时**不报错、直接用旧的**（[build-release.md](build-release.md)）。C++ 改了没生效先看构建输出有没有编译 `DeskNookShellExt.vcxproj`。
- 清理旧副本：`--unregister` 会删除全部 DLL 副本（仍被占用的删不掉，Explorer 重启后再清）。

## Explorer 崩溃了怎么查

菜单代理代码运行在 `explorer.exe` 里，异常会带崩 Explorer（任务栏消失一次后自动重启）。

1. 看 `shellext.log` 最后几行：代理每个入口都有 `__try`/`catch(...)` 与日志，最后一条记录就是现场。
2. Windows 事件查看器 → Windows 日志 → 应用程序，找 `explorer.exe` 的 Application Error，故障模块是 `DeskNookShellExt.<hash>.dll` 即我们的问题。
3. 遵守 C++ 安全规则（见 AGENTS.md）：不抛异常出边界、不在持锁时回调外部、不持有 COM 指针跨线程、所有入口 `catch(...)`、日志不能阻塞、与主程序通信必须有超时。
4. 复现：`tools/test/test-menu-v2.ps1` 里有连续 30 次弹出关闭与 Explorer PID 不变断言。

## 构建时 exe 被锁

报 `MSB3027/MSB3021` 无法复制 `DeskNook.exe`：运行中的实例锁住了输出文件。先 `src\DeskNook\bin\Release\net9.0-windows\DeskNook.exe --exit`，再构建/测试。测试脚本也是同一个 exe，运行前确认没有用户实例（`Stop-DeskNook` 会关掉同名进程）。

## CI 找不到 Visual Studio / 工具集

现象：`未找到带 C++ 工具集的 Visual Studio`，或 `MSB8020 找不到 v143 生成工具`。

- runner 可能只有 VS2026（18.x）：csproj 先找 `[17.0,18.0)` 再 `-latest [17.0,)`，vcxproj 的工具集用 `$(DefaultPlatformToolset)`（提交 `46ffbdc` 的修复）。不要把 `v143` 写回去。
- vswhere 必须带 `-requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64`。
- 测试步骤会编译 C++，所以 `dotnet test` 也依赖 VS。

## 与原生菜单仍有的差异

- 动词拦截（如“显示桌面图标”“排序方式”“查看”）：代理路径里拦截器的返回值**被忽略**，行为靠代理自己拦截并通知主程序；改拦截逻辑时别指望返回值生效。
- 位置 `BeforeNew` 与 `AfterRefresh` 在 Explorer 路径里都折叠成“底部”（尾部），只有进程内回退路径才区分。
- 只识别中文/英文菜单标题，其他语言的系统上排序/查看同步与显示桌面图标拦截不生效。
- 第三方扩展的深色主题、图标由 Explorer 绘制，一般一致，但没有逐个验证。

## 未验证 / 仅手测

见 [testing.md](testing.md) 末尾手测清单：Win11 24H2、多显示器混合 DPI、第三方菜单扩展、安装包升级与卸载、开机自启实际登录行为。`desktop-layer.md` 里标“推断”的内容是我的分析，不是实测结论。

## 踩过的坑（按领域）

**桌面层**

- 隐藏系统图标要用 `ShowWindow(SW_HIDE)`，不能改注册表（`HideIcons`）：Explorer 重启后窗口重建，用注册表会留下副作用且难恢复。
- 宿主窗口必须 owner 挂载到 `DefViewParent`、`HWND_BOTTOM`，并用 `WinEventHook(EVENT_SYSTEM_FOREGROUND)` + `WM_WINDOWPOSCHANGING` 保持 Z 序；Win+D 与 Explorer 重启后要重挂。
- 透明用 DWM 方案，不用 `0x052C` 消息（见 [desktop-layer.md](desktop-layer.md)）。
- PerMonitorV2 DPI：像素 ↔ DIP 转换按各显示器 `Scale`，不要用全局缩放；副屏在左/上时坐标为负。

**菜单**

- 不能把 Shell 菜单在 DeskNook 进程里弹出来替代：第三方扩展要求在 Explorer 的桌面线程里运行，所以才有代理。
- shellview 跨进程取对象失败 `0x80040155`，只能钩子 + PIN。
- 代理版本不匹配要靠窗口标题检测，别靠 DLL 文件版本（Explorer 里的和磁盘上的可能不同）。
- COPYDATA magic `0x4B584D31` 是历史遗留，C++ 与 C# 必须同步，别改。
- 菜单项 Id 由 `BuildWire` 分配唯一值，别在别处硬编码。

**数据**

- `layout.json` 的 key 唯一归属；任何写操作都要先从原位置摘除。
- 消失项保留 7 天，改名要走 `LayoutReconciler.Rename`，否则丢位置。
- 保存是 500ms 去抖，测试里读文件要 `Wait-Saved`；退出必须 `Flush`。
- 损坏文件会被重命名成 `.bad-<时间戳>`，不是删除，排查数据丢失先看它。

**构建发布**

- MSI 的自定义动作跑在会话 0，找不到用户窗口，所以 `--exit` 用 `Global\DeskNook.Exit.<SID>` 事件。
- `UpgradeCode` 永不改；`SuppressIces` 的 ICE38/43/57 是误报。
- 升级/修复不要重写自启，只有全新安装才 `--autostart=on`。
- CI 的 `--filter "Category!=Desktop"` 目前没有对应特性，等于没过滤；加依赖桌面的测试时要标 `Category=Desktop`。

**测试**

- 测试只创建 `xk-test-` 前缀文件，结束必须还原 `layout/settings/organize-undo` 与 `Run` 值；中断遗留的备份不会被覆盖，下次先还原。
- 脚本硬编码主显示器 `\\.\DISPLAY1`、100% 缩放、坐标 (1500,700)，换环境会点偏。
