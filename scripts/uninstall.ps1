<#
  卸载【LabGuard】并还原系统设置。
  必须用【管理员】PowerShell 运行：
      powershell -ExecutionPolicy Bypass -File uninstall.ps1
  带 -Force 时不弹密码（用于批量维护）。
#>
[CmdletBinding()]
param([switch]$ForceReset, [switch]$KeepFiles, [string]$Password)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $here) { $here = (Get-Location).Path }

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$p = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请用【管理员】PowerShell 运行本脚本。'
}

Write-Host "=== LabGuard卸载 ===" -ForegroundColor Cyan

$svc = Get-Service -Name 'LabGuardSvc' -ErrorAction SilentlyContinue
if ($svc) { & sc.exe stop LabGuardSvc | Out-Null; Start-Sleep -Seconds 2 }
Get-Process -Name 'LabGuard.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name 'LabGuard.Settings' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 先记下安装目录（后面会删注册表键）
$installDirFromReg = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\LabGuard' -Name 'InstallDir' -ErrorAction SilentlyContinue).InstallDir

$uninstaller = Join-Path $here 'LabGuard.Uninstall.exe'
if (Test-Path $uninstaller) {
    $arguments = @()
    if ($Password) { $arguments += @('--password', $Password) }
    if ($ForceReset) {
        Write-Host '⚠ 已选择 -ForceReset：跳过密码校验（仅在老师忘记密码时使用，会记录到日志）。' -ForegroundColor Yellow
        $arguments += '--force'
    }
    Write-Host '调用卸载程序执行"还原系统设置"…（需要小助手密码，或用 -Password 提供）'
    if ($Password -or $ForceReset) { $arguments += '--quiet' }   # 有密码/强制时才静默，否则让程序弹密码框
    Start-Process -FilePath $uninstaller -ArgumentList $arguments -Wait
} else {
    Write-Host '未找到 LabGuard.Uninstall.exe，仅清理服务与自启动。' -ForegroundColor Yellow
}

& schtasks.exe /delete /tn 'LabGuard\Agent' /f 2>$null | Out-Null
if (Get-Service -Name 'LabGuardSvc' -ErrorAction SilentlyContinue) { & sc.exe delete LabGuardSvc | Out-Null }

Remove-Item -Path 'HKLM:\SOFTWARE\LabGuard' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabGuard', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabGuardReplica' -Recurse -Force -ErrorAction SilentlyContinue -ErrorAction SilentlyContinue

$programs = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'LabGuard'
if (Test-Path $programs) { [System.IO.Directory]::Delete($programs, $true) }
$desktopLnk = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'LabGuard.lnk'
if (Test-Path $desktopLnk) { Remove-Item -LiteralPath $desktopLnk -Force }

if (-not $KeepFiles) {
    $dir = $installDirFromReg
    $isOurs = $false
    if ($dir) {
        if ($dir -like '*LabGuard*') { $isOurs = $true }
        elseif ($dir -match '^[A-Za-z]:\\f\d+$') { $isOurs = $true }
    }
    if ($isOurs -and (Test-Path $dir)) {
        Write-Host "删除程序目录：$dir"
        [System.IO.Directory]::Delete($dir, $true)
    } elseif ($dir) {
        Write-Host "为安全起见不自动删除目录：$dir（如需删除请手工处理）" -ForegroundColor Yellow
    }
}

Write-Host '卸载完成。建议重启电脑让 USB、任务栏、安全模式等设置完全恢复。' -ForegroundColor Green
