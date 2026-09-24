<#
  安装【机房管理助手（复刻）】学生端。

  必须用【管理员】PowerShell 运行：
      powershell -ExecutionPolicy Bypass -File install.ps1

  与原始软件一致的地方：
    · 安装目录默认取"内存指纹"随机化路径：C:\f<物理内存MB><虚拟内存MB>（可显式指定 -InstallDir）
    · 装 Windows 服务 LabGuardSvc（SYSTEM 权限）+ 登录计划任务（最高权限）
    · 桌面/开始菜单快捷方式、卸载登记项
  与原始软件不同的地方（刻意改进）：
    · 不修改/关闭 Windows Defender 与杀毒软件、不改 bcdedit 之外的引导项
    · 不劫持浏览器主页（除非在设置里显式填写）、不冒充其它浏览器
    · 所有改动都能通过"设置→退出"或卸载程序一键还原
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$SimplePath,          # 用 C:\LabGuard（便于维护）
    [switch]$NoFirewallRule,
    [switch]$SkipShortcuts,
    [string]$Password,            # 无人值守：直接设好小助手密码
    [string]$OldPassword,         # 该机已设过密码时，用它通过校验后改密码
    [string]$ConfigJson,          # 无人值守：套用预设配置（presets\*.json）
    [string]$Dns,                 # 无人值守：集中 DNS（教师机 AdGuard IP，可逗号分隔多个）
    [switch]$DryRun               # 只打印将要做什么，不实际改动（给老师预演用）
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $here) { $here = (Get-Location).Path }

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '请用【管理员】PowerShell 运行本脚本。'
    }
}

function Get-MemoryFingerprintPath {
    # 复刻原版做法：用物理内存/虚拟内存 MB 数拼目录名，学生不容易猜到
    $cs = Get-CimInstance Win32_ComputerSystem
    $os = Get-CimInstance Win32_OperatingSystem
    $phys = [int]($cs.TotalPhysicalMemory / 1MB)
    $virt = [int]($os.TotalVirtualMemorySize / 1KB)
    return "C:\f$phys$virt"
}

Assert-Admin
Write-Host "=== 机房管理助手（复刻）安装 ===" -ForegroundColor Cyan
if ($DryRun) { Write-Host '【预演模式】只打印步骤，不实际改动系统。' -ForegroundColor Yellow }

function Invoke-Step([string]$desc, [scriptblock]$action) {
    Write-Host ('  · ' + $desc)
    if ($DryRun) { return }
    & $action
}

if (-not $InstallDir) {
    $InstallDir = if ($SimplePath) { 'C:\LabGuard' } else { Get-MemoryFingerprintPath }
}
$InstallDir = $InstallDir.TrimEnd('\')
Write-Host "安装目录：$InstallDir"

if (-not (Test-Path (Join-Path $here 'LabGuard.Agent.exe'))) {
    throw "当前目录没有找到程序文件，请先运行 scripts\build.ps1 生成 dist，并在 dist 目录里执行本脚本。"
}

# ---------------------------------------------------------------- 1. 复制文件
if ($DryRun) {
    Write-Host ('  · 将创建安装目录 ' + $InstallDir + ' 并把本目录文件复制过去')
} else {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    if ((Resolve-Path $here).Path.TrimEnd('\') -ne (Resolve-Path $InstallDir).Path.TrimEnd('\')) {
        Copy-Item -Path (Join-Path $here '*') -Destination $InstallDir -Recurse -Force
        Write-Host "已复制程序文件。" -ForegroundColor Green
    } else {
        Write-Host "已在目标目录中运行，跳过复制。" -ForegroundColor Yellow
    }
}

# 安装目录权限：管理员/SYSTEM 完全控制，普通用户只读+执行（对应原版 regini 加固思路）
if ($DryRun) {
    Write-Host '  · 将把安装目录权限收紧为「管理员/SYSTEM 完全控制，普通用户只读」'
} else {
    $acl = Get-Acl $InstallDir
    $acl.SetAccessRuleProtection($true, $false)
    $inheritFlags = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
                    [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propFlags = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'FullControl', $inheritFlags, $propFlags, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('NT AUTHORITY\SYSTEM', 'FullControl', $inheritFlags, $propFlags, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Users', 'ReadAndExecute', $inheritFlags, $propFlags, $allow)))
    Set-Acl -Path $InstallDir -AclObject $acl
    Write-Host "已加固目录权限。" -ForegroundColor Green
}

# ---------------------------------------------------------------- 2. 壁纸素材
$wallpaperDir = Join-Path $InstallDir 'wallpaper'
if (-not (Test-Path $wallpaperDir) -or -not (Get-ChildItem $wallpaperDir -Filter *.jpg -ErrorAction SilentlyContinue)) {
    if (-not $DryRun) { & (Join-Path $here 'make-wallpapers.ps1') -OutDir $wallpaperDir }
    else { Write-Host '  · 将生成 6 张机房规范壁纸' }
} else {
    Write-Host "已存在壁纸素材，跳过生成。"
}

# ---------------------------------------------------------------- 3. 注册表登记
$regRoot = 'HKLM:\SOFTWARE\LabGuard'
if (-not $DryRun) {
    New-Item -Path $regRoot -Force | Out-Null
    Set-ItemProperty -Path $regRoot -Name 'Installed' -Value 1 -Type DWord
    Set-ItemProperty -Path $regRoot -Name 'InstallDir' -Value $InstallDir -Type String
    Set-ItemProperty -Path $regRoot -Name 'Version' -Value 1 -Type DWord
}
if (-not $DryRun) { Write-Host "已写入注册表（HKLM\SOFTWARE\LabGuard）。" -ForegroundColor Green }

# ---------------------------------------------------------------- 4. 服务
$svcExe = Join-Path $InstallDir 'LabGuard.Service.exe'
if (Get-Service -Name 'LabGuardSvc' -ErrorAction SilentlyContinue) {
    Write-Host "服务已存在，先停止并删除旧服务。"
    if (-not $DryRun) {
        & sc.exe stop LabGuardSvc | Out-Null
        Start-Sleep -Seconds 2
        & sc.exe delete LabGuardSvc | Out-Null
        Start-Sleep -Seconds 1
    }
}
if (-not $DryRun) {
    & sc.exe create LabGuardSvc binPath= "`"$svcExe`"" start= auto obj= LocalSystem DisplayName= "机房管理助手守护服务（复刻）" | Out-Null
    & sc.exe description LabGuardSvc "机房管理助手（复刻）：系统级策略守护，保证小助手与电子教室保护持续生效。" | Out-Null
# 原版用 sc failure actions=reboot（服务异常就重启电脑），这里默认保守处理：只自动重启服务
    & sc.exe failure LabGuardSvc actions= restart/60000/restart/60000/restart/60000 reset= 900 | Out-Null
}
if (-not $DryRun) { Write-Host "已创建服务 LabGuardSvc。" -ForegroundColor Green }

# ---------------------------------------------------------------- 5. 登录自启（最高权限计划任务）
$agentExe = Join-Path $InstallDir 'LabGuard.Agent.exe'
$taskName = 'LabGuard\Agent'
if (-not $DryRun) { & schtasks.exe /create /tn $taskName /tr "`"$agentExe`"" /sc onlogon /rl highest /f | Out-Null }
if (-not $DryRun) { Write-Host "已创建登录自启计划任务 $taskName。" -ForegroundColor Green }

# ---------------------------------------------------------------- 5.5 无人值守配置（可选）
if ($Password -or $ConfigJson -or $Dns) {
    $settingsExe = Join-Path $InstallDir 'LabGuard.Settings.exe'
    if (-not $Password) {
        Write-Host '  提示：未提供 -Password，安装完成后请手动运行【设置】程序设置密码（未设密码前监控不启用）。' -ForegroundColor Yellow
    } elseif ($DryRun) {
        Write-Host ('  · 将执行：LabGuard.Settings.exe --init ***' +
            ($(if ($ConfigJson) { ' --import ' + $ConfigJson } else { '' })) +
            ($(if ($Dns) { ' --dns ' + $Dns } else { '' })))
    } else {
        $argList = @('--init', $Password)
        if ($OldPassword) { $argList += @('--old', $OldPassword) }
        if ($ConfigJson) { $argList += @('--import', $ConfigJson) }
        if ($Dns) { $argList += @('--dns', $Dns) }
        & $settingsExe @argList
        Write-Host "已完成无人值守配置（密码 + 预设 + DNS）。" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- 6. 快捷方式
if (-not $SkipShortcuts) {
    if ($DryRun) { Write-Host '  · 将创建桌面与开始菜单快捷方式' }
    else {
    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
    $programs = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) '机房管理助手'
    New-Item -ItemType Directory -Force -Path $programs | Out-Null

    $lnk = $shell.CreateShortcut((Join-Path $desktop '学生机房管理助手.lnk'))
    $lnk.TargetPath = $agentExe
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = '机房管理助手（复刻）小助手'
    $lnk.Save()

    $lnk2 = $shell.CreateShortcut((Join-Path $programs '设置.lnk'))
    $lnk2.TargetPath = (Join-Path $InstallDir 'LabGuard.Settings.exe')
    $lnk2.WorkingDirectory = $InstallDir
    $lnk2.Save()

    $lnk3 = $shell.CreateShortcut((Join-Path $programs '卸载.lnk'))
    $lnk3.TargetPath = (Join-Path $InstallDir 'LabGuard.Uninstall.exe')
    $lnk3.WorkingDirectory = $InstallDir
    $lnk3.Save()

    $lnk4 = $shell.CreateShortcut((Join-Path $programs '解除锁定（输入密码）.lnk'))
    $lnk4.TargetPath = (Join-Path $InstallDir 'LabGuard.Agent.exe')
    $lnk4.Arguments = '--unlock'
    $lnk4.WorkingDirectory = $InstallDir
    $lnk4.Save()
    Write-Host "已创建快捷方式。" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- 7. 卸载登记项
$uninstKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabGuardReplica'
if (-not $DryRun) {
    New-Item -Path $uninstKey -Force | Out-Null
    Set-ItemProperty -Path $uninstKey -Name 'DisplayName'    -Value '机房管理助手（复刻）'
    Set-ItemProperty -Path $uninstKey -Name 'UninstallString' -Value "`"$InstallDir\LabGuard.Uninstall.exe`""
    Set-ItemProperty -Path $uninstKey -Name 'InstallLocation' -Value $InstallDir
    Set-ItemProperty -Path $uninstKey -Name 'Publisher'       -Value 'LabGuard Replica'
    Set-ItemProperty -Path $uninstKey -Name 'DisplayVersion'  -Value '1.0.0'
}

# ---------------------------------------------------------------- 8. 启动
if (-not $DryRun) {
    Start-Service LabGuardSvc -ErrorAction SilentlyContinue
    Write-Host "已启动守护服务。" -ForegroundColor Green
}

Write-Host ''
Write-Host '安装完成！接下来：' -ForegroundColor Yellow
Write-Host '  1) 运行桌面/开始菜单里的【设置】，设置密码并逐项选择管控策略（未设置密码前监控不会启用）'
Write-Host '  2) 托盘会出现小助手图标：系统功能（F）→ 暂停监控 / 退出程序（都要密码）'
Write-Host "  3) 想让小助手随电子教室一起开机运行，确认计划任务 $taskName 处于启用状态"
Write-Host ''
Write-Host "提示：建议先在【一台】学生机上试运行一周，确认与你的电子教室/考试软件兼容后再批量部署。" -ForegroundColor Yellow
