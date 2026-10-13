#Requires -Version 5.1
<#
  USB 无线网卡 / 手机 USB 共享"插上不出现""能连上但打不开"的**只读**诊断

  背景（老师问的）：学生机插 USB WiFi 不出现无线网卡 —— 到底跟 LabGuard 有没有关系？
  口径（本脚本就是来证实/证伪它的）：
    · LabGuard **从不**禁用/隐藏网卡、不碰无线服务（WlanSvc 等）、不碰 USB 控制器/集线器、不设任何设备安装策略；
      它只写三处系统设置：① usbstor（**只禁 USB 存储**）② 网卡 DNS（集中过滤）③ 被判据网卡的 IP/网关（改回原值）。
    · 唯一可能"让网卡不出现"的路径：**这块网卡靠自带的虚拟光驱/安装盘装驱动** → usbstor 被禁 → 驱动装不上。
  所以本脚本的第一件事就是回答：**设备管理器里有没有"从无到有"的新设备**（有 = 驱动没装上 = 可能是我们；
  完全没有 = 设备压根没被 Windows 看到 = 端口/线/供电/网卡本体/学校策略，跟我们无关）。

  本脚本**只读**：只查状态、只读日志，不改任何系统设置、不安装/不删除任何东西。

  用法（在出问题的**学生机**上跑）：
      powershell -ExecutionPolicy Bypass -File diagnose-usb-nic.ps1
      powershell -ExecutionPolicy Bypass -File diagnose-usb-nic.ps1 -DataDir D:\lg-sandbox -Days 3
  把整屏输出（或 -OutFile 生成的 txt）发给老师/我即可定位。
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:ProgramData 'LabGuard'),
    [int]$Days = 3,
    [string]$OutFile
)

$report = New-Object System.Collections.ArrayList
function W([string]$s = '') { [void]$report.Add($s); Write-Host $s }

W '================ LabGuard USB 网卡诊断（只读）================'
W ('时间：' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '    机器：' + $env:COMPUTERNAME + '    用户：' + $env:USERNAME)
W ('LabGuard 数据目录：' + $DataDir)
W

# ---------------------------------------------------------------- ① 我们自己的状态
W '① LabGuard 自身状态'
$svc = Get-Service -Name 'LabGuardSvc' -ErrorAction SilentlyContinue
if ($svc) { W ('   服务 LabGuardSvc：' + $svc.Status) } else { W '   服务 LabGuardSvc：未安装（= 本机没有受管控，排除我们）' }
$procs = @(Get-Process -Name 'LabGuard.Service','LabGuard.Agent','LabGuard.Settings' -ErrorAction SilentlyContinue)
W ('   相关进程：' + $(if ($procs.Count -eq 0) { '无' } else { ($procs | ForEach-Object { $_.ProcessName + '(' + $_.Id + ')' }) -join ', ' }))
$cfgPath = Join-Path $DataDir 'config.json'
W ('   配置文件存在：' + (Test-Path -LiteralPath $cfgPath) + '（存在 = 这台机被配置过管控；不存在 = 基本可排除我们）')
W

# ---------------------------------------------------------------- ② 网卡清单（含隐藏/已禁用）
W '② 网络适配器（**含已禁用/隐藏**——"网络连接"面板默认不显示已禁用的网卡）'
try {
    $nics = Get-NetAdapter -IncludeHidden -ErrorAction Stop | Sort-Object ifIndex
    foreach ($n in $nics) {
        W ('   [' + $n.Status + '/' + $n.AdminStatus + '] ' + $n.Name + '  |  ' + $n.InterfaceDescription +
          '  |  ' + $n.MacAddress + '  |  ifIndex=' + $n.ifIndex)
    }
    $usbNics = @($nics | Where-Object { $_.InterfaceDescription -match 'USB|Wireless|WLAN|802\.11|RNDIS|Remote NDIS|Wi-?Fi' })
    if ($usbNics.Count -eq 0) { W '   → **没有看到任何 USB/无线类适配器**（见下面 ③④ 的判定）' }
    else { W ('   → 看到 USB/无线类适配器 ' + $usbNics.Count + ' 个（说明 Windows 已经认到它）') }
} catch { W ('   查询失败：' + $_.Exception.Message) }
W

# ---------------------------------------------------------------- ③ 无线服务
W '③ 无线/网络相关服务（学校域策略停掉 WlanSvc 也会导致"没有无线网卡"）'
foreach ($name in @('WlanSvc', 'netprofm', 'NlaSvc', 'Dot3Svc', 'SharedAccess')) {
    $s = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($s) { W ('   ' + $name + '：' + $s.Status + '（启动类型 ' + (Get-CimInstance Win32_Service -Filter ("Name='" + $name + "'") -ErrorAction SilentlyContinue).StartMode + '）' ) }
    else { W ('   ' + $name + '：不存在') }
}
W

# ---------------------------------------------------------------- ④ USB 设备与问题设备（决定性判定）
W '④ 设备管理器视角：USB 设备与"有问题的设备"（**这一节决定是不是我们**）'
try {
    $problem = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.Status -ne 'OK' })
    if ($problem.Count -eq 0) { W '   · 当前**没有**任何带问题的设备（黄叹号）' }
    else {
        W ('   · 带问题的设备 ' + $problem.Count + ' 个（前 15 个）：')
        foreach ($d in ($problem | Select-Object -First 15)) {
            W ('       [' + $d.Status + '/' + $d.Class + '] ' + $d.FriendlyName + '   ' + $d.InstanceId)
        }
    }
    $unknown = @($problem | Where-Object { $_.Class -eq $null -or $_.Class -eq 'Unknown' -or $_.FriendlyName -match '未知|Unknown' })
    $netUsb = @()
    try { $netUsb = @(Get-PnpDevice -PresentOnly -Class Net -ErrorAction Stop | Where-Object { $_.InstanceId -like 'USB*' }) } catch { }
    if ($netUsb.Count -gt 0) {
        W ('   · **USB 网卡已被 Windows 认到** ' + $netUsb.Count + ' 个：')
        foreach ($d in $netUsb) { W ('       [' + $d.Status + '] ' + $d.FriendlyName + '   ' + $d.InstanceId) }
    } else {
        W '   · **没有任何 USB 网卡设备**（Windows 压根没认到这块网卡）'
    }
    if ($unknown.Count -gt 0) {
        W ''
        W '   ★ 判定：有"未知设备/无法识别的设备" → 设备插上了但**驱动没装好**。'
        W '     若这块网卡需要靠自带虚拟光驱装驱动，而本机开了 LabGuard 的「USB 存储禁用」，就可能是我们挡的（见 ⑥ 的日志）。'
    } else {
        W ''
        W '   ★ 判定：既没有"未知设备"、也没有任何 USB 网卡设备 → **问题不在驱动安装层**，'
        W '     多半是：USB 端口/线/供电、BIOS 禁了 USB、或学校域策略/第三方管控在拦（与我们无关，见 ⑥）。'
    }
} catch { W ('   查询失败（需要设备管理器模块）：' + $_.Exception.Message) }
W

# ---------------------------------------------------------------- ⑤ 无线接口与他人视角
W '⑤ Windows 自己的无线接口视图（netsh wlan）'
try {
    $wlan = & netsh.exe wlan show interfaces 2>&1 | Out-String
    if ($wlan -match '不存在|not running|No wireless') { W ('   netsh wlan 说：' + ($wlan.Trim() -split "`r?`n" | Select-Object -First 2 | Out-String).Trim()) }
    else { W ('   ' + (($wlan.Trim() -split "`r?`n" | Select-Object -First 8) -join ' / ')) }
} catch { W ('   失败：' + $_.Exception.Message) }
W

# ---------------------------------------------------------------- ⑥ 我们动过什么（日志证据）
W ('⑥ LabGuard 日志里最近 ' + $Days + ' 天与本问题相关的动作（我们只可能写这三处）')
$logs = @()
try { $logs = Get-ChildItem -LiteralPath (Join-Path $DataDir 'logs') -Filter 'guard-*.log' -ErrorAction Stop |
        Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-$Days) } } catch { }
if ($logs.Count -eq 0) { W '   （没有日志文件）' }
$hits = New-Object System.Collections.ArrayList
foreach ($f in $logs) {
    # 日志是 ANSI(GBK) 写的；这里只匹配 **ASCII 标记**（usbstor / netsh / SET-REG…），不受编码影响
    $text = [Text.Encoding]::GetEncoding(936).GetString([IO.File]::ReadAllBytes($f.FullName))
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match 'usbstor' -or $line -match 'netsh\.exe' -or $line -match 'interface (ip )?set ' -or
            $line -match 'USB') { [void]$hits.Add(('   ' + $f.Name + '  ' + $line.Trim())) }
    }
}
if ($hits.Count -eq 0) {
    W '   → **日志里没有任何 usbstor / netsh / USB 相关动作**：说明我们没动过这类设置（基本可排除我们）'
} else {
    W ('   共 ' + $hits.Count + ' 条（前 20 条）：')
    foreach ($h in ($hits | Select-Object -First 20)) { W $h }
    W '   → 上面若有 `usbstor …Start = 4`：本机 USB 存储被禁（可能挡了"靠虚拟光驱装驱动"的网卡）；'
    W '     若有 `interface ip set dns`：本机某块网卡的 DNS 被改成了集中过滤服务器（表现为"连上但打不开"）；'
    W '     若有 `interface ip set address`：本机**判据网卡**的 IP 被还原过（表现为"时通时断"）。'
}
W

# ---------------------------------------------------------------- ⑦ 结论与建议
W '⑦ 怎么定性（把 ②③④⑥ 对起来看）'
W '   · 「设备管理器有未知设备 + 日志里有 usbstor Start = 4」 → **可能是我们**：装驱动时临时关掉「USB 存储禁用」，装好再开。'
W '   · 「设备管理器完全没有新设备」 → **不是我们**：查 USB 口/线/供电（换后置口、不要用延长线/集线器）、BIOS 的 USB 设置、学校域策略/其它管控软件。'
W '   · 「网卡在，能连上但打不开网页」 → 大概率是 DNS：检查我们是否开了「集中 DNS 锁定」且把这块网卡的 DNS 改成了校内地址；'
W '     建议把这台机的 USB 网卡关键字加进「忽略的网卡关键字」，并保持「DNS 只锁判据网卡」为开。'
W ''
W '把以上输出全文发给老师/开发者即可定位。本脚本没有修改任何系统设置。'

if ($OutFile) {
    [IO.File]::WriteAllText($OutFile, ($report -join "`r`n"), (New-Object Text.UTF8Encoding($true)))
    Write-Host ('已保存报告：' + $OutFile)
}
