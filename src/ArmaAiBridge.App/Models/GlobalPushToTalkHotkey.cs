using System.Windows.Input;

namespace ArmaAiBridge.App.Models;

[Flags]
public enum GlobalHotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed record GlobalPushToTalkHotkey(
    bool Enabled,
    GlobalHotkeyModifiers Modifiers,
    int VirtualKey)
{
    public static GlobalPushToTalkHotkey Default { get; } = new(true, GlobalHotkeyModifiers.Shift, 0x20);

    public void Validate()
    {
        const GlobalHotkeyModifiers all = GlobalHotkeyModifiers.Alt | GlobalHotkeyModifiers.Control |
                                          GlobalHotkeyModifiers.Shift | GlobalHotkeyModifiers.Windows;
        if (Modifiers == GlobalHotkeyModifiers.None || (Modifiers & ~all) != 0)
            throw new InvalidOperationException("A supported modifier and one primary key are required.");
        if (VirtualKey is < 0x08 or > 0xFE || VirtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
            0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
            throw new InvalidOperationException("The primary hotkey must be one non-modifier key.");
    }

    public string DisplayName
    {
        get
        {
            List<string> parts = new();
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Control)) parts.Add("Control");
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Windows)) parts.Add("Windows");
            Key key = KeyInterop.KeyFromVirtualKey(VirtualKey);
            parts.Add(key == Key.Space ? "Space" : key.ToString());
            return string.Join(" + ", parts);
        }
    }
}
