# AGENTS.md

## 项目定位

DeskNook（界面显示名「桌面整理」）：Windows 桌面整理工具。WPF（.NET 9）宿主接管桌面，隐藏系统桌面图标并自绘图标与格子；C++ 扩展 `DeskNookShellExt.dll` 作为 Shell 扩展并在 Explorer 内提供菜单代理，让第三方右键菜单项与原生一致。仓库目录名 `xk-desk` 是历史名称。

## 必读路由（先读 docs/，不要重新分析代码）

入口：docs/README.md（含“要改什么就读哪篇”的对照表）。

| 要做的事 | 必读 |
|---|---|
| 任何改动前 | docs/architecture.md |
| 改桌面挂载、Z 序、DPI、透明 | docs/desktop-layer.md |
| 改图标枚举、拖放、重命名、选择 | docs/desktop-items.md |
| 改右键菜单、ShellContextMenu、C++ 扩展 | docs/menu.md（必读） |
| 改格子、布局对账 | docs/boxes.md |
| 改一键整理、设置、托盘、自启 | docs/organize.md |
| 改 JSON、注册表、命令行参数 | docs/data-and-config.md |
| 构建、安装包、CI、发版 | docs/build-release.md |
| 写或跑测试 | docs/testing.md |
| 出问题 | docs/troubleshooting.md |

## 铁律

1. 动 `ShellContextMenu` 或菜单相关代码前，必须先读完 docs/menu.md。
2. 运行在 Explorer 里的 C++ 代码（`src/DeskNookShellExt`）要保证不拖垮 Explorer：异常不得越出边界（入口全部 `catch(...)`/`__try`），不跨线程持有 COM 指针，不在持锁时回调外部，通信必须有超时，日志不能阻塞。
3. 提交信息一律用简体中文。
4. 测试只创建、删除 `xk-test-` 前缀的桌面文件；测试前备份、结束后还原 `layout.json`、`settings.json`、`organize-undo.json` 与 `Run\DeskNook` 值，不碰用户真实文件。
5. 构建或测试前先退出运行中的实例：`src\DeskNook\bin\Release\net9.0-windows\DeskNook.exe --exit`，否则 exe 被锁。
6. 不改 MSI 的 UpgradeCode、CLSID、COPYDATA magic；新增参数、设置、注册项要同步更新 docs/data-and-config.md。
7. 文档里每条结论要对应 `文件:符号`；改了行为就同步改对应文档。
8. 不要在文档或提交里写本机隐私（用户名路径、邮箱等），路径用相对路径或 `%AppData%` 之类变量。

## 常用命令

- 构建：`dotnet build DeskNook.sln -c Release`（同时编译 C++，需 VS 的 C++ 工具集）
- 单测：`dotnet test DeskNook.sln -c Release`
- 安装包：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1`
