## 下载哪个文件？

| 文件 | 形式 | 自带 .NET 运行时 | 适合谁 |
|---|---|---|---|
| `DeskNook-{{VERSION}}-x64.msi` | 安装包 | 是 | **大多数用户选这个**：双击安装，不需要额外环境 |
| `DeskNook-{{VERSION}}-x64-noruntime.msi` | 安装包 | 否 | 电脑已装 .NET 9 桌面运行时，想要更小的下载 |
| `DeskNook-{{VERSION}}-x64-portable.zip` | 便携版 | 是 | 不想安装，解压即用 |
| `DeskNook-{{VERSION}}-x64-noruntime-portable.zip` | 便携版 | 否 | 不想安装，且已装 .NET 9 桌面运行时 |

- 不确定电脑上有没有 .NET 9 桌面运行时，就选**自带运行时**的版本（文件名不含 `noruntime`）。
- 不带运行时的版本需要先安装 [.NET 9 桌面运行时（x64）](https://dotnet.microsoft.com/download/dotnet/9.0)（在页面中选择 “.NET Desktop Runtime 9.x” 的 Windows x64 安装程序）。缺少运行时时，安装包会提示并停止安装，便携版启动时会弹窗提示下载。
- 系统要求：Windows 10 / 11（x64）。
- 安装包需要管理员权限；升级时直接运行新版安装包，设置和格子布局都会保留。同一版本的两种安装包可以互相覆盖切换。
- 便携版不写安装信息，右键菜单扩展、开机自启等在程序设置里开启。
