using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsAppProtector.ViewModels;

public class ProtectedAppViewModel : ObservableObject
{
    private string name = string.Empty;
    private string identity = string.Empty;
    private string path = string.Empty;
    private string status = "\uB300\uAE30";

    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    public string Identity
    {
        get => identity;
        set => SetProperty(ref identity, value);
    }

    public string Path
    {
        get => path;
        set => SetProperty(ref path, value);
    }

    public string Status
    {
        get => status;
        set => SetProperty(ref status, value);
    }

    public override string ToString()
    {
        var lockState = Status.Length == 0 ? "\uB300\uAE30" : Status;
        return $"{Name}    {lockState}    {Path}";
    }
}
