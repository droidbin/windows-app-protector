$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Get-Process WindowsAppProtector.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force

& "$root\build_winui3.bat"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$payloadSource = Join-Path $root "src\WinUI3\bin\x64\Release\net8.0-windows10.0.19041.0"
$serviceSource = Join-Path $root "src\Service\WindowsAppProtector.Service.cs"
$serviceBuildDir = Join-Path $root "build\service"
$serviceExe = Join-Path $serviceBuildDir "WindowsAppProtector.Service.exe"
$buildDir = Join-Path $root "build\installer"
$payloadDir = Join-Path $buildDir "payload"
$distDir = Join-Path $root "dist\installer"
$setupSource = Join-Path $root "src\Installer\Setup.cs"
$manifest = Join-Path $root "src\Installer\app.manifest"
$setupExe = Join-Path $distDir "Setup.exe"
$zipPath = Join-Path $buildDir "payload.zip"

Remove-Item -LiteralPath $buildDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $distDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $serviceBuildDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $serviceBuildDir | Out-Null
New-Item -ItemType Directory -Path $payloadDir | Out-Null
New-Item -ItemType Directory -Path $distDir | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

& $csc /nologo /target:exe /optimize+ /reference:System.ServiceProcess.dll /reference:System.Runtime.Serialization.dll /out:$serviceExe $serviceSource
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Copy-Item -Path (Join-Path $payloadSource "*") -Destination $payloadDir -Recurse -Force
Copy-Item -LiteralPath $serviceExe -Destination $payloadDir -Force

& $csc /nologo /target:winexe /optimize+ /win32manifest:$manifest /reference:System.IO.Compression.FileSystem.dll /out:$setupExe $setupSource
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($payloadDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipBytes = [System.IO.File]::ReadAllBytes($zipPath)
$lengthBytes = [BitConverter]::GetBytes([Int64]$zipBytes.Length)
$markerBytes = [System.Text.Encoding]::ASCII.GetBytes("WAPZIP01")
$stream = [System.IO.File]::Open($setupExe, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
try {
    $stream.Write($zipBytes, 0, $zipBytes.Length)
    $stream.Write($lengthBytes, 0, $lengthBytes.Length)
    $stream.Write($markerBytes, 0, $markerBytes.Length)
}
finally {
    $stream.Dispose()
}

Write-Host "Created $setupExe"
