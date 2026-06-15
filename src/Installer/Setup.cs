using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

internal static class Setup
{
    private static readonly byte[] Marker = System.Text.Encoding.ASCII.GetBytes("WAPZIP01");
    private const string AppName = "Windows App Protector";
    private const string AppVersion = "1.1.3";
    private const string ExeName = "WindowsAppProtector.WinUI.exe";
    private const string ServiceExeName = "WindowsAppProtector.Service.exe";
    private const string ServiceName = "WindowsAppProtectorService";
    private const string IfeoRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string MarkerName = "WindowsAppProtectorManaged";
    private const int DialogResultYes = 6;
    private const uint MessageBoxInformation = 0x00000040;
    private const uint MessageBoxError = 0x00000010;
    private const uint MessageBoxQuestionYesNo = 0x00000024;

    private static int Main(string[] args)
    {
        try
        {
            if (!IsAdministrator())
            {
                RelaunchAsAdmin(args);
                return 0;
            }

            string installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                AppName);
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppName);

            WaitForUpdateSourceProcess(args);
            StopAndDeleteService();
            CleanupOldProcesses();
            TryCleanupManagedIfeoRules();
            DeleteDirectoryIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsAppProtector"));
            DeleteDirectoryIfExists(installDir);
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(dataDir);
            GrantUsersModify(dataDir);
            EnsureSharedConfigFile(dataDir);

            string zipPath = Path.Combine(Path.GetTempPath(), "WindowsAppProtector.Payload.zip");
            ExtractEmbeddedPayload(zipPath);
            ZipFile.ExtractToDirectory(zipPath, installDir);
            File.Delete(zipPath);

            string targetPath = Path.Combine(installDir, ExeName);
            bool createShortcuts = AskCreateShortcuts();
            if (createShortcuts)
            {
                CreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk"),
                    targetPath,
                    installDir);
                CreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", AppName + ".lnk"),
                    targetPath,
                    installDir);
            }

            WriteUninstaller(installDir);
            if (createShortcuts)
            {
                CreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", AppName + " Uninstall.lnk"),
                    Path.Combine(installDir, "Uninstall.bat"),
                    installDir);
            }

            TryWriteUninstallRegistry(installDir);
            InstallAndStartService(Path.Combine(installDir, ServiceExeName));

            MessageBoxW(IntPtr.Zero, "\uC124\uCE58\uAC00 \uC644\uB8CC\uB418\uC5C8\uC2B5\uB2C8\uB2E4.", AppName, MessageBoxInformation);
            Process.Start(new ProcessStartInfo
            {
                FileName = targetPath,
                WorkingDirectory = installDir,
                UseShellExecute = true
            });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, "\uC124\uCE58 \uC2E4\uD328:\n" + ex.Message, AppName, MessageBoxError);
            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    private static void RelaunchAsAdmin(string[] args)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Assembly.GetExecutingAssembly().Location,
            Arguments = string.Join(" ", args.Select(QuoteArgument)),
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    private static void CleanupOldProcesses()
    {
        foreach (string imageName in new[] { ExeName, ServiceExeName, "WindowsAppProtector.exe", "WindowsAppProtector-1.6.0.exe" })
        {
            RunHidden("taskkill.exe", "/IM \"" + imageName + "\" /F /T");
        }

        int currentProcessId = Process.GetCurrentProcess().Id;
        foreach (string name in new[] { "WindowsAppProtector.WinUI", "WindowsAppProtector.Service", "WindowsAppProtector", "WindowsAppProtector-1.6.0" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.Id == currentProcessId)
                    {
                        continue;
                    }

                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch
                {
                }
            }
        }
    }

    private static void WaitForUpdateSourceProcess(string[] args)
    {
        int processId = ReadUpdateProcessId(args);
        if (processId <= 0 || processId == Process.GetCurrentProcess().Id)
        {
            return;
        }

        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                if (!process.WaitForExit(15000))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
        }
        catch
        {
        }
    }

    private static int ReadUpdateProcessId(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            int parsedValue;
            if (arg.StartsWith("--update-pid=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg.Substring("--update-pid=".Length), out parsedValue))
            {
                return parsedValue;
            }

            if (string.Equals(arg, "--update-pid", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out parsedValue))
            {
                return parsedValue;
            }
        }

        return 0;
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static void InstallAndStartService(string servicePath)
    {
        if (!File.Exists(servicePath))
        {
            return;
        }

        RunHidden("sc.exe", "create " + ServiceName + " binPath= \"" + servicePath + "\" start= auto DisplayName= \"" + AppName + " Service\"");
        RunHidden("sc.exe", "failure " + ServiceName + " reset= 60 actions= restart/5000/restart/5000/restart/5000");
        RunHidden("sc.exe", "start " + ServiceName);
    }

    private static void StopAndDeleteService()
    {
        RunHidden("sc.exe", "stop " + ServiceName);
        RunHidden("sc.exe", "delete " + ServiceName);
    }

    private static void RunHidden(string fileName, string arguments)
    {
        try
        {
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                if (process != null)
                {
                    process.WaitForExit(10000);
                }
            }
        }
        catch
        {
        }
    }

    private static void GrantUsersModify(string path)
    {
        RunHidden("icacls.exe", "\"" + path + "\" /inheritance:e /grant *S-1-5-32-545:(OI)(CI)M /grant *S-1-5-11:(OI)(CI)M /T /C");
    }

    private static void EnsureSharedConfigFile(string dataDir)
    {
        string configPath = Path.Combine(dataDir, "config.json");
        if (!File.Exists(configPath))
        {
            File.WriteAllText(
                configPath,
                "{\r\n" +
                "  \"ProtectionEnabled\": false,\r\n" +
                "  \"CloseToBackground\": true,\r\n" +
                "  \"UnlockMinutes\": 10,\r\n" +
                "  \"UnlockUntil\": null,\r\n" +
                "  \"AppPinSalt\": \"\",\r\n" +
                "  \"AppPinHash\": \"\",\r\n" +
                "  \"GlobalHotkeys\": {},\r\n" +
                "  \"ProtectedApps\": []\r\n" +
                "}\r\n",
                System.Text.Encoding.UTF8);
        }

        RunHidden("icacls.exe", "\"" + configPath + "\" /inheritance:e /grant *S-1-5-32-545:M /grant *S-1-5-11:M /C");
    }

    private static void TryCleanupManagedIfeoRules()
    {
        try
        {
            using (var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var root = localMachine.OpenSubKey(IfeoRegistryPath, writable: true))
            {
                if (root == null)
                {
                    return;
                }

                foreach (string name in root.GetSubKeyNames().ToArray())
                {
                    TryCleanupManagedIfeoRule(root, name);
                }
            }
        }
        catch
        {
        }
    }

    private static void TryCleanupManagedIfeoRule(RegistryKey root, string name)
    {
        try
        {
            using (var key = root.OpenSubKey(name, writable: true))
            {
                if (key == null || Convert.ToString(key.GetValue(MarkerName)) != "1")
                {
                    return;
                }

                key.DeleteValue("Debugger", false);
                key.DeleteValue(MarkerName, false);
                key.DeleteValue("UseFilter", false);
            }

            try
            {
                root.DeleteSubKey(name, false);
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private static void ExtractEmbeddedPayload(string zipPath)
    {
        string self = Assembly.GetExecutingAssembly().Location;
        byte[] allBytes = File.ReadAllBytes(self);
        if (allBytes.Length < Marker.Length + sizeof(long))
        {
            throw new InvalidOperationException("\uC124\uCE58 \uD398\uC774\uB85C\uB4DC\uAC00 \uC5C6\uC2B5\uB2C8\uB2E4.");
        }

        int markerOffset = allBytes.Length - Marker.Length;
        for (int i = 0; i < Marker.Length; i++)
        {
            if (allBytes[markerOffset + i] != Marker[i])
            {
                throw new InvalidOperationException("\uC124\uCE58 \uD398\uC774\uB85C\uB4DC \uB9C8\uCEE4\uB97C \uCC3E\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.");
            }
        }

        long zipLength = BitConverter.ToInt64(allBytes, markerOffset - sizeof(long));
        long zipOffset = markerOffset - sizeof(long) - zipLength;
        if (zipOffset < 0 || zipLength <= 0)
        {
            throw new InvalidOperationException("\uC124\uCE58 \uD398\uC774\uB85C\uB4DC\uAC00 \uC190\uC0C1\uB418\uC5C8\uC2B5\uB2C8\uB2E4.");
        }

        using (var source = new MemoryStream(allBytes, (int)zipOffset, (int)zipLength))
        using (var output = File.Create(zipPath))
        {
            source.CopyTo(output);
        }
    }

    private static bool AskCreateShortcuts()
    {
        int result = MessageBoxW(
            IntPtr.Zero,
            "\uBC14\uD0D5\uD654\uBA74 \uBC0F \uC2DC\uC791 \uBA54\uB274 \uBC14\uB85C\uAC00\uAE30\uB97C \uC0DD\uC131\uD558\uC2DC\uACA0\uC2B5\uB2C8\uAE4C?",
            AppName,
            MessageBoxQuestionYesNo);

        return result == DialogResultYes;
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        string parent = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType);
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = targetPath;
        shortcut.Save();
    }

    private static void WriteUninstaller(string installDir)
    {
        string uninstallPath = Path.Combine(installDir, "Uninstall.bat");
        File.WriteAllText(
            uninstallPath,
            "@echo off\r\n" +
            "setlocal\r\n" +
            "\r\n" +
            "net session >nul 2>&1\r\n" +
            "if errorlevel 1 (\r\n" +
            "  powershell -NoProfile -ExecutionPolicy Bypass -Command \"Start-Process -FilePath '%~f0' -Verb RunAs\"\r\n" +
            "  exit /b\r\n" +
            ")\r\n" +
            "sc stop WindowsAppProtectorService >nul 2>&1\r\n" +
            "sc delete WindowsAppProtectorService >nul 2>&1\r\n" +
            "taskkill /IM WindowsAppProtector.WinUI.exe /F >nul 2>&1\r\n" +
            "taskkill /IM WindowsAppProtector.Service.exe /F >nul 2>&1\r\n" +
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"$root='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options'; Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object { $p=Get-ItemProperty $_.PsPath -ErrorAction SilentlyContinue; if ($p.WindowsAppProtectorManaged -eq '1') { Remove-Item $_.PsPath -Force -ErrorAction SilentlyContinue } }\"\r\n" +
            "rmdir /s /q \"%ProgramData%\\Windows App Protector\" >nul 2>&1\r\n" +
            "rmdir /s /q \"%AppData%\\WindowsAppProtector.WinUI\" >nul 2>&1\r\n" +
            "rmdir /s /q \"%AppData%\\WindowsAppProtector\" >nul 2>&1\r\n" +
            "del \"%PUBLIC%\\Desktop\\Windows App Protector.lnk\" >nul 2>&1\r\n" +
            "del \"%ProgramData%\\Microsoft\\Windows\\Start Menu\\Programs\\Windows App Protector.lnk\" >nul 2>&1\r\n" +
            "del \"%ProgramData%\\Microsoft\\Windows\\Start Menu\\Programs\\Windows App Protector Uninstall.lnk\" >nul 2>&1\r\n" +
            "reg delete \"HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\WindowsAppProtector\" /f >nul 2>&1\r\n" +
            "cd /d \"%ProgramFiles%\"\r\n" +
            "rmdir /s /q \"Windows App Protector\" >nul 2>&1\r\n" +
            "echo Windows App Protector has been removed.\r\n" +
            "pause\r\n",
            System.Text.Encoding.ASCII);
    }

    private static void TryWriteUninstallRegistry(string installDir)
    {
        try
        {
            using (var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = localMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsAppProtector", true))
            {
                if (key == null)
                {
                    return;
                }

                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", AppVersion);
                key.SetValue("Publisher", "Windows App Protector");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", Path.Combine(installDir, ExeName));
                key.SetValue("UninstallString", "\"" + Path.Combine(installDir, "Uninstall.bat") + "\"");
            }
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Exception lastError = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                StopAndDeleteService();
                CleanupOldProcesses();
                Thread.Sleep(700);
            }
        }

        if (Directory.Exists(path))
        {
            throw new IOException("\uC2E4\uD589 \uC911\uC778 \uD504\uB85C\uC138\uC2A4\uB97C \uC885\uB8CC\uD588\uC9C0\uB9CC \uC124\uCE58 \uD3F4\uB354\uB97C \uC0AD\uC81C\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4. Windows App Protector \uD504\uB85C\uC138\uC2A4\uB97C \uC218\uB3D9\uC73C\uB85C \uC885\uB8CC\uD55C \uB4A4 \uB2E4\uC2DC \uC2E4\uD589\uD558\uC138\uC694.", lastError);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
