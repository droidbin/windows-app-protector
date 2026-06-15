namespace WindowsAppProtector.Models;

public sealed class PackagedAppInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsPackaged { get; set; } = true;
    public string PackageFamilyName { get; set; } = string.Empty;
    public string AppUserModelId { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    public override string ToString()
    {
        var location = IsPackaged
            ? (string.IsNullOrWhiteSpace(PackageFamilyName) ? AppUserModelId : PackageFamilyName)
            : Path;
        return $"{Name}    {ExecutableName}    {Source}    {location}";
    }
}
