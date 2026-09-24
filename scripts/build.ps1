<#
  构建 + 发布（默认输出到 dist\）
  用法：
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SelfTest
  说明：需要 .NET SDK（本机已装 8.0）；目标框架 net48，生成的程序在 Win7/10/11 上直接可跑（系统自带 .NET Framework 4.8）。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

Write-Host "== 构建 $Configuration ==" -ForegroundColor Cyan
Push-Location $root
try {
    & dotnet build 'LabGuard.sln' -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败" }

    Write-Host "== 发布到 $dist ==" -ForegroundColor Cyan
    if (Test-Path $dist) { [System.IO.Directory]::Delete($dist, $true) }
    New-Item -ItemType Directory -Force -Path $dist | Out-Null

    $apps = @('LabGuard.Agent', 'LabGuard.Service', 'LabGuard.Settings', 'LabGuard.Uninstall', 'LabGuard.Launcher')
    foreach ($app in $apps) {
        $src = Join-Path $root "src\$app\bin\$Configuration\net48"
        if (-not (Test-Path $src)) { throw "找不到构建输出：$src" }
        Get-ChildItem -LiteralPath $src -File |
            Where-Object { $_.Extension -in @('.exe', '.dll', '.config') -or $_.Name -like '*.exe.config' } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dist -Force }
    }
    Copy-Item -LiteralPath (Join-Path $root 'scripts\install.ps1')   -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\uninstall.ps1') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\make-wallpapers.ps1') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\verify-dryrun.ps1') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\test-hosts-watchdog.ps1') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\test-disconnect.ps1') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\install-as-admin.cmd') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\harden-service.ps1') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\monitor-receiver.ps1') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\cleanup-all.ps1') -Destination $dist -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $dist 'presets') | Out-Null
    if (Test-Path (Join-Path $root 'presets')) {
        Copy-Item -Path (Join-Path $root 'presets\*') -Destination (Join-Path $dist 'presets') -Force
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $dist 'wallpaper') | Out-Null
    Write-Host '== 生成机房规范壁纸（1920x1080 x6） ==' -ForegroundColor Cyan
    & (Join-Path $root 'scripts\make-wallpapers.ps1') -OutDir (Join-Path $dist 'wallpaper')
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $dist -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $dist -Force

    Write-Host "== 产物 ==" -ForegroundColor Cyan
    Get-ChildItem -LiteralPath $dist | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host

    if ($SelfTest) {
        Write-Host "== 自检（干跑，不修改系统） ==" -ForegroundColor Cyan
        & (Join-Path $root "src\LabGuard.SelfTest\bin\$Configuration\net48\LabGuard.SelfTest.exe")
        if ($LASTEXITCODE -ne 0) { throw "自检失败" }
    }
}
finally {
    Pop-Location
}
Write-Host "完成。安装：右键以管理员身份运行 dist\install.ps1" -ForegroundColor Green
