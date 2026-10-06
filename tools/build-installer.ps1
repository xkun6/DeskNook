# 构建 MSI 安装包：self-contained / win-x64 发布 → WiX v5 打包，输出 artifacts/DeskNook-<版本>-x64.msi。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1
# 参数：-Version x.y.z 覆盖 csproj 版本（CI tag 构建）；-NameSuffix 只加在产物文件名上（如 -ci.12）。
# 产物：artifacts/DeskNook-<版本>[后缀]-x64.msi 与 DeskNook-<版本>[后缀]-x64-portable.zip（便携版 = self-contained 发布目录，不含 pdb）。
# 需要：.NET 9 SDK、Visual Studio 2022（C++ 工具集，编译 DeskNookShellExt.dll）；WiX 通过 NuGet 还原，无需全局安装。
param([string]$Version, [string]$NameSuffix = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root 'artifacts\publish'

# 1. 自带运行时的发布（复用 publish.ps1）
$pubArgs = @{ SelfContained = $true; Output = $publish }
if ($Version) { $pubArgs.Version = $Version }
& (Join-Path $PSScriptRoot 'publish.ps1') @pubArgs
if ($LASTEXITCODE -ne 0) { throw "publish.ps1 失败（退出码 $LASTEXITCODE）" }

# 2. 发布目录自检：必须有 Shell 扩展与运行时，不得带数据目录
foreach ($f in 'DeskNook.exe', 'DeskNookShellExt.dll', 'coreclr.dll') {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "发布目录缺少 $f（self-contained 发布不完整）" }
}
if (Test-Path (Join-Path $publish 'data')) { throw "发布目录里不应有 data/" }

# 3. 版本号 = csproj 的 <Version>（与 exe 文件版本一致，MSI 内部版本取自 exe）
$version = $Version
if (-not $version) { $version = ([xml](Get-Content (Join-Path $root 'src\DeskNook\DeskNook.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $version) { throw "src/DeskNook/DeskNook.csproj 里没有 <Version>" }

# 4. WiX 打包
& dotnet build (Join-Path $root 'installer\DeskNook.Installer.wixproj') -c Release -nologo "-p:ProductVersion=$version" "-p:AppPublishDir=$publish"
if ($LASTEXITCODE -ne 0) { throw "WiX 构建失败（退出码 $LASTEXITCODE）" }

# exe 文件版本必须等于期望版本（MSI 的 ProductVersion 取自它）
$fileVer = (Get-Item (Join-Path $publish 'DeskNook.exe')).VersionInfo.FileVersion
if ($fileVer -notmatch "^$([regex]::Escape($version))(\.0)?$") { throw "DeskNook.exe 文件版本 $fileVer 与期望版本 $version 不一致" }

$msi = Join-Path $root "artifacts\DeskNook-$version-x64.msi"
if (-not (Test-Path $msi)) { throw "没有生成 $msi" }
if ($NameSuffix) {   # 后缀只改文件名
    $renamed = Join-Path $root "artifacts\DeskNook-$version$NameSuffix-x64.msi"
    Move-Item $msi $renamed -Force
    $msi = $renamed
}
$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "安装包：$msi（$size MB）" -ForegroundColor Green

# 5. 便携版 zip（不含 pdb、不含 data/）
$zip = Join-Path $root "artifacts\DeskNook-$version$NameSuffix-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    $base = $publish.TrimEnd([char]92) + [char]92
    foreach ($f in Get-ChildItem $publish -Recurse -File | Where-Object { $_.Extension -ne '.pdb' }) {
        $entry = $f.FullName.Substring($base.Length).Replace([char]92, [char]47)
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, 'Optimal')
    }
} finally { $archive.Dispose() }
Write-Host "便携版：$zip" -ForegroundColor Green
