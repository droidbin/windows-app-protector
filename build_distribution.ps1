$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

& "$root\build_setup.bat"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$packageDir = Join-Path $root "dist\WindowsAppProtector"
$zipPath = Join-Path $root "dist\WindowsAppProtector.zip"
$setupExe = Join-Path $root "dist\installer\Setup.exe"
$uninstallSource = Join-Path $root "src\Installer\Uninstall.bat"
$readmeSource = Join-Path $root "src\Installer\README.txt"

if (!(Test-Path $setupExe)) {
    throw "Setup.exe not found: $setupExe"
}

Remove-Item -LiteralPath $packageDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $packageDir | Out-Null

Copy-Item -LiteralPath $setupExe -Destination (Join-Path $packageDir "Setup.exe") -Force
Copy-Item -LiteralPath $uninstallSource -Destination (Join-Path $packageDir "Uninstall.bat") -Force
Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $packageDir "README.txt") -Force

Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force

Write-Host "Created $zipPath"
