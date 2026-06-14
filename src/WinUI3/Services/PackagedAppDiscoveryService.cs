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
$startNames = @{}
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

    $items.Add([pscustomobject]@{
      Name = $name
      PackageFamilyName = $pkg.PackageFamilyName
      AppUserModelId = $aumid
      ExecutableName = [System.IO.Path]::GetFileName($exe)
      InstallLocation = $pkg.InstallLocation
    })
  }
}

$items | Sort-Object Name, AppUserModelId -Unique | ConvertTo-Json -Depth 3
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
