# 桌面项

对应代码：`src/DeskNook/Desktop/DesktopItemSource.cs`、`src/DeskNook/Desktop/DesktopController.cs`、`src/DeskNook/Desktop/ShellIconCache.cs`、`src/DeskNook/Desktop/SystemDesktopView.cs`、`src/DeskNook/Desktop/DesktopDropTarget.cs`、`src/DeskNook/Desktop/ShellActions.cs`、`src/DeskNook/Views/DesktopSurface.cs`、`src/DeskNook/Views/IconItemControl.xaml.cs`、`src/DeskNook/Services/GridLayout.cs`、`src/DeskNook/Services/ItemDiff.cs`、`src/DeskNook/Services/ItemSorter.cs`。

## 数据模型：`DesktopItem`

`src/DeskNook/Model/DesktopItem.cs:DesktopItem`，来自 Shell 枚举，只读：

| 字段 | 含义 |
|---|---|
| `Key` | 唯一键 = `GetDisplayNameOf(SHGDN_FORPARSING)`：文件/文件夹是完整路径，虚拟项（此电脑、回收站…）是 `::{CLSID}`。映射格子里的项带前缀 `<格子Id>` + `DesktopItemSource.KeySeparator`(0x1F) |
| `Container` | 空 = 桌面项；否则是映射格子 Id（决定用哪个父文件夹做 Shell 操作） |
| `DisplayName` / `EditName` | 显示名 / 重命名框里的名字（`SHGDN_INFOLDER|FOREDITING`，隐藏扩展名时不含扩展名） |
| `Pidl` | 绝对 PIDL（从桌面根起）的字节拷贝。桌面项即相对桌面根的子 PIDL；映射项是 `ILCombine(目录绝对PIDL, 子PIDL)` |
| `Attributes` | `SFGAO_FOLDER/LINK/HIDDEN/GHOSTED/FILESYSTEM/CANRENAME` |
| `FilePath` | 文件系统路径；虚拟项为 null（`IsVirtual`） |
| `Size/Modified/Extension` | 排序用，取自 `FileInfo`（文件瞬间消失按空属性处理） |
| `Fingerprint` | `显示名|属性|大小|修改时间`，diff 判“更新”用 |

`Pidl` 字节拷贝而不是持有 COM 指针：项可以安全地在线程间传递（图标后台线程用它），用时 `ShellApi.PidlFromBytes` 还原。

## 枚举

`DesktopItemSource.Enumerate()`（桌面）与 `EnumerateFolder(path, container)`（映射目录）共用 `EnumerateInto`：

- 桌面：对桌面根 `IShellFolder`（`ShellApi.Desktop`）`EnumObjects(SHCONTF_FOLDERS|NONFOLDERS)`，这样自动合并**用户桌面 + 公共桌面 + 虚拟项**。是否包含隐藏/超级隐藏项由注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` 的 `Hidden`/`ShowSuperHidden`（值为 1）决定，与 Explorer 一致。
- 过滤：位于用户桌面目录或公共桌面目录的文件系统项直接保留；其余项（用户文件夹、OneDrive、网盘等命名空间项）只保留**系统桌面视图里本来就显示的**——`SystemDesktopView.IsShown`（`IFolderView2.GetItemPosition` 成功即显示）。系统视图取不到（`sysView==null`）时不过滤。
- 映射目录：`ShellApi.BindFolder(path, out abs)` 绑定该目录，枚举同样走 `EnumerateInto`，不做系统视图过滤。目录绑不上（被删/不可达）返回空列表并记日志。
- 半透明：`IsHidden`/`IsGhosted` 的项以 50% 不透明度显示（`IconItemControl.UpdateVisual`），与系统桌面一致；“剪切”状态也是 50%。

## 监听与去抖 diff

`DesktopItemSource` 构造时创建一个隐藏窗口 `DeskNookItemNotify`，对下列位置**各注册一次** `SHChangeNotifyRegister`（`SHCNRF_ShellLevel|InterruptLevel|NewDelivery`，`SHCNE_ALLEVENTS|SHCNE_INTERRUPT`，消息 `WM_APP+1`）：桌面根（虚拟项）、用户桌面、公共桌面；映射目录则注册该目录本身。**不能一次传多个 entry**：这台机器上 `cEntries > 1` 会访问冲突，代码里每个位置单独注册（见 [troubleshooting.md](troubleshooting.md)）。

收到通知（`HandleNotify`）：

- `SHCNE_UPDATEIMAGE` / `SHCNE_ASSOCCHANGED`：`IconInvalidated(null)`，整体失效图标缓存，不触发 diff。
- 其余：读取两个 PIDL 的解析名；`RENAMEITEM/RENAMEFOLDER` 记录**改名提示** `_renameHints[旧key]=新key`；`UPDATEITEM/ATTRIBUTES` 让该 key 的图标失效；然后 `Schedule()` 重置 300ms 去抖计时器。
- 去抖到期 → `Refresh(useShownCache: true)`：重新 `Load()` → `ItemDiff.Compute(旧集合, 新集合, 改名提示)` → 触发 `Changed(diff)` 与逐项 `IconInvalidated`。

**耗时与虚拟项缓存**：桌面 255 项空闲时 `Enumerate()` 约 15ms（首次约 180ms），整条刷新应在“去抖 300ms + 几十 ms”内完成。枚举时对**不在用户/公共桌面目录下的项（回收站、此电脑等虚拟项）**要问系统桌面视图是否显示（`DesktopItemSource.Enumerate` → `SystemDesktopView.Acquire/IsShown`），这是**跨进程 COM 调用**进 Explorer 桌面线程；而“新建 ▸ 文件/文件夹”由 Explorer DefView 执行（日志 `代理已执行 … DefView=True`），它忙于新项与进入自身重命名时，该调用会被阻塞数秒（实测新建后 2~3 秒才刷新，删除/DefView=False 的操作只有约 0.4 秒）。因此：结果按解析名缓存在 `DesktopItemSource._shownCache`，去抖路径命中缓存则不再询问 Explorer；显式 `Refresh()`（菜单“刷新”、`CommitRename`）清缓存重新询问。刷新完成日志带“刷新耗时 X ms，枚举 Y ms”，单次询问超过 200ms 另记一行“询问系统桌面视图耗时”，用于确认是否仍被阻塞。另外 `DesktopSurface.cs:OnRenameRequested` 打出的“原位重命名框已显示”日志带“排队/抢前台/聚焦”分段耗时（排队起点为 `DesktopController.cs:RenamePostedAt`，`BringToForeground` 内 `SetForegroundWindow` 超 100ms 另记一行），`DesktopSurface.cs:LoadIcon` 的图标加载超过 500ms 单独记日志，用于定位“新建后迟迟不显示”。

**缓存失效规则**：`DesktopItemSource.NeedsShownCacheReset` 判断每条通知，满足任一则下次去抖刷新先清 `_shownCache`：事件含 `SHCNE_ASSOCCHANGED`（该事件会额外触发一次去抖刷新）；任一 PIDL 解析名以 `::` 开头（虚拟/CLSID 项变化，如“桌面图标设置”勾选此电脑/回收站）；`UPDATEDIR/UPDATEITEM` 且两个 PIDL 都无解析名（作用于桌面根）。新建文件那批通知（`CREATE/MKDIR [桌面目录下路径]`、`UPDATEITEM [桌面目录]`、`0x4000000` 空路径）不触发，单测 `ShownCacheResetTests`。隐藏窗口未处理 `WM_SETTINGCHANGE`。

`ItemDiff.Compute` 的规则：key 比较忽略大小写；先按改名提示配对（旧 key 在旧集合且不在新集合、新 key 在新集合且不在旧集合才成立），配对的进 `Renamed`，**不**算新增/删除；其余新增、删除、指纹变化（`Updated`）。没有改名提示（例如外部程序批量改名）则按“删除 + 新增”处理，位置会丢（新增项走空位分配）。

### 改名保留位置

`DesktopController.OnSourceChanged` 对 `diff.Renamed` 调 `LayoutReconciler.Rename(Layout, 旧, 新)`：自由区位置与所有格子的成员关系都转给新 key，选择/锚点同步迁移。DeskNook 自己改名时（`CommitRename`）先 `AddRenameHint` 再 `Source.Refresh()`，不依赖通知到达。

## 控制器如何响应变化（`OnSourceChanged`）

顺序：重建 `_byKey` → 判定“撤消”标签（见下）→ 处理改名 → `Reconcile()`（对账，新项分配空位）→ 处理 `_pendingDrop`（拖入落位）→ 处理 `_expectNewUntil`（“新建”命令产生的新项自动选中并进入重命名）→ `ScheduleSave()` → `ItemsChanged`/`SelectionChanged`。

两个“时间窗”机制（都是“某操作触发，随后出现的新项归它”）：

| 机制 | 窗口 | 设置处 | 效果 |
|---|---|---|---|
| `ExpectNewItem` | 8 秒 | 代理回报“新建”子菜单里的命令（`pick` 事件且父菜单为“新建/New”）；回退路径 `ShellContextMenu.Show` 同理；`MoveToNewFolder(renameAfter:true)` 也会设置 | 之后出现的最新新项选中并 `RenameRequested` |
| `SetPendingDrop` / `SetPendingDropBox` | 10 秒 | `DesktopDropTarget.Drop` 转发给 Shell 之前 | 外部拖入/从映射目录拖出产生的新项落在鼠标释放处（自由区格子，或某个普通格子的插入位置） |
| `ArmUndo(op)` | 20 秒 | 删除、粘贴、粘贴快捷方式（含代理回报的粘贴命令）；重命名成功则直接置 `UndoLabel="重命名"` | 随后出现了对应变化（删除看 `Removed`，其余看 `Added`）才把 `UndoLabel` 置为“撤消 xxx”，菜单里才会有撤消项 |

## 图标缓存

`ShellIconCache`：

- 请求 `Get(item, px, done)`：缓存键 `(item.Key, px)`；命中同步回调；未命中排队，相同键的并发请求合并为一个 `_pending` 等待列表。
- 2 条后台 STA 线程 `ShellIconWorker0/1` 消费队列：`SHCreateItemFromIDList` → `IShellItemImageFactory.GetImage(size, flags=0)` → `GetDIBits` → 补 alpha（位图全 0 alpha 时视为旧式图标，补成不透明）→ 转冻结的 `Pbgra32` `BitmapSource`。
- 像素尺寸 `px = IconSize(DIP) × 该显示器 Scale`，所以不同 DPI 的显示器各有一份缓存。
- 快捷方式箭头：`GetImage` 不带箭头，`isLink` 时用 `SHGetStockIconInfo(SIID_LINK=29)` 取系统链接图标，按约 0.65 倍图标宽度叠在左下角（`AddLinkArrow`）。
- 失效：`Invalidate(null)` 清空全部并 `_generation++`（后台正在加载的过期结果被丢弃）；`Invalidate(key)` 清该 key 的所有尺寸。失败返回 null 时 `IconItemControl.NeedsIcon=true`，下次重建重试。
- 缓存字典只在 UI 线程访问（后台线程只产出结果并 `BeginInvoke` 回来）。

## 首次导入系统图标位置

`DesktopController.Initialize`：

1. `SystemDesktopView.Read`：`IShellWindows.FindWindowSW(SWC_DESKTOP)` → `IServiceProvider` → `IShellBrowser.QueryActiveShellView` → `IFolderView2`，读 `GetViewModeAndIconSize`、`GetSpacing`，以及每项 `GetItemPosition`（再 `ClientToScreen` 成屏幕物理坐标）。
2. 格子尺寸：`CellW = IconSize + (系统水平间距 - 图标像素)/主屏Scale`，`CellH` 同理用垂直间距（默认 27 / 52 DIP，即 75×100 的格子，图标 48）。`SyncFromSystemView` 在“查看 ▸ 大/中/小图标”后重新读取。
3. 只在 `Layout.SystemPositionsImported == false` 时导入：`GridLayout.FromScreenPixel` 把屏幕坐标换成 `(显示器, 列, 行)` 写入 `FreeIcons`，然后置位标志。之后系统桌面的位置不再参与。
4. 图标大小：首次用系统值；`Settings.IconSizeMode` 为 `small/medium/large`（32/48/96）则覆盖，`system` 跟随系统。

## 网格与空位算法（`GridLayout`）

| 函数 | 规则 |
|---|---|
| `FirstEmpty(grid, occupied)` | Explorer 规则：从左上起**按列向下**填；网格满则向右无限扩展列 |
| `NearestEmpty(grid, occupied, col, row)` | 先把目标 clamp 进网格，取欧氏距离平方最小的空格；平局列小优先再行小；没有空位退化为 `FirstEmpty` |
| `FromPixels` | 相对工作区的物理像素 → 格子坐标，四舍五入并 clamp（拖放落位用） |
| `FromScreenPixel` | 屏幕坐标 → `(显示器, 列, 行)`；点不在任何工作区取距离最近的显示器 |

`MonitorGrid.Cols/Rows = floor(工作区物理尺寸 / Scale / 格子尺寸)`，至少 1。被格子覆盖的格子（`BoxGeometry.CoveredCells`，含部分覆盖）不能放自由图标。

对账规则（谁占哪个格子、消失项保留 7 天等）见 [boxes.md](boxes.md)。

## 选择、框选、拖动、重命名

全部状态在 `DesktopController`（`Selected` 集合、`AnchorKey`），`DesktopSurface` 只转发输入：

- 单击选中；Ctrl+点击切换；Shift+点击 `SelectRange`（锚点与目标之间的矩形区域，限同一区域：同一显示器自由区或同一格子）。右键点未选中项：先只选中它。
- 方向键 `Navigate`：同一区域内按“主方向距离×1000 + 次方向距离”选最近项；Shift 扩展选择。格子内的行列由 `DesktopController.BuildLocs` 按格子视图（`BoxState.ViewMode`，见 [boxes.md](boxes.md)）算出，列表视图按列优先。
- 框选：空白处按下开始（Ctrl 保留原选择）；在格子内空白处开始的框选只选该格子的图标，且要与格子可视区域相交（`_bandBox`）。
- 拖动发起：按下后移动超过 `SystemParameters.MinimumHorizontal/VerticalDragDistance` → `DesktopController.StartDrag`（`SHDoDragDrop` 阻塞到拖放结束）。只拖与锚点同来源（同 `Container`）的项，因为 Shell 数据对象要求同一父文件夹。
- 双击图标：`OpenItems` → `ShellContextMenu.InvokeDefault`（`CMF_DEFAULTONLY` + `GetMenuDefaultItem`，与 Explorer 双击行为一致）。
- 双击空白处（不在格子内）：切换隐藏/显示，受 `AppSettings.DoubleClickToggle` 控制，见 [organize.md](organize.md)。

### 原位重命名

触发：F2、菜单“重命名”（动词 `rename` 被拦截后回到 DeskNook）、“新建”命令后自动。流程：`DesktopController.BeginRename(key)` → `RenameRequested` → `DesktopSurface.OnRenameRequested` 在图标的文字位置放一个 `TextBox`（横排视图（小图标/列表，`IconItemControl.Horizontal`）下框在文字区：`left = GetLeft(ctl) + LabelBounds.X`、宽 = 控件宽 − `LabelBounds.X` − 2、左对齐不换行；回车提交、Esc 取消、失焦提交；非文件夹且有扩展名时默认选中不含扩展名的部分）→ `CommitRename` → `ShellActions.Rename`：在项所在文件夹上调用 `IShellFolder.SetNameOf`（与 Explorer 一致地处理隐藏扩展名、非法字符提示）→ 拿到新 PIDL/新 key → 登记改名提示并刷新。映射目录里的项新的绝对 PIDL = 父绝对 PIDL + 新子 PIDL。

为什么拦截 `rename`：DeskNook 没有真实 DefView 宿主，系统的 `rename` 动词不会在 DeskNook 的画布上进入编辑；所以走 `MenuExtensions.VerbInterceptors["rename"]`。

### 快捷键（`DesktopSurface.HandleKey`）

| 键 | 行为 |
|---|---|
| F2 | 重命名选中的第一项 |
| Delete / Shift+Delete | `DeleteSelected`：对每个来源分组调用 `ShellContextMenu.InvokeVerb("delete")`，Shift 时带 `CMIC_MASK_SHIFT_DOWN` 即永久删除（走 Shell 的确认框） |
| Ctrl+A / C / X / V | 全选 / 复制 / 剪切 / 粘贴（`copy`/`cut`/`paste` 动词，作用在 Shell 数据对象上） |
| Ctrl+Z | `Undo`：向隐藏着的系统 DefView 发 `WM_COMMAND 0x701B`（`FCIDM_SHVIEW_UNDO`），由 Shell 自己的撤销栈执行；仅当 `UndoLabel != null` |
| Enter / Alt+Enter | 打开 / 属性（`properties` 动词） |
| F5 | `Refresh`：清图标缓存、桌面与全部映射目录重新枚举 |
| Esc | 清除选择 |
| 方向键（+Shift） | `Navigate` |

重命名框或格子标题编辑期间 `HandleKey` 直接返回 false，不拦截按键。

“粘贴”的目标：最近点击的是映射格子（`ActiveBox`）→ 粘贴到该目录（`_menuBox` + `CreateMenuOverride`），否则粘贴到桌面。

### 剪切半透明

`ClipboardWatcher` 用 `AddClipboardFormatListener` 监听剪贴板；`GetCutPaths()` 读取 `Preferred DropEffect`，含 `DROPEFFECT_MOVE` 且有 `FileDrop` 时返回文件路径集合（读取失败重试 3 次，每次间隔 30ms）。`DesktopController.UpdateCutState` 变化时触发 `CutStateChanged`，对应图标 50% 透明。

## 拖入与拖出

**拖出**：`StartDrag` → `ShellActions.DoDragDrop`：`GetUIObjectOf(IDataObject)` + `SHDoDragDrop`（带系统拖拽图像，允许 copy/move/link）。

**拖入**：`DesktopDropTarget` 是宿主窗口的 OLE `IDropTarget`（先 `RevokeDragDrop` 掉 WPF 自己注册的，再 `RegisterDragDrop`）。**拖放规则不自己实现，交给 Shell 的 `IDropTarget` 处理**（移动/复制/链接规则、修饰键、右键拖放菜单都与 Explorer 一致），自己只决定转发给谁。`Evaluate` 的优先级：

1. 鼠标下的图标且 `CanDropOn`（文件夹、虚拟项、快捷方式、可执行类扩展名，且带 `SFGAO_DROPTARGET`）→ 转发给该项的 `IDropTarget`（标签 `item:<key>`）。拖的就是它自己除外。
2. 映射格子空白处 → 转发给该目录的 `IDropTarget`（`map:<格子Id>`）。拖回自己所在的映射格子：不接收。
3. 外部拖入，或从映射格子拖到桌面/普通格子 → 桌面背景的 `IDropTarget`（`bg`，文件操作）。
4. 桌面项在桌面内拖动（`DragKeys != null` 且来源为桌面）→ 不转发，`Drop` 时只改布局：普通格子上 `MoveKeysToBox`（带插入位置），否则 `MoveSelection`；并把返回的 `pdwEffect` 置为 `DROPEFFECT_NONE`，避免源端把它当成“移动文件”。

另：`IDropTargetHelper`（`CLSID_DragDropHelper`）提供拖拽图像；落点反馈由 `DesktopSurface.SetDropFeedback` 画（普通格子画插入位置竖线、映射格子整体高亮）。

`MoveSelection` 的细节：锚点项落到目标格，其余自由图标保持相对位置（按原列、行排序依次平移），目标被占用/越界就近找空位；来自格子（没有自由位置）的项就近放在目标附近；手动摆放后清 `View.SortKey`。

## 排序（桌面自由区）

`DesktopController.SortBy(key)`：对每个显示器上的自由项按 `ItemSorter` 排序后，从左上角**按列**依次填入空格（避开格子覆盖区），并记录 `Layout.View.SortKey`。`ItemSorter`：虚拟项恒在最前；名称用 `StrCmpLogicalW`（Explorer 的自然排序）；`name/size` 默认文件夹优先，`type` 先按扩展名，`date` 按修改时间降序。
