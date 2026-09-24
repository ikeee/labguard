<#
  教师端监视器（可选，零依赖）：接收学生机小助手的心跳，实时看到哪台机器还在、哪台被停了。

  用法（在教师机上，管理员 PowerShell 或普通权限均可）：
      powershell -ExecutionPolicy Bypass -File monitor-receiver.ps1            # 监听 http://本机IP:8080/
      powershell -ExecutionPolicy Bypass -File monitor-receiver.ps1 -Port 8080
  然后在学生机设置里把小助手的「心跳上报地址」填成：http://<教师机IP>:8080/heartbeat
  浏览 http://<教师机IP>:8080/ 就能看到表格：机位 / 最后上报时间 / 服务状态 / 是否暂停 / DNS / 是否疑似被破坏
#>
[CmdletBinding()]
param([int]$Port = 8080, [int]$StaleSeconds = 180)

$ErrorActionPreference = 'Stop'
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://+:$Port/")
try { $listener.Start() }
catch {
    Write-Host '启动失败：普通用户绑定 http://+:8080 需要管理员权限或 urlacl。' -ForegroundColor Yellow
    Write-Host '可改用（管理员执行一次）：netsh http add urlacl url=http://+:8080/ user=Everyone'
    throw
}
Write-Host ("教师端监视器已启动：http://" + $env:COMPUTERNAME + ":" + $Port + "   （Ctrl+C 退出）") -ForegroundColor Green
Write-Host ("学生机设置里把「心跳上报地址」填成： http://" + (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike '169.254*' -and $_.IPAddress -ne '127.0.0.1' } |
    Select-Object -First 1).IPAddress + ":" + $Port + "/heartbeat")

$store = @{}
while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    try {
        if ($ctx.Request.HttpMethod -eq 'POST') {
            $body = (New-Object System.IO.StreamReader($ctx.Request.InputStream, [Text.Encoding]::UTF8)).ReadToEnd()
            try { $data = $body | ConvertFrom-Json } catch { $data = $null }
            if ($data -and $data.machine) {
                $store[$data.machine] = [pscustomobject]@{ data = $data; at = Get-Date }
            }
            $ctx.Response.StatusCode = 204
        } else {
            $now = Get-Date
            $rows = $store.Values | Sort-Object { $_.data.machine } | ForEach-Object {
                $age = [int]($now - $_.at).TotalSeconds
                $stale = $age -gt $StaleSeconds
                $color = if ($stale) { '#c00' } elseif ($_.data.service -eq $false -or $_.data.tamper -eq $true) { '#e67e22' } else { '#2e7d32' }
                "<tr style='color:$color'><td>$($_.data.machine)</td><td>$($_.data.user)</td><td>$age 秒前</td>" +
                "<td>服务=$($_.data.service)</td><td>暂停=$($_.data.paused)</td><td>只监视有线=$($_.data.wiredOnly)</td>" +
                "<td>$($_.data.dns)</td><td>疑似被破坏=$($_.data.tamper)</td><td>$($_.data.time)</td></tr>"
            }
            $html = @"
<html><head><meta charset='utf-8'><title>机房小助手状态</title>
<meta http-equiv='refresh' content='5'>
<style>body{font-family:微软雅黑,Segoe UI;margin:16px}h1{font-size:18px}
table{border-collapse:collapse}td,th{border:1px solid #ddd;padding:4px 8px;font-size:13px}
th{background:#f0f0f0}</style></head><body>
<h1>机房小助手状态（每 5 秒刷新；红色=超过 $StaleSeconds 秒没上报，疑似被停/被删）</h1>
<table><tr><th>机位</th><th>用户</th><th>最后上报</th><th>服务</th><th>暂停</th><th>只监视有线</th><th>DNS</th><th>疑似被破坏</th><th>上报时间</th></tr>
$($rows -join "`n")
</table><p>共 $($store.Count) 台机器上报过。</p></body></html>
"@
            $buf = [Text.Encoding]::UTF8.GetBytes($html)
            $ctx.Response.ContentType = 'text/html; charset=utf-8'
            $ctx.Response.OutputStream.Write($buf, 0, $buf.Length)
        }
    } catch { } finally { $ctx.Response.Close() }
}
