# 发布：Release / win-x64 / 依赖框架（需要目标机器装有 .NET 9 桌面运行时），输出到 dist/。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 [-SingleFile]
# -SingleFile：把托管程序集合并成单个 DeskNext.exe（DeskNextShellExt.dll 仍作为独立文件输出在旁边）。
param([switch]$SingleFile)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

$args2 = @('publish', (Join-Path $root 'src\DeskNext\DeskNext.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $dist, '-nologo')
if ($SingleFile) { $args2 += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
& dotnet @args2
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

$dataDir = Join-Path $dist 'data'   # 运行时数据目录绝不随发布输出分发
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
foreach ($f in 'DeskNext.exe', 'DeskNextShellExt.dll') {
    if (-not (Test-Path (Join-Path $dist $f))) { throw "发布输出缺少 $f" }
}
Get-ChildItem $dist -Exclude *.pdb | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "发布完成：$dist" -ForegroundColor Green
