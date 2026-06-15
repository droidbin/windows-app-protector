using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WindowsAppProtector.Models;

namespace WindowsAppProtector.Services;

public sealed class PackagedAppDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<PackagedAppInfo>> GetInstalledAppsAsync(CancellationToken cancellationToken = default)
    {
        var script = @"
$ErrorActionPreference = 'SilentlyContinue'
$seen = @{}
$startNames = @{}

function Add-InstalledAppItem {
  param(
    [string]$Name,
    [string]$Path,
    [bool]$IsPackaged,
    [string]$PackageFamilyName,
    [string]$AppUserModelId,
    [string]$ExecutableName,
    [string]$InstallLocation,
    [string]$Source
  )

  if ([string]::IsNullOrWhiteSpace($Name)) { return }
  if ([string]::IsNullOrWhiteSpace($ExecutableName)) {
    if (![string]::IsNullOrWhiteSpace($Path)) {
      $ExecutableName = [System.IO.Path]::GetFileName($Path)
    }
  }
  if ([string]::IsNullOrWhiteSpace($ExecutableName)) { return }

  $key = if ($IsPackaged) { 'pkg:' + $AppUserModelId } else { 'exe:' + $Path.ToLowerInvariant() }
  if ([string]::IsNullOrWhiteSpace($key) -or $seen.ContainsKey($key)) { return }
  $seen[$key] = $true

  $items.Add([pscustomobject]@{
    Name = $Name
    Path = $Path
    IsPackaged = $IsPackaged
    PackageFamilyName = $PackageFamilyName
    AppUserModelId = $AppUserModelId
    ExecutableName = $ExecutableName
    InstallLocation = $InstallLocation
    Source = $Source
  })
}

function Add-Win32Exe {
  param([string]$Name, [string]$Path, [string]$InstallLocation, [string]$Source)
  if ([string]::IsNullOrWhiteSpace($Path)) { return }
  if ($Name -match '(제거|삭제|언인스톨|uninstall|remove)') { return }
  $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim('""'))
  if (!(Test-Path -LiteralPath $expanded -PathType Leaf)) { return }
  if ([System.IO.Path]::GetExtension($expanded) -ne '.exe') { return }
  $fileName = [System.IO.Path]::GetFileName($expanded)
  if ($fileName -match '^(unins|uninst|uninstall|setup|install|update|crash|report|helper|vc_redist)') { return }
  Add-InstalledAppItem -Name $Name -Path $expanded -IsPackaged $false -PackageFamilyName '' -AppUserModelId '' -ExecutableName $fileName -InstallLocation $InstallLocation -Source $Source
}

function Get-DisplayIconExe {
  param([string]$DisplayIcon)
  if ([string]::IsNullOrWhiteSpace($DisplayIcon)) { return '' }
  $value = [Environment]::ExpandEnvironmentVariables($DisplayIcon.Trim())
  if ($value.StartsWith('""')) {
    $end = $value.IndexOf('""', 1)
    if ($end -gt 1) { return $value.Substring(1, $end - 1) }
  }
  $match = [regex]::Match($value, '^[^,]+\.exe')
  if ($match.Success) { return $match.Value.Trim('""') }
  return ''
}

function Get-PrimaryExeFromDirectory {
  param([string]$InstallLocation, [string]$DisplayName)
  if ([string]::IsNullOrWhiteSpace($InstallLocation)) { return '' }
  $dir = [Environment]::ExpandEnvironmentVariables($InstallLocation.Trim('""'))
  if (!(Test-Path -LiteralPath $dir -PathType Container)) { return '' }

  $directories = New-Object System.Collections.Generic.List[string]
  $directories.Add($dir)
  Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue | Select-Object -First 12 | ForEach-Object {
    $directories.Add($_.FullName)
  }

  $candidates = New-Object System.Collections.Generic.List[object]
  foreach ($candidateDir in $directories) {
    Get-ChildItem -LiteralPath $candidateDir -Filter *.exe -File -ErrorAction SilentlyContinue | ForEach-Object {
      if ($_.Name -match '^(unins|uninst|uninstall|setup|install|update|crash|report|helper|vc_redist)') { return }
      $score = 0
      $normalizedDisplay = ($DisplayName -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()
      $normalizedExe = ($_.BaseName -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()
      if ($normalizedDisplay.Length -gt 0 -and ($normalizedDisplay.Contains($normalizedExe) -or $normalizedExe.Contains($normalizedDisplay))) {
        $score += 100
      }
      if ($_.DirectoryName -eq $dir) { $score += 20 }
      $score += [Math]::Min(10, [int]($_.Length / 1MB))
      $candidates.Add([pscustomobject]@{ Path = $_.FullName; Score = $score })
    }
  }

  $best = $candidates | Sort-Object Score -Descending | Select-Object -First 1
  if ($best) { return $best.Path }
  return ''
}

Get-StartApps | ForEach-Object {
  if ($_.AppID -and $_.Name) {
    $startNames[$_.AppID] = $_.Name
  }
}

$items = New-Object System.Collections.Generic.List[object]

Get-AppxPackage | Where-Object { $_.InstallLocation } | ForEach-Object {
  $pkg = $_
  $manifestPath = Join-Path $pkg.InstallLocation 'AppxManifest.xml'
  if (!(Test-Path -LiteralPath $manifestPath)) { return }

  [xml]$manifest = Get-Content -LiteralPath $manifestPath
  $apps = $manifest.Package.Applications.Application
  foreach ($app in $apps) {
    $exe = [string]$app.Executable
    $id = [string]$app.Id
    if ([string]::IsNullOrWhiteSpace($exe) -or [string]::IsNullOrWhiteSpace($id)) { continue }

    $aumid = $pkg.PackageFamilyName + '!' + $id
    if (!$startNames.ContainsKey($aumid)) { continue }

    $name = $startNames[$aumid]
    if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('ms-resource:')) {
      $name = $pkg.Name
    }

    Add-InstalledAppItem -Name $name -Path $aumid -IsPackaged $true -PackageFamilyName $pkg.PackageFamilyName -AppUserModelId $aumid -ExecutableName ([System.IO.Path]::GetFileName($exe)) -InstallLocation $pkg.InstallLocation -Source '스토어/MSIX'
  }
}

$shortcutRoots = @(
  [Environment]::GetFolderPath('StartMenu'),
  [Environment]::GetFolderPath('CommonStartMenu'),
  [Environment]::GetFolderPath('Desktop'),
  [Environment]::GetFolderPath('CommonDesktopDirectory')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$shell = New-Object -ComObject WScript.Shell
foreach ($root in $shortcutRoots) {
  Get-ChildItem -LiteralPath $root -Filter *.lnk -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $shortcut = $shell.CreateShortcut($_.FullName)
      $target = [string]$shortcut.TargetPath
      $name = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
      Add-Win32Exe -Name $name -Path $target -InstallLocation ([System.IO.Path]::GetDirectoryName($target)) -Source '시작 메뉴'
    } catch {}
  }
}

$uninstallRoots = @(
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
)

foreach ($root in $uninstallRoots) {
  Get-ItemProperty $root -ErrorAction SilentlyContinue | ForEach-Object {
    $name = [string]$_.DisplayName
    if ([string]::IsNullOrWhiteSpace($name)) { return }
    if ($name -match '(Update|Hotfix|Security Update|Redistributable|Runtime|Driver Package)') { return }

    $path = Get-DisplayIconExe ([string]$_.DisplayIcon)
    if ([string]::IsNullOrWhiteSpace($path)) {
      $path = Get-PrimaryExeFromDirectory ([string]$_.InstallLocation) $name
    }
    Add-Win32Exe -Name $name -Path $path -InstallLocation ([string]$_.InstallLocation) -Source '설치 정보'
  }
}

$items | Sort-Object Name, Source, Path, AppUserModelId -Unique | ConvertTo-Json -Depth 4
";

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + EncodePowerShellCommand(script),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot start PowerShell to read installed apps.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Cannot read installed apps." : error.Trim());
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<PackagedAppInfo>();
        }

        var trimmed = output.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return JsonSerializer.Deserialize<List<PackagedAppInfo>>(trimmed, JsonOptions) ?? new List<PackagedAppInfo>();
        }

        var single = JsonSerializer.Deserialize<PackagedAppInfo>(trimmed, JsonOptions);
        return single is null ? Array.Empty<PackagedAppInfo>() : new[] { single };
    }

    private static string EncodePowerShellCommand(string command)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }
}
