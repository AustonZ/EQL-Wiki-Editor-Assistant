namespace EQLWikiAssistant.Core.Input;

/// <summary>
/// Modifier keys of a hotkey. The values are Win32's <c>MOD_*</c> flags for <c>RegisterHotKey</c>, so the capture
/// project passes them straight through.
///
/// Here rather than in the capture project because the setting is saved by the portable pipeline, which cannot
/// reference a Windows-only assembly — and one enum in one place beats two that must be kept equal.
/// </summary>
[Flags]
public enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

/// <summary>
/// A key combination for the global capture hotkey: modifiers plus one Win32 virtual-key code.
///
/// <see cref="Problem"/> is the one rule about which combinations are acceptable, used by the settings window before
/// it tries one and by the settings loader on a saved one, so the two cannot disagree.
/// </summary>
public sealed record HotKeyChord(HotKeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>Ctrl+Shift+E, the hotkey the tool has always shipped with.</summary>
    public static HotKeyChord Default { get; } = new(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x45);

    /// <summary>
    /// Why this combination cannot be the capture hotkey, or null if it can.
    ///
    /// **Ctrl, Alt or Win is required.** The hotkey is global and swallows its keystroke wherever it is pressed, so a
    /// bare key or a Shift+key would fire while typing — in the game's own chat, most of all — and the typed
    /// character would never arrive. Whether Windows will actually grant a combination (another program may hold it)
    /// is only known by asking, so that is the caller's to report.
    /// </summary>
    /// <remarks>Ignored by JSON: it is derived, and a saved copy would only ever be stale.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Problem =>
        VirtualKey == 0 || IsModifierKey(VirtualKey)
            ? "Choose a key to go with the modifiers."
            : (Modifiers & (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Windows)) == 0
                ? "Include Ctrl, Alt or Win. A hotkey without one would fire while typing, game chat included, " +
                  "and swallow the key you typed."
                : null;

    /// <summary>The combination as a person would write it: <c>Ctrl+Shift+E</c>.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotKeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotKeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotKeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotKeyModifiers.Windows)) parts.Add("Win");
        if (VirtualKey != 0 && !IsModifierKey(VirtualKey)) parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>The virtual-key codes of the modifier keys themselves (either side), which cannot be the main key.</summary>
    public static bool IsModifierKey(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 // VK_SHIFT, VK_CONTROL, VK_MENU
        or 0x5B or 0x5C // VK_LWIN, VK_RWIN
        or >= 0xA0 and <= 0xA5; // VK_LSHIFT .. VK_RMENU

    /// <summary>A key's name as printed on the keyboard, for the keys a hotkey plausibly uses; anything else by code.</summary>
    public static string KeyName(uint virtualKey) => virtualKey switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x60 and <= 0x69 => $"Num {virtualKey - 0x60}",
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        0x91 => "Scroll Lock",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"Key 0x{virtualKey:X2}",
    };
}
