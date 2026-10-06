# 格子（普通格子与映射格子）

对应代码：`src/DeskNook/Model/LayoutState.cs`（模型）、`src/DeskNook/Views/BoxControl.cs`（交互与渲染）、`src/DeskNook/Views/DesktopSurface.cs`（承载与命中）、`src/DeskNook/Services/BoxGeometry.cs`（几何）、`src/DeskNook/Services/BoxOps.cs`（成员操作）、`src/DeskNook/Services/LayoutReconciler.cs`（对账）、`src/DeskNook/Desktop/DesktopController.cs`（格子相关操作）、`src/DeskNook/Desktop/DesktopItemSource.cs`（映射目录来源）。单测集中在 `tests/DeskNook.Tests/BoxTests.cs` 与 `LogicTests.cs`。

核心原则：**格子只是布局，不动文件**。把图标放进/拖出格子、解散格子、整理，改的都只是 `layout.json`。

## 模型：`BoxState`

`src/DeskNook/Model/LayoutState.cs:BoxState`（持久化到 `layout.json` 的 `Boxes[]`）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | string | 12 位十六进制（`BoxOps.NewId`），缺失时加载时补上 |
| `Name` | string | 标题。新建时 `UniqueBoxName` 保证不重名（`新格子`、`新格子 2`…；映射格子默认取目录名）；重命名（`RenameBox`）不检查重名 |
| `Kind` | `Normal` / `Mapped`（JSON 里是字符串） | 普通格子成员是桌面项；映射格子内容来自目录 |
| `MappedPath` | string? | 仅映射格子：目录路径 |
| `Monitor` | string | 所在显示器设备名；显示器不存在时**临时**显示在主屏，这里不改写 |
| `Rect` | `{X,Y,W,H}` | 相对该显示器**工作区左上角**的 DIP，**展开状态下**的完整尺寸（折叠不改它） |
| `Collapsed` | bool | 折叠：显示时只有标题栏高度 |
| `Locked` | bool | 锁定：不能移动/缩放 |
| `SortMode` | string | `""`=手动顺序（仅普通格子）｜`name`｜`date`｜`size`｜`type` |
| `ItemKeys` | string[] | 普通格子的成员 key（有序）；映射格子不使用（对账时清空） |
| `Gone` | `{key: 消失时间}` | `ItemKeys` 里已从桌面消失的 key 的首次消失时间（7 天保留） |

相关：`IconSlot`（自由图标位置 `Monitor/Col/Row/LastSeenUtc`）、`ViewSettings`（`IconSize`、`SortKey`、`IconsHidden`）、`LayoutState`（`FreeIcons`、`Boxes`、`View`、`SystemPositionsImported`）。完整 JSON 示例见 [data-and-config.md](data-and-config.md)。

## 几何常量与规则（`BoxGeometry`）

| 常量 | 值 | 含义 |
|---|---|---|
| `TitleH` | 32 | 标题栏高度（DIP） |
| `PadBottom` | 4 | 底部留白 |
| `ChromeH` | 36 | 标题栏 + 底部留白；内容区可视高度 = 总高 − 36 |
| `MinCols` / `MinRows` | 2 / 1 | 缩放下限：宽 ≥ 2×CellW，高 ≥ 36 + 1×CellH |
| `DefaultCols` / `DefaultRows` | 4 / 2 | 新建格子默认列数/行数 |
| `SnapThreshold` | 8（DIP） | 边缘吸附距离 |

- **有效矩形** `Effective(box, monitors, ignoreCollapsed)`：找不到 `box.Monitor` 就用 `monitors[0]`（主屏）；宽高夹到工作区内、位置夹到工作区内；折叠且 `!ignoreCollapsed` 时高度 = `TitleH`。只读，不改状态。
- **占用格** `CoveredCells`：所有格子（含折叠后的有效高度）覆盖的网格单元集合（含部分覆盖，用 `Span` + `Eps`）；自由图标不能占这些格。
- **内容排布**：列数 `Cols = max(1, floor(宽/CellW))`；成员按行优先（`index % cols`, `index / cols`）；内容在格子里水平居中（`OffsetX = (宽 - Cols*CellW)/2`），所以非整格宽度也均匀。
- **插入位置** `InsertIndexAt(x, y, cols, cellW, cellH, count)`：落点所在列，格内偏右一半则插到该图标之后；`y` 已含滚动偏移。

## 交互（`BoxControl`）

### 移动与缩放：逐像素 + 边缘吸附 + 辅助线

- 标题栏按下拖动 = 移动；边缘/角（`EdgeSize=6` DIP，上边 4）按下 = 缩放，光标随边变化。锁定或折叠的格子不能缩放，锁定/折叠的格子不能移动（但仍可双击标题改名、点按钮、右键）。
- **跟手**：位置/边按“起始矩形 + 鼠标位移”算出原始值，取整到**设备像素**（`PixelSnap`，按该显示器 `Scale`），不会漂移。
- **吸附**：只有当拖动的边与目标位置（屏幕工作区边缘 / 其他格子的左右上下边）距离 ≤ 8 DIP 才吸到该位置，否则保持原始值；移动时左右边对左右边、左边对右边、右边对左边都是候选。缩放时被拖动的边吸附；Left/Top 拖动时对侧边不动。
- **辅助线**：`FindGuides` 找当前矩形与其他格子/屏幕边缘重合（<0.5 DIP）的线，`DesktopSurface.ShowGuides` 画细线（Z 序 150）。
- **只更新当前格子**：拖动过程中不触发 `DesktopController.ItemsChanged`，也就不会重建整个桌面；`BoxControl` 自己维护 `_preview` 矩形并 `Relayout()`。松手（`EndDrag(commit:true)`）且矩形有变化才调用 `DesktopController.SetBoxRect`（写 `Monitor`、`Rect` → `Reconcile` → 保存 → `ItemsChanged`）。取消/丢失鼠标捕获则丢弃预览。
- **合帧**：`OnMouseMove` 只记录最新鼠标位置 `_pendingPos`；`CompositionTarget.Rendering` 每帧调用 `ProcessPending()` 处理最近一次位置（60 步 MouseMove 也最多每帧算一次）。拖动开始时才挂接 `Rendering`，结束时摘掉。拖动开始还会缓存本显示器的其他格子矩形（`_dragOthers`）与工作区尺寸，过程中不再访问控制器。
- 自动化测试覆盖：`tools/test/test-box-smooth.ps1`（S02~S07，含“拖动期间不重建桌面”）。

### 折叠、锁定、排序、滚动

- **折叠**：按钮或菜单切换 `Collapsed`；折叠时 `DisplayRect` 高度只有 `TitleH`、内容区隐藏。鼠标移入临时展开（`_hoverExpanded`，Z 序提到 50），移出 250ms（`_leaveTimer`）后收起；展开状态不变（`Collapsed` 不改）。展开/折叠都会 `Reconcile`（展开后覆盖到的自由图标要让开）。
- **锁定**：菜单切换 `Locked`，标题旁显示锁图标。
- **排序**（`SetBoxSort`）：`""` 手动顺序；其他模式由 `ItemSorter` 即时排序显示，不改 `ItemKeys`。从排序模式切回手动或往排序格子里插入时先 `MaterializeOrder`：把当前显示顺序固化进 `ItemKeys`，再转手动，插入位置才有意义。映射格子没有“手动顺序”，默认按名称。
- **滚动**：内容行数超出可视高度时出现细滚动条；滚轮每格 60 DIP；可拖动滑块（`Mode.Thumb`）。`MaxScroll = 内容总高 − 可视高`。
- **标题重命名**：双击标题 → `BeginTitleEdit`（`TextBox`，回车提交，失焦提交，Esc 取消）；菜单“重命名”走 `BoxRenameRequested`。
- 标题栏右侧两个按钮：折叠/展开、菜单（弹出与右键相同的格子菜单）。

### 跨区域拖动只改布局

图标可在“自由区 / 各普通格子”之间拖动：`DesktopDropTarget.Drop` 内部拖放分支调用 `MoveKeysToBox`（普通格子，带插入位置）或 `MoveSelection`（自由区），**不动文件**，并把 `pdwEffect` 置 `DROPEFFECT_NONE`。同一格子内拖动 = 调序（`BoxOps.MoveToBox` 的 `index` 是**移动前**列表里的插入位置，同格内移动自动扣除前面被移走的项）。格子本身拖到别的位置/显示器就是 `SetBoxRect`。

菜单项“移动到格子 ▸”“移出格子”（`MenuExtensions`）调用 `MoveItemsToBox`/`MoveItemsOutOfBoxes`（移出时放到格子左上角附近的空位）。

## 映射格子

内容对应任意目录，右键“作为桌面格子显示”（`NewMappedBoxAt`）或“桌面整理 ▸ 新建映射格子…”（`NewMappedBox`，`OpenFolderDialog` 选目录）创建。

- **独立的项来源**：每个映射格子一个 `DesktopItemSource(path, boxId)`（`MappedRuntime`），有自己的 `SHChangeNotifyRegister`（监听该目录）与 300ms 去抖 diff；项的 key 带 `格子Id + 0x1F` 前缀，`Container` = 格子 Id。`DesktopController.SyncMapped` 让来源集合与布局里的映射格子保持一致：新增则监听，删除或改路径则释放。控制器没有统一的“当前项集合”，所以 `ItemOf(key)` 要同时查 `_byKey`（桌面）和 `_mappedByKey`。
- **内容与排序**：目录内容，默认 `name` 排序（文件夹优先、自然排序）；`SortMode` 可改为 `date/size/type`。
- **菜单**：
  - 图标右键：`ProxyRequest.Folder = MappedPath`，`Items` = 文件系统路径，代理对该目录取 `GetUIObjectOf`；
  - 空白处/标题栏右键：`ShowBoxMenu` 发 `background` 请求 `Folder = MappedPath`——Explorer 里弹的是**该目录的原生背景菜单**（含“新建”），DeskNook 的格子项（重命名/折叠/锁定/排序/解散，`Position=Top`、`Applies=InBoxMenu`）通过 `ctx.Box` 查询出来注入；回退路径用 `CreateMenuOverride` 取目录的 `CreateViewObject(IContextMenu)`；
  - 普通格子（`Normal`）空白处只有自定义格子菜单，直接用 `BoxMenu.Show`（原生菜单 API，不涉及 Shell/代理）。
- **拖放**：拖到映射格子空白处 → `CreateMappedDropTarget` 取该目录的 `IDropTarget`（真实文件操作）；从映射格子拖到桌面/普通格子 → 桌面背景的 `IDropTarget`（也是文件操作，落点登记为 `pendingDrop`）；拖回自己所在的映射格子不接收。
- **粘贴**：最近点击的是映射格子（`ActiveBox`）时 Ctrl+V 粘贴到该目录（`_menuBox`）。
- **解散**：直接删除格子，不涉及任何文件，目录原样保留。
- 目录不存在/绑定失败：`EnumerateFolder` 返回空列表并记日志，格子仍显示（空）。
- 映射项不参与一键整理（见 [organize.md](organize.md)），也没有 `ItemKeys`/`Gone`（对账时清空）。

## 创建与选位

`DesktopController.CreateBox(...)`：以鼠标位置换算期望列/行，`BoxGeometry.FindSpot` 在网格里找 `wCells×hCells` 的空地（避开自由图标与其他格子），取左上角离期望位置最近的一块（平局列小优先再行小）；找不到则退回期望位置。尺寸 = `DefaultCols`（不超过显示器列数）× `rows`，宽 = `cols*CellW`，高 = `ChromeH + rows*CellH`。`NewBoxFromItems` 的 `rows = max(2, ceil(n/4))` 并立刻 `MoveToBox`。托盘“新建格子”（`NewBoxCentered`）在主屏工作区中央新建并确保图标可见。

## 解散逻辑（`BoxOps.Dissolve`）

- 映射格子：删除即可。
- 普通格子：先用 `Effective(ignoreCollapsed:true)` 取展开矩形，删除格子；成员里**当前在场**的 key 依次回到该显示器自由区：起点是格子左上角所在的单元格，`NearestEmpty` 找空位（占用 = 剩余格子覆盖 + 已在场的自由图标）；已消失（`Gone`）的成员随格子一起丢弃。返回回到自由区的 key 列表。
- 若格子所在显示器不存在，用主屏的有效矩形做同样处理。

## 对账：`LayoutReconciler` 的不变量

`Reconcile(state, currentKeys, monitors, nowUtc)`（纯逻辑，`DesktopController.Reconcile` 在每次项/显示器/格子变化后调用）：

1. **key 唯一归属**：一个 key 只能属于“自由区”或“某一个普通格子”。多个格子都含同一 key 时先到先得（后者被剔除），同一格子内去重；在格子里的 key 立即从 `FreeIcons` 移除。写操作也维护这点：`BoxOps.MoveToBox` 从自由区和所有其他格子摘除；`LayoutReconciler.Move` 先 `RemoveFromBoxes`。
2. **消失项保留 7 天**（`DefaultRetention`）：格子里的成员不在当前项集合里 → 记入 `Gone[key]=首次消失时间`，仍保留在 `ItemKeys`（顺序与位置不丢，文件回来时原样出现）；超过 7 天才删除。自由图标同理用 `IconSlot.LastSeenUtc`。回来则清除标记。`Gone` 里不再属于任何成员的 key 会被清掉。临时重命名/临时移动因此不会丢位置。
3. **自由图标位置合法性**：在场且位置合法（显示器存在、行列在网格内、未被格子覆盖、未被同屏其他图标占用）的保留；否则重新安置：原显示器还在 → 原行列就近 `NearestEmpty`；显示器消失 → **主屏** + 原行列就近；全新项 → 主屏 `FirstEmpty`（按列向下填）。
4. **显示器缺失时格子的临时落位**：`Effective` 把格子放到主屏并夹进工作区，**不改写** `box.Monitor`；显示器回来后自动回到原位。`SetBoxRect`（用户亲手移动/缩放）才会写入新的 `Monitor`。
5. **格子移动/缩放/展开后的自由图标让位**：`SetBoxRect`、`ToggleBoxCollapsed` 都会再对账，被盖住的自由图标走第 3 条就近挪开。
6. **映射格子**：`ItemKeys` 与 `Gone` 每次对账清空。
7. **改名**：`LayoutReconciler.Rename(state, 旧, 新)` 把旧 key 的自由位置与所有格子成员关系转给新 key（`Gone` 标记丢弃）。
8. **持久化兜底**：`LayoutStore.Load` 把 JSON 里的 null 字段修正为默认值，缺 `Id` 的格子补 Id；损坏文件重命名为 `layout.json.bad-<时间戳>` 并回退默认。

修改以上任何一条都要同步改 `BoxTests.cs`/`LogicTests.cs`，它们按“故事”写成了不变量断言（`同一key只归属一处_格子优先且去重`、`格子内消失项保留七天后清理` 等）。

## 修改建议

- 想加格子的新属性：加到 `BoxState`（给默认值，旧 `layout.json` 缺字段时自动取默认；见 `BoxPersistenceTests.读取阶段1占位格子_缺新字段时补默认值`），在 `LayoutStore.Load` 补 null 修正，渲染在 `BoxControl.Relayout`，菜单项加到 `MenuExtensions.Items`（`Applies = InBoxMenu`）。
- 改交互手感（吸附阈值、最小尺寸）只改 `BoxGeometry` 常量/函数并补单测；`BoxControl` 不含数学。
- 任何会触发 `ItemsChanged` 的路径都会让所有显示器的 `DesktopSurface.Rebuild()`，拖动过程中不要走它。
