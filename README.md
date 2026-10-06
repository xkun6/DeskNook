# DeskNook（桌面整理）

类腾讯桌面整理的 Windows 桌面工具（WPF / .NET 9 / x64）。程序接管桌面图标的绘制：文件仍留在桌面目录，不移动、不改属性；退出或崩溃后系统图标自动恢复。

## 功能

- **桌面格子**：新建格子、映射格子（内容对应任意目录）、拖动 / 缩放吸附网格、折叠 / 锁定 / 排序、图标可在格子与桌面之间拖动，布局持久化（多显示器各自独立）。
- **一键整理**：按分类规则（文件夹 / 快捷方式与程序 / 文档 / 图片 / 视频 / 音频 / 压缩包 / 其他，可在设置里编辑）自动建格子并归类，可撤销。
- **双击隐藏**：双击桌面空白处，所有图标与格子淡出；再双击淡入恢复。状态会保存，重启后保持。可在设置里关闭。
- **托盘常驻**：左键单击切换隐藏 / 显示；右键菜单：一键整理、撤销整理、新建格子、隐藏 / 显示桌面图标、设置、开机自启、退出。Explorer 重启后托盘图标自动重建。
- **原生右键菜单**：菜单由 Explorer 进程内的代理弹出（第三方扩展、图标、深色主题与资源管理器完全一致），DeskNook 的自定义项（整理至新格子、整理至新文件夹、作为桌面格子显示、桌面整理 子菜单等）作为 Shell 扩展注入。
- **设置**（托盘 → 设置，或再次启动程序）：常规（双击隐藏、开机自启、图标大小、格子透明度实时预览）、整理规则。
- **单实例**：再次启动会打开已运行实例的设置窗口。

## 构建

需要：.NET 9 SDK、Visual Studio 2022（含“使用 C++ 的桌面开发”，用于编译 `src/DeskNookShellExt`）。

```powershell
dotnet build DeskNook.sln -c Release      # 同时编译 C++ 扩展 DeskNookShellExt.dll
dotnet test  DeskNook.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1   # 发布到 dist/（依赖框架，win-x64）
```

`tools/publish.ps1 -SingleFile` 可把托管程序集合并为单个 exe（`DeskNookShellExt.dll` 仍在旁边）。发布产物需要目标机器安装 .NET 9 桌面运行时。

菜单与托盘用到的图标由 `tools/gen-icons.ps1` 生成（输出 `src/DeskNook/Assets/*.ico`，已提交）。

## 运行参数

| 参数 | 作用 |
|---|---|
| `--attach=owner` / `--attach=child` | 宿主窗口挂载方式（默认 owner） |
| `--transparency=dwm` / `--transparency=layered` | 透明方案（默认 dwm） |
| `--no-proxy` | 不使用 Explorer 内的菜单代理，右键菜单走进程内回退路径 |
| `--exit` | 通知已运行的实例退出 |
| `--unregister` | 删除 Shell 扩展的全部注册项和开机自启项后退出（卸载用） |
| `--autostart=on` / `--autostart=off` | 只改写开机自启项（`HKCU\...\Run\DeskNook`）后立即退出，不启动界面（安装包用） |
| `--simulate-outdated-proxy` | 测试用：把 Explorer 内的菜单组件当作旧版，验证警告与托盘气泡 |

## 安装包

WiX v5 生成的 per-machine MSI：自带 .NET 运行时（self-contained，win-x64）、默认装到 `C:\Program Files\DeskNook`（需要管理员，会弹 UAC；安装界面可改目录）、界面为简体中文。WiX 通过 NuGet 还原，`dotnet build` 即可，不需要全局安装。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1   # 输出 artifacts\DeskNook-<版本>-x64.msi
```

版本号取自 `src/DeskNook/DeskNook.csproj` 的 `<Version>`；升级安装（MajorUpgrade）靠固定的 UpgradeCode，旧版会被替换，已安装更新版本时拒绝降级。

升级、修复、重装时 `AUTOSTART` 被忽略，保留原来的开机自启状态；要改，请在程序里设置。

安装选项（安装界面「选项」页，或命令行属性）：

| 属性 | 默认 | 作用 |
|---|---|---|
| `AUTOSTART` | 1 | 1 = 安装后写入开机自启（以安装用户身份运行 `DeskNook.exe --autostart=on`）；0 = 不写 |
| `DESKTOPSHORTCUT` | 1 | 1 = 创建桌面快捷方式；0 = 不创建（开始菜单快捷方式固定创建） |

完成页「运行桌面整理」默认勾选，经 `explorer.exe` 间接启动，保证程序以普通用户身份而非提权身份运行（静默安装不会启动程序）。

```powershell
msiexec /i DeskNook-1.0.0-x64.msi /qn                                   # 静默安装（两项默认开启）
msiexec /i DeskNook-1.0.0-x64.msi AUTOSTART=0 DESKTOPSHORTCUT=0 /qn     # 静默安装，不自启、无桌面快捷方式
msiexec /x DeskNook-1.0.0-x64.msi /qn                                   # 静默卸载
```

卸载 / 升级时会先执行 `DeskNook.exe --exit` 让运行中的实例退出（恢复系统桌面图标）；真正卸载时再执行 `--unregister`，删除右键菜单扩展注册和开机自启项，升级则保留。数据不随卸载删除：装在 Program Files 下时数据在 `%AppData%\DeskNook`，需要时手动删除。

## 发布

GitHub Actions（`.github/workflows/release.yml`）在 push main、PR、打 tag、手动触发时都会在 `windows-latest` 上测试并构建安装包与便携版，产物可在运行页的 Artifacts 下载（非 tag 构建的文件名带 `-ci.<运行号>` 后缀，文件版本沿用 csproj）。

发布新版本：

```powershell
git tag v1.0.1
git push origin v1.0.1
```

推送 `v*` tag 后，工作流会以 tag 版本（`v1.0.1` → `1.0.1`）覆盖 csproj 的 `<Version>` 构建，校验 `DeskNook.exe` 文件版本与 tag 一致，并创建 GitHub Release（自动生成更新说明）。tag 带 `-`（如 `v1.2.0-beta.1`）时标记为预发布，文件版本取 `-` 前的 `1.2.0`。Release 里的文件：

- `DeskNook-<版本>-x64.msi`：安装包（自带 .NET 运行时，per-machine，需要管理员）。
- `DeskNook-<版本>-x64-portable.zip`：便携版（self-contained win-x64 发布目录，不含 pdb 与 data），解压即用；Shell 扩展注册、开机自启等需由程序内设置完成，不会写入安装信息。

本地模拟：`tools/build-installer.ps1 -Version 1.0.1`（可加 `-NameSuffix -ci.1` 只改文件名）。

## 魔改入口：自定义右键菜单项

所有自定义菜单项都集中在 `src/DeskNook/Desktop/MenuExtensions.cs` 的 `Items` 列表。加一个项只需在列表里追加：

```csharp
new()
{
    Title = "复制完整路径(&P)", Position = MenuPosition.Bottom, InExplorer = true,   // InExplorer：资源管理器窗口里也出现
    Icon = I("organize"),                                                            // 可选，图标见 Assets/*.ico
    Applies = c => !c.IsBackground && c.SelectedPaths.Count == 1,                    // 适用条件
    Handler = c => System.Windows.Clipboard.SetText(c.SelectedPaths[0]),
},
```

`Children` 做子菜单，`IsSeparator` 做分隔线，`Checked` / `Enabled` / `DynamicTitle` 可按上下文动态计算；`VerbInterceptors` 可拦截 Shell 动词（如 `rename`、`delete`）。C++ 扩展只负责转发，不含业务逻辑。

## 数据位置

数据根目录（下称“数据根”）：程序位于 `C:\Program Files`、`C:\Program Files (x86)` 之下时为 `%AppData%\DeskNook`（该处程序目录通常不可写）；其余位置（便携版、解压/自行安装的目录）一律为程序目录下的 `data\`。

- 数据根下：`layout.json`（布局与隐藏状态）、`settings.json`（设置）、`organize-undo.json`、`logs\`（`desknook.log`、`shellext.log`）、`running.flag`（运行标记，异常退出时残留，下次启动记录日志）
- 数据根下：`shellext\`（按哈希命名的扩展 DLL 副本）、`icons\`（菜单图标）
- 旧版（曾用名 XkDesk / DeskNext，数据在 `%AppData%\DeskNext`、`%LocalAppData%\DeskNext`）不会自动迁移；需要旧布局时手动把 `%AppData%\DeskNext\layout.json` 等复制到新的数据根即可。首次启动会清理旧名称遗留的右键菜单注册与开机自启项（开机自启开着的会改写为 DeskNook）。

## 卸载（手动 / 便携版）

用安装包安装的直接在「设置 → 应用」里卸载即可（见上节）。便携版手动卸载：

1. 运行 `DeskNook.exe --exit` 退出程序（或托盘 → 退出）。
2. 运行 `DeskNook.exe --unregister` 删除 Shell 扩展注册项；托盘 / 设置里取消“开机自启”（或删除 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DeskNook`）。
3. 删除整个程序目录即可（若提示 `data\shellext` 里的 DLL 被占用，重启资源管理器后再删）；若程序装在 Program Files 下，数据在 `%AppData%\DeskNook`，一并手动删除。

## 已知限制

Explorer 里已经加载了旧版菜单组件时，新版本启动会尝试让旧代理退出并重新加载；若仍是旧版，DeskNook 会在日志里警告并在托盘弹一次气泡提示“已更新右键菜单组件，重启资源管理器后生效”（不会自动重启 Explorer）。

## 开发文档

开发者与 AI 协作者请从 [docs/README.md](docs/README.md) 开始：架构、右键菜单、格子、数据与配置、构建发布、测试与排障都有对应文档。
