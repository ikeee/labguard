<#
  卸载【LabGuard】并还原系统设置。
  必须用【管理员】PowerShell 运行：
      powershell -ExecutionPolicy Bypass -File uninstall.ps1
  带 -Password 时不弹密码框（用于批量维护）。
  注意：v0.07 起 -ForceReset 不再免密（红队 B9：--force 自杀开关已拆）——
        忘记密码请改用同目录 cleanup-all.ps1（纯 PowerShell 后门，不依赖任何程序文件）。
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
        # 红队 B9：--force 免密是"编译在学生机里的自杀开关"，v0.07 已拆。
        # 忘记密码的救援通道 = cleanup-all.ps1（老师介质上的纯脚本后路）。
        $cleanup = Join-Path $here 'cleanup-all.ps1'
        Write-Host '⚠ -ForceReset 已不再免密（v0.07 起）。改走 cleanup-all.ps1 彻底清理…' -ForegroundColor Yellow
        if (Test-Path $cleanup) { & $cleanup -Force -KeepFiles:$KeepFiles; exit $LASTEXITCODE }
        throw '未找到 cleanup-all.ps1——请从老师介质重新获取后再执行忘记密码卸载。'
    }
    Write-Host '调用卸载程序执行"还原系统设置"…（需要小助手密码，或用 -Password 提供）'
    if ($Password) { $arguments += '--quiet' }   # 有密码才静默，否则让程序弹密码框
    Start-Process -FilePath $uninstaller -ArgumentList $arguments -Wait
} else {
    Write-Host '未找到 LabGuard.Uninstall.exe，仅清理服务与自启动。' -ForegroundColor Yellow
}

# 先删"防拆兜底任务"，否则它会在卸载过程中把服务又拉起来（每分钟一次）
& schtasks.exe /delete /tn 'LabGuard\Guard' /f 2>$null | Out-Null
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
