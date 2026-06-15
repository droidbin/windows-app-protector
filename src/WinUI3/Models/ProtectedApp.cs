namespace WindowsAppProtector.Models;

public sealed class ProtectedApp
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsPackaged { get; set; }
    public string PackageFamilyName { get; set; } = string.Empty;
    public string AppUserModelId { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
