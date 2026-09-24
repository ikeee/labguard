@echo off
rem 双击本文件 = 以管理员身份运行同目录的 install.ps1（老师部署用，避免手动开管理员 PowerShell）
rem 想无人值守安装：先在命令行里带参数运行 install.ps1，或修改下面的 ARGS
setlocal
set ARGS=-ExecutionPolicy Bypass -NoProfile -File "%~dp0install.ps1"
echo 正在以管理员身份启动安装（会弹 UAC 确认）...
powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '%ARGS%'"
endlocal
timeout /t 3 >nul
