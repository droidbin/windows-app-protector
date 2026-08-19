using Microsoft.Win32;
using System.Diagnostics;

namespace WindowsAppProtector.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Windows App Protector";
    private const string TaskName = "Windows App Protector";

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            CreateStartupTask(GetExecutablePath());
            DeleteRunValue();
            return;
        }

        DeleteStartupTask();
        DeleteRunValue();
    }

    public static bool IsEnabled()
    {
        if (StartupTaskExists())
        {
            return true;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("현재 실행 파일 경로를 확인할 수 없습니다.");
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static void CreateStartupTask(string executablePath)
    {
        var result = RunSchtasks(
            "/Create",
            "/TN",
            TaskName,
            "/TR",
            Quote(executablePath),
            "/SC",
            "ONLOGON",
            "/RL",
            "HIGHEST",
            "/F");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error)
                    ? "시작프로그램 예약 작업을 만들 수 없습니다."
                    : result.Error.Trim());
        }
    }

    private static void DeleteStartupTask()
    {
        var result = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        if (result.ExitCode != 0 && !TaskNotFound(result))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error)
                    ? "시작프로그램 예약 작업을 삭제할 수 없습니다."
                    : result.Error.Trim());
        }
    }

    private static bool StartupTaskExists()
    {
        return RunSchtasks("/Query", "/TN", TaskName).ExitCode == 0;
    }

    private static void DeleteRunValue()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static bool TaskNotFound((int ExitCode, string Output, string Error) result)
    {
        var text = result.Output + "\n" + result.Error;
        return text.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("찾을 수 없습니다", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("없습니다", StringComparison.OrdinalIgnoreCase);
    }

    private static (int ExitCode, string Output, string Error) RunSchtasks(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("작업 스케줄러를 실행할 수 없습니다.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
