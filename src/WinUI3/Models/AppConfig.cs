namespace WindowsAppProtector.Models;

public sealed class AppConfig
{
    public bool ProtectionEnabled { get; set; }
    public bool CloseToBackground { get; set; } = true;
    public int UnlockMinutes { get; set; } = 10;
    public string? UnlockUntil { get; set; }
    public string AppPinSalt { get; set; } = string.Empty;
    public string AppPinHash { get; set; } = string.Empty;
    public Dictionary<string, string> GlobalHotkeys { get; set; } = new()
    {
        ["show-window"] = "Ctrl+Alt+W",
        ["lock-all"] = "Ctrl+Shift+L",
        ["unlock-all"] = "Ctrl+Shift+U",
    };

    public List<ProtectedApp> ProtectedApps { get; set; } = new();
}
