using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsAppProtector.ViewModels;

public class HotkeySettingViewModel : ObservableObject
{
    private string hotkeyText;
    private bool hasDuplicate;
    private string validationMessage = string.Empty;

    public HotkeySettingViewModel(string actionKey, string displayName, string description, string hotkeyText)
    {
        ActionKey = actionKey;
        DisplayName = displayName;
        Description = description;
        this.hotkeyText = hotkeyText;
    }

    public string ActionKey { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string HotkeyText
    {
        get => hotkeyText;
        set => SetProperty(ref hotkeyText, value);
    }

    public bool HasDuplicate
    {
        get => hasDuplicate;
        set => SetProperty(ref hasDuplicate, value);
    }

    public string ValidationMessage
    {
        get => validationMessage;
        set => SetProperty(ref validationMessage, value);
    }
}
