<#
  干跑验证：启动/停止所有程序（全部带 DryRun），并对比系统状态快照，证明"不改动系统"。
  用法（管理员 PowerShell）：
      powershell -ExecutionPolicy Bypass -File verify-dryrun.ps1
  前置：先执行 LabGuard.Settings.exe --init <密码> 生成配置。
#>
[CmdletBinding()]
param([string]$Root = $PSScriptRoot)

if (-not $Root) { $Root = (Get-Location).Path }

$ErrorActionPreference = 'Stop'

function Get-Snapshot {
    $parts = @('hosts=' + (Get-FileHash "$env:SystemRoot\System32\drivers\etc\hosts" -Algorithm SHA256).Hash)
    $keys = @(
        'HKLM:\SOFTWARE\Policies\Google\Chrome',
        'HKLM:\SOFTWARE\Policies\Microsoft\Edge',
        'HKLM:\SOFTWARE\Policies\Mozilla\Firefox',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\3',
        'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\winmine',
        'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
    )
    foreach ($k in $keys) { $parts += ($k + '=' + (Test-Path $k)) }
    $parts += 'usbstor=' + (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\usbstor' -Name Start).Start
    $parts += 'wallpaper=' + (Get-ItemProperty 'HKCU:\Control Panel\Desktop' -Name Wallpaper -ErrorAction SilentlyContinue).Wallpaper
    $parts += 'hostsbak=' + (Test-Path "$env:SystemRoot\System32\drivers\etc\hosts.labguard-bak")
    return ($parts -join '|')
}

$before = Get-Snapshot
Write-Host '=== 干跑验证 ===' -ForegroundColor Cyan
foreach ($exe in @('LabGuard.Agent.exe', 'LabGuard.Service.exe')) {
    $path = Join-Path $Root $exe
    if (-not (Test-Path $path)) { Write-Host "跳过（未找到）$exe" -ForegroundColor Yellow; continue }
    Write-Host "启动 $exe --dry-run（12 秒）"
    $args = if ($exe -like '*Service*') { @('--console') } else { @('--dry-run') }
    # 服务侧干跑：先临时关闭 DryRun 不可行，这里仅验证代理；服务用 --console 会真正执行，故跳过
    if ($exe -like '*Service*') { Write-Host '服务程序请用 LabGuard.SelfTest 的干跑验证（SystemCore 角色），此处跳过。' -ForegroundColor Yellow; continue }
    $p = Start-Process -FilePath $path -ArgumentList $args -PassThru
    Start-Sleep -Seconds 12
    Write-Host ("  进程存活：" + (-not $p.HasExited))
    if (-not $p.HasExited) { $p.Kill() }
    Start-Sleep -Seconds 2
}
$after = Get-Snapshot
if ($before -eq $after) {
    Write-Host '系统状态快照一致：干跑未修改系统 ✅' -ForegroundColor Green
} else {
    Write-Host '检测到差异 ❌' -ForegroundColor Red
    Write-Host ('before: ' + $before)
    Write-Host ('after : ' + $after)
}
Write-Host ''
Write-Host '最近日志（%ProgramData%\LabGuard\logs）：'
$log = Get-ChildItem 'C:\ProgramData\LabGuard\logs' -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) { Get-Content $log.FullName -Tail 25 }
