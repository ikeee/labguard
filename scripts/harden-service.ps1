<#
  给已安装的「LabGuard」做抗破解加固（可重复执行）：
    1) 服务权限：普通用户只能查询/启动，**不能停止**（sc sdset）
    2) 服务崩溃/被异常终止后 5 秒内自动重启（sc failureflag + sc failure）
    3) 登记为"安全模式也启动"的服务（避免学生用安全模式绕过）
    4) 建立系统级备份副本 %ProgramData%\LabGuard\payload（只有 SYSTEM/管理员能访问），
       程序文件被删/被改后可自动恢复（LabGuard.Service.exe --restore-files）
  用法（管理员 PowerShell）：powershell -ExecutionPolicy Bypass -File harden-service.ps1 [-InstallDir 路径]
#>
[CmdletBinding()]
param([string]$InstallDir)

$ErrorActionPreference = 'Stop'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请用【管理员】PowerShell 运行。'
}

$svc = 'LabGuardSvc'
if (-not (Get-Service -Name $svc -ErrorAction SilentlyContinue)) {
    throw "没有找到服务 $svc，请先安装（安装程序或 install.ps1）。"
}
if (-not $InstallDir) {
    $InstallDir = (Get-ItemProperty 'HKLM:\SOFTWARE\LabGuard' -Name InstallDir -ErrorAction SilentlyContinue).InstallDir
}
if (-not $InstallDir -or -not (Test-Path $InstallDir)) { throw '找不到安装目录，请用 -InstallDir 指定。' }
Write-Host ("安装目录：" + $InstallDir)

Write-Host '1) 服务权限：普通用户不能停止' -ForegroundColor Cyan
$sddl = 'D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCSWRPRC;;;BU)'
& sc.exe sdset $svc $sddl | Out-Null
Write-Host '   当前 DACL：' 
(& sc.exe sdshow $svc) | ForEach-Object { '   ' + $_ }

Write-Host '2) 异常终止后 5 秒自动重启' -ForegroundColor Cyan
& sc.exe failureflag $svc 1 | Out-Null
& sc.exe failure $svc actions= restart/5000/restart/5000/restart/5000 reset= 300 | Out-Null
& sc.exe qfailure $svc | Where-Object { $_ -match 'FAILURE_ACTIONS|RESTART|RESET_PERIOD' } | ForEach-Object { '   ' + $_.Trim() }

Write-Host '3) 安全模式**不**加载本服务（留给老师当后路），并清理历史登记' -ForegroundColor Cyan
foreach ($root in @('HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal',
                    'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network')) {
    $key = Join-Path $root $svc
    if (Test-Path $key) {
        $sub = ($root -replace '^HKLM:\\', '') + '\' + $svc
        try { [Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree($sub, $false); Write-Host ("   已移除历史登记 " + $key) }
        catch { Write-Host ("   移除失败：" + $_.Exception.Message) }
    }
}
Write-Host '   → 进安全模式（无网络）时服务/小助手不会加载，可在其中彻底卸载或修复'

Write-Host '4) 建立系统级备份副本（用于被删自动恢复）' -ForegroundColor Cyan
$mirror = Join-Path $env:ProgramData 'LabGuard\payload'
New-Item -ItemType Directory -Force -Path $mirror | Out-Null
foreach ($f in @('LabGuard.Agent.exe','LabGuard.Service.exe','LabGuard.Settings.exe','LabGuard.Uninstall.exe',
                 'LabGuard.Launcher.exe','LabGuard.Core.dll')) {
    $src = Join-Path $InstallDir $f
    if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $mirror $f) -Force }
}
if (Test-Path (Join-Path $InstallDir 'wallpaper')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $mirror 'wallpaper') | Out-Null
    Copy-Item -Path (Join-Path $InstallDir 'wallpaper\*') -Destination (Join-Path $mirror 'wallpaper') -Force
}
$acl = Get-Acl $mirror
$acl.SetAccessRuleProtection($true, $false)
$inherit = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('NT AUTHORITY\SYSTEM', 'FullControl', $inherit, 'None', 'Allow')))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'FullControl', $inherit, 'None', 'Allow')))
Set-Acl -Path $mirror -AclObject $acl
Write-Host ("   备份副本：" + $mirror + "（" + ((Get-ChildItem $mirror -Recurse -File | Measure-Object).Count) + " 个文件，仅 SYSTEM/管理员可访问）")

Write-Host ''
Write-Host '加固完成。' -ForegroundColor Green
Write-Host '· 学生（标准账户）无法停止服务、无法删除程序目录、删了文件也会自动恢复'
Write-Host '· 若学生是本地管理员，以上都只是"减速带"——请配合：收回管理员权限 / 无盘还原 / 教师端监视器'
