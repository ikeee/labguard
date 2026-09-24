<#
  「拔网线」端到端演练：临时禁用网卡 → 观察小助手是否在 20~30 秒内提示 → 恢复网卡。

  ⚠️ 请在**学生测试机**上运行（会断开这台机器约 1 分钟的网络）。
     不要在有 Codex 会话 / 远程桌面的机器上跑，网络一断会话就断。

  用法（管理员 PowerShell）：
      powershell -ExecutionPolicy Bypass -File test-disconnect.ps1
      powershell -ExecutionPolicy Bypass -File test-disconnect.ps1 -Nic "本地连接" -WaitSeconds 45
#>
[CmdletBinding()]
param(
    [string]$Nic,
    [int]$WaitSeconds = 45,
    [string]$LogDir = 'C:\ProgramData\LabGuard\logs'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请用【管理员】PowerShell 运行。'
}

if (-not $Nic) {
    $Nic = (Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceType -ne 24 } |
             Sort-Object ifIndex | Select-Object -First 1).Name
}
if (-not $Nic) { throw '找不到可用网卡，请用 -Nic 指定。' }

Write-Host ("=== 拔网线演练：网卡「" + $Nic + "」 ===") -ForegroundColor Cyan
Write-Host '注意：这台机器将断网约 1 分钟；结束时脚本会自动恢复网卡。' -ForegroundColor Yellow
if ((Read-Host '确认继续？(y/N)') -ne 'y') { Write-Host '已取消'; return }

$before = Get-NetIPAddress -InterfaceAlias $Nic -AddressFamily IPv4 -ErrorAction SilentlyContinue |
          Select-Object -ExpandProperty IPAddress
Write-Host ("当前 IP：" + ($before -join ', '))
$logBefore = (Get-ChildItem $LogDir -Filter *.log -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime | Select-Object -Last 1).FullName
$linesBefore = if ($logBefore) { (Get-Content $logBefore).Count } else { 0 }

try {
    Write-Host '>> 禁用网卡（模拟拔网线）…' -ForegroundColor Yellow
    Disable-NetAdapter -Name $Nic -Confirm:$false
    Write-Host ("   等待 " + $WaitSeconds + " 秒，观察小助手反应…")
    Start-Sleep -Seconds $WaitSeconds
    Write-Host '>> 期间日志：' -ForegroundColor Cyan
    if ($logBefore) { Get-Content $logBefore | Select-Object -Skip $linesBefore | Select-Object -Last 12 }
}
finally {
    Write-Host '>> 恢复网卡…' -ForegroundColor Yellow
    Enable-NetAdapter -Name $Nic -Confirm:$false
    Start-Sleep -Seconds 12
    Write-Host '>> 恢复后日志：' -ForegroundColor Cyan
    if ($logBefore) { Get-Content $logBefore | Select-Object -Last 8 }
}

Write-Host ''
Write-Host '预期结果：' -ForegroundColor Green
Write-Host '  · 断网 20~30 秒后应出现「你拔了网线或禁用了网络连接！请马上恢复网络，否则会全屏锁定！」'
Write-Host '  · 若设置里把「网络违规时全屏锁定」打开，此时还会出现全屏锁定，输入密码可解除'
Write-Host '  · 插回网线（本脚本已自动恢复）后，状态栏/日志应在一个轮询周期内回到正常'
