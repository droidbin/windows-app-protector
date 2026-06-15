@echo off
setlocal
cd /d "%~dp0"

set "SAVED_PATH=%PATH%"
set "PATH="
set "Path=%SAVED_PATH%"

set "PROJECT=src\WinUI3\WindowsAppProtector.WinUI.csproj"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSBUILD="

if exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\Current\Bin\amd64\MSBuild.exe`) do (
    set "MSBUILD=%%i"
  )
)

if not "%MSBUILD%"=="" (
  "%MSBUILD%" "%PROJECT%" /restore /p:Configuration=Release /p:Platform=x64 /p:UseSharedCompilation=false
  exit /b %errorlevel%
)

dotnet --list-sdks >nul 2>&1
if errorlevel 1 (
  echo .NET SDK or Visual Studio MSBuild is not installed.
  exit /b 1
)

echo Visual Studio MSBuild was not found. Falling back to dotnet build.
dotnet build "%PROJECT%" -c Release -p:Platform=x64 -p:UseSharedCompilation=false
exit /b %errorlevel%
