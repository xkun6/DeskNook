# 一键整理、设置、双击隐藏、托盘与开机自启

对应代码：`src/DeskNook/Services/AutoOrganizer.cs`（含 `OrganizeUndoStore`）、`src/DeskNook/Model/AppSettings.cs`、`src/DeskNook/Views/SettingsWindow.xaml(.cs)`、`src/DeskNook/Desktop/TrayIcon.cs`、`src/DeskNook/Services/AutoStart.cs`、`src/DeskNook/Services/CliArgs.cs`、`src/DeskNook/Desktop/DesktopController.cs`（`OrganizeAll/UndoOrganize/ApplySettings/SetIconsVisible`）。单测：`tests/DeskNook.Tests/OrganizerTests.cs`、`Stage4Tests.cs`、`CliArgsTests.cs`。

## 一键整理 `AutoOrganizer`

入口：右键空白处“桌面整理 ▸ 一键整理”、托盘“一键整理”（托盘会先 `SetIconsVisible(true)`）→ `DesktopController.OrganizeAll()`：`Plan` → `Apply` → 保存撤销记录 → `AfterBoxChange`（对账/同步/保存/刷新）。**只改布局，不动文件。**

### 规则与分类

默认规则（`AppSettings.DefaultRules`，顺序即优先级，先匹配到的生效）：文件夹（`<dir>`）、快捷方式、程序、文档、图片、视频、音频、压缩包、其他（`*`）。规则 = 分类名 + 扩展名列表（不含点、小写）。
拆分只影响默认规则（新安装，或在设置中「恢复默认」）；已保存在 `settings.json` 里的规则不变，旧的「快捷方式与程序」格子也不会自动拆分。

`AutoOrganizer.Categorize(item, rules)`：

- 虚拟项（`IsVirtual`，此电脑/回收站等）→ `null`，**不参与整理**。
- `<dir>` 约定：`item.IsFolder && item.Extension.Length == 0`（真正的目录；名字带点的目录扩展名非空，会被当成带扩展名的项）。
- `*` 约定：匹配所有项（通常放最后一条兜底）。
- 其余按扩展名匹配（比较前去掉 `*`、`.`，小写）；目录不匹配扩展名；没有扩展名的非目录项只能被 `*` 命中。
- 没有任何规则命中 → `null`，该项留在自由区。

### 计划 `Plan(state, items, rules, monitors)`

1. 只处理**自由区**的项：`Container==""`（映射项不处理）、不在任何普通格子里（`BoxOps.BoxOfKey==null`）、非虚拟、分类非空。
2. 按分类分组，分组顺序 = 规则顺序，组内按显示名不区分大小写排序。
3. **复用同名格子**：已存在 `Kind==Normal` 且 `Name==分类名` 的格子（取第一个）→ 追加；映射格子即使同名也**不**复用。
4. 否则新建：
   - 尺寸 `SizeFor`：列数 = `clamp(ceil(sqrt(n)), 3, 5)` 且不超过显示器列数；行数 = `clamp(ceil(n/cols), 1, 4)`，若总高超过显示器行数则减行；
   - 选位 `FindRightSpot`：从**最右侧的列**开始向左，每列自上而下找第一块放得下的空地；占用 = 现有格子覆盖区 + 不参与整理而留在自由区的图标 + 本次已规划的新格子；
   - 按显示器顺序（主屏优先）逐个尝试，都放不下则该分类留在自由区。

### 执行与撤销

`Apply`：新建格子（`Normal`）加入布局，并对每个移入的 key 记录一条 `UndoMove`（它整理前的自由区位置），再 `BoxOps.MoveToBox`。返回 `OrganizeUndo`；没有任何项要移动时返回 null，`OrganizeAll` 记日志“没有可整理的项”，**不覆盖**已有撤销记录。

撤销记录只保存一级：每次成功整理覆盖上一次。持久化在 `<数据根>\organize-undo.json`（`OrganizeUndoStore`，临时文件 + 替换；加载时没有任何 `Moves` 视为无记录；损坏则忽略并记日志），程序重启后仍可撤销，撤销成功后文件被删。格式见 [data-and-config.md](data-and-config.md)。

`ApplyUndo(state, undo, presentKeys, monitors)`——**只回滚本次**：

- 只处理仍在场的 key（已消失的忽略）：从格子摘除，回到整理前的自由区位置；
- 本次新建的格子若已空则删除；用户整理之后又往里加了东西的格子保留（只回滚本次移入的项）；
- 原位置越界或被格子覆盖 → 就近空位；原位置被**别的自由图标**占用 → 以回归的 key 为准，把对方挪到最近空位；
- 返回回到自由区的项数。

菜单里“撤销整理”的可用状态是 `CanUndoOrganize`（`_organizeUndo != null`）。

### 修改建议

- 加分类规则的默认值：改 `AppSettings.DefaultRules`（只影响没有 `settings.json` 或点“恢复默认”的用户）。
- 改整理的选位策略：只动 `FindRightSpot/SizeFor`，保持 `Plan` 纯函数，补 `OrganizerTests`。
- 复用格子只靠“普通格子 + 名称相等”判断；用户把分类格改了名，下次整理会再新建一个同名分类格。

## 设置窗口

`SettingsWindow`（普通顶层窗口，不参与桌面层 Z 序）：`ShowSingleton` 单例；托盘“设置”、右键“桌面整理 ▸ 设置…”、再次启动程序（第二实例通知首实例）都打开它。

**常规页**：

| 控件 | 效果 |
|---|---|
| 双击桌面空白处隐藏/显示图标与格子 | `AppSettings.DoubleClickToggle`，保存后生效 |
| 开机自启 | **立即生效**（点击即调用 `AutoStart.Default.SetEnabled`），状态以注册表为准，不在 `settings.json` 里 |
| 图标大小 | `IconSizeMode`：`system`（跟随系统桌面“查看”）/ `small` 32 / `medium` 48 / `large` 96 |
| 格子透明度滑块 | `BoxOpacity` 0.2~1.0（默认 0.7）；拖动时实时预览（`PreviewBoxOpacity`），不保存；窗口关闭时还原为已保存值 |
| 日志保留天数 | `LogRetentionDays`：1 / 3 / 7（默认）/ 14 / 30 天单选（`SettingsWindow.xaml.cs:LoadGeneral/SelectedLogDays`）；保存后由 `Log.SetRetention` 立即清理超期日志 |

**整理规则页**：列表（上移/下移/新增/删除/恢复默认）+ 编辑区（分类名、扩展名文本，分隔符为空格/逗号/分号/换行，含中文逗号分号）。“恢复默认”只改列表，不保存。

保存 `OnSave` → `DesktopController.ApplySettings`：用窗口里的字段**新建**一个 `AppSettings`（`OrganizeRules`、`DoubleClickToggle`、`IconSizeMode`、`BoxOpacity`、`LogRetentionDays`），`Normalize()`、写 `settings.json`、`Log.SetRetention(LogRetentionDays)`（设保留天数并立即清理）、预览透明度；图标大小是固定值则 `SetIconSize`，否则 `SyncFromSystemView()`。

**加一个设置项**：给 `AppSettings` 加属性（带默认值，旧 `settings.json` 缺字段自动取默认），必要时在 `Normalize()` 里夹范围；`SettingsWindow.xaml` 加控件、`LoadGeneral` 读、`OnSave` 写进新建的 `AppSettings`（**别漏**，否则保存后被默认值覆盖）；在 `ApplySettings` 里应用；补 `Stage4SettingsTests`。

## 双击隐藏 / 显示

- 触发：`DesktopSurface` 自由区空白处（不在格子内）双击，且 `Settings.DoubleClickToggle`；托盘图标左键单击；托盘菜单“隐藏/显示桌面图标”；右键菜单的“显示桌面图标”（动词拦截，见 [menu.md](menu.md)）。
- 实现：`DesktopController.SetIconsVisible(bool)` 改 `IconsVisible`，并写 `Layout.View.IconsHidden`（保存，所以重启后保持）；`IconsVisibleChanged` → 每个 `DesktopSurface.ApplyIconsVisible(animate)`：200ms 不透明度淡入/淡出，淡出结束后把图标与格子 `Visibility=Hidden`（用 `_fadeVersion` 防止中途再次切换时的竞态）；隐藏时清除选择。
- 隐藏时画布本身仍存在且可命中：右键仍弹桌面背景菜单，再次双击恢复。**系统 ListView 始终保持隐藏**（这里的“隐藏”只针对 DeskNook 自己画的内容）。

## 托盘

`TrayIcon`（自写 `Shell_NotifyIcon`，复用 `ShellMessageWindow` 的窗口与 `AddHook`；图标取自嵌入的 `app.ico`，按 `SM_CXSMICON` 挑帧）：

- 左键单击：切换隐藏/显示；右键：原生深色菜单（`TrackPopupMenuEx`，`_menuOpen` 防重入）：一键整理、撤销整理（无记录时置灰）、新建格子、隐藏/显示桌面图标、设置…、开机自启（带勾）、退出。
- `Recreate()`：先删后加；Explorer 重启（`TaskbarCreated`）后由 `App` 调用。`ShowBalloon` 用于“菜单组件已更新，重启资源管理器后生效”提示。
- “新建格子”→ `NewBoxCentered`（主屏工作区中央）。

## 开机自启

`AutoStart`（`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `DeskNook`，值为**带引号的 exe 路径**，取自 `Environment.ProcessPath`）。状态一律以注册表为准（`IsEnabled` 读注册表）；`SetEnabled(true/false)` 写入/删除；构造函数可注入子键/值名/路径提供者，单测用专用键 `HKCU\Software\DeskNookTests`（`AutoStartTests`），绝不碰真实键。

- `--autostart=on|off`：只改写该值后立即退出，不启动界面（`CliArgs.ParseAutostart` 不区分大小写，多个取最后一个合法值）。安装包用它在安装用户身份下写自启值（见 [build-release.md](build-release.md)）。
- Run 值里只有不带参数的 exe 路径，开机自启的实例与手动启动没有区别（`--autostart=` 只是“设置命令”，不是运行模式）。
- 旧名迁移：`ShellExtRegistrar.CleanLegacyRegistration(migrateAutostart:true)` 发现旧名 `XkDesk`/`DeskNext` 的 Run 值就删除并把 `DeskNook` 改写为开启；`--unregister` 只删除不迁移，并删除 `DeskNook` 的 Run 值。
- 升级/修复安装不改动自启：MSI 里只有全新安装才执行 `--autostart=on`，见 [build-release.md](build-release.md)。
