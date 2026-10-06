# DeskNook 开发文档

DeskNook（界面显示名「桌面整理」）是类腾讯桌面整理的 Windows 桌面工具：C# WPF（.NET 9）主程序接管桌面图标的绘制，C++ Shell 扩展 `DeskNookShellExt` 把右键菜单放进 `explorer.exe` 里弹出。文件始终留在桌面目录，程序只改“布局”，不移动、不改属性。

这套文档面向后续开发者和 AI 代理，目标是**不必重新通读代码**。原则：

- 以代码为准；每个论断尽量写成 `文件:符号` 引用，不写易变的行号。
- 只写“为什么这样做、在哪、怎么改、哪里有坑”，不复述代码。
- 路径一律用仓库相对路径或 `%AppData%` 之类的变量。

使用者向的说明（功能、构建、参数、安装包、发布）在仓库根 `README.md`，这里不重复。

## 文档索引

| 文档 | 内容 |
|---|---|
| [architecture.md](architecture.md) | 进程/线程模型、模块分层与目录、启动流程、退出与崩溃兜底、Explorer 重启与显示器变化重建 |
| [desktop-layer.md](desktop-layer.md) | 桌面层接管：窗口定位、隐藏系统图标、宿主窗口挂载与透明、Z 序、焦点、DPI、Win10/Win11 差异 |
| [desktop-items.md](desktop-items.md) | 桌面项：枚举、变化监听与 diff、图标缓存、首次导入位置、网格算法、选择/拖放/重命名/快捷键 |
| [menu.md](menu.md) | **最重要**：右键菜单 v2（Explorer 内代理 + Shell 扩展）的完整设计、协议、魔改指南、C++ 安全约束 |
| [boxes.md](boxes.md) | 格子（普通/映射）模型、交互、几何与吸附、对账不变量 |
| [organize.md](organize.md) | 一键整理与撤销、设置窗口、双击隐藏、托盘、开机自启 |
| [data-and-config.md](data-and-config.md) | 数据目录规则、全部文件格式、注册表项、命令行参数、历史改名与旧注册清理 |
| [build-release.md](build-release.md) | 本地构建、C++ 编译 Target、publish/installer 脚本、MSI 设计、CI 与发版 |
| [testing.md](testing.md) | 单元测试范围、tools/test 截图自动化框架、隔离规则、手测清单 |
| [troubleshooting.md](troubleshooting.md) | 已知问题与排障、踩过的坑 |

## 按任务找文档

| 我要做的事 | 先读 |
|---|---|
| 改右键菜单的行为 / 排查菜单缺项、缺图标、深色不对 | [menu.md](menu.md) |
| 加一个自定义右键菜单项 | [menu.md#魔改指南](menu.md#魔改指南) |
| 拦截某个 Shell 动词（如 delete） | [menu.md#动词拦截](menu.md#动词拦截) |
| 改 C++ 扩展/代理（跑在 Explorer 里） | [menu.md#崩溃安全约束](menu.md#崩溃安全约束) 先读完再动手 |
| 改格子的拖动/缩放/吸附/折叠/滚动 | [boxes.md](boxes.md) |
| 改图标选择、框选、拖放、重命名、快捷键 | [desktop-items.md](desktop-items.md) |
| 新增/修改一键整理规则、撤销逻辑 | [organize.md](organize.md) |
| 加一个设置项 | [organize.md#设置窗口](organize.md#设置窗口) + [data-and-config.md](data-and-config.md) |
| 数据存在哪、文件格式、改格式如何兼容旧文件 | [data-and-config.md](data-and-config.md) |
| 加命令行参数 | [data-and-config.md#命令行参数](data-and-config.md#命令行参数) |
| 桌面图标被隐藏后没恢复 / 退出异常 | [troubleshooting.md](troubleshooting.md) + [architecture.md#退出与崩溃兜底](architecture.md#退出与崩溃兜底) |
| Explorer 崩溃 / 重启后图标或菜单不见 | [troubleshooting.md](troubleshooting.md) + [menu.md](menu.md) |
| 宿主窗口盖住了别的窗口 / Win+D 后消失 / 多显示器错位 | [desktop-layer.md](desktop-layer.md) |
| 本地构建失败、exe 被占用、CI 找不到 VS | [build-release.md](build-release.md) + [troubleshooting.md](troubleshooting.md) |
| 发布新版本、改安装包行为 | [build-release.md](build-release.md) |
| 写/跑测试、截图自动化 | [testing.md](testing.md) |

## 术语

| 术语 | 含义 |
|---|---|
| 自由区 / 自由图标 | 不在任何普通格子里的桌面项，位置记录在 `layout.json` 的 `FreeIcons`（显示器 + 列 + 行） |
| 普通格子 `Normal` | 成员是桌面项 key 列表的格子，只改布局 |
| 映射格子 `Mapped` | 内容对应任意目录的格子，有独立的 `DesktopItemSource` |
| key | 桌面项唯一键：`IShellFolder.GetDisplayNameOf(SHGDN_FORPARSING)` 的解析名（文件路径或 `::{CLSID}`）；映射格子里的项前缀为 `格子Id + 0x1F` |
| 数据根 | `<exe 目录>\data`；程序在 Program Files 下时为 `%AppData%\DeskNook`（见 [data-and-config.md](data-and-config.md)） |
| 代理 / 菜单代理 | `DeskNookShellExt.dll` 加载进 `explorer.exe` 后，在桌面线程上建的隐藏窗口 `DeskNook.MenuProxy`，负责弹原生菜单 |
| 回退路径 / v1 | 进程内弹菜单 `ShellContextMenu`，代理不可用或 `--no-proxy` 时使用 |
