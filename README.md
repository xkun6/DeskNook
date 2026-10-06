# DeskNext（桌面整理）

类腾讯桌面整理的 Windows 桌面工具（WPF / .NET 9 / x64）。程序接管桌面图标的绘制：文件仍留在桌面目录，不移动、不改属性；退出或崩溃后系统图标自动恢复。

## 功能

- **桌面格子**：新建格子、映射格子（内容对应任意目录）、拖动 / 缩放吸附网格、折叠 / 锁定 / 排序、图标可在格子与桌面之间拖动，布局持久化（多显示器各自独立）。
- **一键整理**：按分类规则（文件夹 / 快捷方式与程序 / 文档 / 图片 / 视频 / 音频 / 压缩包 / 其他，可在设置里编辑）自动建格子并归类，可撤销。
- **双击隐藏**：双击桌面空白处，所有图标与格子淡出；再双击淡入恢复。状态会保存，重启后保持。可在设置里关闭。
- **托盘常驻**：左键单击切换隐藏 / 显示；右键菜单：一键整理、撤销整理、新建格子、隐藏 / 显示桌面图标、设置、开机自启、退出。Explorer 重启后托盘图标自动重建。
- **原生右键菜单**：菜单由 Explorer 进程内的代理弹出（第三方扩展、图标、深色主题与资源管理器完全一致），DeskNext 的自定义项（整理至新格子、整理至新文件夹、作为桌面格子显示、桌面整理 子菜单等）作为 Shell 扩展注入。
- **设置**（托盘 → 设置，或再次启动程序）：常规（双击隐藏、开机自启、图标大小、格子透明度实时预览）、整理规则。
- **单实例**：再次启动会打开已运行实例的设置窗口。

## 构建

需要：.NET 9 SDK、Visual Studio 2022（含“使用 C++ 的桌面开发”，用于编译 `src/DeskNextShellExt`）。

```powershell
dotnet build DeskNext.sln -c Release      # 同时编译 C++ 扩展 DeskNextShellExt.dll
dotnet test  DeskNext.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1   # 发布到 dist/（依赖框架，win-x64）
```

`tools/publish.ps1 -SingleFile` 可把托管程序集合并为单个 exe（`DeskNextShellExt.dll` 仍在旁边）。发布产物需要目标机器安装 .NET 9 桌面运行时。

菜单与托盘用到的图标由 `tools/gen-icons.ps1` 生成（输出 `src/DeskNext/Assets/*.ico`，已提交）。

## 运行参数

| 参数 | 作用 |
|---|---|
| `--attach=owner` / `--attach=child` | 宿主窗口挂载方式（默认 owner） |
| `--transparency=dwm` / `--transparency=layered` | 透明方案（默认 dwm） |
| `--no-proxy` | 不使用 Explorer 内的菜单代理，右键菜单走进程内回退路径 |
| `--exit` | 通知已运行的实例退出 |
| `--unregister` | 删除 Shell 扩展的全部注册项后退出（卸载用） |
| `--simulate-outdated-proxy` | 测试用：把 Explorer 内的菜单组件当作旧版，验证警告与托盘气泡 |

## 魔改入口：自定义右键菜单项

所有自定义菜单项都集中在 `src/DeskNext/Desktop/MenuExtensions.cs` 的 `Items` 列表。加一个项只需在列表里追加：

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

数据根目录（下称“数据根”）：程序位于 `C:\Program Files`、`C:\Program Files (x86)` 之下时为 `%AppData%\DeskNext`（该处程序目录通常不可写）；其余位置（便携版、解压/自行安装的目录）一律为程序目录下的 `data\`。

- 数据根下：`layout.json`（布局与隐藏状态）、`settings.json`（设置）、`organize-undo.json`、`logs\`（`desknext.log`、`shellext.log`）、`running.flag`（运行标记，异常退出时残留，下次启动记录日志）
- 数据根下：`shellext\`（按哈希命名的扩展 DLL 副本）、`icons\`（菜单图标）
- 旧版（数据在 `%AppData%\DeskNext`、`%LocalAppData%\DeskNext`）不会自动迁移；需要旧布局时手动把 `%AppData%\DeskNext\layout.json` 等复制到新的数据根即可。

## 卸载

1. 运行 `DeskNext.exe --exit` 退出程序（或托盘 → 退出）。
2. 运行 `DeskNext.exe --unregister` 删除 Shell 扩展注册项；托盘 / 设置里取消“开机自启”（或删除 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DeskNext`）。
3. 删除整个程序目录即可（若提示 `data\shellext` 里的 DLL 被占用，重启资源管理器后再删）；若程序装在 Program Files 下，数据在 `%AppData%\DeskNext`，一并手动删除。

## 已知限制

Explorer 里已经加载了旧版菜单组件时，新版本启动会尝试让旧代理退出并重新加载；若仍是旧版，DeskNext 会在日志里警告并在托盘弹一次气泡提示“已更新右键菜单组件，重启资源管理器后生效”（不会自动重启 Explorer）。
