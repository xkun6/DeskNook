# 构建 MSI 安装包：self-contained / win-x64 发布 → WiX v5 打包，输出 artifacts/DeskNook-<版本>-x64.msi。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1
# 需要：.NET 9 SDK、Visual Studio 2022（C++ 工具集，编译 DeskNookShellExt.dll）；WiX 通过 NuGet 还原，无需全局安装。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root 'artifacts\publish'

# 1. 自带运行时的发布（复用 publish.ps1）
& (Join-Path $PSScriptRoot 'publish.ps1') -SelfContained -Output $publish
if ($LASTEXITCODE -ne 0) { throw "publish.ps1 失败（退出码 $LASTEXITCODE）" }

# 2. 发布目录自检：必须有 Shell 扩展与运行时，不得带数据目录
foreach ($f in 'DeskNook.exe', 'DeskNookShellExt.dll', 'coreclr.dll') {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "发布目录缺少 $f（self-contained 发布不完整）" }
}
if (Test-Path (Join-Path $publish 'data')) { throw "发布目录里不应有 data/" }

# 3. 版本号 = csproj 的 <Version>（与 exe 文件版本一致，MSI 内部版本取自 exe）
$version = ([xml](Get-Content (Join-Path $root 'src\DeskNook\DeskNook.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "src/DeskNook/DeskNook.csproj 里没有 <Version>" }

# 4. WiX 打包
& dotnet build (Join-Path $root 'installer\DeskNook.Installer.wixproj') -c Release -nologo "-p:ProductVersion=$version" "-p:AppPublishDir=$publish"
if ($LASTEXITCODE -ne 0) { throw "WiX 构建失败（退出码 $LASTEXITCODE）" }

$msi = Join-Path $root "artifacts\DeskNook-$version-x64.msi"
if (-not (Test-Path $msi)) { throw "没有生成 $msi" }
$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "安装包：$msi（$size MB）" -ForegroundColor Green
