using System.Diagnostics;
using Microsoft.Win32;
using WindowsAppProtector.Models;

namespace WindowsAppProtector.Services;

public sealed class ProtectionService : IProtectionService
{
    private const string IfeoRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string MarkerName = "WindowsAppProtectorManaged";

    public Task SyncExecutionBlockRulesAsync(IEnumerable<ProtectedApp> apps, CancellationToken cancellationToken = default)
    {
        var appPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(appPath))
        {
            throw new InvalidOperationException("Cannot resolve the application executable path.");
        }

        var desired = apps
            .Where(app => app.Enabled)
            .Where(app => !string.IsNullOrWhiteSpace(GetBlockRuleName(app)))
            .GroupBy(GetBlockRuleName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = localMachine.OpenSubKey(IfeoRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open the IFEO registry path. Run the app as administrator.");

        foreach (var existingName in ReadManagedRuleNames(root).Except(desired.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            DeleteManagedRule(root, existingName);
        }

        foreach (var exeName in desired.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var key = root.CreateSubKey(exeName, writable: true)
                ?? throw new InvalidOperationException($"Cannot create a block rule for {exeName}.");

            key.SetValue("Debugger", $"\"{appPath}\" --blocked", RegistryValueKind.String);
            key.SetValue(MarkerName, "1", RegistryValueKind.String);
            key.DeleteValue("UseFilter", throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    public Task StopProtectedProcessesAsync(IEnumerable<ProtectedApp> apps, CancellationToken cancellationToken = default)
    {
        // Do not terminate already-running targets. Only future launches are blocked through IFEO.
        return Task.CompletedTask;
    }

    private static IEnumerable<string> ReadManagedRuleNames(RegistryKey root)
    {
        foreach (var name in root.GetSubKeyNames())
        {
            RegistryKey? key = null;
            try
            {
                key = root.OpenSubKey(name);
                if (key?.GetValue(MarkerName)?.ToString() == "1")
                {
                    yield return name;
                }
            }
            finally
            {
                key?.Dispose();
            }
        }
    }

    private static void DeleteManagedRule(RegistryKey root, string exeName)
    {
        try
        {
            using var key = root.OpenSubKey(exeName, writable: true);
            if (key?.GetValue(MarkerName)?.ToString() != "1")
            {
                return;
            }

            key.DeleteValue("Debugger", throwOnMissingValue: false);
            key.DeleteValue(MarkerName, throwOnMissingValue: false);
            key.DeleteValue("UseFilter", throwOnMissingValue: false);

            try
            {
                root.DeleteSubKey(exeName, throwOnMissingSubKey: false);
            }
            catch (InvalidOperationException)
            {
                // Keep the subkey if another product still owns values under the same IFEO entry.
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip keys protected by Windows or security products.
        }
    }

    private static string GetBlockRuleName(ProtectedApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.ExecutableName))
        {
            return app.ExecutableName;
        }

        return string.IsNullOrWhiteSpace(app.Path) ? string.Empty : Path.GetFileName(app.Path);
    }
}
