<#
  端到端验证「学生改 hosts 绕过集中 DNS」的防护：
  1) 单独运行 HostsGuard（真跑，只影响 hosts 文件的 ACL 与内容）
  2) 运行时模拟学生往 hosts 写一条映射（8.8.8.8 minecraft.net）
  3) 观察程序是否在轮询周期内还原
  4) 结束后校验 hosts 与运行前**字节一致**、ACL 恢复默认继承
  用法（管理员 PowerShell）：powershell -ExecutionPolicy Bypass -File test-hosts-watchdog.ps1
#>
[CmdletBinding()]
param([string]$Root = $PSScriptRoot, [int]$WaitSeconds = 30)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = $PSScriptRoot }
if (-not $Root) { $Root = (Get-Location).Path }
$hosts = "$env:SystemRoot\System32\drivers\etc\hosts"
$exe = Join-Path $Root 'LabGuard.SelfTest.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $Root '..\src\LabGuard.SelfTest\bin\Release\net48\LabGuard.SelfTest.exe' }
if (-not (Test-Path $exe)) { throw '找不到 LabGuard.SelfTest.exe' }

$before = (Get-FileHash $hosts -Algorithm SHA256).Hash
Write-Host ("运行前 hosts SHA256 = " + $before)
$beforeContent = Get-Content $hosts -Raw

$logFile = Join-Path $env:TEMP 'hosts-watchdog-test.log'
$proc = Start-Process -FilePath $exe -ArgumentList '--only', 'HostsGuard', '--seconds', ($WaitSeconds + 20) `
    -PassThru -NoNewWindow -RedirectStandardOutput $logFile
Start-Sleep -Seconds 5
Write-Host ('运行中 ACL 已收紧（True 预期）= ' + (Get-Acl $hosts).AreAccessRulesProtected)

Write-Host '模拟学生写入：8.8.8.8  minecraft.net'
Add-Content -LiteralPath $hosts -Value "`r`n8.8.8.8 minecraft.net" -Encoding UTF8
Start-Sleep -Seconds $WaitSeconds

$stillThere = (Get-Content $hosts -Raw).Contains('minecraft.net')
Write-Host ('等待 ' + $WaitSeconds + ' 秒后，映射是否还在（False 预期）= ' + $stillThere)

if (-not $proc.HasExited) { $proc | Wait-Process -Timeout ($WaitSeconds + 30) -ErrorAction SilentlyContinue }
if (-not $proc.HasExited) { $proc.Kill() }
Start-Sleep -Seconds 3

$after = (Get-FileHash $hosts -Algorithm SHA256).Hash
Write-Host ('结束后 hosts SHA256 = ' + $after)
Write-Host ('字节一致（True 预期）= ' + ($before -eq $after))
Write-Host ('ACL 恢复默认继承（True 预期）= ' + (-not (Get-Acl $hosts).AreAccessRulesProtected))

$ok = (-not $stillThere) -and ($before -eq $after) -and (-not (Get-Acl $hosts).AreAccessRulesProtected)
Write-Host ''
if ($ok) { Write-Host '结论：学生改 hosts 被自动还原，且测试后系统状态与测试前一致 ✅' -ForegroundColor Green }
else { Write-Host '结论：有不符合预期的项 ❌' -ForegroundColor Red }
Write-Host '（若中途异常退出，可用 备份内容 或 手工删除 minecraft.net 行 恢复）'
