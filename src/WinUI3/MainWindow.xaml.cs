using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.System;
using WindowsAppProtector.Models;
using WindowsAppProtector.Services;
using WindowsAppProtector.ViewModels;
using WinRT.Interop;

namespace WindowsAppProtector;

public sealed class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int ModShift = 0x0004;
    private const int ModWin = 0x0008;
    private const int ModNoRepeat = 0x4000;
    private const int WmClose = 0x0010;
    private const int WmTrayIcon = 0x0400 + 1;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;
    private const int OfnEnableSizing = 0x00800000;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const string MessageWindowClassName = "WindowsAppProtectorMessageWindow";
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr IdiApplication = new(32512);

    private readonly ListView appList = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly Dictionary<int, string> registeredHotkeys = new();
    private readonly HashSet<IntPtr> hiddenProtectedWindows = new();
    private readonly GitHubUpdateService updateService = new();
    private readonly DispatcherTimer windowGuardTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly WndProcDelegate messageWndProcDelegate;
    private readonly IntPtr messageWindowHandle;
    private bool isClosing;
    private bool isExitRequested;
    private bool isAuthenticating;
    private bool isCheckingForUpdates;
    private bool isAutoLockCheckRunning;
    private bool trayIconAdded;
    private IntPtr hwnd;
    private AppWindow? appWindow;
    private SettingsWindow? settingsWindow;
    private int nextHotkeyId = 100;

    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        messageWndProcDelegate = MessageWndProc;
        messageWindowHandle = CreateMessageWindow();
        Title = "Windows App Protector";
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        AddTrayIcon();
        Content = BuildContent();
        windowGuardTimer.Tick += WindowGuardTimer_Tick;
        windowGuardTimer.Start();
        _ = InitializeAsync();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        EnsureMainWindowInterop();
    }

    private async Task InitializeAsync()
    {
        await ViewModel.InitializeAsync();
        RegisterConfiguredHotkeys();
        if (!await EnsureWindowAccessAsync(isInitialLaunch: true))
        {
            ExitApplication();
            return;
        }

        _ = CheckForUpdatesAsync(showNoUpdateMessage: false);
    }

    private FrameworkElement BuildContent()
    {
        var root = new Grid
        {
            Background = Brush(0xF6, 0xF7, 0xF9),
            Padding = new Thickness(18),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var commandBarHost = new Border
        {
            Background = Brush(0xEA, 0xEE, 0xF3),
            BorderBrush = Brush(0xD7, 0xDE, 0xE8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
        };

        var commandBar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            Background = new SolidColorBrush(Colors.Transparent),
        };

        var settingsButton = new AppBarButton { Label = "\uC124\uC815" };
        settingsButton.Click += Settings_Click;

        var updateButton = new AppBarButton { Label = "\uC5C5\uB370\uC774\uD2B8 \uD655\uC778" };
        updateButton.Click += CheckUpdate_Click;

        var exitButton = new AppBarButton { Label = "\uC885\uB8CC" };
        exitButton.Click += (_, _) => ExitApplication();

        commandBar.PrimaryCommands.Add(settingsButton);
        commandBar.SecondaryCommands.Add(updateButton);
        commandBar.SecondaryCommands.Add(exitButton);
        commandBarHost.Child = commandBar;
        Grid.SetRow(commandBarHost, 0);
        root.Children.Add(commandBarHost);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 10),
        };
        toolbar.Children.Add(CreateButton("\uC571 \uCD94\uAC00", AddApp_Click));
        toolbar.Children.Add(CreateButton("\uC124\uCE58 \uC571 \uCD94\uAC00", AddInstalledApp_Click));
        toolbar.Children.Add(CreateButton("\uC0AD\uC81C", RemoveApp_Click));
        toolbar.Children.Add(CreateButton("\uC120\uD0DD \uC571 \uC7A0\uAE08/\uD574\uC81C", ToggleApp_Click));
        toolbar.Children.Add(CreateButton("\uBAA9\uB85D \uC7A0\uAE08", LockList_Click));
        toolbar.Children.Add(CreateButton("\uBAA9\uB85D \uD574\uC81C", UnlockList_Click));

        var searchBox = new TextBox
        {
            Width = 260,
            PlaceholderText = "\uAC80\uC0C9",
        };
        searchBox.SetBinding(TextBox.TextProperty, new Binding
        {
            Source = ViewModel,
            Path = new PropertyPath(nameof(ViewModel.SearchText)),
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        toolbar.Children.Add(searchBox);

        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var listHost = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = Brush(0xD9, 0xDF, 0xE8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
        };

        appList.ItemsSource = ViewModel.Apps;
        appList.SelectionChanged += AppList_SelectionChanged;
        listHost.Child = appList;
        Grid.SetRow(listHost, 2);
        root.Children.Add(listHost);

        var statusText = new TextBlock
        {
            Foreground = Brush(0x4C, 0x57, 0x66),
            Margin = new Thickness(2, 10, 0, 0),
        };
        statusText.SetBinding(TextBlock.TextProperty, new Binding
        {
            Source = ViewModel,
            Path = new PropertyPath(nameof(ViewModel.StatusText)),
            Mode = BindingMode.OneWay,
        });
        Grid.SetRow(statusText, 3);
        root.Children.Add(statusText);

        return root;
    }

    private static Button CreateButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 78,
            Padding = new Thickness(12, 6, 12, 6),
        };
        button.Click += handler;
        return button;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (settingsWindow is not null)
            {
                settingsWindow.Activate();
                settingsWindow.ResizeWindow(900, 620);
                return;
            }

            settingsWindow = new SettingsWindow(ViewModel);
            settingsWindow.Closed += (_, _) =>
            {
                settingsWindow = null;
                RegisterConfiguredHotkeys();
            };
            settingsWindow.Activate();
            settingsWindow.ResizeWindow(900, 620);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            settingsWindow = null;
            ViewModel.StatusText = $"\uC124\uC815 \uCC3D\uC744 \uC5F4 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4: {ex.Message}";
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(showNoUpdateMessage: true);
    }

    private async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ShowExecutableOpenDialog();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await ViewModel.AddAppAsync(path);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            ViewModel.StatusText = $"\uC571\uC744 \uCD94\uAC00\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4: {ex.Message}";
        }
    }

    private string? ShowExecutableOpenDialog()
    {
        EnsureMainWindowInterop();
        var buffer = Marshal.AllocCoTaskMem(4096 * sizeof(char));

        try
        {
            var emptyBuffer = new byte[4096 * sizeof(char)];
            Marshal.Copy(emptyBuffer, 0, buffer, emptyBuffer.Length);

            var dialog = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = hwnd,
                lpstrFilter = "\uC2E4\uD589 \uD30C\uC77C (*.exe)\0*.exe\0\uBAA8\uB4E0 \uD30C\uC77C (*.*)\0*.*\0",
                lpstrFile = buffer,
                nMaxFile = 4096,
                lpstrTitle = "\uC571 \uCD94\uAC00",
                lpstrDefExt = "exe",
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnEnableSizing,
            };

            if (GetOpenFileName(dialog))
            {
                return Marshal.PtrToStringUni(buffer);
            }

            var error = CommDlgExtendedError();
            if (error != 0)
            {
                throw new InvalidOperationException($"\uD30C\uC77C \uC120\uD0DD \uCC3D \uC624\uB958: 0x{error:X}");
            }

            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private async void AddInstalledApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.StatusText = "\uC124\uCE58\uB41C \uC571 \uBAA9\uB85D\uC744 \uBD88\uB7EC\uC624\uB294 \uC911\uC785\uB2C8\uB2E4.";
            var apps = await new PackagedAppDiscoveryService().GetInstalledAppsAsync();
            if (apps.Count == 0)
            {
                ViewModel.StatusText = "\uCD94\uAC00\uD560 \uC124\uCE58 \uC571\uC744 \uCC3E\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.";
                return;
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 420,
                ItemsSource = apps,
            };

            var dialog = new ContentDialog
            {
                Title = "\uC124\uCE58 \uC571 \uCD94\uAC00",
                Content = list,
                PrimaryButtonText = "\uCD94\uAC00",
                CloseButtonText = "\uB2EB\uAE30",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && list.SelectedItem is PackagedAppInfo selected)
            {
                await ViewModel.AddPackagedAppAsync(selected);
            }
            else
            {
                ViewModel.StatusText = "\uC124\uCE58 \uC571 \uCD94\uAC00\uAC00 \uCDE8\uC18C\uB418\uC5C8\uC2B5\uB2C8\uB2E4.";
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            ViewModel.StatusText = $"\uC124\uCE58 \uC571\uC744 \uCD94\uAC00\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4: {ex.Message}";
        }
    }

    private async void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RemoveSelectedAppAsync();
    }

    private async void ToggleApp_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleSelectedAppAsync();
    }

    private async void LockList_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SetListedAppsEnabledAsync(true);
    }

    private async void UnlockList_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SetListedAppsEnabledAsync(false);
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedApp = appList.SelectedItem as ProtectedAppViewModel;
    }

    private void RegisterConfiguredHotkeys()
    {
        UnregisterConfiguredHotkeys();
        ViewModel.ValidateHotkeys();
        nextHotkeyId = 100;

        if (!ViewModel.CanSaveHotkeys)
        {
            return;
        }

        foreach (var hotkey in ViewModel.Hotkeys)
        {
            if (!TryParseHotkey(hotkey.HotkeyText, out var modifiers, out var virtualKey))
            {
                continue;
            }

            var id = nextHotkeyId++;
            if (RegisterHotKey(messageWindowHandle, id, modifiers | ModNoRepeat, virtualKey))
            {
                registeredHotkeys[id] = hotkey.ActionKey;
            }
            else
            {
                ViewModel.ReportHotkeyRegistrationFailure(hotkey.ActionKey);
            }
        }
    }

    private void UnregisterConfiguredHotkeys()
    {
        foreach (var id in registeredHotkeys.Keys.ToArray())
        {
            UnregisterHotKey(messageWindowHandle, id);
        }

        registeredHotkeys.Clear();
    }

    private IntPtr MessageWndProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmTrayIcon && (lParam.ToInt32() == WmLButtonDblClk || lParam.ToInt32() == WmRButtonUp))
        {
            _ = ShowMainWindowAsync();
            return IntPtr.Zero;
        }

        if (message == WmHotkey && registeredHotkeys.TryGetValue(wParam.ToInt32(), out var actionKey))
        {
            _ = ExecuteHotkeyActionAsync(actionKey);
            return IntPtr.Zero;
        }

        return DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private async Task ExecuteHotkeyActionAsync(string actionKey)
    {
        switch (actionKey)
        {
            case "show-window":
                await ToggleWindowVisibilityAsync();
                break;
            case "lock-all":
                await ViewModel.SetListedAppsEnabledAsync(true);
                break;
            case "unlock-all":
                await ViewModel.SetListedAppsEnabledAsync(false);
                break;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (isClosing)
        {
            return;
        }

        isClosing = true;
        windowGuardTimer.Stop();
        RestoreHiddenProtectedWindows();
        UnregisterConfiguredHotkeys();
        RemoveTrayIcon();
        if (appWindow is not null)
        {
            appWindow.Closing -= AppWindow_Closing;
        }
        if (messageWindowHandle != IntPtr.Zero)
        {
            DestroyWindow(messageWindowHandle);
        }

        Application.Current.Exit();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (isExitRequested || !ViewModel.CloseToBackground)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private IntPtr CreateMessageWindow()
    {
        var instance = Marshal.GetHINSTANCE(typeof(MainWindow).Module);
        var wndClass = new WindowClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(messageWndProcDelegate),
            hInstance = instance,
            lpszClassName = MessageWindowClassName,
        };

        var classAtom = RegisterClassEx(ref wndClass);
        var registerError = Marshal.GetLastWin32Error();
        if (classAtom == 0 && registerError != 1410)
        {
            throw new InvalidOperationException("\uB2E8\uCD95\uD0A4 \uBA54\uC2DC\uC9C0 \uCC3D\uC744 \uB4F1\uB85D\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.");
        }

        var handle = CreateWindowEx(
            0,
            MessageWindowClassName,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("\uB2E8\uCD95\uD0A4 \uBA54\uC2DC\uC9C0 \uCC3D\uC744 \uB9CC\uB4E4 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.");
        }

        return handle;
    }

    private void AddTrayIcon()
    {
        EnsureTrayIcon();
    }

    private void EnsureTrayIcon()
    {
        var data = CreateTrayIconData();
        if (trayIconAdded && Shell_NotifyIcon(NimModify, ref data))
        {
            return;
        }

        trayIconAdded = Shell_NotifyIcon(NimAdd, ref data);
    }

    private void RemoveTrayIcon()
    {
        if (!trayIconAdded)
        {
            return;
        }

        var data = CreateTrayIconData();
        Shell_NotifyIcon(NimDelete, ref data);
        trayIconAdded = false;
    }

    private NotifyIconData CreateTrayIconData()
    {
        return new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = messageWindowHandle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayIcon,
            hIcon = LoadIcon(IntPtr.Zero, IdiApplication),
            szTip = "Windows App Protector",
        };
    }

    private void ExitApplication()
    {
        isExitRequested = true;
        Close();
    }

    private async Task CheckForUpdatesAsync(bool showNoUpdateMessage)
    {
        if (isCheckingForUpdates)
        {
            return;
        }

        isCheckingForUpdates = true;
        try
        {
            if (showNoUpdateMessage)
            {
                ViewModel.StatusText = "\uC5C5\uB370\uC774\uD2B8\uB97C \uD655\uC778\uD558\uB294 \uC911\uC785\uB2C8\uB2E4.";
            }

            var result = await updateService.CheckForUpdateAsync();
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                if (showNoUpdateMessage)
                {
                    await ShowMessageDialogAsync("\uC5C5\uB370\uC774\uD2B8 \uD655\uC778 \uC2E4\uD328", result.ErrorMessage);
                }

                return;
            }

            if (!result.IsUpdateAvailable || result.Release is null)
            {
                if (showNoUpdateMessage)
                {
                    await ShowMessageDialogAsync(
                        "\uC5C5\uB370\uC774\uD2B8",
                        $"\uD604\uC7AC \uCD5C\uC2E0 \uBC84\uC804\uC785\uB2C8\uB2E4. ({GetCurrentVersionText()})");
                }

                return;
            }

            await PromptAndInstallUpdateAsync(result.Release);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            if (showNoUpdateMessage)
            {
                await ShowMessageDialogAsync("\uC5C5\uB370\uC774\uD2B8 \uD655\uC778 \uC2E4\uD328", ex.Message);
            }
        }
        finally
        {
            isCheckingForUpdates = false;
        }
    }

    private async Task PromptAndInstallUpdateAsync(GitHubReleaseInfo release)
    {
        var releaseName = string.IsNullOrWhiteSpace(release.ReleaseName)
            ? release.TagName
            : release.ReleaseName;
        var dialog = new ContentDialog
        {
            Title = "\uC0C8 \uC5C5\uB370\uC774\uD2B8",
            Content = new TextBlock
            {
                Text = $"현재 버전: {GetCurrentVersionText()}\n최신 버전: {release.LatestVersion}\n\n{releaseName}을 설치하시겠습니까?",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "\uC124\uCE58",
            CloseButtonText = "\uB098\uC911\uC5D0",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.StatusText = "\uC5C5\uB370\uC774\uD2B8\uB97C \uB2E4\uC6B4\uB85C\uB4DC\uD558\uB294 \uC911\uC785\uB2C8\uB2E4.";
        var installerPath = await updateService.DownloadInstallerAsync(release);

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "--update-pid " + Environment.ProcessId,
            UseShellExecute = true,
            Verb = "runas",
        });

        ExitApplication();
    }

    private async Task ShowMessageDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "\uD655\uC778",
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private static string GetCurrentVersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private void HideToTray()
    {
        EnsureMainWindowInterop();
        ShowWindow(hwnd, SwHide);
        ViewModel.StatusText = "\uBC31\uADF8\uB77C\uC6B4\uB4DC\uC5D0\uC11C \uC2E4\uD589 \uC911\uC785\uB2C8\uB2E4. \uC228\uACA8\uC9C4 \uC544\uC774\uCF58\uC5D0\uC11C \uB2E4\uC2DC \uC5F4 \uC218 \uC788\uC2B5\uB2C8\uB2E4.";
    }

    private async Task ShowMainWindowAsync()
    {
        EnsureMainWindowInterop();
        ShowWindow(hwnd, SwShow);
        ShowWindow(hwnd, SwRestore);
        Activate();
        if (!await EnsureWindowAccessAsync(isInitialLaunch: false))
        {
            HideToTray();
        }
    }

    private async Task ToggleWindowVisibilityAsync()
    {
        EnsureMainWindowInterop();
        if (IsWindowVisible(hwnd))
        {
            HideToTray();
            return;
        }

        await ShowMainWindowAsync();
    }

    private async Task<bool> EnsureWindowAccessAsync(bool isInitialLaunch)
    {
        if (isAuthenticating)
        {
            return false;
        }

        isAuthenticating = true;
        try
        {
            EnsureMainWindowInterop();
            ShowWindow(hwnd, SwShow);
            ShowWindow(hwnd, SwRestore);
            Activate();

            if (!ViewModel.HasAppPin)
            {
                return await ShowCreatePinDialogAsync();
            }

            return await ShowVerifyPinDialogAsync(isInitialLaunch);
        }
        finally
        {
            isAuthenticating = false;
        }
    }

    private async Task<bool> ShowCreatePinDialogAsync()
    {
        var errorText = string.Empty;

        while (true)
        {
            var pinBox = new PasswordBox
            {
                Header = "\uC0C8 PIN",
                PlaceholderText = "4\uC790\uB9AC \uC774\uC0C1",
                MaxLength = 32,
            };
            var confirmBox = new PasswordBox
            {
                Header = "PIN \uD655\uC778",
                PlaceholderText = "\uB2E4\uC2DC \uC785\uB825",
                MaxLength = 32,
            };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = "\uC571 \uC2E4\uD589 \uBC0F \uBC31\uADF8\uB77C\uC6B4\uB4DC\uC5D0\uC11C \uB2E4\uC2DC \uC5F4 \uB54C \uC0AC\uC6A9\uD560 PIN\uC744 \uC124\uC815\uD558\uC138\uC694.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(0x4C, 0x57, 0x66),
            });
            if (!string.IsNullOrWhiteSpace(errorText))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = errorText,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush(0xA5, 0x21, 0x2B),
                });
            }
            panel.Children.Add(pinBox);
            panel.Children.Add(confirmBox);

            var dialog = new ContentDialog
            {
                Title = "PIN \uC124\uC815",
                Content = panel,
                PrimaryButtonText = "\uC124\uC815",
                CloseButtonText = "\uC885\uB8CC",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return false;
            }

            if (pinBox.Password.Length < 4)
            {
                errorText = "PIN\uC740 4\uC790\uB9AC \uC774\uC0C1\uC73C\uB85C \uC124\uC815\uD558\uC138\uC694.";
                ViewModel.StatusText = errorText;
                continue;
            }

            if (!string.Equals(pinBox.Password, confirmBox.Password, StringComparison.Ordinal))
            {
                errorText = "PIN \uD655\uC778\uC774 \uC77C\uCE58\uD558\uC9C0 \uC54A\uC2B5\uB2C8\uB2E4.";
                ViewModel.StatusText = errorText;
                continue;
            }

            await ViewModel.SetAppPinAsync(pinBox.Password);
            ViewModel.StatusText = "PIN\uC774 \uC124\uC815\uB418\uC5C8\uC2B5\uB2C8\uB2E4.";
            return true;
        }
    }

    private async Task<bool> ShowVerifyPinDialogAsync(bool isInitialLaunch)
    {
        var errorText = string.Empty;

        while (true)
        {
            var pinBox = new PasswordBox
            {
                Header = "PIN",
                PlaceholderText = "PIN \uC785\uB825",
                MaxLength = 32,
            };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = isInitialLaunch
                    ? "\uC571\uC744 \uC5F4\uB824\uBA74 PIN\uC744 \uC785\uB825\uD558\uC138\uC694."
                    : "\uBC31\uADF8\uB77C\uC6B4\uB4DC\uC5D0\uC11C \uB2E4\uC2DC \uC5F4\uB824\uBA74 PIN\uC744 \uC785\uB825\uD558\uC138\uC694.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(0x4C, 0x57, 0x66),
            });
            if (!string.IsNullOrWhiteSpace(errorText))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = errorText,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush(0xA5, 0x21, 0x2B),
                });
            }
            panel.Children.Add(pinBox);

            var dialog = new ContentDialog
            {
                Title = "\uC778\uC99D",
                Content = panel,
                PrimaryButtonText = "\uD655\uC778",
                CloseButtonText = isInitialLaunch ? "\uC885\uB8CC" : "\uB2EB\uAE30",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return false;
            }

            if (ViewModel.VerifyAppPin(pinBox.Password))
            {
                return true;
            }

            errorText = "PIN\uC774 \uC77C\uCE58\uD558\uC9C0 \uC54A\uC2B5\uB2C8\uB2E4.";
            ViewModel.StatusText = errorText;
        }
    }

    private async void WindowGuardTimer_Tick(object? sender, object e)
    {
        try
        {
            EnsureTrayIcon();
            if (!isAutoLockCheckRunning)
            {
                isAutoLockCheckRunning = true;
                try
                {
                    await ViewModel.AutoLockIfIdleAsync(GetSystemIdleTime());
                }
                finally
                {
                    isAutoLockCheckRunning = false;
                }
            }

            var lockedExecutables = ViewModel.GetLockedExecutableNames();
            if (lockedExecutables.Count == 0)
            {
                RestoreHiddenProtectedWindows();
                return;
            }

            HideLockedAppWindows(lockedExecutables);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
        }
    }

    private static TimeSpan GetSystemIdleTime()
    {
        var lastInput = new LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>(),
        };

        if (!GetLastInputInfo(ref lastInput))
        {
            return TimeSpan.Zero;
        }

        var currentTick = unchecked((uint)Environment.TickCount);
        var idleMilliseconds = unchecked(currentTick - lastInput.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    private void HideLockedAppWindows(IReadOnlySet<string> lockedExecutables)
    {
        var ownProcessId = Environment.ProcessId;
        EnumWindows((windowHandle, _) =>
        {
            if (windowHandle == hwnd || !IsWindowVisible(windowHandle))
            {
                return true;
            }

            GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId == 0 || processId == ownProcessId)
            {
                return true;
            }

            var exeName = GetProcessExecutableName(processId);
            if (string.IsNullOrWhiteSpace(exeName) || !lockedExecutables.Contains(exeName))
            {
                return true;
            }

            ShowWindow(windowHandle, SwHide);
            hiddenProtectedWindows.Add(windowHandle);
            ViewModel.StatusText = $"\uC7A0\uAE08\uB41C \uC571 \uCC3D\uC744 \uC228\uACBC\uC2B5\uB2C8\uB2E4: {exeName}";
            return true;
        }, IntPtr.Zero);
    }

    private void RestoreHiddenProtectedWindows()
    {
        foreach (var windowHandle in hiddenProtectedWindows.ToArray())
        {
            if (IsWindow(windowHandle))
            {
                ShowWindow(windowHandle, SwShow);
                ShowWindow(windowHandle, SwRestore);
            }
        }

        hiddenProtectedWindows.Clear();
    }

    private static string GetProcessExecutableName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            try
            {
                var fileName = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return Path.GetFileName(fileName);
                }
            }
            catch
            {
            }

            return process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";
        }
        catch
        {
            return string.Empty;
        }
    }

    private void EnsureMainWindowInterop()
    {
        if (hwnd == IntPtr.Zero)
        {
            hwnd = WindowNative.GetWindowHandle(this);
        }

        if (hwnd == IntPtr.Zero || appWindow is not null)
        {
            return;
        }

        appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Closing += AppWindow_Closing;
    }

    private static bool TryParseHotkey(string text, out int modifiers, out int virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        var normalizedText = text
            .Replace("Num+", "NumPlus", StringComparison.OrdinalIgnoreCase)
            .Replace("Num-", "NumMinus", StringComparison.OrdinalIgnoreCase)
            .Replace("Num*", "NumMultiply", StringComparison.OrdinalIgnoreCase)
            .Replace("Num/", "NumDivide", StringComparison.OrdinalIgnoreCase)
            .Replace("Num.", "NumDecimal", StringComparison.OrdinalIgnoreCase);

        var parts = normalizedText
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts.Take(parts.Length - 1))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        return TryParseVirtualKey(parts[^1], out virtualKey);
    }

    private static bool TryParseVirtualKey(string keyText, out int virtualKey)
    {
        virtualKey = 0;
        var normalized = keyText.ToUpperInvariant();

        if (normalized.Length == 1)
        {
            var ch = normalized[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (normalized.StartsWith("F", StringComparison.Ordinal) &&
            int.TryParse(normalized[1..], out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            virtualKey = 0x70 + functionNumber - 1;
            return true;
        }

        if (Enum.TryParse<VirtualKey>(keyText, ignoreCase: true, out var key))
        {
            virtualKey = (int)key;
            return true;
        }

        virtualKey = normalized switch
        {
            "ESC" => (int)VirtualKey.Escape,
            "BACKSPACE" => (int)VirtualKey.Back,
            "PAGEUP" => (int)VirtualKey.PageUp,
            "PAGEDOWN" => (int)VirtualKey.PageDown,
            "NUMPLUS" => (int)VirtualKey.Add,
            "NUMMINUS" => (int)VirtualKey.Subtract,
            "NUMMULTIPLY" => (int)VirtualKey.Multiply,
            "NUMDIVIDE" => (int)VirtualKey.Divide,
            "NUMDECIMAL" => (int)VirtualKey.Decimal,
            _ => 0,
        };

        return virtualKey != 0;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName([In, Out] OpenFileName lpofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();
}
