namespace WindowsAppProtector.Models;

public sealed class AppConfig
{
    public bool ProtectionEnabled { get; set; }
    public bool CloseToBackground { get; set; } = true;
    public int UnlockMinutes { get; set; } = 10;
    public int AuthCacheMinutes { get; set; } = 10;
    public string? UnlockUntil { get; set; }
    public Dictionary<string, string> GlobalHotkeys { get; set; } = new()
    {
        ["show-window"] = "Ctrl+Alt+W",
        ["lock-all"] = "Ctrl+Shift+L",
        ["unlock-all"] = "Ctrl+Shift+U",
    };

    public List<ProtectedApp> ProtectedApps { get; set; } = new();
}
