<#
  生成"一键安装程序"（单个 exe，内置全部文件）：
    1) 构建解决方案并发布 dist
    2) 把 dist 的内容作为"安装包体"塞进 LabGuard.Installer
    3) 输出 release\LabGuard-一键安装程序.exe
  用法：powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#>
[CmdletBinding()]
param([switch]$SkipSolutionBuild, [string]$Version = '0.01')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $root 'src\LabGuard.Installer\payload'
$release = Join-Path $root 'release'

if (-not $SkipSolutionBuild) {
    Write-Host '== 1/3 构建解决方案并发布 dist ==' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
}

$dist = Join-Path $root 'dist'
if (-not (Test-Path (Join-Path $dist 'LabGuard.Agent.exe'))) {
    throw 'dist 里没有程序文件，请先运行 scripts\build.ps1'
}

Write-Host '== 2/3 准备安装包体（内置到安装程序） ==' -ForegroundColor Cyan
if (Test-Path $payload) { [System.IO.Directory]::Delete($payload, $true) }
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Copy-Item -Path (Join-Path $dist '*') -Destination $payload -Recurse -Force
# 安装程序自己不需要被装进去
Get-ChildItem -Path $payload -Filter 'LabGuard.Installer*' -Recurse -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
# 只把"程序本体 + 壁纸"装到学生机：部署脚本、预设、文档（含忘记密码的救援命令）留在老师手里，
# 避免学生打开安装目录就看到"怎么免密卸载/重置密码"的说明书。
foreach ($exclude in @('docs', 'presets', 'README.md')) {
    $p = Join-Path $payload $exclude
    if (Test-Path $p) {
        if ((Get-Item $p) -is [System.IO.DirectoryInfo]) { [System.IO.Directory]::Delete($p, $true) }
        else { Remove-Item -LiteralPath $p -Force }
    }
}
Get-ChildItem -Path $payload -Filter '*.ps1' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
Get-ChildItem -Path $payload -Filter '*.cmd' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
$payloadFiles = (Get-ChildItem -Path $payload -Recurse -File | Measure-Object).Count
$payloadSize = [math]::Round((Get-ChildItem -Path $payload -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 2)
Write-Host ("   包体：" + $payloadFiles + " 个文件 / " + $payloadSize + " MB")

Write-Host '== 3/3 编译安装程序 ==' -ForegroundColor Cyan
& dotnet build (Join-Path $root 'src\LabGuard.Installer\LabGuard.Installer.csproj') -c Release -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败' }

New-Item -ItemType Directory -Force -Path $release | Out-Null
$src = Join-Path $root 'src\LabGuard.Installer\bin\Release\net48\LabGuard.Installer.exe'
$dst = Join-Path $release 'LabGuard-一键安装程序.exe'
Copy-Item -LiteralPath $src -Destination $dst -Force
$size = [math]::Round((Get-Item $dst).Length / 1MB, 2)

# 顺带打一个完整发布包（dist + presets + docs + README + 安装程序），文件名带版本
$zip = Join-Path $release ("labguard-v" + $Version + ".zip")
$staging = Join-Path $release '_staging'
if (Test-Path $staging) { [System.IO.Directory]::Delete($staging, $true) }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item -Path (Join-Path $root 'dist\*') -Destination $staging -Recurse -Force
foreach ($item in @('presets', 'docs')) {
    $s = Join-Path $root $item
    if (Test-Path $s) { Copy-Item -Path $s -Destination $staging -Recurse -Force }
}
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $staging -Force
if (Test-Path (Join-Path $root 'CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination $staging -Force }
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $staging -Force }
Copy-Item -LiteralPath $dst -Destination (Join-Path $staging 'LabGuard-一键安装程序.exe') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip)
[System.IO.Directory]::Delete($staging, $true)
Write-Host ''
Write-Host ("✅ 已生成：" + $dst + "（" + $size + " MB，单文件、内置全部程序与壁纸）") -ForegroundColor Green
Write-Host ("✅ 已生成：" + $zip + "（" + [math]::Round((Get-Item $zip).Length / 1MB, 2) + " MB，完整发布包）") -ForegroundColor Green
Write-Host '   双击即可安装：欢迎 → 安装位置 → 密码 → 90+ 项功能开关 → 安装'
Write-Host '   无人值守：LabGuard-一键安装程序.exe --silent --password xxx [--config 预设.json] [--dns 教师机IP]'
Write-Host '   预演：    LabGuard-一键安装程序.exe --dry-run'
