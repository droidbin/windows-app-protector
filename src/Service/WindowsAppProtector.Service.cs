using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

internal static class Program
{
    private const string ServiceName = "WindowsAppProtectorService";

    private static void Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase)))
        {
            using (var worker = new ProtectionWorker())
            {
                worker.Start();
                Console.WriteLine("Windows App Protector service worker is running. Press Enter to stop.");
                Console.ReadLine();
                worker.Stop();
            }
            return;
        }

        ServiceBase.Run(new ProtectorService());
    }

    private sealed class ProtectorService : ServiceBase
    {
        private readonly ProtectionWorker worker = new ProtectionWorker();

        public ProtectorService()
        {
            ServiceName = Program.ServiceName;
            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            worker.Start();
        }

        protected override void OnStop()
        {
            worker.Stop();
        }

        protected override void OnShutdown()
        {
            worker.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                worker.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed class ProtectionWorker : IDisposable
{
    private const string AppName = "Windows App Protector";
    private const string UiExeName = "WindowsAppProtector.WinUI.exe";
    private const string IfeoRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string SaferCodeIdentifiersPath = @"SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers";
    private const string MarkerName = "WindowsAppProtectorManaged";
    private const int SePrivilegeEnabled = 0x00000002;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private static readonly HashSet<string> WindowOnlyExecutableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "arc.exe",
        "brave.exe",
        "chrome.exe",
        "firefox.exe",
        "iexplore.exe",
        "msedge.exe",
        "opera.exe",
        "opera_gx.exe",
        "samsunginternet.exe",
        "vivaldi.exe",
        "whale.exe",
    };
    private readonly string configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppName,
        "config.json");
    private readonly string uiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UiExeName);
    private readonly Timer timer;
    private readonly object syncRoot = new object();
    private bool running;

    public ProtectionWorker()
    {
        timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        running = true;
        timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        running = false;
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        timer.Dispose();
    }

    private void Tick()
    {
        if (!running || !Monitor.TryEnter(syncRoot))
        {
            return;
        }

        try
        {
            var config = LoadConfig();
            if (config == null)
            {
                return;
            }

            var protectedApps = config.ProtectedApps ?? new List<ProtectedApp>();
            SyncRules(protectedApps);
        }
        catch (Exception ex)
        {
            try
            {
                EventLog.WriteEntry("Windows App Protector", ex.ToString(), EventLogEntryType.Warning);
            }
            catch
            {
            }
        }
        finally
        {
            Monitor.Exit(syncRoot);
        }
    }

    private AppConfig LoadConfig()
    {
        string selectedPath = FindNewestConfigPath();
        if (string.IsNullOrEmpty(selectedPath) || !File.Exists(selectedPath))
        {
            return null;
        }

        using (var stream = File.OpenRead(selectedPath))
        {
            var serializer = new DataContractJsonSerializer(typeof(AppConfig));
            return serializer.ReadObject(stream) as AppConfig;
        }
    }

    private string FindNewestConfigPath()
    {
        string newestPath = string.Empty;
        DateTime newestWriteTime = DateTime.MinValue;

        foreach (string path in EnumerateConfigPaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(path);
                if (writeTime > newestWriteTime)
                {
                    newestWriteTime = writeTime;
                    newestPath = path;
                }
            }
            catch
            {
            }
        }

        return newestPath;
    }

    private IEnumerable<string> EnumerateConfigPaths()
    {
        yield return configPath;

        string usersRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Users");
        if (!Directory.Exists(usersRoot))
        {
            yield break;
        }

        string[] profileDirectories;
        try
        {
            profileDirectories = Directory.GetDirectories(usersRoot);
        }
        catch
        {
            yield break;
        }

        foreach (string profileDirectory in profileDirectories)
        {
            yield return Path.Combine(
                profileDirectory,
                "AppData",
                "Roaming",
                "WindowsAppProtector.WinUI",
                "config.json");
        }
    }

    private void SyncRules(IEnumerable<ProtectedApp> apps)
    {
        if (!File.Exists(uiPath))
        {
            return;
        }

        var desired = apps
            .Where(app => app.Enabled)
            .Where(app => !string.IsNullOrWhiteSpace(GetBlockRuleName(app)))
            .Where(app => !UsesWindowOnlyBlocking(app))
            .GroupBy(app => GetBlockRuleName(app), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        using (var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            CleanupLegacySoftwareRestrictionRules(localMachine, apps);

            using (var root = localMachine.OpenSubKey(IfeoRegistryPath, writable: true))
            {
                if (root == null)
                {
                    return;
                }

                foreach (var existingName in ReadManagedRuleNames(root).Except(desired.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    DeleteManagedRule(root, existingName);
                }

                foreach (var exeName in desired.Keys)
                {
                    using (var key = root.CreateSubKey(exeName, writable: true))
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        key.SetValue("Debugger", "\"" + uiPath + "\" --blocked", RegistryValueKind.String);
                        key.SetValue(MarkerName, "1", RegistryValueKind.String);
                        key.DeleteValue("UseFilter", throwOnMissingValue: false);
                    }
                }
            }
        }
    }

    private static void RemoveManagedRules()
    {
        using (var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var root = localMachine.OpenSubKey(IfeoRegistryPath, writable: true))
        {
            if (root == null)
            {
                return;
            }

            foreach (var existingName in ReadManagedRuleNames(root).ToArray())
            {
                DeleteManagedRule(root, existingName);
            }
        }
    }

    private static string GetBlockRuleName(ProtectedApp app)
    {
        if (app != null && !string.IsNullOrWhiteSpace(app.ExecutableName))
        {
            return app.ExecutableName;
        }

        if (app == null || string.IsNullOrWhiteSpace(app.Path))
        {
            return string.Empty;
        }

        return Path.GetFileName(app.Path);
    }

    private static bool UsesWindowOnlyBlocking(ProtectedApp app)
    {
        string executableName = GetBlockRuleName(app);
        return !string.IsNullOrWhiteSpace(executableName) &&
            WindowOnlyExecutableNames.Contains(executableName);
    }

    private static void CleanupLegacySoftwareRestrictionRules(RegistryKey localMachine, IEnumerable<ProtectedApp> apps)
    {
        var signatures = BuildLegacyRuleSignatures(apps);
        if (signatures.Count == 0)
        {
            return;
        }

        using (var codeIdentifiers = localMachine.OpenSubKey(SaferCodeIdentifiersPath, writable: true))
        {
            if (codeIdentifiers == null)
            {
                return;
            }

            DeleteMatchingLegacyRules(codeIdentifiers, @"0\Paths", signatures);
            DeleteMatchingLegacyRules(codeIdentifiers, @"0\Hashes", signatures);
        }
    }

    private static HashSet<string> BuildLegacyRuleSignatures(IEnumerable<ProtectedApp> apps)
    {
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (app == null)
            {
                continue;
            }

            AddSignature(signatures, app.Name);
            AddSignature(signatures, app.Path);
            AddSignature(signatures, app.ExecutableName);
            AddSignature(signatures, app.PackageFamilyName);
            AddSignature(signatures, app.AppUserModelId);

            if (!string.IsNullOrWhiteSpace(app.PackageFamilyName))
            {
                int separator = app.PackageFamilyName.IndexOf('_');
                if (separator > 0)
                {
                    AddSignature(signatures, app.PackageFamilyName.Substring(0, separator));
                }
            }
        }

        return signatures;
    }

    private static void AddSignature(HashSet<string> signatures, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string trimmed = value.Trim();
        if (trimmed.Length >= 4)
        {
            signatures.Add(trimmed);
        }
    }

    private static void DeleteMatchingLegacyRules(RegistryKey codeIdentifiers, string relativePath, HashSet<string> signatures)
    {
        using (var container = codeIdentifiers.OpenSubKey(relativePath, writable: true))
        {
            if (container == null)
            {
                return;
            }

            foreach (var ruleName in container.GetSubKeyNames())
            {
                try
                {
                    using (var rule = container.OpenSubKey(ruleName))
                    {
                        if (rule == null || !LegacyRuleMatches(rule, signatures))
                        {
                            continue;
                        }
                    }

                    DeleteLegacyRule(container, ruleName);
                }
                catch
                {
                }
            }
        }
    }

    private static bool LegacyRuleMatches(RegistryKey rule, HashSet<string> signatures)
    {
        foreach (var valueName in new[] { "Description", "ItemPath", "ItemData" })
        {
            var value = Convert.ToString(rule.GetValue(valueName));
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var signature in signatures)
            {
                if (value.IndexOf(signature, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
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
            using (var rule = container.OpenSubKey(
                ruleName,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ReadKey |
                RegistryRights.ReadPermissions |
                RegistryRights.ChangePermissions |
                RegistryRights.TakeOwnership))
            {
                if (rule == null)
                {
                    return false;
                }

                var security = rule.GetAccessControl(AccessControlSections.All);
                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                security.SetAccessRuleProtection(false, false);
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
        }
        catch
        {
            return false;
        }
    }

    private static bool EnablePrivilege(string privilegeName)
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
        {
            return false;
        }

        try
        {
            Luid luid;
            if (!LookupPrivilegeValue(null, privilegeName, out luid))
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

    private static IEnumerable<string> ReadManagedRuleNames(RegistryKey root)
    {
        foreach (var name in root.GetSubKeyNames())
        {
            RegistryKey key = null;
            try
            {
                key = root.OpenSubKey(name);
                if (key != null && Convert.ToString(key.GetValue(MarkerName)) == "1")
                {
                    yield return name;
                }
            }
            finally
            {
                if (key != null)
                {
                    key.Dispose();
                }
            }
        }
    }

    private static void DeleteManagedRule(RegistryKey root, string exeName)
    {
        try
        {
            using (var key = root.OpenSubKey(exeName, writable: true))
            {
                if (key == null || Convert.ToString(key.GetValue(MarkerName)) != "1")
                {
                    return;
                }

                key.DeleteValue("Debugger", throwOnMissingValue: false);
                key.DeleteValue(MarkerName, throwOnMissingValue: false);
                key.DeleteValue("UseFilter", throwOnMissingValue: false);
            }

            try
            {
                root.DeleteSubKey(exeName, throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }
        catch
        {
        }
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
    private static extern bool LookupPrivilegeValue(string systemName, string name, out Luid luid);

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

[DataContract]
internal sealed class AppConfig
{
    public AppConfig()
    {
        UnlockUntil = string.Empty;
        ProtectedApps = new List<ProtectedApp>();
    }

    [DataMember]
    public bool ProtectionEnabled { get; set; }

    [DataMember]
    public string UnlockUntil { get; set; }

    [DataMember]
    public List<ProtectedApp> ProtectedApps { get; set; }
}

[DataContract]
internal sealed class ProtectedApp
{
    public ProtectedApp()
    {
        Name = string.Empty;
        Path = string.Empty;
        PackageFamilyName = string.Empty;
        AppUserModelId = string.Empty;
        ExecutableName = string.Empty;
        Enabled = true;
    }

    [DataMember]
    public string Name { get; set; }

    [DataMember]
    public string Path { get; set; }

    [DataMember]
    public bool IsPackaged { get; set; }

    [DataMember]
    public string PackageFamilyName { get; set; }

    [DataMember]
    public string AppUserModelId { get; set; }

    [DataMember]
    public string ExecutableName { get; set; }

    [DataMember]
    public bool Enabled { get; set; }
}
