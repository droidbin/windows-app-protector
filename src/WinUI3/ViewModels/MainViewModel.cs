using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using WindowsAppProtector.Models;
using WindowsAppProtector.Services;

namespace WindowsAppProtector.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly ISettingsStore settingsStore;
    private readonly IProtectionService protectionService;
    private readonly AppConfig config = new();
    private bool protectionEnabled;
    private string searchText = string.Empty;
    private string statusText = "\uC7A0\uAE08 \uC0C1\uD0DC: \uB300\uAE30";
    private string hotkeyValidationMessage = string.Empty;
    private bool canSaveHotkeys = true;
    private ProtectedAppViewModel? selectedApp;

    public MainViewModel()
        : this(new JsonSettingsStore(), new ProtectionService())
    {
    }

    public MainViewModel(ISettingsStore settingsStore, IProtectionService protectionService)
    {
        this.settingsStore = settingsStore;
        this.protectionService = protectionService;
        ValidateHotkeys();
    }

    public bool ProtectionEnabled
    {
        get => protectionEnabled;
        private set => SetProperty(ref protectionEnabled, value);
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value))
            {
                RefreshApps();
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    public string HotkeyValidationMessage
    {
        get => hotkeyValidationMessage;
        set => SetProperty(ref hotkeyValidationMessage, value);
    }

    public bool CanSaveHotkeys
    {
        get => canSaveHotkeys;
        set => SetProperty(ref canSaveHotkeys, value);
    }

    public ProtectedAppViewModel? SelectedApp
    {
        get => selectedApp;
        set => SetProperty(ref selectedApp, value);
    }

    public bool CloseToBackground
    {
        get => config.CloseToBackground;
        set
        {
            if (config.CloseToBackground != value)
            {
                config.CloseToBackground = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<ProtectedAppViewModel> Apps { get; } = new();

    public bool HasAppPin =>
        !string.IsNullOrWhiteSpace(config.AppPinSalt) &&
        !string.IsNullOrWhiteSpace(config.AppPinHash);

    public ObservableCollection<HotkeySettingViewModel> Hotkeys { get; } = new()
    {
        new("show-window", "\uCC3D \uBCF4\uC774\uAE30/\uC228\uAE30\uAE30", "\uBC31\uADF8\uB77C\uC6B4\uB4DC \uC2E4\uD589 \uC911\uC778 \uC571 \uCC3D\uC744 \uC804\uD658\uD569\uB2C8\uB2E4.", "Ctrl+Alt+W"),
        new("lock-all", "\uBAA9\uB85D \uC7A0\uAE08", "\uD604\uC7AC \uC571 \uBAA9\uB85D\uC758 \uC7A0\uAE08\uC744 \uD65C\uC131\uD654\uD569\uB2C8\uB2E4.", "Ctrl+Shift+L"),
        new("unlock-all", "\uBAA9\uB85D \uD574\uC81C", "\uD604\uC7AC \uC571 \uBAA9\uB85D\uC758 \uC7A0\uAE08\uC744 \uD574\uC81C\uD569\uB2C8\uB2E4.", "Ctrl+Shift+U"),
    };

    public IReadOnlySet<string> GetLockedExecutableNames()
    {
        if (!LockRulesActive())
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return config.ProtectedApps
            .Where(app => app.Enabled)
            .Select(GetExecutableName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync()
    {
        var loaded = await settingsStore.LoadAsync();
        config.ProtectionEnabled = loaded.ProtectionEnabled;
        config.CloseToBackground = loaded.CloseToBackground;
        config.UnlockUntil = null;
        OnPropertyChanged(nameof(CloseToBackground));
        if (loaded.GlobalHotkeys.Count > 0)
        {
            foreach (var hotkey in loaded.GlobalHotkeys)
            {
                config.GlobalHotkeys[hotkey.Key] = hotkey.Value;
            }
        }
        config.ProtectedApps.Clear();
        config.ProtectedApps.AddRange(loaded.ProtectedApps);

        foreach (var hotkey in Hotkeys)
        {
            if (config.GlobalHotkeys.TryGetValue(hotkey.ActionKey, out var value))
            {
                hotkey.HotkeyText = value;
            }
        }

        ProtectionEnabled = LockRulesActive();
        RefreshApps();
        ValidateHotkeys();
        await SyncProtectionRulesAsync();
        await SaveAsync();
        UpdateStatus();
    }

    public async Task SetProtectionEnabledAsync(bool enabled)
    {
        foreach (var app in config.ProtectedApps)
        {
            app.Enabled = enabled;
        }

        ProtectionEnabled = LockRulesActive();
        RefreshApps();
        await SyncProtectionRulesAsync();
        await SaveAsync();
        UpdateStatus();
    }

    public async Task AddAppAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText = "\uC2E4\uD589 \uD30C\uC77C\uC744 \uCC3E\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.";
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (config.ProtectedApps.Any(app => string.Equals(Path.GetFullPath(app.Path), fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "\uC774\uBBF8 \uBCF4\uD638 \uBAA9\uB85D\uC5D0 \uB4F1\uB85D\uB41C \uC571\uC785\uB2C8\uB2E4.";
            return;
        }

        config.ProtectedApps.Add(new ProtectedApp
        {
            Name = Path.GetFileName(fullPath),
            Path = fullPath,
            ExecutableName = Path.GetFileName(fullPath),
            Enabled = true,
        });

        RefreshApps();
        await SyncProtectionRulesAsync();
        await SaveAsync();
        StatusText = $"\uC571 \uCD94\uAC00\uB428: {Path.GetFileName(fullPath)}";
    }

    public async Task AddPackagedAppAsync(PackagedAppInfo app)
    {
        if (!app.IsPackaged)
        {
            await AddDiscoveredWin32AppAsync(app);
            return;
        }

        if (string.IsNullOrWhiteSpace(app.AppUserModelId) || string.IsNullOrWhiteSpace(app.ExecutableName))
        {
            StatusText = "\uC124\uCE58 \uC571\uC758 \uC2E4\uD589 \uC815\uBCF4\uB97C \uD655\uC778\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.";
            return;
        }

        if (config.ProtectedApps.Any(item =>
            string.Equals(item.AppUserModelId, app.AppUserModelId, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(item.ExecutableName) &&
                string.Equals(item.ExecutableName, app.ExecutableName, StringComparison.OrdinalIgnoreCase))))
        {
            StatusText = "\uC774\uBBF8 \uBCF4\uD638 \uBAA9\uB85D\uC5D0 \uB4F1\uB85D\uB41C \uC571\uC785\uB2C8\uB2E4.";
            return;
        }

        config.ProtectedApps.Add(new ProtectedApp
        {
            Name = app.Name,
            Path = app.AppUserModelId,
            IsPackaged = true,
            PackageFamilyName = app.PackageFamilyName,
            AppUserModelId = app.AppUserModelId,
            ExecutableName = app.ExecutableName,
            Enabled = true,
        });

        RefreshApps();
        await SyncProtectionRulesAsync();
        await SaveAsync();
        StatusText = $"\uC124\uCE58 \uC571 \uCD94\uAC00\uB428: {app.Name}";
    }

    private async Task AddDiscoveredWin32AppAsync(PackagedAppInfo app)
    {
        if (string.IsNullOrWhiteSpace(app.Path) || !File.Exists(app.Path))
        {
            StatusText = "\uC124\uCE58 \uC571\uC758 \uC2E4\uD589 \uD30C\uC77C\uC744 \uCC3E\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.";
            return;
        }

        var fullPath = Path.GetFullPath(app.Path);
        if (config.ProtectedApps.Any(item =>
            !item.IsPackaged &&
            string.Equals(Path.GetFullPath(item.Path), fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "\uC774\uBBF8 \uBCF4\uD638 \uBAA9\uB85D\uC5D0 \uB4F1\uB85D\uB41C \uC571\uC785\uB2C8\uB2E4.";
            return;
        }

        config.ProtectedApps.Add(new ProtectedApp
        {
            Name = string.IsNullOrWhiteSpace(app.Name) ? Path.GetFileName(fullPath) : app.Name,
            Path = fullPath,
            IsPackaged = false,
            ExecutableName = string.IsNullOrWhiteSpace(app.ExecutableName) ? Path.GetFileName(fullPath) : app.ExecutableName,
            Enabled = true,
        });

        RefreshApps();
        await SyncProtectionRulesAsync();
        await SaveAsync();
        StatusText = $"\uC124\uCE58 \uC571 \uCD94\uAC00\uB428: {app.Name}";
    }

    public async Task RemoveSelectedAppAsync()
    {
        if (SelectedApp is null)
        {
            StatusText = "\uBA3C\uC800 \uC571\uC744 \uC120\uD0DD\uD558\uC138\uC694.";
            return;
        }

        var model = FindModel(SelectedApp);
        if (model is null)
        {
            return;
        }

        config.ProtectedApps.Remove(model);
        SelectedApp = null;
        RefreshApps();
        await SyncProtectionRulesAsync();
        await SaveAsync();
        StatusText = "\uC571\uC774 \uC0AD\uC81C\uB418\uC5C8\uC2B5\uB2C8\uB2E4.";
    }

    public async Task ToggleSelectedAppAsync()
    {
        if (SelectedApp is null)
        {
            StatusText = "\uBA3C\uC800 \uC571\uC744 \uC120\uD0DD\uD558\uC138\uC694.";
            return;
        }

        var model = FindModel(SelectedApp);
        if (model is null)
        {
            return;
        }

        model.Enabled = !model.Enabled;
        RefreshApps();
        await SyncProtectionRulesAsync();
        await SaveAsync();
        StatusText = model.Enabled ? $"\uC7A0\uAE08 \uD65C\uC131\uD654: {model.Name}" : $"\uC7A0\uAE08 \uD574\uC81C: {model.Name}";
    }

    public async Task SetListedAppsEnabledAsync(bool enabled)
    {
        foreach (var app in config.ProtectedApps)
        {
            app.Enabled = enabled;
        }

        RefreshApps();
        await SyncProtectionRulesAsync();
        await SaveAsync();
        StatusText = enabled
            ? $"\uBAA9\uB85D \uC7A0\uAE08 \uC801\uC6A9: {config.ProtectedApps.Count}\uAC1C"
            : $"\uBAA9\uB85D \uC7A0\uAE08 \uD574\uC81C: {config.ProtectedApps.Count}\uAC1C";
    }

    public async Task SavePreferencesAsync(bool closeToBackground)
    {
        CloseToBackground = closeToBackground;
        await SaveAsync();
        StatusText = "\uC124\uC815\uC774 \uC800\uC7A5\uB418\uC5C8\uC2B5\uB2C8\uB2E4.";
    }

    public async Task SetAppPinAsync(string pin)
    {
        var hash = PinHasher.CreateHash(pin);
        config.AppPinSalt = hash.Salt;
        config.AppPinHash = hash.Hash;
        await SaveAsync();
    }

    public bool VerifyAppPin(string pin)
    {
        return PinHasher.Verify(pin, config.AppPinSalt, config.AppPinHash);
    }

    public async void SetHotkey(string actionKey, string hotkeyText)
    {
        var hotkey = Hotkeys.FirstOrDefault(item => item.ActionKey == actionKey);
        if (hotkey is null)
        {
            return;
        }

        hotkey.HotkeyText = hotkeyText;
        ValidateHotkeys();
        if (CanSaveHotkeys)
        {
            config.GlobalHotkeys[actionKey] = hotkeyText;
            await SaveAsync();
        }
    }

    public void ValidateHotkeys()
    {
        foreach (var hotkey in Hotkeys)
        {
            hotkey.HasDuplicate = false;
            hotkey.ValidationMessage = string.Empty;
        }

        var duplicateGroups = Hotkeys
            .Where(item => !string.IsNullOrWhiteSpace(item.HotkeyText))
            .GroupBy(item => NormalizeHotkey(item.HotkeyText))
            .Where(group => group.Count() > 1)
            .ToArray();

        foreach (var group in duplicateGroups)
        {
            foreach (var hotkey in group)
            {
                hotkey.HasDuplicate = true;
                hotkey.ValidationMessage = "\uC774\uBBF8 \uB2E4\uB978 \uAE30\uB2A5\uC5D0\uC11C \uC0AC\uC6A9 \uC911\uC785\uB2C8\uB2E4.";
            }
        }

        CanSaveHotkeys = duplicateGroups.Length == 0;
        HotkeyValidationMessage = CanSaveHotkeys
            ? "\uB2E8\uCD95\uD0A4\uB97C \uC785\uB825\uD558\uBA74 \uC989\uC2DC \uBCC0\uACBD\uB429\uB2C8\uB2E4."
            : $"\uC911\uBCF5\uB41C \uB2E8\uCD95\uD0A4\uAC00 \uC788\uC2B5\uB2C8\uB2E4: {string.Join(", ", duplicateGroups.Select(group => group.Key))}";
    }

    public void ReportHotkeyRegistrationFailure(string actionKey)
    {
        var hotkey = Hotkeys.FirstOrDefault(item => item.ActionKey == actionKey);
        if (hotkey is null)
        {
            return;
        }

        hotkey.ValidationMessage = "\uB2E4\uB978 \uD504\uB85C\uADF8\uB7A8\uC774 \uC0AC\uC6A9 \uC911\uC778 \uB2E8\uCD95\uD0A4\uC785\uB2C8\uB2E4.";
        StatusText = $"\uB2E8\uCD95\uD0A4 \uB4F1\uB85D \uC2E4\uD328: {hotkey.DisplayName} ({hotkey.HotkeyText})";
    }

    private async Task SyncProtectionRulesAsync()
    {
        try
        {
            config.UnlockUntil = null;
            var apps = config.ProtectedApps.Where(app => app.Enabled);

            config.ProtectionEnabled = LockRulesActive();
            ProtectionEnabled = config.ProtectionEnabled;
            await SaveAsync();
            await protectionService.SyncExecutionBlockRulesAsync(apps);
        }
        catch (Exception ex)
        {
            StatusText = $"\uC7A0\uAE08 \uADDC\uCE59 \uC801\uC6A9 \uC2E4\uD328: {ex.Message}";
        }
    }

    private async Task SaveAsync()
    {
        config.ProtectionEnabled = ProtectionEnabled;
        config.UnlockUntil = null;
        config.GlobalHotkeys = Hotkeys.ToDictionary(item => item.ActionKey, item => item.HotkeyText);
        await settingsStore.SaveAsync(config);
    }

    private void RefreshApps()
    {
        Apps.Clear();

        var apps = string.IsNullOrWhiteSpace(SearchText)
            ? config.ProtectedApps
            : config.ProtectedApps
                .Where(app =>
                    app.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    app.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    app.AppUserModelId.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    app.PackageFamilyName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    app.ExecutableName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();

        foreach (var app in apps)
        {
            Apps.Add(new ProtectedAppViewModel
            {
                Name = app.Name,
                Identity = GetAppIdentity(app),
                Path = GetDisplayPath(app),
                Status = app.Enabled ? "\uC7A0\uAE08" : "\uD574\uC81C",
            });
        }
    }

    private ProtectedApp? FindModel(ProtectedAppViewModel viewModel)
    {
        return config.ProtectedApps.FirstOrDefault(app =>
            string.Equals(GetAppIdentity(app), viewModel.Identity, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetAppIdentity(ProtectedApp app)
    {
        return app.IsPackaged && !string.IsNullOrWhiteSpace(app.AppUserModelId)
            ? app.AppUserModelId
            : app.Path;
    }

    private static string GetDisplayPath(ProtectedApp app)
    {
        if (app.IsPackaged)
        {
            var exeName = string.IsNullOrWhiteSpace(app.ExecutableName) ? "\uC2E4\uD589 \uD30C\uC77C \uBBF8\uD655\uC778" : app.ExecutableName;
            var identity = string.IsNullOrWhiteSpace(app.AppUserModelId) ? app.PackageFamilyName : app.AppUserModelId;
            return $"{identity} ({exeName})";
        }

        return app.Path;
    }

    private static string GetExecutableName(ProtectedApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.ExecutableName))
        {
            return app.ExecutableName;
        }

        return string.IsNullOrWhiteSpace(app.Path) ? string.Empty : Path.GetFileName(app.Path);
    }

    private bool LockRulesActive()
    {
        return config.ProtectedApps.Any(app => app.Enabled);
    }

    private void UpdateStatus()
    {
        var lockedCount = config.ProtectedApps.Count(app => app.Enabled);
        StatusText = lockedCount > 0
            ? $"\uC7A0\uAE08 \uC0C1\uD0DC: \uC801\uC6A9 \uC911, \uC7A0\uAE08 \uC571 {lockedCount}\uAC1C"
            : "\uC7A0\uAE08 \uC0C1\uD0DC: \uC7A0\uAE08\uB41C \uC571 \uC5C6\uC74C";
    }

    private static string NormalizeHotkey(string hotkeyText)
    {
        return string.Join(
            "+",
            hotkeyText
                .Split('+')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Select(part => part.ToUpperInvariant()));
    }
}
