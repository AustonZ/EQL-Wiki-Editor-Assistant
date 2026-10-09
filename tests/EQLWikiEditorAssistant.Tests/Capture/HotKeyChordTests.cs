using EQLWikiEditorAssistant.Capture;
using EQLWikiEditorAssistant.Core.Input;

namespace EQLWikiEditorAssistant.Tests.Capture;

public class HotKeyChordTests
{
    [Fact]
    public void TheDefaultIsCtrlShiftEAndAcceptable()
    {
        Assert.Equal("Ctrl+Shift+E", HotKeyChord.Default.ToString());
        Assert.Null(HotKeyChord.Default.Problem);
    }

    /// <summary>The values are passed to RegisterHotKey as they are, so they must be Win32's MOD_* flags.</summary>
    [Fact]
    public void ModifierValuesAreWin32Flags()
    {
        Assert.Equal(0x1u, (uint)HotKeyModifiers.Alt);
        Assert.Equal(0x2u, (uint)HotKeyModifiers.Control);
        Assert.Equal(0x4u, (uint)HotKeyModifiers.Shift);
        Assert.Equal(0x8u, (uint)HotKeyModifiers.Windows);
    }

    /// <summary>A global hotkey swallows its keystroke, so one without Ctrl, Alt or Win would eat typed characters.
    /// Each of the three is enough on its own, which is the control: a rule demanding more would also pass these.</summary>
    [Theory]
    [InlineData(HotKeyModifiers.None, false)]
    [InlineData(HotKeyModifiers.Shift, false)]
    [InlineData(HotKeyModifiers.Control, true)]
    [InlineData(HotKeyModifiers.Alt, true)]
    [InlineData(HotKeyModifiers.Windows, true)]
    [InlineData(HotKeyModifiers.Shift | HotKeyModifiers.Alt, true)]
    public void NeedsCtrlAltOrWin(HotKeyModifiers modifiers, bool acceptable)
    {
        Assert.Equal(acceptable, new HotKeyChord(modifiers, 0x45).Problem is null);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x10u)] // VK_SHIFT
    [InlineData(0xA2u)] // VK_LCONTROL
    [InlineData(0x5Bu)] // VK_LWIN
    public void AModifierIsNotAKey(uint virtualKey)
    {
        Assert.NotNull(new HotKeyChord(HotKeyModifiers.Control, virtualKey).Problem);
    }

    [Theory]
    [InlineData(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x78u, "Ctrl+Alt+F9")]
    [InlineData(HotKeyModifiers.Windows | HotKeyModifiers.Shift, 0x33u, "Shift+Win+3")]
    [InlineData(HotKeyModifiers.Alt, 0xC0u, "Alt+`")]
    [InlineData(HotKeyModifiers.Control, 0x6Bu, "Ctrl+Num +")]
    [InlineData(HotKeyModifiers.Control, 0xFEu, "Ctrl+Key 0xFE")]
    public void ReadsTheWayItIsWritten(HotKeyModifiers modifiers, uint virtualKey, string expected)
    {
        Assert.Equal(expected, new HotKeyChord(modifiers, virtualKey).ToString());
    }

    /// <summary>
    /// Changing the hotkey registers the new combination before releasing the old, so two must be able to exist at
    /// once. They could not: every instance registered the same window class name, so the second failed outright and
    /// no change was ever possible. Combinations nobody uses, so the test does not fight a real program for them.
    /// </summary>
    [Fact]
    public void TwoHotKeysCanBeRegisteredAtOnce()
    {
        const HotKeyModifiers All = HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift;
        using var first = new GlobalHotKey(new HotKeyChord(All, 0x86)); // F23
        using var second = new GlobalHotKey(new HotKeyChord(All, 0x87)); // F24
    }

    /// <summary>A combination Windows refuses must leave nothing behind, or the next attempt — the user trying
    /// something else — would fail for a reason that has nothing to do with the combination they chose.</summary>
    [Fact]
    public void ARefusedCombinationDoesNotSpoilTheNextAttempt()
    {
        const HotKeyModifiers All = HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift;
        var taken = new HotKeyChord(All, 0x86);
        using var holder = new GlobalHotKey(taken);

        Assert.Throws<InvalidOperationException>(() => new GlobalHotKey(taken));

        using var next = new GlobalHotKey(new HotKeyChord(All, 0x87));
    }
}
