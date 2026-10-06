# 右键菜单（v2：在 Explorer 进程内弹出）

这是整个项目最复杂、风险最高的部分：一部分代码（`DeskNookShellExt.dll`）直接跑在用户的 `explorer.exe` 里，写错会让用户的桌面和任务栏崩溃。改这里之前请通读本文，尤其是 [崩溃安全约束](#崩溃安全约束)。

对应代码：

| 角色 | 文件 |
|---|---|
| 魔改入口、上下文、动词拦截表 | `src/DeskNook/Desktop/MenuExtensions.cs` |
| 协议 DTO 与 JSON | `src/DeskNook/Services/MenuProtocol.cs` |
| DeskNook 端代理管理 | `src/DeskNook/Desktop/ExplorerMenuProxy.cs` |
| 管道服务端 | `src/DeskNook/Desktop/MenuPipeServer.cs` |
| 注册表/DLL 副本 | `src/DeskNook/Desktop/ShellExtRegistrar.cs` |
| v1 进程内回退 | `src/DeskNook/Desktop/ShellContextMenu.cs`、`src/DeskNook/Desktop/BoxMenu.cs` |
| 菜单图标释放 | `src/DeskNook/Services/MenuIcons.cs` |
| C++：入口/导出 | `src/DeskNookShellExt/main.cpp`、`desknook.h`、`DeskNookShellExt.def` |
| C++：Shell 扩展 | `src/DeskNookShellExt/ext.cpp` |
| C++：菜单代理 | `src/DeskNookShellExt/proxy.cpp` |
| C++：日志/JSON/管道/位图 | `src/DeskNookShellExt/util.cpp` |

## 为什么最终在 explorer.exe 里弹菜单

目标：右键菜单要和资源管理器的原生桌面菜单**完全一致**（第三方扩展、图标、深色主题），并且像腾讯桌面整理那样把自己的项（整理至新格子、整理至新文件夹、作为桌面格子显示…）注入原生菜单。

v1（现为回退路径）在 DeskNook 进程里自己向 Shell 要菜单对象（`GetUIObjectOf` / `CreateViewObject` → `IContextMenu`）并在本进程弹出。实测发现：夸克、百度网盘、NVIDIA 等扩展的 DLL 里**写死了只在 `explorer.exe` 中工作**，在 DeskNook 进程里取菜单时这些扩展缺项或缺图标。因此用户拍板（2026-10-06）改为 v2：**所有桌面右键菜单都在 Explorer 进程内由代理弹出**，自定义项作为普通 Shell 扩展 handler 出现在菜单里。

| | v1 进程内（`ShellContextMenu`） | v2 代理（`ExplorerMenuProxy` + `proxy.cpp`） |
|---|---|---|
| 弹菜单的进程 | DeskNook.exe | explorer.exe（桌面线程） |
| 第三方扩展（夸克/百度/NVIDIA…） | 部分缺项/缺图标 | 与原生一致 |
| 深色主题 | 手动 `SetPreferredAppMode`：背景菜单深色、图标菜单强制浅色以模拟原生 | 随 Explorer 进程自身 |
| 桌面背景里 DefView 自带项（查看/排序/刷新/粘贴/撤消） | 拿不到，由 `MenuExtensions` 里 `FallbackOnly` 项补上 | 真实 DefView 菜单自带 |
| 自定义项来源 | 直接 `InsertMenuItem`（命令 ID ≥ `0x8000`） | Shell 扩展经管道查询 DeskNook，再由扩展插入 |
| 资源管理器普通窗口里右键 | 无 | 同一个扩展也出现（`InExplorer=true` 的项） |
| 状态 | **回退路径**：代理不可用、`--no-proxy`、代理 1s 无响应、代理报 `nocm` 等错误时使用；另外 `InvokeVerb/InvokeDefault` 仍用它做 open/delete/copy/cut/paste/properties | 默认 |

> 改 `ShellContextMenu.cs` 之前请先读完本文：它不只是“旧代码”，删除/复制/粘贴/双击打开/重命名外的所有 Shell 动词执行（`InvokeVerb`、`InvokeDefault`）仍依赖它，且回退菜单的位置逻辑与代理路径不同。

## DeskNookShellExt 的两个角色

同一个 DLL（`{EA0A2ED4-03C2-402D-A461-E558E4D20973}`）：

1. **Shell 扩展**（`ext.cpp:DnExt`，实现 `IShellExtInit` + `IContextMenu`）：注册在 HKCU 的三个 `ContextMenuHandlers` 下，**任何**弹出文件/文件夹/背景右键菜单的进程（Explorer 窗口、对话框、第三方文件管理器…）都会加载它。`QueryContextMenu` 时把上下文经管道发给 DeskNook（200ms 超时，DeskNook 没运行/超时就不加任何项），拿回要插入的项；用户点击后 `InvokeCommand` 再经管道通知 DeskNook 执行。
2. **菜单代理**（`proxy.cpp`）：DLL 一旦进入“拥有桌面的那个 explorer.exe”，就在 Explorer 的**桌面（DefView）线程**上建隐藏窗口 `DeskNook.MenuProxy`。DeskNook 用 `WM_COPYDATA` 发请求，代理在 Explorer 进程里取真实的 `IContextMenu`、`TrackPopupMenuEx` 弹出、转发菜单消息、执行命令，并经管道回报事件。

代理为什么必须在桌面线程：`IShellView` 属于桌面线程的 COM 套间，别的线程拿到的是代理对象，`GetItemObject(SVGIO_BACKGROUND)` 会失败（`0x80040155`）。窗口建在桌面线程上，则菜单创建、`GetItemObject`、`InvokeCommand` 全在该线程内，与原生桌面右键完全一致。

## 加载进 Explorer 的方式

`ExplorerMenuProxy.EnsureLoadedCore`（后台 STA 线程，幂等，`_ensuring` 防重入）：

1. 如果 `FindWindow("DeskNook.MenuProxy")` 已存在：窗口标题就是它所属 DLL 的**文件名**（含内容哈希）；与当前注册的 DLL 文件名一致 → 就绪；不一致 → 发 `{"t":"quit"}` 让旧代理退出（最多等 1.5s）后重新加载。
2. **方式一 `shellview`**（先尝试）：DeskNook 跨进程 `IShellWindows` → `IShellView.GetItemObject(SVGIO_BACKGROUND, IContextMenu)` + `QueryContextMenu`，想借此让 Explorer 自己实例化我们的 handler，handler 的 `DllGetClassObject` 里 `EnsureProxy()` 建代理。**实测不可行**：`GetItemObject` 返回 `0x80040155`（跨线程 COM 代理），日志里是 `shellview：GetItemObject(BACKGROUND) 失败`。代码仍保留这个尝试（`TryLoadViaShellView`，等 2.5s），失败后立即走方式二；所以代理尚未加载（如每次 Explorer 重启后首次）时，日志里会先看到这条失败再看到 hook 成功。
3. **方式二 `hook`**（实际生效）：`TryLoadViaHook`：
   - DeskNook 自己 `LoadLibraryW(已注册的 DLL 副本)`，`GetProcAddress("DnHookProc")`；
   - `SetWindowsHookEx(WH_GETMESSAGE, DnHookProc, hmod, 桌面 DefView 线程 id)`：Windows 会把这个 DLL 注入 Explorer 并在下一条消息时回调 `DnHookProc`；
   - 反复向 DefView `PostMessage(WM_NULL)`（最多 20 次、每次 100ms）直到代理窗口出现；
   - `finally` 里 `UnhookWindowsHookEx` + `FreeLibrary`（DeskNook 自己的那份）。
   - Explorer 里：`DnHookProc`（`main.cpp`）在 `g_isExplorer && !g_proxyAlive` 时调用 `OnDesktopThreadHook()` → `CreateProxyWindowHere()`，**建窗口前先 `GetModuleHandleEx(PIN)` 把 DLL 钉死**。

   必须 PIN 的原因：窗口过程在本 DLL 里；DeskNook 摘钩子后，Windows 会把 DLL 从 Explorer 里卸掉，不 PIN 就会在下一条消息时崩溃。`DllCanUnloadNow` 在代理存活时也返回 `S_FALSE`。
4. 两种都失败：`LoadMethod="失败"`，菜单一律走回退路径。

DLL 内部还有三条建窗口的路径（`proxy.cpp:EnsureProxy`），按优先级：当前线程就是桌面线程 → 直接建；否则 `StartOnDesktopThread`（DLL 自己对桌面线程 `SetWindowsHookEx(WH_GETMESSAGE, SelfHookProc)`，`PostThreadMessage(WM_NULL)` 触发，最多等约 1s）；再不行才开专用 STA 线程（`ProxyThread`，这时背景菜单取不到真实 DefView，改走 `CreateViewObject`）。`EnsureProxy` 只在“拥有 Progman 的那个 shell 进程”里生效（`FindDesktopThread` 的 PID == 当前 PID），独立进程的文件夹窗口不建代理。

何时加载：`App.OnStartup`（`EnsureRegistered` 成功后）、`StartReattach` 成功后（Explorer 重启后新进程里没有代理了）、`TryShow` 发现代理窗口不存在时（后台重新加载，本次先走回退菜单）。

## 版本不一致检测与气泡

Explorer 里的 DLL 一旦加载就被 PIN，**升级后新 DLL 不会替换已加载的旧 DLL**。`CheckVersion`：代理窗口标题 ≠ 当前注册的 DLL 文件名（`IsOutdated`，单测 `ProxyVersionTests`）→ 日志写“警告：Explorer 内的菜单组件是旧版…重启资源管理器后生效”，并触发一次 `ComponentOutdated` 事件（每个进程生命周期只报一次），`App` 弹托盘气泡“已更新右键菜单组件，重启资源管理器后生效”。不会自动重启 Explorer。`--simulate-outdated-proxy` 可模拟（验证日志与气泡）。

新旧代理的关系：新版启动时会尝试 `quit` 旧代理再加载新 DLL（新旧文件名不同，两份都在 Explorer 里，旧的被钉住不会卸载）；但已被 COM 缓存的 **Shell 扩展 handler** 仍是旧 DLL，直到 Explorer 重启。所以协议变更后“资源管理器窗口里的右键菜单缺新项/行为旧”需要重启 Explorer。

## 注册

`ShellExtRegistrar.EnsureRegistered()`（每次启动，幂等）：

1. 把 `AppContext.BaseDirectory\DeskNookShellExt.dll` 按内容 **SHA-256 前 8 位**复制为 `<数据根>\shellext\DeskNookShellExt.<hash8>.dll`（已存在不覆盖）。**目的是 DLL 防锁**：Explorer 会一直占用已加载的 DLL，重新编译/覆盖输出目录里的 DLL 会失败；DeskNook 注册的永远是副本。（夸克也是这样带时间戳命名。）
2. 清理旧名（XkDesk、DeskNext）遗留注册；写入当前注册；`CleanOld` 删除其他哈希的旧副本（被 Explorer 占用的删不掉，下次再清）。
3. 有改动时 `SHChangeNotify(SHCNE_ASSOCCHANGED)`。
4. `RegisteredDll` 记录路径，供加载代理、版本检测使用。

注册位置（HKCU，不需要管理员）：

| 键 | 值 |
|---|---|
| `HKCU\Software\Classes\CLSID\{EA0A2ED4-03C2-402D-A461-E558E4D20973}` | 默认值 `DeskNook Context Menu` |
| `…\CLSID\{…}\InprocServer32` | 默认值 = DLL 副本路径；`ThreadingModel=Apartment` |
| `HKCU\Software\Classes\*\shellex\ContextMenuHandlers\DeskNook` | 默认值 = CLSID |
| `HKCU\Software\Classes\Directory\shellex\ContextMenuHandlers\DeskNook` | 同上 |
| `HKCU\Software\Classes\Directory\Background\shellex\ContextMenuHandlers\DeskNook` | 同上（桌面空白处也走它，桌面本身是目录背景） |

`--unregister`（卸载）删除上述键 + 旧名键 + 开机自启值 + 全部 DLL 副本。CLSID 同时写在 `ShellExtRegistrar.ClsidText` 与 `desknook.h`，**两处必须一致**。

## 完整时序

```
用户右键 (桌面图标 / 空白处 / 格子)
  |
  v
DesktopSurface 右键 -> DesktopController.ShowMenu / ShowBoxMenu            [DeskNook UI 线程]
  | 构造 MenuContext(Items/Box/ScreenPoint/Shift/Source)
  | ExplorerMenuProxy.TryShow(ctx, ProxyRequest)
  |   - 代理窗口不存在 -> 返回 false -> 回退 ShellContextMenu.Show，并后台 EnsureLoadedAsync
  |   - req.Id="rN"; _pending[rN]=ctx; req.interceptVerbs=VerbInterceptors.Keys; iconsVisible=...
  |   - AllowSetForegroundWindow(Explorer PID)
  |   - Task.Run: SendMessageTimeout(代理窗口, WM_COPYDATA, 1000ms, SMTO_ABORTIFHUNG)
  |       超时/失败 -> UI 线程 _pending.Remove(rN) + 回退 ShellContextMenu.Show(ctx)
  |   - 立即返回(不阻塞 UI 线程)
  |
  +----------------------------- WM_COPYDATA ----------------------------+
                                                                          v
 [explorer.exe 桌面线程] 代理窗口过程
   校验: dwData==0x4B584D31, cbData<=1MB, 发送方 exe 名==desknook.exe
   复制 JSON -> PostMessage(WM_DN_REQ) -> 立即 return TRUE (SendMessage 随即返回)
   WM_DN_REQ -> HandleRequest(忙则回报 closed{error:"busy"}) -> RunMenu:
     1. 取 IContextMenu:
        桌面项  -> 桌面根 EnumObjects 按解析名匹配 -> GetUIObjectOf
        目录项  -> SHParseDisplayName -> 父文件夹 GetUIObjectOf
        桌面背景-> 真实 DefView.GetItemObject(SVGIO_BACKGROUND) (失败退 CreateViewObject)
        目录背景-> 该目录 IShellFolder.CreateViewObject
        取不到 -> 回报 closed{error:"nocm"} -> DeskNook 回退菜单
     2. (非 DefView 背景时) IObjectWithSite.SetSite(DefView)
     3. QueryContextMenu(hmenu,0,1,0x7FFF, 0x20420 背景 / 0x20490 图标 [+Shift: CMF_EXTENDEDVERBS])
        |-> Shell 实例化已注册的 handler DnExt (及夸克/百度等其他 handler)
        |-> DnExt.Initialize(pdtobj/pidlFolder) 记录选中项解析名或所在文件夹
        |-> DnExt.QueryContextMenu: 读取线程局部的当前请求 Id rN
        |       -- 命名管道 {"t":"query","kind","folder","items","shift","proc","pid","req":"rN"} (<=200ms) -->
        |                                                         [DeskNook 管道线程 -> UI 线程]
        |                                  Evaluate: ctx=_proxy.ContextOf(rN) ; MenuExtensions.Evaluate (<=150ms)
        |       <-- {"q":N,"items":[WireMenuItem...]} ----------------------
        |-> DnExt 按 pos 把自定义项插入 hmenu (图标走 WIC -> 32bpp PARGB)
     4. FixIconsItem(“显示桌面图标”勾选 = DeskNook 真实状态)；RecordTitles(记下 id->标题)
     5. TrackPopupMenuEx(TPM_RETURNCMD) 模态; WM_INITMENUPOPUP/DRAWITEM/MEASUREITEM/MENUCHAR/MENUSELECT 转发给 IContextMenu2/3
     6. 选择/取消返回:
        -- 事件 closed{picked}  --> DeskNook: MenuClosed
        cmd!=0: GetCommandString(GCS_VERBW) 取动词; 找标题与父菜单标题
        -- 事件 pick{verb,title,parent} --> DeskNook: “新建”子菜单 -> ExpectNewItem; 粘贴 -> ArmUndo
        拦截判断:
          标题=="显示桌面图标"/"Show desktop icons" -> 事件 verb{showdesktopicons} --> DeskNook SetIconsVisible(!)   (不再 Invoke)
          动词 ∈ interceptVerbs                      -> 事件 verb{<动词>}          --> DeskNook VerbInterceptors[动词](ctx) (不再 Invoke)
          否则 cm->InvokeCommand(CMINVOKECOMMANDINFOEX, ptInvoke/Shift/Ctrl/工作目录=桌面)
             - 原生命令: Shell 自己执行
             - 我们的命令: DnExt.InvokeCommand -- 管道 {"t":"invoke","q":N,"id":k,"req":"rN"} (<=500ms) -->
                          DeskNook: dispatcher.BeginInvoke(RunInvoke) 立即应答 {"ok":true}; UI 线程稍后执行 Handler
          -- 事件 invoked{verb,title,parent,defView,hr} --> DeskNook OnNativeInvoked: 排序/查看/刷新同步到 DeskNook
     7. 释放 IContextMenu/菜单/站点
```

要点：
- 每一步“进 DeskNook 的管道调用”都是**一行 JSON 请求 + 一行 JSON 应答**，服务端读行上限 4MB，单连接总超时 1.5s。
- DeskNook 对 `closed/pick/verb/invoked` 全部 `BeginInvoke` 到 UI 线程后立即应答，不让 Explorer 等 UI 线程。
- “代理一次只处理一个请求”（`g_busy`）；回报 `busy` 时 DeskNook 只记日志，不回退（避免双弹）。

## 协议

以 `src/DeskNook/Services/MenuProtocol.cs` 为准；C++ 侧是手写极简 JSON 解析（`util.cpp:JsonParse`），**字段名两边必须逐字一致**。单测 `tests/DeskNook.Tests/MenuProtocolTests.cs` 里有“字段名与 C++ 侧约定一致”的用例，改协议先改测试。

常量（`MenuProtocol` ↔ `desknook.h` / `proxy.cpp`，两处同步）：

| 常量 | 值 |
|---|---|
| 管道名 | `\\.\pipe\DeskNook.Menu`（`MenuProtocol.PipeName` ↔ `DN_PIPE_NAME`） |
| 代理窗口类 | `DeskNook.MenuProxy`（`ProxyWindowClass` ↔ `DN_PROXY_CLASS`） |
| COPYDATA magic | `0x4B584D31`（`CopyDataMagic` ↔ `kCopyDataMagic`；历史上的 "KXM1"，别改） |

### 通道 1：DeskNook → 代理（`WM_COPYDATA`，UTF-8 JSON，`dwData=magic`）

`ProxyRequest`（`{"t":"quit"}` 为退出指令，其余视为菜单请求）：

| 字段 | 类型 | 含义 |
|---|---|---|
| `t` | string | `menu`（默认）/ `quit` |
| `id` | string | 请求 Id（`r1`、`r2`…），后续所有事件与查询都带它（`req`） |
| `kind` | string | `item` / `background` |
| `folder` | string | 所在文件夹解析名；桌面为 `::desktop`（空也视为桌面）；映射格子为目录路径 |
| `items` | string[] | 选中项：桌面项为 key（解析名），映射项为文件系统路径（`DesktopController.ProxyItemName`） |
| `x`,`y` | int | 弹出位置，屏幕物理像素 |
| `shift` | bool | 按住 Shift（加 `CMF_EXTENDEDVERBS` 并传给 InvokeCommand） |
| `iconsVisible` | bool | DeskNook 当前是否显示图标（修正“显示桌面图标”勾选） |
| `interceptVerbs` | string[] | 需要拦截的动词（= `MenuExtensions.VerbInterceptors.Keys`） |

### 通道 2：扩展/代理 → DeskNook（命名管道，一行请求一行应答）

查询（扩展发，`MenuQuery`）：

| 字段 | 含义 |
|---|---|
| `t` | `query` |
| `kind` | `item` / `background` |
| `folder` | 背景菜单所在文件夹的解析名（`SIGDN_DESKTOPABSOLUTEPARSING`）；项菜单为空 |
| `items` | 选中项解析名（最多 4096 个） |
| `shift` | `uFlags & CMF_EXTENDEDVERBS` |
| `proc` | 宿主进程 exe 名（小写，如 `explorer.exe`） |
| `pid` | 宿主进程 PID |
| `req` | 当前线程的代理请求 Id；普通资源管理器窗口里的右键为空（`proxy.cpp:CurrentRequestId`，线程局部） |

应答：`{"q": 查询Id, "items":[WireMenuItem…]}`；无项时 `q=0`、`items=[]`。`WireMenuItem`：

| 字段 | 含义 |
|---|---|
| `id` | 叶子项 Id（本次查询内唯一，从 1 起；子菜单/分隔线为 0） |
| `title` | 标题（含 `&` 助记符与 `\t` 快捷键提示） |
| `icon` | 可选，ICO/PNG 路径（由扩展用 WIC 解码为位图） |
| `enabled` | 默认 true |
| `checked` / `radio` / `sep` | 仅为 true 时输出 |
| `pos` | `top` / `bottom` / `beforeNew`（见魔改指南的位置语义；`MenuProtocol.cs` 注释里写的 `afterOpen` 是过时说明，代码里没有这个值） |
| `children` | 子菜单项数组 |

调用（扩展发）：`{"t":"invoke","q":查询Id,"id":项Id,"req":请求Id}` → `{"ok":true}`。

事件（代理发，`ProxyEvent`，应答 `{"ok":true}`）：

| `t` | 额外字段 | 时机 |
|---|---|---|
| `closed` | `picked`（bool）或 `error`（`nocm` 取不到菜单对象 / `busy` / `exception`） | 菜单关闭（用户选了或取消），先于命令执行 |
| `pick` | `verb`、`title`（标准化后的菜单文本）、`parent`（顶层父菜单标题，如“新建”“排序方式”“查看”） | 用户选了命令，执行前 |
| `verb` | `verb`（`showdesktopicons` 或被拦截的动词名） | 命令被拦截（不交给 Shell） |
| `invoked` | `verb`、`title`、`parent`、`defView`（是否用了真实 DefView）、`hr` | 命令已执行 |

标题标准化 `NormalizeTitle`：去掉 `&` 助记符与 `(&X)` 形式、截掉 `\t` 之后的快捷键提示、去首尾空白。

### 会话与超时

| 参数 | 值 | 位置 |
|---|---|---|
| 扩展查询超时 | 200ms | `ext.cpp:Query` → `PipeRoundTrip` |
| 扩展调用超时 | 500ms | `ext.cpp:Send` |
| 代理回报事件超时 | 500ms | `proxy.cpp:SendEvent` |
| DeskNook 评估查询超时 | 150ms，超时返回空列表 | `MenuPipeServer.QueryTimeoutMs` |
| 管道连接处理总超时 | 1.5s | `MenuPipeServer.HandleClient` |
| `WM_COPYDATA` 发送超时 | 1000ms（`SMTO_ABORTIFHUNG`） | `ExplorerMenuProxy.SendCopyData` |
| 查询会话保留 | 最近 64 个（`MaxSessions`），更旧的 `invoke` 会报“找不到项” | `MenuPipeServer` |
| 待处理请求保留 | 5 分钟（`Purge`） | `ExplorerMenuProxy` |

## 动词拦截

DeskNook 拿不到的“交给 DeskNook 自己做”的动作，分两类：

1. **按标题拦截“显示桌面图标”**（`proxy.cpp:IsShowDesktopIcons`，匹配“显示桌面图标”/“Show desktop icons”）：不执行，发 `verb{showdesktopicons}`，DeskNook `SetIconsVisible(!IconsVisible)`。同时 `FixIconsItem` 在菜单弹出前和每个子菜单 `WM_INITMENUPOPUP` 时把该项的勾选改成 DeskNook 的真实状态（系统 ListView 永远是隐藏的，原生勾选不反映 DeskNook）。
2. **按动词名拦截**：请求里的 `interceptVerbs`（= `MenuExtensions.VerbInterceptors.Keys`），用 `GetCommandString(GCS_VERBW)` 取动词名后不区分大小写比较。命中则发 `verb{<动词>}` 且**不再 `InvokeCommand`**，DeskNook 在 `ExplorerMenuProxy.OnEvent` 里 `VerbInterceptors[动词](ctx)`。当前只有 `rename`（没有真实 DefView 宿主，系统重命名在 DeskNook 画布上不生效，改为 DeskNook 原位重命名）。

注意两条路径的语义差别（容易踩坑）：

- 代理路径下**只要动词在拦截表里就一定被吞掉**，`VerbInterceptors` 委托的返回值被忽略。
- 回退路径（`ShellContextMenu.Show`）里委托返回 `true` 才算已处理，返回 `false` 会继续 `Invoke` 给 Shell。
- 拦截只作用于 **DeskNook 自己发起的菜单**；用户在普通资源管理器窗口里右键不会带 `interceptVerbs`。

未拦截但执行后需要同步的原生命令（`ExplorerMenuProxy.OnNativeInvoked`，依赖中英文标题）：
- 父菜单为“排序方式/Sort by”：按标题映射 `name/size/type/date` → `SortBy`；
- 父菜单为“查看/View”：250ms 后 `SyncFromSystemView()` 重读系统视图的图标大小与间距（DefView 在自己线程里应用）；
- 动词 `refresh` 或标题“刷新/Refresh”：`DesktopController.Refresh()`。
- 粘贴类（动词 `paste/pastelink` 或标题以“粘贴”开头）在 `pick` 阶段 `ArmUndo`，撤消由 `DesktopController.Undo` 发 `WM_COMMAND 0x701B` 给系统 DefView。

## 深色模式与菜单图标

**深色**：
- DeskNook 进程在 `OnStartup` 最早处 `SetPreferredAppMode(1)`（AllowDark）+ `FlushMenuThemes()`，托盘菜单、格子菜单与回退菜单跟随系统；这些是 `uxtheme.dll` 的未公开序号 `#135/#133/#136`（`Native/Win32Shell.cs`），旧系统不存在时异常被吞。
- 回退菜单 `ShellContextMenu.Show`：背景菜单 `SetPreferredAppMode(1)`（深色），图标菜单 `3`（强制浅色），为了和 Explorer 里“带旧式扩展的图标菜单是浅色”的现象一致。
- 代理窗口创建时对自己 `AllowDarkModeForWindow(hwnd, TRUE)` + `FlushMenuThemes`；其余由 Explorer 进程自身决定，所以和原生一致。

**菜单图标**：
- `CustomMenuItem.Icon` 是路径；资源是嵌入 `Assets/*.ico`，`MenuIcons.EnsureExtracted()` 启动时释放到 `<数据根>\icons\<name>.ico`（长度不一致才覆盖），`MenuExtensions.I("name")` 取路径。图标由 `tools/gen-icons.ps1` 生成。
- 扩展侧 `util.cpp:LoadMenuBitmap`：WIC 解码，ICO 多帧挑“不小于目标尺寸的最小帧（没有则最大帧）”，缩放到 `SM_CXSMICON`（至少 16），转 `32bppPBGRA` 的 DIB 节，设到 `hbmpItem`（`MIIM_BITMAP`）。**按路径缓存、不释放**；解码失败也缓存 `nullptr`，之后同一路径永远没有图标（直到宿主进程重启）。
- 代理里菜单带 `MNS_CHECKORBMP` 样式，位图与勾选才同时可用。
- 回退路径下 Shell 扩展给的 32 位位图在 DeskNook 进程的菜单绘制里画不出来（原因未明），`ShellContextMenu.Reapply` 把它们复制成自己的 DIB 节再设回菜单，菜单结束后释放（见 `Owned`）。

## 死锁规避

- **`WM_COPYDATA` 一律在后台线程发送**（`Task.Run`，`SendMessageTimeout`）。菜单弹出期间扩展会经管道回调 DeskNook，应答需要 UI 线程；在 UI 线程同步等待 Explorer 就会互相等死。
- 代理的 `WM_COPYDATA` 处理里**不做任何实际工作**：复制数据后 `PostMessageW(WM_DN_REQ)` 立刻返回 `TRUE`，真正的菜单在随后的消息里运行，所以发送方的 `SendMessage` 立即返回（模态菜单循环不会卡住发送方）。
- 管道服务端对每个连接设 1.5s 总超时，UI 线程评估 150ms 超时；事件类请求 `BeginInvoke` 后立即应答。
- 扩展端所有管道调用带总期限、重叠 I/O + `CancelIoEx`，DeskNook 卡住时 Explorer 最多被拖 200ms/500ms。
- 双保险（推断）：就算代理窗口过程里 Post 后立即返回，仍把发送放在后台线程，这样 Explorer 桌面线程自身卡住时（`SMTO_ABORTIFHUNG` 之外的情况）也不会拖住 DeskNook 的 UI 线程。

## 魔改指南

所有自定义菜单项集中在 `src/DeskNook/Desktop/MenuExtensions.cs` 的 `MenuExtensions.Items`（`IReadOnlyList<CustomMenuItem>`）。C++ 扩展只转发，不含业务逻辑；加一项**只改这一个文件**，不需要动 C++、不需要重新注册。

### 加一个菜单项（可编译示例）

在 `Items` 列表里追加（例如放在“图标：整理类”那组后面）。下面的项给“恰好选中一个文件系统项”的右键菜单加“复制完整路径”，在桌面、格子、普通资源管理器窗口里都出现：

```csharp
new()
{
    Title = "复制完整路径(&P)",
    Position = MenuPosition.Bottom,
    InExplorer = true,                       // 资源管理器窗口里也出现（经 Shell 扩展）
    Icon = I("organize"),                    // 可选；I("名字") = data\icons\名字.ico，可用名见 Assets/*.ico
    Applies = c => !c.IsBackground && c.SelectedPaths.Count == 1 && c.SelectedPaths.Count == c.SelectionCount,
    Handler = c => System.Windows.Clipboard.SetText(c.SelectedPaths[0]),
},
```

子菜单 + 动态标题 + 勾选（`Children` 项的 `Applies` 同样生效；全部子项不适用则整个子菜单自动消失）：

```csharp
new()
{
    Title = "示例(&X)", Position = MenuPosition.Bottom, InExplorer = true,
    Applies = c => !c.IsBackground,
    Children = new CustomMenuItem[]
    {
        new() { Title = "选中项数", DynamicTitle = c => $"已选 {c.SelectionCount} 项", Enabled = _ => false },
        new() { IsSeparator = true },
        new() { Title = "写日志", Handler = c => DeskNook.Services.Log.Info($"示例：{string.Join(", ", c.SelectedPaths)}") },
    },
},
```

要点：

- `Handler` 在 **UI 线程**执行（`MenuPipeServer.RunInvoke` 经 `Dispatcher.BeginInvoke`），可以直接访问 `c.Controller`、剪贴板、WPF；执行异常会被捕获并记日志（`自定义菜单项执行异常`），不会影响 Explorer。执行期间 `MenuExtensions.Current` 已设置为该次上下文。
- 评估（`Applies/DynamicTitle/Checked/Enabled`）发生在管道查询的 UI 线程评估里，**必须很快**（总预算 150ms，含 JSON 序列化；超时整份菜单没有自定义项）。不要做磁盘/网络 IO。动态子菜单用 `MenuExtensions.LazyChildren`（读取 `Current` 实时生成）。
- 不要把 `Position=Top` 的项设成会和 Shell 项抢位置的组合而不看真机：见下面的位置语义。
- 项的 Id（`WireMenuItem.Id`）每次查询重新分配，不要缓存。

### `CustomMenuItem` 字段

| 字段 | 含义 |
|---|---|
| `Title` / `DynamicTitle` | 标题（含 `&` 助记符）；`DynamicTitle` 非 null 时优先 |
| `Position` | `Top` / `AfterRefresh` / `BeforeNew` / `Bottom`（见下） |
| `Applies` | 适用条件；null = 总是出现 |
| `Handler` | 点击后执行（UI 线程） |
| `Checked` / `Radio` / `Enabled` | 勾选、单选样式、是否可用（null=可用） |
| `IsSeparator` | 分隔线 |
| `Children` | 子菜单；有 `Children` 时没有 `Handler` |
| `Icon` | ICO/PNG 路径；null = 无图标 |
| `InExplorer` | 在普通资源管理器窗口里也出现（经 Shell 扩展）；**默认 false，只在 DeskNook 自己弹的菜单里出现** |
| `FallbackOnly` | 只用于进程内回退路径（补“查看/排序/刷新/粘贴/撤消”）；代理路径下 Explorer 原生菜单自带，所以顶层不会发给扩展 |

### 可用上下文 `MenuContext`

| 成员 | 说明 |
|---|---|
| `Controller` | `DesktopController`，所有桌面操作的入口（布局、格子、整理、设置…） |
| `Source` | `MenuSource.Desktop / Box / MappedBox / Explorer` |
| `InDeskNook` | `Source != Explorer` |
| `Items` | 选中的桌面项/映射项（`DesktopItem`）；空 = 背景菜单或资源管理器选择 |
| `Paths` | 资源管理器里选中项的解析名（文件路径或 `::{CLSID}`） |
| `SelectedPaths` | 上面两者里的**文件系统路径**（虚拟项不在内） |
| `SelectionCount` | 选中项总数（`Items` 或 `Paths`） |
| `IsBackground` | `Items` 与 `Paths` 都为空 |
| `IsDesktopBackground` | 桌面自由区空白处（不含格子、不含资源管理器窗口） |
| `Box` | 在格子上右键（空白处/标题栏）时的 `BoxState` |
| `FolderPath` | 资源管理器背景菜单所在文件夹 |
| `Monitor` / `ScreenPoint` / `Hwnd` | 显示器设备名 / 弹出位置（屏幕物理像素；资源管理器来源时取评估时的光标位置，只是近似）/ 宿主窗口（Shell 对话框 owner） |
| `Shift` | 是否按住 Shift |
| `Verb` | 动词拦截时有值 |

辅助：`MenuExtensions.DesktopItemsOf(c)`（把 `Paths` 解析成桌面项）、`InSameFolder(paths, out folder)`、`MenuContext.IsFileSystemPath`。

### 位置语义（代理路径与回退路径不同）

`MenuPosition` 有四个值，但**发给扩展的只有三个名字**（`MenuExtensions.PositionName`）：`Top→"top"`、`BeforeNew→"beforeNew"`、其余（含 `AfterRefresh`、`Bottom`）→`"bottom"`。`ext.cpp:InsertItems` 只区分 `"top"` 与其他：

| 位置 | 代理/Shell 扩展路径（Explorer 菜单） | 回退路径（`ShellContextMenu.Show`） |
|---|---|---|
| `Top` | 插到菜单最前（index 0 起，按列表顺序依次） | 同样在最前（“新建文件夹”扩展项之后） |
| `AfterRefresh` | **等同 Bottom**（但这类项都是 `FallbackOnly`，不会发给扩展） | “刷新”及其后分隔线之后 |
| `BeforeNew` | **等同 Bottom**：C++ 没有实现“新建之前”的定位 | “新建”子菜单（及其前面分隔线）之前 |
| `Bottom` | 插到 Shell 调用 handler 时传入的 `indexMenu` 处（即第三方 handler 的位置），不是绝对末尾 | 菜单末尾 |

所以 Explorer 里“桌面整理 ▸”和“一键整理”（`BeforeNew`）实际出现的位置由 Shell 传给 handler 的 `indexMenu` 决定；想要精确定位需要改 C++（那就要遵守下面的安全约束），先用真机截图对照（`tools/test/test-menu-v2.ps1` 有 C/D 对比）。

### 动词拦截表

`MenuExtensions.VerbInterceptors`：`Dictionary<string, Func<MenuContext, bool>>`，键为 Shell 动词名（`GetCommandString(GCS_VERBW)` 的结果，不区分大小写）。例：要把 `delete` 改成先确认，则加：

```csharp
["delete"] = c =>
{
    // 代理路径：只要键存在，命令就被吞掉，Shell 不会再执行；需要的话在这里自己调用 c.Controller.DeleteSelected(...)
    // 回退路径：返回 true = 已处理；返回 false = 继续交给 Shell
    return true;
},
```

动词名取决于具体菜单项（扩展项常常没有动词名），不要拿标题当动词。拦截一个动词会影响所有 DeskNook 自己弹的菜单。

### 魔改后的验证

1. `dotnet build DeskNook.sln -c Release`（先 `DeskNook.exe --exit`，否则 exe 被锁）。
2. 运行；右键触发；看 `data\logs\desknook.log` 的“自定义菜单项（Shell 扩展）：<标题>”和 `shellext.log` 的“插入 N 个命令”。
3. 单测：仿 `MenuProtocolTests` 用 `MenuExtensions.BuildWire(...)` 断言适用条件/子菜单行为（纯函数，不需要桌面）。
4. 改的是 C# 时**不需要**重启 Explorer；只有改 C++ 或协议才需要（Explorer 里的旧 DLL 被钉住）。

## 崩溃安全约束

**改 C++ 时必须遵守**（`DeskNookShellExt` 运行在用户的 explorer.exe 和任意第三方进程里，任何未捕获异常或死锁都会拖垮宿主）：

1. **每个 COM/回调入口必须 SEH 包裹**：`DllGetClassObject`、`QueryContextMenu`、`InvokeCommand`、`DnHookProc`、`SelfHookProc`、窗口过程（`ProxyWndProc`）、线程过程（`ProxyThread`）、管道调用（`SafePipeCall`）。模式是外层 `__try/__except(EXCEPTION_EXECUTE_HANDLER)` 的薄包装函数 + 内层实现（MSVC 不允许在有对象展开的函数里直接用 `__try`）。工程用 `/EHa`（`ExceptionHandling=Async`），`catch(...)` 也能接到结构化异常。
2. **返回“成功但不加项”而不是失败**：任何错误下 `QueryContextMenu` 返回 `MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0)`，不要返回失败 HRESULT（会让 Shell 对该 handler 做不必要的处理/重试）。
3. **所有跨进程等待必须有总期限**：管道只用重叠 I/O + `WaitForSingleObject(deadline)` + `CancelIoEx`；`WaitNamedPipe` 带剩余时间。扩展查询 200ms、调用 500ms；DeskNook 不运行时 `CreateFile` 立即失败，什么都不做。绝不能无限等 DeskNook。
4. **不在 `DllMain` 里做事**：只记 `g_hMod`、`DisableThreadLibraryCalls`、判断是否 explorer。不要在里面加载库、建线程、取锁。
5. **DLL 里有窗口过程/钩子/线程在运行就必须 PIN**（`GET_MODULE_HANDLE_EX_FLAG_PIN`），`DllCanUnloadNow` 在代理存活时返回 `S_FALSE`。新增任何“DLL 卸载后仍会被调用”的东西（窗口、线程、钩子、定时器）都要先想清楚生命周期。
6. **代理窗口过程里不做重活**：`WM_COPYDATA` 只校验、复制、`PostMessage` 并返回；校验失败返回 `FALSE`。只接受 exe 名为 `desknook.exe` 的发送方，`cbData ≤ 1MB`，magic 必须匹配。
7. **不能退出 Explorer 自己的消息循环**：桌面线程是 Explorer 的；`WM_DESTROY` 里只有专用线程（`g_dedicated`）才 `PostQuitMessage`。
8. **输入全部按不可信处理**：JSON 深度 ≤ 32、总长 ≤ 4MB、数字长度 ≤ 40；项数 ≤ 4096、命令数 ≤ 0x1000 且不超过 `idCmdLast`；菜单递归深度 ≤ 8；字符串缓冲有长度限制。
9. **日志永不抛异常**（`Log` 内部 try/catch、SRW 锁、超 1MB 截断）。注意日志里会出现选中项/文件夹路径，新增日志时不要写入更敏感的内容。
10. **静态 CRT**（`MultiThreaded`）：不依赖宿主进程里有没有 VC 运行库，也避免与宿主自己的 CRT 版本冲突。不要引入需要额外运行库或全局构造/析构时序复杂的静态对象（`util.cpp` 里的缓存用 `new` 出来永不销毁，就是为了避开卸载时序）。
11. **COM 线程模型**：在 Explorer 里是 `ThreadingModel=Apartment`；`LoadMenuBitmap` 里的 `CoCreateInstance(WIC)` 依赖调用线程已初始化 COM（Explorer 的 shell 线程满足）。
12. **协议变更是破坏性的**：Explorer 里钉住的是旧 DLL，新 DeskNook + 旧 DLL 必须仍然不出错（至少“不加项”）；字段只增不改名；改完要同步 `MenuProtocol.cs`、`MenuProtocolTests.cs`、本文。
13. **每次改完在真机验证 Explorer 的 PID 没变**（`tools/test/test-menu-v2.ps1` 用 `Get-ExplorerPids`/`Assert-ExplorerAlive` 断言；见 [testing.md](testing.md)）。验证前先让旧 DLL 退出（重启 Explorer），否则测的还是旧代码。

回退保障：代理出任何问题，DeskNook 会走进程内菜单，所以“C++ 出错”的用户可见表现应是“菜单变回 v1 的样子”，而不是桌面不可用——改动时保持这个性质（例如 `closed{error}` 必须能让 DeskNook 回退）。
