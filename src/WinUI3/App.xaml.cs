using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace WindowsAppProtector;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--blocked", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBoxW(IntPtr.Zero, "\uC774 \uC571\uC740 Windows App Protector\uC5D0 \uC758\uD574 \uCC28\uB2E8\uB418\uC5C8\uC2B5\uB2C8\uB2E4.", "Windows App Protector", 0x00000030);
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
    }

    internal static void WriteCrashLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Windows App Protector",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "winui-crash.log");
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {exception}\r\n\r\n");
        }
        catch
        {
        }
    }

}
