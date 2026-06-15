using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsAppProtector.Services;
using WindowsAppProtector.ViewModels;
using WinRT.Interop;

namespace WindowsAppProtector;

public sealed class SettingsWindow : Window
{
    private readonly Dictionary<string, TextBox> hotkeyTextBoxes = new();
    private readonly Dictionary<string, TextBlock> validationTextBlocks = new();
    private readonly StackPanel hotkeyList = new() { Spacing = 14 };
    private readonly Button saveButton = new() { Content = "\uC800\uC7A5", MinWidth = 84 };
    private readonly ToggleSwitch closeToBackgroundSwitch = new()
    {
        Header = "\uB2EB\uAE30 \uBC84\uD2BC\uC744 \uBC31\uADF8\uB77C\uC6B4\uB4DC\uB85C \uC804\uD658",
        OnContent = "\uCF1C\uC9D0",
        OffContent = "\uAEBC\uC9D0",
    };
    private readonly TextBlock summaryText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(0x4C, 0x57, 0x66),
        Margin = new Thickness(0, 0, 0, 8),
    };

    public SettingsWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        Title = "\uC124\uC815";
        Content = BuildContent();
        saveButton.Click += SaveButton_Click;
        LoadPreferenceValues();
        BuildHotkeyRows();
        RefreshHotkeyRows();
    }

    public MainViewModel ViewModel { get; }

    private FrameworkElement BuildContent()
    {
        var root = new Grid
        {
            Background = Brush(0xF6, 0xF7, 0xF9),
            Padding = new Thickness(22),
            MinWidth = 820,
            MinHeight = 560,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "\uC124\uC815",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var settingsContent = new StackPanel { Spacing = 22 };
        settingsContent.Children.Add(BuildGeneralSection());
        settingsContent.Children.Add(BuildHotkeySection());
        scroll.Content = settingsContent;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var closeButton = new Button { Content = "\uB2EB\uAE30", MinWidth = 84 };
        closeButton.Click += (_, _) => Close();
        footer.Children.Add(saveButton);
        footer.Children.Add(closeButton);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private FrameworkElement BuildGeneralSection()
    {
        var section = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = Brush(0xD9, 0xDF, 0xE8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(18),
        };

        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = "\uC77C\uBC18",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(closeToBackgroundSwitch);

        section.Child = panel;
        return section;
    }

    private FrameworkElement BuildHotkeySection()
    {
        var section = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = Brush(0xD9, 0xDF, 0xE8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(18),
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "\uB2E8\uCD95\uD0A4",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(hotkeyList);
        section.Child = panel;
        return section;
    }

    private void LoadPreferenceValues()
    {
        closeToBackgroundSwitch.IsOn = ViewModel.CloseToBackground;
    }

    private void BuildHotkeyRows()
    {
        hotkeyList.Children.Clear();
        hotkeyTextBoxes.Clear();
        validationTextBlocks.Clear();
        hotkeyList.Children.Add(summaryText);

        foreach (var hotkey in ViewModel.Hotkeys)
        {
            var row = new Grid
            {
                ColumnSpacing = 16,
                MinHeight = 72,
                Padding = new Thickness(0, 8, 0, 8),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelPanel = new StackPanel { Spacing = 4 };
            labelPanel.Children.Add(new TextBlock
            {
                Text = hotkey.DisplayName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            labelPanel.Children.Add(new TextBlock
            {
                Text = hotkey.Description,
                FontSize = 12,
                Foreground = Brush(0x6B, 0x72, 0x80),
                TextWrapping = TextWrapping.Wrap,
            });

            var editor = new TextBox
            {
                IsReadOnly = true,
                PlaceholderText = "\uD0A4 \uC785\uB825",
                Tag = hotkey.ActionKey,
                MinWidth = 200,
                VerticalAlignment = VerticalAlignment.Center,
            };
            editor.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(HotkeyTextBox_KeyDown), true);

            var validationText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(0xA5, 0x21, 0x2B),
                TextWrapping = TextWrapping.Wrap,
            };

            Grid.SetColumn(labelPanel, 0);
            Grid.SetColumn(editor, 1);
            Grid.SetColumn(validationText, 2);

            row.Children.Add(labelPanel);
            row.Children.Add(editor);
            row.Children.Add(validationText);

            hotkeyList.Children.Add(row);
            hotkeyTextBoxes[hotkey.ActionKey] = editor;
            validationTextBlocks[hotkey.ActionKey] = validationText;
        }
    }

    private void HotkeyTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string actionKey })
        {
            return;
        }

        var hotkeyText = HotkeyFormatter.FromVirtualKey(e.Key);
        if (hotkeyText is null)
        {
            e.Handled = true;
            return;
        }

        ViewModel.SetHotkey(actionKey, hotkeyText);
        RefreshHotkeyRows();
        e.Handled = true;
    }

    private void RefreshHotkeyRows()
    {
        ViewModel.ValidateHotkeys();
        saveButton.IsEnabled = ViewModel.CanSaveHotkeys;
        summaryText.Text = ViewModel.HotkeyValidationMessage;
        summaryText.Foreground = ViewModel.CanSaveHotkeys
            ? Brush(0x4C, 0x57, 0x66)
            : Brush(0xA5, 0x21, 0x2B);

        foreach (var hotkey in ViewModel.Hotkeys)
        {
            if (hotkeyTextBoxes.TryGetValue(hotkey.ActionKey, out var editor))
            {
                editor.Text = hotkey.HotkeyText;
            }

            if (validationTextBlocks.TryGetValue(hotkey.ActionKey, out var validationText))
            {
                validationText.Text = hotkey.ValidationMessage;
            }
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshHotkeyRows();
        if (ViewModel.CanSaveHotkeys)
        {
            await ViewModel.SavePreferencesAsync(closeToBackgroundSwitch.IsOn);
            Close();
        }
    }

    public void ResizeWindow(int width, int height)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(width, height));
        }
        catch
        {
        }
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

}
