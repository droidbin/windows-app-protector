namespace WindowsAppProtector.Models;

public sealed class PackagedAppInfo
{
    public string Name { get; set; } = string.Empty;
    public string PackageFamilyName { get; set; } = string.Empty;
    public string AppUserModelId { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"{Name}    {ExecutableName}    {PackageFamilyName}";
    }
}
