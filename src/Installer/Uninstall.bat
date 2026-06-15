@echo off
setlocal

net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

sc stop WindowsAppProtectorService >nul 2>&1
sc delete WindowsAppProtectorService >nul 2>&1
taskkill /IM WindowsAppProtector.WinUI.exe /F >nul 2>&1
taskkill /IM WindowsAppProtector.Service.exe /F >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command "$root='HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'; Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object { $p=Get-ItemProperty $_.PsPath -ErrorAction SilentlyContinue; if ($p.WindowsAppProtectorManaged -eq '1') { Remove-Item $_.PsPath -Force -ErrorAction SilentlyContinue } }"

rmdir /s /q "%ProgramData%\Windows App Protector" >nul 2>&1
rmdir /s /q "%AppData%\WindowsAppProtector.WinUI" >nul 2>&1
rmdir /s /q "%AppData%\WindowsAppProtector" >nul 2>&1
del "%PUBLIC%\Desktop\Windows App Protector.lnk" >nul 2>&1
del "%ProgramData%\Microsoft\Windows\Start Menu\Programs\Windows App Protector.lnk" >nul 2>&1
del "%ProgramData%\Microsoft\Windows\Start Menu\Programs\Windows App Protector Uninstall.lnk" >nul 2>&1
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsAppProtector" /f >nul 2>&1

cd /d "%ProgramFiles%"
rmdir /s /q "Windows App Protector" >nul 2>&1

echo Windows App Protector has been removed.
pause
