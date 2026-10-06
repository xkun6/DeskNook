# 测试

两层：

1. **单元测试**（xUnit，`tests/DeskNook.Tests`）：覆盖 `Services/` 与 `Model/` 的纯逻辑，不需要桌面，CI 里跑。
2. **截图自动化**（PowerShell，`tools/test`）：在**真实桌面**上启动 DeskNook，用 `SendInput` 模拟鼠标键盘、读日志/`layout.json`、截图断言。CI 不跑，只能本机跑。

## 单元测试

```powershell
dotnet test DeskNook.sln -c Release
```

`DeskNook.Tests` 引用主项目（`InternalsVisibleTo`），所以构建它会触发 C++ 扩展的编译（需要 VS C++ 工具集）。测试方法名用中文写成“故事”，改逻辑前先读对应测试，它们就是行为规格。

| 文件 | 覆盖 |
|---|---|
| `AppPathsTests.cs` | 数据根规则：Program Files / Program Files (x86) 下用 `%AppData%\DeskNook`、普通目录用 `data`、前缀相似不误判、路径为空不误判、各文件位置 |
| `CliArgsTests.cs` | `--autostart=on|off` 解析（大小写、非法值、多个取最后一个、与其他参数混用） |
| `LogicTests.cs` | `GridLayout`（FirstEmpty/NearestEmpty/FromPixels/FromScreenPixel 含 150% 缩放与负坐标、`MonitorGrid` 行列数）；`LayoutReconciler`（新项放首空格、保留合法位置、7 天保留、改名保位、显示器消失回退主屏、冲突就近安置）；`ItemDiff`（增删改、改名提示命中/不成立）；`LayoutStore`（往返、损坏回退并备份、null 修正、无残留 tmp） |
| `BoxTests.cs` | 格子布局：key 唯一归属、避开格子覆盖、消失项 7 天、改名同步、显示器缺失暂落主屏、有效矩形夹取/折叠、解散（普通/映射/避开其他格子）、`MoveToBox` 插入与调序语义、排序（名称自然排序/大小/类型/时间/虚拟项在前）、内容排布与插入位置、**移动/缩放逐像素与吸附阈值**、新建选位；`BoxPersistenceTests`：旧 `layout.json` 兼容、完整往返 |
| `OrganizerTests.cs` | `AutoOrganizer`：各类扩展名归类、文件夹/虚拟项、自定义规则顺序、整理建格子（靠右从上到下再向左不重叠）、复用同名格子、映射格子/映射项不参与、避开已有格子与不整理的自由图标、空计划、**撤销**（恢复布局、整理后又改动只回滚本次、已消失项忽略、记录读写与损坏）；`SettingsStoreTests`：默认规则、往返无残留、损坏备份、规则清理 |
| `MenuProtocolTests.cs` | `MenuExtensions.BuildWire`：适用条件过滤并分配唯一 Id、动态标题与状态、子菜单只保留适用子项、分隔线与 `FallbackOnly`、资源管理器来源只出现 `InExplorer` 项、位置名映射；协议 JSON 往返、中文不转义、**字段名与 C++ 侧约定一致**（查询/应答/代理请求）、解析容错、非法 JSON 返回 null；`MenuContext.IsFileSystemPath`、`InSameFolder` |
| `Stage4Tests.cs` | `AutoStart`（注入专用注册表键，写入带引号路径、禁用幂等、状态以注册表为准、取不到 exe 路径失败）；设置默认值/旧版 `settings.json` 兼容/往返、透明度夹取、图标大小模式、日志保留天数规整与 `Log.ExpiredFiles` 过期判定；`layout.json` 隐藏状态兼容；`ProxyVersionTests`（`ExplorerMenuProxy.IsOutdated` 判定） |

不在单测里、必须靠自动化/手测的：Win32/COM 调用、宿主窗口、`BoxControl` 渲染与鼠标、菜单代理、安装包。

注意：`AutoStartTests` 会写 `HKCU\Software\DeskNookTests`，测完删除；不要让测试碰真实的 `Run\DeskNook`。

## 截图自动化框架（`tools/test`）

PowerShell 5.1；运行方式：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/test-menu-v2.ps1 [-Only 名称片段]
```

前置：先 `dotnet build DeskNook.sln -c Release`，被测的是 `src\DeskNook\bin\Release\net9.0-windows\DeskNook.exe`；它的数据根就是该目录下的 `data\`（`lib.ps1` 的 `$Script:DataDir`）。**脚本假设**：主显示器设备名 `\\.\DISPLAY1`、100% 缩放、工作区原点 (0,0)（任务栏在底部）、分辨率足够大（空白点固定取 (1500,700)，部分截图区域到 x=2500/y=1300，开发机是 2K 以上屏）；格子 75×100 DIP、图标 48。换环境要改 `common.ps1`/`box-lib.ps1` 里的常量。

输出（截图、文本、日志副本）写到 `tools/test/out/`（已在 `.gitignore`），文件名前缀按脚本区分（`m-`、`h-`、`s-`、`b-`…）。

### 公共库提供的函数

**`lib.ps1`**（基础）

| 分类 | 函数 |
|---|---|
| 截图/图像 | `Get-VirtualScreen`、`Save-Screen -Name -Rect`、`Crop-Image`、`Get-ImageDiffRatio -PathA -PathB -Rect -Tolerance`（不同像素比例） |
| 输入 | `Move-Mouse`、`Click-Mouse -Button Left/Right -Ctrl -Shift`、`DoubleClick-Mouse`、`Drag-Mouse`、`Press-Key`（别名 `Send-Keys`，支持 Ctrl/Shift/Alt）、`Type-Text`（Unicode）、`Send-WinD` |
| 窗口 | `Minimize-All`/`Restore-All`（`Shell.Application`）、`Get-ForegroundTitle`、`Stop-ProcessByName` |
| 日志 | `Get-LogMark`（当前字节偏移）、`Get-LogSince`、`Wait-Log -Pattern -Since -TimeoutSec`（按偏移读，日志很大也快） |
| 生命周期 | `Start-DeskNook [-AppArgs]`（等日志“已隐藏系统桌面图标”）、`Stop-DeskNook`（`--exit`，超时则强杀并 `ShowSystemIcons` 兜底恢复系统图标） |
| 测试文件 | `New-TestFile`（**文件名必须 `xk-test-` 开头且无路径分隔符**，否则抛错）、`Remove-TestFiles`（只删桌面上 `xk-test-*`） |
| 断言/报告 | `Assert-True`、`Invoke-Test -Name -Script`（失败自动截图 `fail-*`）、`Write-Result`、`Show-Summary` |

**`common.ps1`**：`Get-IconCenter <名字>`（读 `layout.json` 算图标屏幕坐标）、`Get-BlankPoint`。

**`box-lib.ps1`**（格子）：`Backup-Layout`/`Restore-Layout`（备份仅在尚无备份时做，防中途失败后二次覆盖）、`Read-Layout`、`Wait-Saved`（500ms 保存去抖）、`Get-Boxes/Get-BoxByName/Get-BoxScreenRect/Get-BoxIconCenter`、`Wait-Layout <条件>`（轮询 `layout.json`）、`Scroll-Wheel`、`Drag-Mouse-Shot`（拖动中截图）、`Start-BoxTest`/`Finish-BoxTest`（统一的准备与还原：备份布局、清空布局、建测试文件、`Minimize-All`、启动；结束时 `--exit`、清理、还原）、`Clear-TestArtifacts`。**菜单定位**见下节。

**`m-lib.ps1`**（菜单 v2）：`Backup-AppData`/`Restore-AppData`（`layout/settings/organize-undo` 三个 json）、`Get-FileMark/Get-FileSince/Wait-FileLog`（读 `shellext.log` 等任意日志）、`Get-ExplorerPids`/`Restart-Explorer`、`Capture-Menu`（右键→等菜单→截图→记录文本→Esc）、`Normalize-MenuTexts`（剔除我们的项后与原生菜单比对）、`New-SideBySide`（左右拼图对比 C/D）、`Clear-NewFolders`、`DnTest.Ext.SystemListViewVisible()`（断言系统 ListView 保持隐藏）。

**`h-lib.ps1`**（阶段 4）：`Backup-HState`/`Restore-HState`（`data\*.json` + `Run\DeskNook` 注册表值，备份在 `%TEMP%`，上次中断遗留的备份不覆盖）、`Get-RunValue`、`Read-Json`、`Write-Settings`、UI Automation 辅助（`Get-UiaWindow/Get-UiaById/Click-Uia`、`Get-WindowRect`、`Close-SettingsWindow`）、托盘辅助（`Find-TrayButton`、`Open-TrayOverflow`、`Locate-TrayIcon`、`Open-TrayMenu`、`Click-TrayMenuItem`）。

### `Click-MenuPath`：路径式菜单定位

`DnTest.MenuApi.Items()`（`box-lib.ps1`）枚举所有可见的菜单窗口（类名 `#32768`），用 `MN_GETHMENU`（`0x1E1`）取 HMENU，再读每一项的文本、矩形、状态——**对 `explorer.exe` 弹出的菜单同样有效**（菜单窗口是顶层窗口，HMENU 可跨进程读取到项信息），所以不依赖截图识别，也不区分菜单是谁弹的。

- `Find-MenuItem <文本> [-Exact]`：去掉 `&`/`(&X)` 后先完全匹配，再按通配符包含匹配（`新建映射格子` 可匹配 `新建映射格子…`），轮询到超时，返回中心点与状态位。
- `Click-MenuPath @('桌面整理','新建格子')` 或 `'桌面整理 ▸ 新建格子'`：逐级点击，父级点击即展开子菜单；找不到会按两次 Esc 关菜单并抛错、附上当前所有菜单文本。
- `Click-ContextMenu X Y -Path …`：右键某点 + 路径点击，统一入口。

### 各脚本覆盖内容

| 脚本 | 覆盖 |
|---|---|
| `test-menu-v2.ps1` | **菜单 v2 主回归**：C（原生）vs D（我们的）图标菜单对比、代理已加载、整理至新格子/新文件夹（多选 + 原位重命名）、作为桌面格子显示、背景菜单“桌面整理”子菜单、一键整理与撤销、资源管理器窗口里的自定义项、菜单“重命名”、查看/显示桌面图标（系统 ListView 保持隐藏）、格子内与映射格子菜单、连续 30 次弹出关闭、**Explorer 重启后代理自动重载**、`--no-proxy` 回退。全程断言 Explorer PID 不变（崩溃检测） |
| `test-stage4.ps1` | 阶段 4：版本检测（旧版组件 → 日志警告 + 托盘气泡；重启 Explorer 后一致）、双击隐藏/恢复及重启保持、关闭双击开关后无效、托盘图标与菜单（一键整理/撤销/新建格子/隐藏/自启/退出）、设置常规页（透明度预览、图标大小、自启）、菜单图标、“打开所在位置”仅格子内、Explorer 重启后托盘重建、崩溃标记 `running.flag`、托盘退出后系统图标恢复 |
| `test-boxes-a.ps1` | 格子 A01~A10：新建、拖入图标（只改布局）、移动吸附与辅助线、缩放、双击标题改名、折叠与悬停展开、锁定、格子间拖动与调序、格子拖到自由区、滚动 |
| `test-boxes-b.ps1` | B01~B11：移动到格子/移出格子、格子内原生菜单、整理至新格子、映射格子（选目录、同步、原生菜单、背景菜单、拖入拖出）、解散普通/映射格子 |
| `test-boxes-c.ps1` | C01~C04：布局持久化（重启后截图+JSON 对比）、Explorer 重启后格子还原、折叠/锁定/排序状态持久化 |
| `test-box-smooth.ps1` | S01~S07：逐像素移动/缩放、对侧边不动、5px 吸附/12px 不吸附、非整格宽度排布、60 步拖动期间不重建桌面 |
| `test-organize.ps1` | 一键整理分类/靠右不重叠/虚拟项不动、撤销、重启后撤销、设置窗口改规则与恢复默认 |
| `test-box-smoke.ps1` | 冒烟：新建格子 |
| `test-interact.ps1` | 选中/Ctrl 加选/框选/F2 重命名（断言磁盘文件名变）/双击打开 |
| `test-contextmenu.ps1`、`test-menus-extra.ps1`、`test-paste-undo.ps1` | 阶段 1 的菜单截图对照（原生 vs 本程序）、子菜单/多选/Shift 菜单、粘贴与撤消 |
| `test-sync.ps1` | 外部创建/改名/删除文件 → 日志里“新增/改名/删除”并统计同步耗时 |
| `test-lnk.ps1` | 快捷方式图标箭头对比截图 |
| `test-zorder-explorer.ps1` | Z 序：cmd 窗口不被盖住、Win+D、Explorer 重启后重挂 |
| `crop.ps1` | 裁剪截图的小工具 |

较早的脚本（`test-box-smoke`、`test-contextmenu`、`test-interact`、`test-lnk`、`test-menus-extra`、`test-paste-undo`、`test-sync`、`test-zorder-explorer`）以“跑出截图/日志供人看”为主，没有统一的 `Invoke-Test` 断言汇总；带 `Invoke-Test` 的脚本才会 `Show-Summary`。

### 测试隔离规则（必须遵守）

1. **只创建/删除 `xk-test-` 前缀的桌面文件**（`New-TestFile` 强制；`Remove-TestFiles` 只删这个前缀）。绝不触碰用户桌面上其他文件。映射目录测试用 `%TEMP%\xk-test-map` 等临时目录。
2. **备份并恢复用户数据**：开始前备份 `layout.json`/`settings.json`/`organize-undo.json`（和 `Run\DeskNook`），结束（含失败）在 `finally` 里还原。一键整理会作用于桌面上的**全部**自由图标（只改布局不动文件），所以必须还原。备份文件放 `%TEMP%`，上次中断遗留的备份**不覆盖**，下一轮先还原再继续。
3. **运行前后让桌面干净**：`Minimize-All` 避免窗口遮挡点击；`finally` 里 `Restore-All`、`Stop-DeskNook`（先 `--exit`，万不得已才强杀并 `ShowSystemIcons`）、关掉测试开的 notepad/cmd。
4. **有破坏性的用例**：重启 Explorer（`Restart-Explorer`）、`taskkill`、Win+D、改开机自启注册表值——都会打扰正在使用这台机器的人，只在专用/可打扰的会话里跑；`Start-DeskNook`/`Stop-DeskNook` 作用于被测 exe 的进程名 `DeskNook`，会一并关掉用户自己正在运行的实例。
5. 旧脚本里仍有只用 `common.ps1`、不做布局备份的（如 `test-interact.ps1`），它们操作的是被测 exe 目录下的 `data\`，不是安装版的数据根；但如果你平时就是从 `bin\Release` 运行 DeskNook，数据会被改动，运行前自行备份。

## 只能手测的项目（手测清单）

自动化覆盖不了或没有环境的，发版前手测：

- **Win11（尤其 24H2）**：桌面窗口结构、宿主窗口 Z 序、Win+D、重启 Explorer、右键菜单（`desktop-layer.md` 里明确标注“未实测”）。
- **多显示器与缩放**：拔插显示器、不同 DPI（100%/150%）混合、副屏在左/上（负坐标）、显示器缺失后格子暂落主屏、恢复后回位。
- **真实第三方扩展**：夸克、百度网盘、NVIDIA、7-Zip/Bandizip、Git 等在桌面图标/背景菜单里的项、图标、深色主题与原生一致（自动化只比较菜单文本和截图）。
- **安装包**：全新安装（UAC、改目录、选项页、完成页启动）、升级（保留自启、旧实例被退出、不降级）、修复、静默安装参数、卸载（注册与自启清理、数据保留）、便携版 `--unregister`。
- **开机自启实际登录行为**、注销/关机时系统图标恢复、睡眠唤醒、锁屏后恢复。
- **旧名迁移清理**：从 XkDesk/DeskNext 遗留的注册项升级到 DeskNook。
- **本地化**：`OnNativeInvoked` 与代理里的标题匹配只认中文/英文（“排序方式/Sort by”“查看/View”“显示桌面图标/Show desktop icons”等），其他语言系统上排序/查看同步和“显示桌面图标”拦截不会生效。
- **全屏应用/游戏**、触摸与高 DPI 触控板下的拖动、右键拖放菜单（Shell 的 `IDropTarget` 自己弹）。
- **性能**：4K 屏上 `dwm` 与 `layered` 的 CPU 对比（阶段 0 的实测依据）；大量图标（数百项）下的重建耗时。
