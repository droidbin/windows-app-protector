using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.System;

namespace WindowsAppProtector.Services;

public static class HotkeyFormatter
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    public static string? FromVirtualKey(VirtualKey key)
    {
        if (IsModifierKey(key))
        {
            return null;
        }

        var keyText = FormatKey(key);
        if (keyText is null)
        {
            return null;
        }

        var parts = new List<string>(5);

        if (IsKeyDown(VkControl))
        {
            parts.Add("Ctrl");
        }

        if (IsKeyDown(VkMenu))
        {
            parts.Add("Alt");
        }

        if (IsKeyDown(VkShift))
        {
            parts.Add("Shift");
        }

        if (IsKeyDown(VkLWin) || IsKeyDown(VkRWin))
        {
            parts.Add("Win");
        }

        parts.Add(keyText);
        return string.Join("+", parts);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static bool IsModifierKey(VirtualKey key)
    {
        return key is VirtualKey.Control
            or VirtualKey.LeftControl
            or VirtualKey.RightControl
            or VirtualKey.Menu
            or VirtualKey.LeftMenu
            or VirtualKey.RightMenu
            or VirtualKey.Shift
            or VirtualKey.LeftShift
            or VirtualKey.RightShift
            or VirtualKey.LeftWindows
            or VirtualKey.RightWindows;
    }

    private static string? FormatKey(VirtualKey key)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            return key.ToString();
        }

        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            return ((int)key - (int)VirtualKey.Number0).ToString();
        }

        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            return $"Num{(int)key - (int)VirtualKey.NumberPad0}";
        }

        if (key is >= VirtualKey.F1 and <= VirtualKey.F24)
        {
            return key.ToString();
        }

        return key switch
        {
            VirtualKey.Back => "Backspace",
            VirtualKey.Tab => "Tab",
            VirtualKey.Enter => "Enter",
            VirtualKey.Escape => "Esc",
            VirtualKey.Space => "Space",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.End => "End",
            VirtualKey.Home => "Home",
            VirtualKey.Left => "Left",
            VirtualKey.Up => "Up",
            VirtualKey.Right => "Right",
            VirtualKey.Down => "Down",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Add => "Num+",
            VirtualKey.Subtract => "Num-",
            VirtualKey.Multiply => "Num*",
            VirtualKey.Divide => "Num/",
            VirtualKey.Decimal => "Num.",
            _ => null,
        };
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
