# 构建安装包与便携版：win-x64 发布 → WiX v5 打包，一次产出两种变体共 4 个文件。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1
# 参数：-Version x.y.z 覆盖 csproj 版本（CI tag 构建）；-NameSuffix 只加在产物文件名上（如 -ci.12）。
# 变体：
#   自带运行时（self-contained，发布目录 artifacts/publish）：DeskNook-<版本>[后缀]-x64.msi、DeskNook-<版本>[后缀]-x64-portable.zip
#   不带运行时（依赖框架，发布目录 artifacts/publish-noruntime，需 .NET 9 桌面运行时；MSI 安装前检查运行时）：
#     DeskNook-<版本>[后缀]-x64-noruntime.msi、DeskNook-<版本>[后缀]-x64-noruntime-portable.zip
#   便携版 = 对应发布目录压缩，不含 pdb 与 data/。
# 需要：.NET 9 SDK、Visual Studio 2022（C++ 工具集，编译 DeskNookShellExt.dll）；WiX 通过 NuGet 还原，无需全局安装。
param([string]$Version, [string]$NameSuffix = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# 1. 版本号 = -Version 或 csproj 的 <Version>（与 exe 文件版本一致，MSI 内部版本取自 exe）
$version = $Version
if (-not $version) { $version = ([xml](Get-Content (Join-Path $root 'src\DeskNook\DeskNook.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $version) { throw "src/DeskNook/DeskNook.csproj 里没有 <Version>" }

# 2. 单个变体：发布 → 自检 → WiX 打包 → 便携版 zip
function Build-Variant([string]$Name, [bool]$SelfContained, [string]$PublishName, [string]$Tag) {
    Write-Host "==== 变体：$Name ====" -ForegroundColor Cyan
    $publish = Join-Path $root "artifacts\$PublishName"

    # 2.1 发布（复用 publish.ps1）
    $pubArgs = @{ Output = $publish }
    if ($SelfContained) { $pubArgs.SelfContained = $true }
    if ($Version) { $pubArgs.Version = $Version }
    & (Join-Path $PSScriptRoot 'publish.ps1') @pubArgs
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 失败（退出码 $LASTEXITCODE）" }

    # 2.2 发布目录自检：自带运行时必须有 coreclr.dll；依赖框架必须有 runtimeconfig 且不得有 coreclr.dll；都不得带数据目录
    $need = @('DeskNook.exe', 'DeskNookShellExt.dll')
    if ($SelfContained) { $need += 'coreclr.dll' } else { $need += 'DeskNook.runtimeconfig.json' }
    foreach ($f in $need) {
        if (-not (Test-Path (Join-Path $publish $f))) { throw "发布目录缺少 $f（$Name 发布不完整）" }
    }
    if ((-not $SelfContained) -and (Test-Path (Join-Path $publish 'coreclr.dll'))) { throw "依赖框架发布目录里不应有 coreclr.dll" }
    if (Test-Path (Join-Path $publish 'data')) { throw "发布目录里不应有 data/" }

    # 2.3 WiX 打包：--no-incremental 必须加：两次构建共用 obj 目录，只改全局属性时增量构建可能复用上一次的中间文件
    # 另外先删 installer\obj：MSBuild 的 IncrementalClean 会按上一次的 FileListAbsolute 删除上一变体已生成的 msi/wixpdb
    $outName = "DeskNook-$version$NameSuffix-x64$Tag"
    $wixObj = Join-Path $root 'installer\obj'
    if (Test-Path $wixObj) { Remove-Item $wixObj -Recurse -Force }
    & dotnet build (Join-Path $root 'installer\DeskNook.Installer.wixproj') -c Release -nologo --no-incremental "-p:ProductVersion=$version" "-p:AppPublishDir=$publish" "-p:NoRuntime=$((-not $SelfContained).ToString().ToLower())" "-p:OutputName=$outName"
    if ($LASTEXITCODE -ne 0) { throw "WiX 构建失败（退出码 $LASTEXITCODE）" }

    # exe 文件版本必须等于期望版本（MSI 的 ProductVersion 取自它）
    $fileVer = (Get-Item (Join-Path $publish 'DeskNook.exe')).VersionInfo.FileVersion
    if ($fileVer -notmatch "^$([regex]::Escape($version))(\.0)?$") { throw "DeskNook.exe 文件版本 $fileVer 与期望版本 $version 不一致" }

    $msi = Join-Path $root "artifacts\$outName.msi"
    if (-not (Test-Path $msi)) { throw "没有生成 $msi" }
    $size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
    Write-Host "安装包：$msi（$size MB）" -ForegroundColor Green

    # 2.4 便携版 zip（不含 pdb、不含 data/）
    $zip = Join-Path $root "artifacts\$outName-portable.zip"
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
    $zsize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "便携版：$zip（$zsize MB）" -ForegroundColor Green
}

Build-Variant -Name '自带运行时' -SelfContained $true -PublishName 'publish' -Tag ''
Build-Variant -Name '不带运行时' -SelfContained $false -PublishName 'publish-noruntime' -Tag '-noruntime'
