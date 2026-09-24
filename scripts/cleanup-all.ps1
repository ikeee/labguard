<#
  【最后的后路】一键彻底卸载 + 系统还原（纯 PowerShell，**不需要安装目录里的任何程序文件**）

  用途（老师用，学生机出问题 / 不再使用 / 要重新部署时）：
    · 程序被破坏、卸载程序删了、密码忘了 —— 都能用它收尾
    · **可以在"安全模式（无网络）"里运行**：我们的服务/小助手在安全模式下不加载，
      所以进安全模式再跑这个脚本，一定能把一切清干净

  用法（管理员 PowerShell）：
      powershell -ExecutionPolicy Bypass -File cleanup-all.ps1              # 会先确认
      powershell -ExecutionPolicy Bypass -File cleanup-all.ps1 -Force       # 不确认（批量）
      powershell -ExecutionPolicy Bypass -File cleanup-all.ps1 -DryRun      # 只打印将做什么
      powershell -ExecutionPolicy Bypass -File cleanup-all.ps1 -KeepFiles   # 保留程序目录
      powershell -ExecutionPolicy Bypass -File cleanup-all.ps1 -KeepLogs:$false  # 连日志一起删

  做这些事：
    停/删服务 → 结束小助手 → 删计划任务与安全模式登记 → 还原 hosts（含 hosts 备份）
    → 恢复 U 盘（usbstor） → 清浏览器组策略/IFEO 映像劫持/任务栏与策略项/壁纸
    → 还原带网络的安全模式（若有备份） → 恢复启动菜单默认值 → 删快捷方式/注册表/程序目录/备份副本
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$DryRun,
    [switch]$KeepFiles,
    [bool]$KeepLogs = $true,
    [string]$InstallDir
)

$ErrorActionPreference = 'Continue'
$svc = 'LabGuardSvc'
$taskName = 'LabGuard\Agent'
$regRoot = 'HKLM:\SOFTWARE\LabGuard'
$uninstKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabGuard'
$uninstKeyLegacy = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabGuardReplica'
$hosts = "$env:SystemRoot\System32\drivers\etc\hosts"
$hostsBak = "$hosts.labguard-bak"
$dataDir = Join-Path $env:ProgramData 'LabGuard'
$mirror = Join-Path $dataDir 'payload'
$beginMark = '# ==== LabGuard 黑名单开始（由本程序自动生成，请勿手工修改） ===='
$endMark = '# ==== LabGuard 黑名单结束 ===='
$done = New-Object System.Collections.Generic.List[string]
$skipped = New-Object System.Collections.Generic.List[string]

function Step([string]$text) { Write-Host ('  · ' + $text) }
function Run-Step([string]$desc, [scriptblock]$action) {
    Step $desc
    if ($DryRun) { $done.Add('[预演] ' + $desc); return }
    try { & $action; $done.Add($desc) } catch { Write-Host ('    ⚠ ' + $_.Exception.Message) -ForegroundColor Yellow }
}
function RegDel([string]$path) {
    try { if (Test-Path $path) { [Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree(($path -replace '^HKLM:\\',''), $false) } } catch {}
}
function RegDelCu([string]$path) {
    try { if (Test-Path $path) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(($path -replace '^HKCU:\\',''), $false) } } catch {}
}
function RegVal([string]$path, [string]$name, $value, [string]$type = 'DWord') {
    try { if (Test-Path $path) { Set-ItemProperty -Path $path -Name $name -Value $value -Type $type -ErrorAction SilentlyContinue } } catch {}
}
function RegDelVal([string]$path, [string]$name) {
    try { if (Test-Path $path) { Remove-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue } } catch {}
}

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host '=== LabGuard· 彻底卸载与还原 ===' -ForegroundColor Cyan
if (-not $isAdmin) { Write-Host '⚠ 当前不是管理员：服务/注册表/HKLM 部分会失败，请用【管理员】PowerShell 重跑。' -ForegroundColor Yellow }
$bootMode = (Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);' -Name SM -Namespace WG -PassThru)::GetSystemMetrics(67)
Write-Host ('当前启动模式：' + $(if ($bootMode -eq 0) { '正常' } elseif ($bootMode -eq 1) { '安全模式' } else { '带网络的安全模式' }))
if (-not $Force -and -not $DryRun) {
    if ((Read-Host '确认彻底卸载并还原所有设置？(y/N)') -ne 'y') { Write-Host '已取消'; return }
}

Write-Host '1) 停止并删除守护服务' -ForegroundColor Cyan
Run-Step ("停止服务 $svc") { & sc.exe stop $svc | Out-Null; Start-Sleep -Seconds 2 }
Run-Step ("结束小助手进程") { Get-Process -Name 'LabGuard.Agent','LabGuard.Service','LabGuard.Settings','LabGuard.Launcher' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
Run-Step ("删除服务 $svc") { & sc.exe delete $svc | Out-Null }
foreach ($root in @('HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal','HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network')) {
    Run-Step ('删除安全模式登记 ' + $root + '\' + $svc) { RegDel (Join-Path $root $svc) }
}

Write-Host '2) 删除自启动计划任务与快捷方式' -ForegroundColor Cyan
Run-Step ('删除计划任务 ' + $taskName) { & schtasks.exe /delete /tn $taskName /f 2>$null | Out-Null }
Run-Step '删除桌面/开始菜单快捷方式' {
    foreach ($lnk in @(
        (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'LabGuard.lnk'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'LabGuard\设置.lnk'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'LabGuard\卸载.lnk'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'LabGuard\解除锁定（输入密码）.lnk'))) {
        if (Test-Path $lnk) { Remove-Item -LiteralPath $lnk -Force }
    }
    $pdir = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'LabGuard'
    if ((Test-Path $pdir) -and (Get-ChildItem $pdir -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) {
        [System.IO.Directory]::Delete($pdir, $true)
    }
}

Write-Host '3) 还原 hosts（域名黑名单）与 U 盘' -ForegroundColor Cyan
Run-Step '移除 hosts 受管区块' {
    if (Test-Path $hosts) {
        $lines = Get-Content -LiteralPath $hosts -Encoding UTF8
        $out = New-Object System.Collections.Generic.List[string]
        $skip = $false
        foreach ($l in $lines) {
            if ($l.Trim() -eq $beginMark) { $skip = $true; continue }
            if ($l.Trim() -eq $endMark) { $skip = $false; continue }
            if (-not $skip) { $out.Add($l) }
        }
        while ($out.Count -gt 0 -and $out[$out.Count - 1].Trim() -eq '') { $out.RemoveAt($out.Count - 1) }
        [System.IO.File]::WriteAllText($hosts, ($out -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    }
    if (Test-Path $hostsBak) {
        # 有备份：说明当初写黑名单前留过原始 hosts，恢复它
        Copy-Item -LiteralPath $hostsBak -Destination $hosts -Force
        Remove-Item -LiteralPath $hostsBak -Force
    }
    $attr = Get-Item -LiteralPath $hosts -Force
    $attr.Attributes = ($attr.Attributes -band (-bnot ([System.IO.FileAttributes]::Hidden -bor [System.IO.FileAttributes]::System)))
    & ipconfig.exe /flushdns | Out-Null
}
Run-Step '恢复 U 盘（usbstor Start=3）' {
    foreach ($hive in @('CurrentControlSet','ControlSet001','ControlSet002','ControlSet003')) {
        $p = "HKLM:\SYSTEM\$hive\Services\usbstor"
        if (Test-Path $p) { RegVal $p 'Start' 3 }
    }
}

Write-Host '4) 清浏览器组策略 / IFEO 映像劫持 / 任务栏与系统工具策略 / 壁纸' -ForegroundColor Cyan
Run-Step '删除浏览器组策略' {
    RegDel 'HKLM:\SOFTWARE\Policies\Google\Chrome'
    RegDel 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
    RegDel 'HKLM:\SOFTWARE\Policies\Mozilla\Firefox'
    RegDel 'HKLM:\SOFTWARE\Policies\Microsoft\Internet Explorer\Restrictions'
    RegDel 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient'
    RegDelVal 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\3' '1803'
    RegDelVal 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\3' '2200'
}
Run-Step '删除 IFEO 映像劫持（小游戏与系统命令）' {
    $ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
    foreach ($exe in @('sidebar.exe','Chess.exe','FreeCell.exe','Hearts.exe','Minesweeper.exe','PurblePlace.exe',
                       'Mahjong.exe','SpiderSolitaire.exe','bckgzm.exe','chkrzm.exe','shvlzm.exe','Solitaire.exe',
                       'winmine','Magnify.exe','taskkill.exe','ntsd.exe','tasklist.exe','route.exe')) {
        RegDelVal (Join-Path $ifeo $exe) 'Debugger'
    }
}
Run-Step '恢复任务栏/系统工具/文件夹选项/壁纸' {
    $adv = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
    RegVal $adv 'ShowTaskViewButton' 1
    RegVal $adv 'HideFileExt' 0
    RegDelVal $adv 'NoFolderOptions'
    RegDelVal 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoTrayContextMenu'
    RegDelVal 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DisableTaskMgr'
    RegDelVal 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DisableRegistryTools'
    RegDelVal 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DisableLockWorkstation'
    RegDelVal 'HKCU:\Software\Policies\Microsoft\Windows\System' 'DisableCMD'
    $img0 = "$env:SystemRoot\Web\Wallpaper\Windows\img0.jpg"
    if (Test-Path $img0) {
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name Wallpaper -Value $img0 -ErrorAction SilentlyContinue
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name WallpaperStyle -Value '10' -ErrorAction SilentlyContinue
    }
}

Write-Host '5) 还原"带网络的安全模式"与启动菜单默认值' -ForegroundColor Cyan
Run-Step '注册表项：带网络的安全模式' {
    $reg = Join-Path $dataDir 'safeboot-network.reg'
    if (Test-Path $reg) { & reg.exe import $reg | Out-Null }
    else { $skipped.Add('没有找到 SafeBoot\Network 的备份（当初未限制过这项，或备份已删）') }
}
Run-Step '启动菜单恢复默认（删除 displaybootmenu 值）' { & bcdedit.exe /deletevalue "{bootmgr}" displaybootmenu 2>$null | Out-Null }

Write-Host '6) 删除注册表项 / 程序目录 / 备份副本' -ForegroundColor Cyan
if (-not $InstallDir) {
    $InstallDir = (Get-ItemProperty $regRoot -Name InstallDir -ErrorAction SilentlyContinue).InstallDir
}
Run-Step '删除注册表项（LabGuard / 卸载登记）' { RegDel $regRoot; RegDel $uninstKey }
if (-not $KeepFiles) {
    $safe = $false
    if ($InstallDir) {
        if ($InstallDir -like '*LabGuard*') { $safe = $true }
        elseif ($InstallDir -match '^[A-Za-z]:\\f\d+$') { $safe = $true }
    }
    if ($safe -and (Test-Path $InstallDir)) {
        Run-Step ('删除程序目录 ' + $InstallDir) { [System.IO.Directory]::Delete($InstallDir, $true) }
    } elseif ($InstallDir) {
        $skipped.Add('程序目录不像本程序目录，未删除：' + $InstallDir + '（确需删除请加 -InstallDir 指定）')
    }
}
if (Test-Path $mirror) { Run-Step ('删除系统级备份副本 ' + $mirror) { [System.IO.Directory]::Delete($mirror, $true) } }
Run-Step '清理配置/暂停标记' {
    foreach ($f in @('config.json','paused.flag','files.sha256','wallpaper-active.jpg')) {
        $p = Join-Path $dataDir $f
        if (Test-Path $p) { Remove-Item -LiteralPath $p -Force }
    }
}
if (-not $KeepLogs -and (Test-Path (Join-Path $dataDir 'logs'))) {
    Run-Step '删除日志目录' { [System.IO.Directory]::Delete((Join-Path $dataDir 'logs'), $true) }
}
if ((Test-Path $dataDir) -and (Get-ChildItem $dataDir -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) {
    Run-Step ('删除空目录 ' + $dataDir) { [System.IO.Directory]::Delete($dataDir, $true) }
}

Write-Host ''
Write-Host ('=== 完成：共执行 ' + $done.Count + ' 步 ===') -ForegroundColor Green
$done | ForEach-Object { Write-Host ('  ✓ ' + $_) }
if ($skipped.Count -gt 0) {
    Write-Host '需要注意：' -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host ('  ! ' + $_) }
}
if ($KeepLogs -and (Test-Path (Join-Path $dataDir 'logs'))) {
    Write-Host ('日志保留在：' + (Join-Path $dataDir 'logs')) -ForegroundColor Gray
}
Write-Host ''
Write-Host '建议重启电脑，让 U 盘/任务栏/安全模式等设置完全恢复。' -ForegroundColor Yellow
Write-Host '（本脚本不需要安装目录里的任何程序文件，安全模式下也能运行；有密码需求时它不做校验——请放在老师手上。）' -ForegroundColor Gray
