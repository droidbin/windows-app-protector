using System.Diagnostics;
using System.Security;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using WindowsAppProtector.Models;

namespace WindowsAppProtector.Services;

public sealed class ProtectionService : IProtectionService
{
    private const string IfeoRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string SaferCodeIdentifiersPath = @"SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers";
    private const string MarkerName = "WindowsAppProtectorManaged";
    private const int SePrivilegeEnabled = 0x00000002;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;

    public Task SyncExecutionBlockRulesAsync(IEnumerable<ProtectedApp> apps, CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator())
        {
            return Task.CompletedTask;
        }

        var appPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(appPath))
        {
            throw new InvalidOperationException("Cannot resolve the application executable path.");
        }

        var allApps = apps.ToArray();
        var desired = allApps
            .Where(app => app.Enabled)
            .Where(app => !string.IsNullOrWhiteSpace(GetBlockRuleName(app)))
            .GroupBy(GetBlockRuleName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        CleanupLegacySoftwareRestrictionRules(localMachine, allApps);

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

    private static void CleanupLegacySoftwareRestrictionRules(RegistryKey localMachine, IEnumerable<ProtectedApp> apps)
    {
        var signatures = BuildLegacyRuleSignatures(apps);
        if (signatures.Count == 0)
        {
            return;
        }

        using var codeIdentifiers = localMachine.OpenSubKey(SaferCodeIdentifiersPath, writable: true);
        if (codeIdentifiers is null)
        {
            return;
        }

        DeleteMatchingLegacyRules(codeIdentifiers, @"0\Paths", signatures);
        DeleteMatchingLegacyRules(codeIdentifiers, @"0\Hashes", signatures);
    }

    private static HashSet<string> BuildLegacyRuleSignatures(IEnumerable<ProtectedApp> apps)
    {
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            AddSignature(signatures, app.Name);
            AddSignature(signatures, app.Path);
            AddSignature(signatures, app.ExecutableName);
            AddSignature(signatures, app.PackageFamilyName);
            AddSignature(signatures, app.AppUserModelId);

            var packageNameSeparator = app.PackageFamilyName.IndexOf('_');
            if (packageNameSeparator > 0)
            {
                AddSignature(signatures, app.PackageFamilyName[..packageNameSeparator]);
            }
        }

        return signatures;
    }

    private static void AddSignature(HashSet<string> signatures, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 4)
        {
            signatures.Add(trimmed);
        }
    }

    private static void DeleteMatchingLegacyRules(RegistryKey codeIdentifiers, string relativePath, HashSet<string> signatures)
    {
        using var container = codeIdentifiers.OpenSubKey(relativePath, writable: true);
        if (container is null)
        {
            return;
        }

        foreach (var ruleName in container.GetSubKeyNames())
        {
            try
            {
                using var rule = container.OpenSubKey(ruleName);
                if (rule is null || !LegacyRuleMatches(rule, signatures))
                {
                    continue;
                }

                DeleteLegacyRule(container, ruleName);
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool LegacyRuleMatches(RegistryKey rule, HashSet<string> signatures)
    {
        foreach (var valueName in new[] { "Description", "ItemPath", "ItemData" })
        {
            var value = rule.GetValue(valueName)?.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (signatures.Any(signature => value.Contains(signature, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static void DeleteLegacyRule(RegistryKey container, string ruleName)
    {
        try
        {
            container.DeleteSubKeyTree(ruleName, throwOnMissingSubKey: false);
            return;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (SecurityException)
        {
        }

        if (!RepairLegacyRuleAccess(container, ruleName))
        {
            return;
        }

        container.DeleteSubKeyTree(ruleName, throwOnMissingSubKey: false);
    }

    private static bool RepairLegacyRuleAccess(RegistryKey container, string ruleName)
    {
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        EnablePrivilege("SeBackupPrivilege");

        try
        {
            using var rule = container.OpenSubKey(
                ruleName,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ReadKey |
                RegistryRights.ReadPermissions |
                RegistryRights.ChangePermissions |
                RegistryRights.TakeOwnership);
            if (rule is null)
            {
                return false;
            }

            var security = rule.GetAccessControl(AccessControlSections.All);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            security.SetAccessRule(new RegistryAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.SetAccessRule(new RegistryAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            rule.SetAccessControl(security);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
