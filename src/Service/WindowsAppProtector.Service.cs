using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
    private const string MarkerName = "WindowsAppProtectorManaged";
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
            SyncRules(protectedApps.Where(app => app.Enabled));
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
            .Where(app => !string.IsNullOrWhiteSpace(GetBlockRuleName(app)))
            .GroupBy(app => GetBlockRuleName(app), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        using (var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
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
