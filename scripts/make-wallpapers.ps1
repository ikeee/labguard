<#
  生成 6 张机房规范壁纸（1920x1080，含机房行为规范文字与"机器编号"占位）。
  说明：不复用任何第三方素材（壁纸为本脚本自绘），本脚本自己画，可自由分发。
  用法：powershell -ExecutionPolicy Bypass -File make-wallpapers.ps1 -OutDir .\wallpaper
#>
[CmdletBinding()]
param([string]$OutDir = '.\wallpaper')

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$themes = @(
    @{ Name = '1-专注课堂';  C1 = '#0F2027'; C2 = '#2C5364'; Accent = '#4FD1C5';
       Title = '机房行为规范'; Rules = @('① 按座位号就座，不随意换机','② 不玩游戏、不下载安装软件','③ 完成课堂任务后按秩序关机') },
    @{ Name = '2-代码空间';  C1 = '#141E30'; C2 = '#243B55'; Accent = '#63B3ED';
       Title = '把时间用在创造上'; Rules = @('① 上课先看任务单','② 遇到问题先自己看报错','③ 同学互助，不代替完成') },
    @{ Name = '3-像素蓝';    C1 = '#0B1F3A'; C2 = '#1B3B6F'; Accent = '#90CDF4';
       Title = '爱护设备 从我做起'; Rules = @('① 不拍打、不私自拆装设备','② 保持桌面整洁，水杯远离机箱','③ 发现故障立即报告老师') },
    @{ Name = '4-静谧紫';    C1 = '#1A1033'; C2 = '#3B2A6B'; Accent = '#D6BCFA';
       Title = '安静 · 专注 · 高效'; Rules = @('① 保持安静，讨论放低声音','② 手机按要求统一存放','③ 下课整理座位，关闭显示器') },
    @{ Name = '5-暖橙';      C1 = '#3B1F0B'; C2 = '#7B3F00'; Accent = '#F6AD55';
       Title = '今天的目标'; Rules = @('① 完成本节任务单全部练习','② 记录一个今天新学会的知识点','③ 有困难举手，老师马上到') },
    @{ Name = '6-青绿';      C1 = '#06251E'; C2 = '#0E4F3C'; Accent = '#68D391';
       Title = '实训机房'; Rules = @('① 先洗手后上机','② 按流程关机，不直接断电','③ 离开前椅子归位') }
)

foreach ($t in $themes) {
    $bmp = New-Object System.Drawing.Bitmap 1920, 1080
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    $rect = New-Object System.Drawing.Rectangle 0, 0, 1920, 1080
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.ColorTranslator]::FromHtml($t.C1),
        [System.Drawing.ColorTranslator]::FromHtml($t.C2),
        35.0)
    $g.FillRectangle($brush, $rect)

    # 装饰斜线
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(28, 255, 255, 255)), 2
    for ($i = -1080; $i -lt 1920; $i += 60) {
        $g.DrawLine($pen, $i, 1080, ($i + 700), 0)
    }

    $accent = [System.Drawing.ColorTranslator]::FromHtml($t.Accent)
    $accentBrush = New-Object System.Drawing.SolidBrush $accent
    $whiteBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $softBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(225, 255, 255, 255))

    $titleFont = New-Object System.Drawing.Font "微软雅黑", 88, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $ruleFont = New-Object System.Drawing.Font "微软雅黑", 38, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $numFont = New-Object System.Drawing.Font "黑体", 62, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)

    $g.DrawString($t.Title, $titleFont, $whiteBrush, 130, 250)
    $g.FillRectangle($accentBrush, 132, 380, 260, 8)
    $y = 470
    foreach ($r in $t.Rules) {
        $g.DrawString($r, $ruleFont, $softBrush, 130, $y)
        $y += 78
    }

    # 右上角机器编号占位（安装后由程序按计算机名后 6 位生成正式壁纸）
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Far
    $numRect = New-Object System.Drawing.RectangleF 0, 40, 1790, 90
    $g.DrawString("MACHINE", $numFont, $softBrush, $numRect, $fmt)

    $g.Dispose()
    $path = Join-Path $OutDir ("wallpaper-" + $t.Name + ".jpg")
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bmp.Dispose()
    Write-Host "已生成 $path"
}
Write-Host "共生成 $($themes.Count) 张壁纸到 $OutDir" -ForegroundColor Green
