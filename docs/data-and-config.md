# 数据与配置

对应代码：`src/DeskNook/Services/AppPaths.cs`、`LayoutStore.cs`、`SettingsStore.cs`、`AutoOrganizer.cs`（`OrganizeUndoStore`）、`Log.cs`、`AutoStart.cs`、`CliArgs.cs`、`src/DeskNook/App.xaml.cs`、`src/DeskNook/Desktop/ShellExtRegistrar.cs`、`src/DeskNook/Model/*.cs`。

## 数据根（`AppPaths`）

`AppPaths.DataDir` 是所有数据位置的**唯一来源**，别在别处拼路径。规则 `AppPaths.ResolveDataDir(baseDir, programFiles, programFilesX86, appData)`（纯函数，可注入路径供单测）：

- 程序目录（`AppContext.BaseDirectory`）位于 **`Program Files` 或 `Program Files (x86)`** 之下 → `%AppData%\DeskNook`（该处程序目录通常不可写；MSI 默认装到 `C:\Program Files\DeskNook`）；
- 否则（便携版、解压目录、开发时 `bin\Release\net9.0-windows\`）→ `<exe 目录>\data`。

边界判断 `AppPaths.IsUnder(path, root)`：两端先 `GetFullPath` 并去掉末尾分隔符，**不区分大小写**，必须是“等于 root”或“以 `root + 目录分隔符` 开头”。所以 `C:\Program Files Foo\x` 不算在 `C:\Program Files` 之下；root 为空/空白一律返回 false（不误判）。单测：`tests/DeskNook.Tests/AppPathsTests.cs`。

首次访问时 `static AppPaths()` 尝试 `Directory.CreateDirectory(DataDir)`，失败也不抛（只读位置，各处读写自行容错：`Log` 永不抛、`LayoutStore.Save` 返回 false 并记日志）。发布输出里**不得**包含 `data\`（`tools/publish.ps1`、`tools/build-installer.ps1` 会检查并删除）；`.gitignore` 也排除了它。

## 文件清单

```
<数据根>\
  layout.json                 布局（格子、自由图标位置、视图状态）
  settings.json               设置（整理规则、双击隐藏、图标大小、格子透明度、日志保留天数）
  organize-undo.json          最近一次一键整理的撤销记录（成功撤销后删除）
  layout.json.bad-<时间戳>    损坏文件的备份（yyyyMMddHHmmssfff），settings.json.bad-* 同理
  logs\desknook-yyyy-MM-dd.log  主程序日志（按天一个文件，按 `LogRetentionDays` 清理；`Services/Log.cs:Log`；旧的 `desknook.log` 不处理）
  logs\shellext.log           C++ 扩展/代理日志（超 1MB 清空重写）
  running.flag                运行标记（内容为 PID），正常退出时删除；残留 = 上次异常退出
  shellext\DeskNookShellExt.<hash8>.dll   按内容哈希命名的扩展 DLL 副本
  icons\*.ico                 菜单线条图标（从嵌入资源释放）
```

所有 JSON 的约定：`System.Text.Json`，`WriteIndented=true`，属性名保持 PascalCase（没有命名策略），枚举 `BoxKind` 用字符串（`JsonStringEnumConverter`），未知字段被忽略，缺失字段取类型默认值，所以**只增字段就是向前兼容的**。

### 原子写入与损坏处理

`LayoutStore.Save` / `SettingsStore.Save` / `OrganizeUndoStore.Save` 都是：写 `<文件>.tmp` → 目标存在则 `File.Replace(tmp, 目标, null)`，否则 `File.Move`。失败只记日志、返回 false。`LayoutStore` 另有内部锁（保存由 500ms 去抖定时器触发，退出时 `Flush`）。

加载失败（JSON 非法或反序列化为 null）：`layout.json`/`settings.json` 被**重命名为 `.bad-<时间戳>`** 并回退默认值（`LayoutStore.Load`、`SettingsStore.Load`）；`organize-undo.json` 损坏只忽略并记日志。

### `layout.json`

```json
{
  "Version": 1,
  "FreeIcons": {
    "C:\\Users\\Alice\\Desktop\\报告.docx": { "Monitor": "\\\\.\\DISPLAY1", "Col": 0, "Row": 2, "LastSeenUtc": null },
    "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}": { "Monitor": "\\\\.\\DISPLAY1", "Col": 0, "Row": 0, "LastSeenUtc": null },
    "C:\\Users\\Alice\\Desktop\\旧文件.txt": { "Monitor": "\\\\.\\DISPLAY1", "Col": 3, "Row": 1, "LastSeenUtc": "2026-10-01T08:00:00Z" }
  },
  "Boxes": [
    {
      "Id": "3f9a1c2b7d10",
      "Name": "文档",
      "Kind": "Normal",
      "MappedPath": null,
      "Monitor": "\\\\.\\DISPLAY1",
      "Rect": { "X": 1200.0, "Y": 100.0, "W": 300.0, "H": 236.0 },
      "Collapsed": false,
      "Locked": false,
      "SortMode": "",
      "ItemKeys": [ "C:\\Users\\Alice\\Desktop\\a.docx", "C:\\Users\\Alice\\Desktop\\b.pdf" ],
      "Gone": { "C:\\Users\\Alice\\Desktop\\b.pdf": "2026-10-05T12:00:00Z" }
    },
    {
      "Id": "8e44d0aa51c7",
      "Name": "下载",
      "Kind": "Mapped",
      "MappedPath": "D:\\Downloads",
      "Monitor": "\\\\.\\DISPLAY2",
      "Rect": { "X": 50.0, "Y": 50.0, "W": 300.0, "H": 236.0 },
      "Collapsed": true,
      "Locked": true,
      "SortMode": "date",
      "ItemKeys": [],
      "Gone": {}
    }
  ],
  "View": { "IconSize": 48, "SortKey": "", "IconsHidden": false },
  "SystemPositionsImported": true
}
```

| 字段 | 说明 |
|---|---|
| `Version` | 目前恒为 1，**还没有按版本迁移的代码**；改格式靠“只增字段 + 默认值” |
| `FreeIcons` | key → `IconSlot`。key 是解析名（路径或 `::{CLSID}`），字典忽略大小写；`Col/Row` 是该显示器网格的列/行；`LastSeenUtc` 仅在项消失后记录首次消失时间，超过 7 天清理 |
| `Boxes[]` | 见 [boxes.md](boxes.md) 的字段表；`Rect` 是展开状态下相对工作区的 DIP |
| `View.IconSize` | 图标大小（DIP，默认 48）；跟随系统时首次取系统值 |
| `View.SortKey` | 最近一次“排序方式”（`name/size/type/date`，手动摆放后清空） |
| `View.IconsHidden` | 双击隐藏状态（重启后保持） |
| `SystemPositionsImported` | 是否已导入过系统桌面图标位置（只导入一次，见 [desktop-items.md](desktop-items.md)） |

向后兼容：阶段 1 的旧文件没有 `Boxes`/`View` 字段，加载后补默认（`BoxPersistenceTests.读取阶段1的layout_json_无Boxes字段`、`读取阶段1占位格子_缺新字段时补默认值`、`Stage4Tests.布局隐藏状态默认显示_旧layout_json兼容_往返保持`）。

### `settings.json`

```json
{
  "Version": 1,
  "DoubleClickToggle": true,
  "IconSizeMode": "system",
  "BoxOpacity": 0.7,
  "LogRetentionDays": 7,
  "OrganizeRules": [
    { "Name": "文件夹", "Extensions": [ "<dir>" ] },
    { "Name": "快捷方式与程序", "Extensions": [ "lnk", "url", "exe", "msi" ] },
    { "Name": "其他", "Extensions": [ "*" ] }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `DoubleClickToggle` | 双击空白处隐藏/显示，默认 true |
| `IconSizeMode` | `system`（默认）/ `small`(32) / `medium`(48) / `large`(96)；未知值 `Normalize()` 回到 `system` |
| `BoxOpacity` | 格子背景不透明度，夹到 [0.2, 1.0]，默认 0.7（缺字段取默认，NaN 取默认） |
| `LogRetentionDays` | 日志保留天数（含今天），取值 1/3/7/14/30，默认 7；缺字段或非法值 `Normalize()` 回到默认（`AppSettings.cs:AppSettings.Normalize`） |
| `OrganizeRules` | 顺序即优先级；`<dir>`=文件夹，`*`=其余所有项；加载时去掉空名字、规则内的 null |

开机自启**不在** `settings.json` 里，状态以注册表为准。

### `organize-undo.json`

```json
{
  "Version": 1,
  "CreatedUtc": "2026-10-07T01:23:45Z",
  "CreatedBoxIds": [ "3f9a1c2b7d10" ],
  "Moves": [
    { "Key": "C:\\Users\\Alice\\Desktop\\a.docx", "Monitor": "\\\\.\\DISPLAY1", "Col": 2, "Row": 1 }
  ]
}
```

`CreatedBoxIds`：本次整理新建的格子；`Moves`：本次移入格子的 key 及其整理前的自由区位置。`Moves` 为空视为没有记录。语义见 [organize.md](organize.md)。

## 注册表项全表

| 位置 | 内容 | 写入者 |
|---|---|---|
| `HKCU\Software\Classes\CLSID\{EA0A2ED4-03C2-402D-A461-E558E4D20973}` | 默认值 `DeskNook Context Menu` | `ShellExtRegistrar.Write` |
| `…\CLSID\{…}\InprocServer32` | 默认值 = `<数据根>\shellext\DeskNookShellExt.<hash8>.dll`，`ThreadingModel=Apartment` | 同上 |
| `HKCU\Software\Classes\*\shellex\ContextMenuHandlers\DeskNook` | 默认值 = 上面的 CLSID | 同上 |
| `HKCU\Software\Classes\Directory\shellex\ContextMenuHandlers\DeskNook` | 同上 | 同上 |
| `HKCU\Software\Classes\Directory\Background\shellex\ContextMenuHandlers\DeskNook` | 同上 | 同上 |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 值 `DeskNook` | `"<exe 完整路径>"`（带引号） | `AutoStart` |
| `HKLM\Software\DeskNook` 值 `StartMenuShortcut` / `DesktopShortcut`（DWORD=1） | MSI 快捷方式组件的 KeyPath，无运行时用途 | `installer/Package.wxs` |
| `HKLM\Software\DeskNook` 值 `InstallDir`（字符串） | 安装目录（`[INSTALLFOLDER]`），1.0.1 起由 MSI 写入、卸载时随组件删除；升级时 `RegistrySearch` 读回作为默认安装目录 | `installer/Package.wxs` |
| `HKCU\Software\DeskNookTests` | 仅单测使用（`AutoStartTests` 的专用 Run 键），测试后删除 | 测试 |
| 只读：`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` 的 `Hidden`、`ShowSuperHidden` | 决定桌面枚举是否包含隐藏/超级隐藏项 | 读取方 `DesktopItemSource` |

注册/卸载代码在 `ShellExtRegistrar`；改动注册内容后要 `SHChangeNotify(SHCNE_ASSOCCHANGED)`（代码已处理）。**CLSID 在 `ShellExtRegistrar.ClsidText` 与 `src/DeskNookShellExt/desknook.h` 两处，必须一致**，也不要随意换（换了等于换名，旧注册要清理）。

## 命令行参数

处理顺序与来源：`App.OnStartup`（一次性命令先于单实例检查）和 `App.ParseArgs`。

| 参数 | 类型 | 作用 |
|---|---|---|
| `--autostart=on` / `--autostart=off` | 一次性命令 | 只改写开机自启值后立即退出，不启动界面、不受单实例影响。大小写不敏感，多个取最后一个合法值（`CliArgs.ParseAutostart`）。安装包用 |
| `--exit` | 一次性命令 | 通知运行中的实例退出：优先置位 `Global\DeskNook.Exit.<SID>` 事件，没有事件再给 `DeskNookMessageWindow` 发 `DeskNook.ExitRequest`；然后最多等 8s 其他 DeskNook 进程退出。构建前、测试前、安装/卸载时使用 |
| `--unregister` | 一次性命令 | 删除 Shell 扩展注册（三个 handler 键 + CLSID）、旧名遗留、`Run\DeskNook`、全部 DLL 副本。卸载用 |
| `--attach=owner` / `--attach=child` | 运行参数 | 宿主窗口挂载方式，默认 owner，见 [desktop-layer.md](desktop-layer.md) |
| `--transparency=dwm` / `--transparency=layered` | 运行参数 | 透明方案，默认 dwm |
| `--no-proxy` | 运行参数 | 不使用 Explorer 内菜单代理（也不注册 Shell 扩展、不启动管道），菜单走进程内回退路径 |
| `--simulate-outdated-proxy` | 测试参数 | 把 Explorer 内的代理版本当作旧版，验证日志警告与托盘气泡 |
| 其他 | | 记日志“忽略未知参数”，不报错 |

`--exit` 与 `--unregister` 用 `OrdinalIgnoreCase` 比较；`ParseArgs` 里的运行参数先 `ToLowerInvariant`。新增参数时：一次性命令放在单实例互斥量**之前**；运行参数放进 `ParseArgs`；同步更新本表与根 `README.md` 的“运行参数”表。

另有进程内命名对象（不是参数但相关）：互斥量 `Local\DeskNook.SingleInstance`；事件 `Global\DeskNook.Exit.<用户SID>`；已注册窗口消息 `TaskbarCreated`、`DeskNook.ExitRequest`、`DeskNook.ShowSettings`；隐藏窗口标题 `DeskNookMessageWindow`、`DeskNookItemNotify`、`DeskNookClipboard`；管道 `\\.\pipe\DeskNook.Menu`；代理窗口类 `DeskNook.MenuProxy`。

## 历史改名与旧注册清理

| 时间 | 名称 | 提交 | 说明 |
|---|---|---|---|
| 初始 | **XkDesk** | `e54a824` 起 | 仓库根目录仍叫 `xk-desk`；曾用 CLSID `{B6F5C3A1-7D2E-4E0B-9C48-5A1E3F7D2B90}`、handler 名 `XkDesk`、Run 值 `XkDesk` |
| 2026-10-06 | **DeskNext** | `906fa89` | 显示名统一为「桌面整理」；新 CLSID `{6DF5B90B-CDC6-4C1E-8D75-5DC321045F29}`、handler `DeskNext`、Run 值 `DeskNext`；Program Files 下数据目录改为 `%AppData%\DeskNext` |
| 2026-10-06 | **DeskNook** | `006f6b4` | 因 GitHub 上已有同名项目 DeskNext 而改名；当前 CLSID `{EA0A2ED4-03C2-402D-A461-E558E4D20973}`；数据目录 `%AppData%\DeskNook` |

数据目录规则本身来自 `1bdc980`（数据根改为程序目录下的 `data`，位于 Program Files 时用 `%AppData%\…`）。

旧数据（`%AppData%\DeskNext`、`%LocalAppData%\DeskNext` 等）**不会自动迁移**，需要时手动复制 `layout.json` 等到新数据根。

旧注册清理（`ShellExtRegistrar.CleanLegacyRegistration`，数组 `Legacy` 里的名称与 CLSID **必须保持原样**，因为要匹配已经写进用户注册表的旧值）：每次启动 `EnsureRegistered` 时删除 XkDesk、DeskNext 的三个 `ContextMenuHandlers` 键与 `CLSID\{旧}` 子树，以及 Run 里的旧值；旧自启开着则改写为 `DeskNook`（`migrateAutostart:true`）。`--unregister` 也会清理（不迁移）。已经被 Explorer 加载的旧 DLL 要到 Explorer 重启后才会卸掉。

遗留命名：COPYDATA magic `0x4B584D31`（"KXM1"）来自 XkDesk 时期，已无意义但**不要改**（C++ 与 C# 要同步，且已发布版本依赖它）。
