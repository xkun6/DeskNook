# 构建与发布

对应文件：`src/DeskNook/DeskNook.csproj`、`src/DeskNookShellExt/DeskNookShellExt.vcxproj`、`tools/publish.ps1`、`tools/build-installer.ps1`、`tools/gen-icons.ps1`、`installer/Package.wxs`、`installer/DeskNook.Installer.wixproj`、`.github/workflows/release.yml`、`.github/release-notes.md`。使用者向的命令示例在根 `README.md`，这里写原理与坑。

## 本地构建

前提：

- **.NET 9 SDK**；
- **Visual Studio 2022 或更新版本**，且带“使用 C++ 的桌面开发”工作负载（组件 `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`）+ Windows 10/11 SDK。用来编译 `src/DeskNookShellExt`；
- WiX 通过 NuGet 还原（`installer/DeskNook.Installer.wixproj` 的 `Sdk="WixToolset.Sdk/5.0.2"`），**不需要**全局安装。

```powershell
dotnet build DeskNook.sln -c Release      # 同时编译 C++ 扩展
dotnet test  DeskNook.sln -c Release
```

`DeskNook.sln` 只包含 `src/DeskNook` 和 `tests/DeskNook.Tests`，**不含**安装包项目和 C++ 项目（由 csproj 的 Target 间接编译；安装包由脚本单独 build）。

### csproj 如何编译 C++ 扩展（`BuildShellExt` Target）

`src/DeskNook/DeskNook.csproj` 里 `Target Name="BuildShellExt" BeforeTargets="BeforeBuild"`：

1. 用 `vswhere.exe`（`$(MSBuildProgramFiles32)\Microsoft Visual Studio\Installer\vswhere.exe`）找安装路径：**先** `-version "[17.0,18.0)"`（VS2022），**找不到再** `-latest -version "[17.0,)"`（VS2026 18.x 等），都要求带 VC 工具集。
2. 用该 VS 的 `MSBuild\Current\Bin\MSBuild.exe` 编译 `src/DeskNookShellExt/DeskNookShellExt.vcxproj`，固定 `-p:Configuration=Release -p:Platform=x64`（**即使主项目是 Debug 构建也编 Release 版 DLL**），输出到 `src/DeskNookShellExt/bin/Release/DeskNookShellExt.dll`。
3. 平台工具集在 vcxproj 里是 `$(DefaultPlatformToolset)`（不再写死 `v143`），随所选 VS 变化——这是 CI 的 runner 只有 VS2026 时的修复（提交 `46ffbdc`）。
4. 该 DLL 以 `<None Include=… Link="DeskNookShellExt.dll" CopyToOutputDirectory="PreserveNewest">` 复制到主程序输出目录，`dotnet publish` 也会带上。
5. 错误条件：**既找不到 VS 的 MSBuild，又没有已存在的 DLL** 才报错“未找到带 C++ 工具集的 Visual Studio”。若找不到 VS 但 DLL 已存在，**静默跳过编译，使用旧 DLL**——C++ 改动没生效时先检查这点。

vcxproj 要点：`DynamicLibrary`、x64、`/utf-8`、`/EHa`（`ExceptionHandling=Async`）、静态 CRT（Release `MultiThreaded`）、`.def` 导出 `DllGetClassObject`/`DllCanUnloadNow`（PRIVATE）与 `DnHookProc`、链接 `windowscodecs.lib` 等。

### 构建与运行时的常见冲突

- **运行中的 DeskNook 会锁住 `DeskNook.exe`**：构建/测试前先 `src\DeskNook\bin\Release\net9.0-windows\DeskNook.exe --exit`（AGENTS.md 里的铁律）。
- C++ DLL 不会被锁：Explorer 加载的是 `<数据根>\shellext\DeskNookShellExt.<hash8>.dll` 副本，不是输出目录里的那份（见 [menu.md](menu.md) 的 DLL 防锁）。
- 改了 C++ 但 Explorer 里还是旧行为：旧 DLL 被钉在 Explorer 进程里，需要重启 Explorer（[troubleshooting.md](troubleshooting.md)）。

### 菜单图标

`tools/gen-icons.ps1`（`System.Drawing`）生成 `src/DeskNook/Assets/*.ico`（多尺寸 PNG 帧）；产物已提交。csproj 把 `Assets/*.ico` 作为嵌入资源（`LogicalName=Assets.<文件名>`），运行时释放到 `<数据根>\icons`。新增菜单图标：改脚本生成 → 提交 ICO → `MenuExtensions` 里 `I("名字")`。

## `tools/publish.ps1`

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 [-SingleFile] [-SelfContained] [-Output <目录>] [-Version <x.y.z>]
```

| 参数 | 作用 |
|---|---|
| `-SingleFile` | `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`；托管程序集合并为单个 exe，`DeskNookShellExt.dll` 仍作为独立文件在旁边 |
| `-SelfContained` | 自带 .NET 运行时（安装包与便携版使用；默认依赖框架，目标机器需装 .NET 9 桌面运行时） |
| `-Output` | 输出目录，默认 `dist/`；相对路径按仓库根解析；先清空 |
| `-Version` | 覆盖 csproj 的 `<Version>`（CI 在 tag 构建时传入） |

固定 `win-x64`、Release；发布后删除可能出现的 `data/`，并检查 `DeskNook.exe`、`DeskNookShellExt.dll` 存在。

## `tools/build-installer.ps1`

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1 [-Version x.y.z] [-NameSuffix -ci.1]
```

一次构建两种变体、共 4 个产物；`tools/build-installer.ps1:Build-Variant` 对每个变体各跑一遍。先确定版本号（`-Version` 或 csproj 的 `<Version>`），再依次：

| 变体 | 发布 | 发布目录 | 自检（`Build-Variant`） | 文件标签 |
|---|---|---|---|---|
| 自带运行时 | `publish.ps1 -SelfContained -Output artifacts\publish` | `artifacts/publish`（CI 的“校验 exe 文件版本”读这里，名字不能变） | 必须有 `DeskNook.exe`、`DeskNookShellExt.dll`、`coreclr.dll` | `''` |
| 不带运行时 | `publish.ps1 -Output artifacts\publish-noruntime`（依赖框架） | `artifacts/publish-noruntime` | 必须有 `DeskNook.exe`、`DeskNookShellExt.dll`、`DeskNook.runtimeconfig.json`，**不得有** `coreclr.dll` | `-noruntime` |

两者都不得有 `data/`。每个变体的后续步骤：

1. `dotnet build installer\DeskNook.Installer.wixproj -c Release -nologo --no-incremental -p:ProductVersion=… -p:AppPublishDir=… -p:NoRuntime=<true|false> -p:OutputName=DeskNook-<版本><后缀>-x64<标签>`。**必须 `--no-incremental`**：两次构建共用 `installer/obj`，只改全局属性时增量构建可能复用上一次的中间文件（文件清单错误）。同时脚本先删除 `installer/obj`：否则 MSBuild 的 IncrementalClean 会按上一次的 `FileListAbsolute` 删掉上一个变体已生成的 msi/wixpdb（实测第二个变体构建后第一个 MSI 消失）。`OutputName` 作为全局属性直接得到最终文件名，不再有“构建后改名”；
2. 校验 `DeskNook.exe` 的**文件版本**与期望版本一致（MSI 的 `Version` 取自 exe 文件版本），确认 MSI 存在，打印大小；
3. 便携版 `artifacts\DeskNook-<版本><后缀>-x64<标签>-portable.zip`：对应发布目录（排除 `*.pdb`、不含 `data/`）压缩。

产物（`<后缀>` 即 `-NameSuffix`，如 `-ci.12`）：

- `DeskNook-<版本><后缀>-x64.msi`、`DeskNook-<版本><后缀>-x64-portable.zip`（自带运行时）；
- `DeskNook-<版本><后缀>-x64-noruntime.msi`、`DeskNook-<版本><后缀>-x64-noruntime-portable.zip`（依赖框架，需 .NET 9 桌面运行时）。

`artifacts/`、`dist/` 都被 `.gitignore` 排除。

## MSI 设计（`installer/Package.wxs`）

- `Package`：`Name="桌面整理"`、`Manufacturer="DeskNook"`、`Version="!(bind.FileVersion.DeskNook.exe)"`、`Language=2052`、`Scope=perMachine`、**`UpgradeCode="E1003CA2-7DC8-4599-8EA1-FD0D1122559E"`（永不改，改了就无法升级旧安装）**。
- `MajorUpgrade DowngradeErrorMessage="已安装更新版本，无法安装此版本。" AllowSameVersionUpgrades="yes"`：升级靠固定 UpgradeCode，旧版被替换，拒绝降级。`AllowSameVersionUpgrades` 的原因：同一版本的“自带运行时/不带运行时”两种 MSI 的 ProductCode 不同，允许互相升级替换，避免并存两份安装记录。
- 变体开关 `NoRuntime`：`installer/DeskNook.Installer.wixproj:NoRuntime`（默认 `false`）经 `DefineConstants` 传成 WiX 预处理变量 `$(NoRuntime)`。`Package.wxs` 里 `<?if $(NoRuntime) = "true" ?>` 内放 `netfx:DotNetCompatibilityCheck Property="DOTNETDESKTOP9" RuntimeType="desktop" Platform="x64" Version="9.0.0" RollForward="minor"`（与应用默认的 Minor 前滚策略一致）和 `Launch Condition="Installed OR DOTNETDESKTOP9 = "0""`：未安装 .NET 9 桌面运行时则提示下载链接并停止安装（已安装时跳过检查，卸载不受影响）。需要 `WixToolset.Netfx.wixext`，根元素声明 `xmlns:netfx`。
- `MediaTemplate EmbedCab=yes`；ARP：图标 `app.ico`，`ARPNOMODIFY=1`（只有“卸载”）。
- 目录：`ProgramFiles64Folder\DeskNook`（`INSTALLFOLDER`，界面可改目录）。`DeskNook.exe` 单独成 Component（`Bitness=always64`，`KeyPath`），因为快捷方式与自定义动作都引用它；其余用 `<Files Include="$(PublishDir)**">` 通配收集，排除 exe 与 `*.pdb`。**data 目录不在安装包里**，卸载也不删数据。
- 快捷方式：开始菜单（固定创建，all users）；桌面快捷方式受 `DESKTOPSHORTCUT=1` 控制。两个组件的 KeyPath 是 `HKLM\Software\DeskNook` 下的 DWORD（见 ICE 说明）。
- `Feature Main`：`AllowAbsent=no`，包含上述全部组件。
- 属性：`AUTOSTART=1`、`DESKTOPSHORTCUT=1`（`Secure=yes`，命令行可覆盖，如 `AUTOSTART=0`）。

### 自定义动作与时序

HKCU 属于用户而不是 per-machine 的安装服务，所以开机自启和 Shell 扩展注册的增删都**交给 exe 自己改写**，以安装用户身份运行（`Execute="deferred" Impersonate="yes"`），全部 `Return="ignore"`：失败不拦截安装/卸载。

| 动作 | 命令 | 排程位置（`InstallExecuteSequence`） | 条件 |
|---|---|---|---|
| `DeskNook.AutostartOn` | `DeskNook.exe --autostart=on` | `After="InstallFiles"` | `AUTOSTART=1 AND NOT Installed AND NOT WIX_UPGRADE_DETECTED AND NOT (REMOVE="ALL")`——**只有全新安装才写自启**；升级/修复/重装一律不碰，保留用户原设置 |
| `DeskNook.Exit` | `DeskNook.exe --exit` | `Before="DeskNook.Unregister"` | `REMOVE="ALL"`（真正卸载，以及 MajorUpgrade 移除旧版时）——让运行中的实例退出并恢复系统图标，再删文件 |
| `DeskNook.Unregister` | `DeskNook.exe --unregister` | `Before="RemoveFiles"` | `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`——**只在真正卸载**时删除 Shell 扩展注册与自启值；升级保留 |
| `LaunchApplication` | `explorer.exe "[#DeskNook.exe]"` | UI：ExitDialog 的 Finish 按钮 `DoAction` | `WIXUI_EXITDIALOGOPTIONALCHECKBOX = 1 AND NOT Installed`，`Return=asyncNoWait`：经 `explorer.exe` 间接启动，使程序以普通用户而非提权身份运行（静默安装不会启动程序） |

兜底：`util:CloseApplication`（`CloseMessage=yes`、`EndSessionMessage=yes`、`RebootPrompt=no`）在 exe 仍被占用时请求关闭，配合 Restart Manager，不强杀。

**为什么 `--exit` 用 `ExitSignal` 而不是窗口消息**：deferred 自定义动作跑在 Windows Installer 服务里，即使 `Impersonate="yes"` 也处于会话 0，`FindWindow/PostMessage` 找不到用户桌面会话里的窗口。命名内核对象 `Global\DeskNook.Exit.<SID>` 不受会话隔离影响（事件名带用户 SID，同一用户的模拟令牌能打开），所以 `--exit` 先走事件。`--exit` 还会 `WaitOthersExit(8000)`，等实例退净再返回，卸载才不会遇到 exe 被占用。

### 界面

自定义 UI（`UI Id="DeskNookUI"`，引用 `WixUI_Common` 与若干对话框）：欢迎 → 选择目录 → **选项页**（自写 `OptionsDlg`）→ 确认安装；没有许可协议页（项目没有 EULA）；完成页带“运行桌面整理”复选框。

选项页的自启复选框在升级时用 `HideCondition="WIX_UPGRADE_DETECTED"` 隐藏，并用一个 `Hidden="yes" ShowCondition="WIX_UPGRADE_DETECTED"` 的文本替代，提示“升级安装将保留当前的开机自启设置”（WiX v5 里 `Control` 用 `HideCondition/ShowCondition` 属性控制显示）。

### 被屏蔽的 ICE

`installer/DeskNook.Installer.wixproj`：`<SuppressIces>ICE38;ICE43;ICE57;ICE61</SuppressIces>`。ICE38/43/57 的原因（项目文件注释）：快捷方式放在 `ProgramMenuFolder`/`DesktopFolder`（per-machine 下解析为 All Users 目录），ICE 仍按“用户配置文件目录”的要求要 HKCU 键值，属于已知误报；组件键值故意用 HKLM 以与 per-machine 安装一致。ICE61：`MajorUpgrade` 开了 `AllowSameVersionUpgrades`（同版本的两种变体可互相覆盖），ICE61 对此只给警告，故屏蔽。

其他：`Cultures=zh-CN`；`PublishDir` 经 `DefineConstants` 传给 WiX（`$(PublishDir)`）；默认输出名 `DeskNook-$(ProductVersion)-x64`（`build-installer.ps1` 用 `-p:OutputName=` 覆盖）；扩展 `WixToolset.UI.wixext`、`WixToolset.Util.wixext`、`WixToolset.Netfx.wixext`（5.0.2）。

### 安装行为速查

| 场景 | 结果 |
|---|---|
| 全新安装 | 文件到 `C:\Program Files\DeskNook`；按选项写自启、建快捷方式；数据在 `%AppData%\DeskNook` |
| 升级 | 先 `--exit` 旧实例，换文件；**不动**自启与注册；新 DLL 在 Explorer 里不会立即生效（需重启 Explorer，程序会弹气泡） |
| 卸载 | `--exit` → `--unregister`（删注册与自启）→ 删文件；数据保留 |
| 静默 | `msiexec /i … /qn [AUTOSTART=0 DESKTOPSHORTCUT=0]`；静默安装不会启动程序 |

## CI（`.github/workflows/release.yml`）

- 触发：`push` 到 `main`、推送 `v*` tag、所有 `pull_request`、`workflow_dispatch`。
- 单个 job `build`，`windows-latest`，该 job 有 `contents: write`（创建 Release 要用）。
- 步骤：
  1. checkout、`setup-dotnet`（9.0.x）；
  2. **解析版本号**：
     - tag 构建（`refs/tags/v*`）：版本 = tag 去掉 `v` 后取 `-` 之前的核心部分，必须匹配 `^\d+\.\d+\.\d+$` 否则失败（`v1.2.0-beta.1` → `1.2.0`）；传给构建的 `-Version` 就是它，**无文件名后缀**。
     - 非 tag 构建：版本沿用 csproj 的 `<Version>`，不传 `-Version`，文件名加 `-ci.<run_number>` 后缀（文件版本不变）。
  3. **测试**：`dotnet test DeskNook.sln -c Release --filter "Category!=Desktop"`（会同时编译 C++，runner 需要 VS 的 C++ 工具集）；失败时把含 `error|Failed|[FAIL]|Exception` 的行以 `::error::` 注解输出。当前没有任何测试带 `Category=Desktop` 特性，过滤是预留的（在 CI 里跑不了依赖真实桌面的测试时再标）。
  4. **构建安装包与便携版**：`tools/build-installer.ps1 [-Version] [-NameSuffix]`（一次产出自带/不带运行时各一个 MSI 与便携版，共 4 个文件），失败同样输出错误注解。
  5. tag 构建额外**校验 `DeskNook.exe` 文件版本**必须匹配 tag 版本（`(\.0)?` 容忍四段式）。
  6. `upload-artifact`：名称 `DeskNook-<版本><后缀>`，包含 `artifacts/*.msi` 与 `artifacts/*-portable.zip`（通配正好匹配上述 4 个文件），缺文件则失败。
  7. tag 构建先“生成发布说明”：读取 `.github/release-notes.md` 模板，把 `{{VERSION}}` 替换为版本号，写成 `release-notes.md`（UTF-8 无 BOM）；再用 `softprops/action-gh-release` 创建 Release：名称 `桌面整理 v<版本>`，`body_path: release-notes.md`（固定的下载选择说明在前）加 `generate_release_notes: true`（自动生成的更新日志追加在后）；**tag 含 `-` 则 `prerelease: true`**；附件为 4 个文件（两个 MSI 与两个便携版 zip）。
- 注意：带后缀的预发布 tag（如 `v1.2.0-beta.1`）产出的 MSI/exe 版本是 `1.2.0`，文件名里不含 `beta`，区分靠 Release 页面的预发布标记。
- 公开仓库的 `push main` 与 PR 也会产出可下载的 Artifacts（不会创建 Release）。

## 发版步骤

1. （推荐）改 `src/DeskNook/DeskNook.csproj` 的 `<Version>` 为新版本，使本地构建与 CI 的非 tag 构建版本一致；提交，提交信息用简体中文。
2. `git push`，等 `main` 上的 CI 全绿（测试 + 安装包构建）。
3. 打 tag 并推送：
   ```powershell
   git tag v1.0.1
   git push origin v1.0.1
   ```
4. 等工作流跑完，检查 Release 页面：4 个文件（两个 MSI、两个 portable zip）都在、顶部有下载选择说明、预发布标记正确、`DeskNook.exe` 文件版本正确。
5. 手测安装/升级/卸载（见 [testing.md](testing.md) 手测清单）：MSI 在 CI 里只验证“能构建”，没有自动安装测试。

本地模拟 CI：`tools/build-installer.ps1 -Version 1.0.1 -NameSuffix -ci.1`。
