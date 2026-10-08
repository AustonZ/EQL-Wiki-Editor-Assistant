using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQLWikiAssistant.Core.Icons;
using EQLWikiAssistant.Core.Input;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Pipeline;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.App;

/// <summary>
/// The UI font, the capture hotkey, whether captures are kept, the wiki icon cache, the wiki login, and a read-only
/// view of the wiki mapping (milestone 6).
///
/// **Opened modally, and not while a capture runs**, for the same reason as the ledger window and one more: the font
/// is read by the capture in progress, so changing it halfway through would read half a frame in each font. The main
/// window disables the button during a capture; being modal stops a capture starting while this is open.
///
/// **The mapping is shown, not edited** (user, 2026-10-05). It is the built-in one and the only one — see
/// <see cref="WikiMapping"/> for why there is no file to edit.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppServices _services;

    /// <summary>The fonts in the order offered: the one the user plays in first.</summary>
    private static readonly UiFont[] Fonts = [UiFont.EqlWikiAssistant, UiFont.Arial];

    /// <summary>True while the controls are being filled in, so selecting the saved font does not save it again.</summary>
    private bool _loading = true;

    /// <summary>True while the next key combination pressed in this window becomes the hotkey.</summary>
    private bool _recording;

    public SettingsWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();
        DarkTitleBar.Apply(this);
        _services = services;

        SettingsPathText.Text = AppPaths.SettingsFile;

        FontBox.ItemsSource = Fonts.Select(UiFonts.DisplayName);
        FontBox.SelectedIndex = Array.IndexOf(Fonts, services.Settings.Font);
        DescribeFont(services.Settings.Font);

        ResetHotKeyButton.Content = $"Reset to {HotKeyChord.Default}";
        ShowHotKey();
        if (services.HotKeyProblem is { } problem) ShowHotKeyStatus(problem, Palette.Attention);

        KeepCapturesBox.IsChecked = services.Settings.KeepCaptures;
        CapturesFolderText.Text = CaptureArchive.Directory;

        IconCacheFolderText.Text = AppPaths.IconCacheDirectory;
        ShowIconCache();

        RefreshStoredLogin();
        ShowMapping(services.Mapping);

        PageList.SelectedIndex = 0;
        _loading = false;
    }

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before the pages exist as fields.
        if (FontPage is null) return;

        if (_recording) StopRecording();

        ScrollViewer[] pages = [FontPage, HotKeyPage, CapturesPage, IconCachePage, AccountPage, MappingPage];
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = PageList.SelectedIndex == i ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================ UI font ============================

    private async void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontBox.SelectedIndex < 0) return;

        UiFont font = Fonts[FontBox.SelectedIndex];
        DescribeFont(font);
        FontSavedText.Text = "";

        try
        {
            await _services.UseFontAsync(font);
            FontSavedText.Foreground = Palette.Done;
            FontSavedText.Text = "Saved. Applies from the next capture.";
        }
        catch (IOException ex)
        {
            // The font is already in use for this session; only remembering it failed.
            FontSavedText.Foreground = Palette.Attention;
            FontSavedText.Text = $"In use now, but could not be saved: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            FontSavedText.Foreground = Palette.Attention;
            FontSavedText.Text = $"In use now, but could not be saved: {ex.Message}";
        }
    }

    private void DescribeFont(UiFont font) => FontDescription.Text = font switch
    {
        UiFont.EqlWikiAssistant =>
            "A modified Arial with a serifed capital I and an r one pixel wider. Every bare stroke is a lowercase l, " +
            "so nothing is guessed.",
        _ =>
            "The game's default. Its capital I and lowercase l are the same pixels, so the tool guesses from the word: " +
            "a stroke that starts a word is read as I. That is right for item names and wrong in lore, where \"lost\" " +
            "reads \"Iost\" — a known limitation of this font.",
    };

    // ============================ Saved captures ============================

    private async void OnKeepCapturesChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool keep = KeepCapturesBox.IsChecked == true;
        KeepCapturesSavedText.Text = "";
        try
        {
            await _services.UseKeepCapturesAsync(keep);
            KeepCapturesSavedText.Foreground = Palette.Done;
            KeepCapturesSavedText.Text = keep ? "Saved. Applies from the next capture." : "Saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Already in effect for this session; only remembering it failed.
            KeepCapturesSavedText.Foreground = Palette.Attention;
            KeepCapturesSavedText.Text = $"In use now, but could not be saved: {ex.Message}";
        }
    }

    private void OnOpenCapturesFolderClick(object sender, RoutedEventArgs e)
    {
        string folder = Directory.Exists(CaptureArchive.Directory) ? CaptureArchive.Directory : AppPaths.Root;
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{folder}\"", UseShellExecute = true });
    }

    // ============================ Icon cache ============================

    private void ShowIconCache()
    {
        IconCacheContents contents = _services.Icons.Describe();
        string icons = contents.Icons == 1 ? "1 icon" : $"{contents.Icons:N0} icons";
        IconCacheSummary.Text = contents.Missing == 0
            ? $"{icons} cached ({FormatSize(contents.Bytes)})."
            : $"{icons} cached ({FormatSize(contents.Bytes)}), and {contents.Missing:N0} remembered as not on the " +
              $"wiki. Those are asked about again after {IconCache.MissingIconLifetime.TotalHours:N0} hours, in case " +
              "somebody has uploaded one.";
        ClearIconCacheButton.IsEnabled = contents.Icons + contents.Missing > 0;
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} bytes" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:N0} KB" : $"{bytes / 1048576.0:N1} MB";

    private void OnIconIdKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnRedownloadIconClick(sender, e);
    }

    private async void OnRedownloadIconClick(object sender, RoutedEventArgs e)
    {
        string iconId = IconIdBox.Text.Trim();
        RedownloadedIconFrame.Visibility = Visibility.Collapsed;
        if (iconId.Length == 0 || !iconId.All(char.IsAsciiDigit))
        {
            ShowRedownload("A lucy_img_ID is a number, such as 584.", Palette.Attention);
            return;
        }

        string file = IconLibraryFolder.WikiFileNameFor(iconId);
        RedownloadIconButton.IsEnabled = false;
        ShowRedownload($"Downloading {file}…", Palette.Muted);
        try
        {
            byte[]? bytes = await _services.Icons.RedownloadAsync(iconId);
            if (bytes is null)
            {
                ShowRedownload($"The wiki has no {file}. Nothing is cached for it.", Palette.Attention);
            }
            else
            {
                RedownloadedIcon.Source = Decode(bytes);
                RedownloadedIconFrame.Visibility = RedownloadedIcon.Source is null ? Visibility.Collapsed : Visibility.Visible;
                ShowRedownload($"Downloaded {file} ({FormatSize(bytes.Length)}). Captures use this copy from now on.",
                    Palette.Done);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Nothing is lost: the old copy is gone, and the next capture that needs the icon fetches it again.
            ShowRedownload($"Could not download {file}: {ex.Message} The next capture that needs it will try again.",
                Palette.Attention);
        }
        finally
        {
            RedownloadIconButton.IsEnabled = true;
            ShowIconCache();
        }
    }

    private void ShowRedownload(string text, Brush brush)
    {
        RedownloadStatus.Text = text;
        RedownloadStatus.Foreground = brush;
        RedownloadResult.Visibility = Visibility.Visible;
    }

    /// <summary>The downloaded file as an image, or null if it is not one WPF can read — the bytes are the wiki's.</summary>
    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private void OnClearIconCacheClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IconCacheContents before = _services.Icons.Describe();
            _services.Icons.Clear();
            ClearIconCacheStatus.Foreground = Palette.Done;
            ClearIconCacheStatus.Text = before.Icons == 1 ? "Cleared 1 icon." : $"Cleared {before.Icons:N0} icons.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClearIconCacheStatus.Foreground = Palette.Attention;
            ClearIconCacheStatus.Text = $"Could not clear it all: {ex.Message}";
        }
        ShowIconCache();
    }

    private void OnOpenIconCacheFolderClick(object sender, RoutedEventArgs e)
    {
        string folder = Directory.Exists(AppPaths.IconCacheDirectory) ? AppPaths.IconCacheDirectory : AppPaths.Root;
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{folder}\"", UseShellExecute = true });
    }

    private void OnOpenSettingsFolderClick(object sender, RoutedEventArgs e)
    {
        string path = AppPaths.SettingsFile;
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{AppPaths.Root}\"",
            UseShellExecute = true,
        });
    }

    // ============================ Capture hotkey ============================

    private void ShowHotKey()
    {
        HotKeyText.Foreground = _services.HotKeyProblem is null ? Palette.Text : Palette.Attention;
        HotKeyText.Text = _services.HotKeyProblem is null
            ? _services.Settings.HotKey.ToString()
            : $"{_services.Settings.HotKey} (not active)";
        ResetHotKeyButton.IsEnabled = _services.Settings.HotKey != HotKeyChord.Default || _services.HotKeyProblem is not null;
    }

    private void ShowHotKeyStatus(string text, Brush brush)
    {
        HotKeyStatus.Foreground = brush;
        HotKeyStatus.Text = text;
    }

    private void OnChangeHotKeyClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording();
            ShowHotKeyStatus("", Palette.Muted);
            return;
        }

        // Released while recording, or the current combination could never be recorded: Windows hands a registered
        // combination to its hotkey and never to a window. Every way out of recording puts it back.
        _services.SuspendHotKey();
        _recording = true;
        // Esc cancels the recording rather than closing the window, so the Close button gives up its claim on it.
        CloseButton.IsCancel = false;
        ChangeHotKeyButton.Content = "Cancel";
        HotKeyBox.BorderBrush = (Brush)FindResource("AccentBrush");
        HotKeyText.Foreground = Palette.Muted;
        HotKeyText.Text = "Press the new combination…";
        ShowHotKeyStatus(
            "Hold Ctrl, Alt or Win and press a key. Esc cancels. Windows keeps some Win combinations for itself: if a " +
            "key does nothing, Windows has it, and no program can use it.", Palette.Muted);
        Keyboard.Focus(HotKeyBox);
    }

    /// <summary>Ends recording. <paramref name="resume"/> is false only when a combination is about to be applied,
    /// which re-registers a hotkey itself — and the caller resumes afterwards in case it was refused.</summary>
    private void StopRecording(bool resume = true)
    {
        _recording = false;
        CloseButton.IsCancel = true;
        ChangeHotKeyButton.Content = "Change…";
        HotKeyBox.BorderBrush = (Brush)FindResource("BorderStrongBrush");
        if (resume) _services.ResumeHotKey();
        ShowHotKey();
    }

    /// <summary>However the window closes, a hotkey suspended for recording comes back.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _services.ResumeHotKey();
        base.OnClosed(e);
    }

    /// <summary>
    /// While recording, every key goes to the recording and nowhere else — not to focus navigation, not to the Close
    /// button, not to the window's menu on Alt. Held modifiers are shown as they go down; the first other key
    /// completes the combination.
    /// </summary>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        // Alt combinations arrive as Key.System, with the real key alongside.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        HotKeyModifiers modifiers = ModifiersHeld();

        if (key == Key.Escape && modifiers == HotKeyModifiers.None)
        {
            StopRecording();
            ShowHotKeyStatus("", Palette.Muted);
            return;
        }

        var chord = new HotKeyChord(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (HotKeyChord.IsModifierKey(chord.VirtualKey))
        {
            HotKeyText.Text = $"{chord}+…";
            return;
        }

        if (chord.Problem is { } problem)
        {
            StopRecording();
            ShowHotKeyStatus($"{chord} was not used. {problem}", Palette.Attention);
            return;
        }

        StopRecording(resume: false);
        await ApplyHotKeyAsync(chord);
    }

    private async void OnResetHotKeyClick(object sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();
        await ApplyHotKeyAsync(HotKeyChord.Default);
    }

    private async Task ApplyHotKeyAsync(HotKeyChord chord)
    {
        bool unchanged = chord == _services.Settings.HotKey;
        try
        {
            string? problem = await _services.UseHotKeyAsync(chord);
            // A refused combination leaves the recording's suspension in place; this puts the old hotkey back.
            _services.ResumeHotKey();
            ShowHotKey();
            if (problem is not null)
                ShowHotKeyStatus($"{problem} {_services.Settings.HotKey} is still the hotkey.", Palette.Attention);
            else if (unchanged)
                ShowHotKeyStatus($"{chord} is still the hotkey.", Palette.Done);
            else
                ShowHotKeyStatus($"Saved. {chord} now captures the game window.", Palette.Done);
        }
        catch (IOException ex)
        {
            // The hotkey is already live for this session; only remembering it failed.
            ShowHotKey();
            ShowHotKeyStatus($"In use now, but could not be saved: {ex.Message}", Palette.Attention);
        }
    }

    /// <summary>
    /// The modifiers physically held right now, asked of Windows rather than of WPF (bug found by the user,
    /// 2026-10-07: Win+Ctrl+R recorded as Ctrl+R). WPF's <c>Keyboard.Modifiers</c> only knows keys that reached this
    /// window, and the shell keeps the Win key's own press for itself — so WPF never saw Win go down.
    /// <c>GetAsyncKeyState</c> reports the keyboard as it is, wherever its keys were delivered.
    /// </summary>
    private static HotKeyModifiers ModifiersHeld()
    {
        HotKeyModifiers modifiers = HotKeyModifiers.None;
        if (IsDown(0x11)) modifiers |= HotKeyModifiers.Control; // VK_CONTROL
        if (IsDown(0x12)) modifiers |= HotKeyModifiers.Alt; // VK_MENU
        if (IsDown(0x10)) modifiers |= HotKeyModifiers.Shift; // VK_SHIFT
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= HotKeyModifiers.Windows; // VK_LWIN, VK_RWIN
        return modifiers;

        static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    // ============================ Wiki account ============================

    private void RefreshStoredLogin()
    {
        BotCredentials? stored = _services.Credentials.Read();

        StoredText.Text = stored is null
            ? "No bot password is stored."
            : $"{stored.UserName} — edits are made as {stored.AccountName}.";
        SessionText.Text = _services.IsLoggedIn
            ? "This session is logged in."
            : "This session has not logged in yet. It does so at the first save.";

        CheckStoredButton.IsEnabled = stored is not null;
        RemoveStoredButton.IsEnabled = stored is not null;

        if (stored is not null && UserNameBox.Text.Length == 0) UserNameBox.Text = stored.UserName;
    }

    private async void OnCheckStoredClick(object sender, RoutedEventArgs e)
    {
        if (_services.Credentials.Read() is not { } stored) return;

        SetAccountBusy(true, "Checking the stored login…");
        try
        {
            CredentialCheck check = await AppServices.CheckCredentialAsync(stored);
            ShowCheck(check, check.MayBeStored ? "The stored login works." : $"The stored login does not work. {check.Refusal}");
        }
        finally
        {
            SetAccountBusy(false);
        }
    }

    private async void OnSaveCredentialClick(object sender, RoutedEventArgs e)
    {
        string user = UserNameBox.Text.Trim();
        string password = PasswordInput.Password;

        // A bot password logs in as User@BotName. Without the @ the wiki would treat it as the account's main
        // password, which this tool must never hold — and which the wiki refuses through this API anyway.
        if (!user.Contains('@'))
        {
            ShowProblem("Enter the full bot user name, in the User@BotName form shown on Special:BotPasswords.");
            return;
        }

        if (password.Length == 0)
        {
            ShowProblem("Enter the bot password.");
            return;
        }

        SetAccountBusy(true, "Checking with the wiki…");
        try
        {
            CredentialCheck check = await _services.SaveCredentialAsync(new BotCredentials(user, password));
            if (check.MayBeStored) PasswordInput.Clear();
            ShowCheck(check, check.MayBeStored ? "Saved." : $"Not saved. {check.Refusal}");
            RefreshStoredLogin();
        }
        finally
        {
            SetAccountBusy(false);
        }
    }

    private void OnRemoveStoredClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            "Remove the stored bot password from Windows Credential Manager? Checking items still works; saving an " +
            "edit will need one entered again.",
            "Remove stored login",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        _services.ForgetCredential();
        CheckPanel.Visibility = Visibility.Collapsed;
        RefreshStoredLogin();
    }

    private void OnBotPasswordsClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://eqlwiki.com/Special:BotPasswords") { UseShellExecute = true });

    private void SetAccountBusy(bool busy, string? message = null)
    {
        SaveCredentialButton.IsEnabled = !busy;
        bool stored = _services.Credentials.Read() is not null;
        CheckStoredButton.IsEnabled = !busy && stored;
        RemoveStoredButton.IsEnabled = !busy && stored;

        if (!busy) return;
        CheckPanel.Visibility = Visibility.Visible;
        CheckHeadline.Foreground = Palette.Muted;
        CheckHeadline.Text = message ?? "";
        RightsList.ItemsSource = null;
    }

    private void ShowProblem(string message)
    {
        CheckPanel.Visibility = Visibility.Visible;
        CheckHeadline.Foreground = Palette.Attention;
        CheckHeadline.Text = message;
        RightsList.ItemsSource = null;
    }

    /// <summary>What the wiki said, one right per line. Editing is required, so its absence is red; creating and
    /// uploading are needed only for new items and icons, so theirs is amber.</summary>
    private void ShowCheck(CredentialCheck check, string headline)
    {
        CheckPanel.Visibility = Visibility.Visible;
        CheckHeadline.Foreground = check.MayBeStored ? Palette.Done : Palette.Attention;
        CheckHeadline.Text = headline;

        if (check.Info is not { } info)
        {
            RightsList.ItemsSource = null;
            return;
        }

        RightsList.ItemsSource = new[]
        {
            info.IsAnonymous
                ? new RightRow("✗", "The wiki sees an anonymous session — the login did not stick.", Palette.Attention)
                : new RightRow("✓", $"The wiki sees {info.Name}.", Palette.Done),
            Right(info.CanEdit, "Edit existing pages", "required", Palette.Attention),
            Right(info.CanCreate, "Create new pages", "needed to create a page for a new item", Palette.Warning),
            Right(info.CanUpload, "Upload new files", "needed to upload an item's icon", Palette.Warning),
        };
    }

    private static RightRow Right(bool granted, string name, string why, Brush missing) => granted
        ? new RightRow("✓", name, Palette.Done)
        : new RightRow("✗", $"{name} — not granted; {why}. Add it at Special:BotPasswords.", missing);

    // ============================ Wiki mapping ============================

    private void ShowMapping(WikiMapping mapping)
    {
        MappingIntro.Text =
            $"The built-in mapping, version {mapping.Version}: how what an item window says becomes what a wiki page " +
            "writes. Read-only in this version. A stat the game shows that is not listed here is reported on the " +
            "review screen as possibly new, never dropped.";

        StatsTable.ItemsSource = StatRows(mapping);
        LineOrderList.ItemsSource = mapping.StatsBlockLineOrder.Select(line =>
            line.Count == 1 && line[0] == WikiMapping.BlankLine ? "(a blank line)" : string.Join("  ", line));
        EffectsTable.ItemsSource = EffectRows(mapping);
        SlotsTable.ItemsSource = SlotRows(mapping);
        CategoriesTable.ItemsSource = CategoryRows();
        TemplateTable.ItemsSource = TemplateRows(mapping);
    }

    /// <summary>
    /// Every stat, in the order the statsblock writes them, with the not-stored ones last.
    ///
    /// **The example in the note is produced by the mapping itself** (<see cref="StatMapping.ToWikiValue"/>) rather
    /// than described here, so this view cannot drift from what the tool actually writes.
    /// </summary>
    private static IEnumerable<MappingRow> StatRows(WikiMapping mapping)
    {
        List<string> order = [.. mapping.StatsBlockLineOrder.SelectMany(line => line)];
        int Position(StatMapping stat) =>
            stat.WikiLabel is { } label && order.IndexOf(label) is >= 0 and var index ? index : int.MaxValue;

        foreach (StatMapping stat in mapping.Stats.Values
                     .OrderBy(s => s.Disposition)
                     .ThenBy(Position)
                     .ThenBy(s => s.GameLabel, StringComparer.Ordinal))
        {
            if (stat.Disposition == StatDisposition.NotStored)
            {
                yield return new MappingRow(stat.GameLabel, "not stored", stat.Note, Palette.Dim);
                continue;
            }

            string? sample = stat.Format == StatValueFormat.SkillModifier ? "Fishing 5 % (10 Max)"
                : stat.WikiSuffix is not null ? "100"
                : stat.Signed ? "5"
                : null;
            string? note = sample is null ? null : $"{sample}  →  {stat.ToWikiValue(sample)}";
            yield return new MappingRow(stat.GameLabel, stat.WikiLabel ?? "", note, Palette.Text);
        }

        foreach (string label in mapping.UnmappedGameLabels)
            yield return new MappingRow(label, "no wiki home yet", "Reported on every capture until one is agreed.", Palette.Attention);
    }

    private static IEnumerable<MappingRow> EffectRows(WikiMapping mapping)
    {
        foreach (string kind in mapping.FocusEffectKinds)
            yield return new MappingRow($"{kind} Effect", $"|{mapping.FocusEffectParameter}=",
                "Its own template parameter: the name only, no link.", Palette.Text);

        foreach ((string game, string wiki) in mapping.EffectKinds)
            yield return new MappingRow($"{game} Effect", $"Effect: [[…]] ({wiki})",
                "A statsblock line, with a tooltip link and the conditions in the parentheses.", Palette.Text);
    }

    private static IEnumerable<MappingRow> SlotRows(WikiMapping mapping)
    {
        foreach ((string game, string wiki) in mapping.Slots)
            yield return new MappingRow(game, wiki, null, Palette.Text);

        yield return new MappingRow("Primary", mapping.ToWikiSlot("Primary"),
            "Every slot not listed above is written in capitals.", Palette.Text);
    }

    private static IEnumerable<MappingRow> CategoryRows()
    {
        yield return new MappingRow($"Class: {CategoryRules.AllClasses}", "every class category", null, Palette.Dim);
        yield return new MappingRow($"Class: {CategoryRules.NoClasses}", "none", null, Palette.Dim);

        foreach ((string code, string category) in CategoryRules.ClassCategories.OrderBy(c => c.Value, StringComparer.Ordinal))
            yield return new MappingRow($"Class: {code}", category, null, Palette.Text);

        foreach ((string slot, string category) in CategoryRules.SlotCategoryNames)
            yield return new MappingRow($"Slot: {slot}", category, null, Palette.Text);

        foreach ((string label, string category) in CategoryRules.FieldCategories)
            yield return new MappingRow($"{label}: …", category, "When the item has this field.", Palette.Text);
    }

    private static IEnumerable<MappingRow> TemplateRows(WikiMapping mapping)
    {
        yield return new MappingRow("template", $"{{{{{mapping.TemplateName}}}}}", null, Palette.Text);
        yield return new MappingRow("era banner", $"{{{{{mapping.CurrentEra} Era}}}}",
            "Set on every page a capture touches: a capture proves the item is in the game.", Palette.Text);
        yield return new MappingRow("parameter order", string.Join(", ", mapping.ParameterOrder),
            "The order the formatting pass lays parameters out in, and where a missing one is inserted.", Palette.Text);
        yield return new MappingRow("block parameters", string.Join(", ", mapping.BlockParameters),
            "Laid out as blocks, framed by blank lines.", Palette.Text);
        yield return new MappingRow("= column", $"{mapping.ParameterAlignmentWidth} characters",
            "The width of the longest parameter name the blueprint declares.", Palette.Text);
    }
}

/// <summary>One row of a mapping table: what the game says, what the wiki writes, and why when it is not obvious.</summary>
internal sealed record MappingRow(string Game, string Wiki, string? Note, Brush WikiBrush);

/// <summary>One line of what the wiki said a bot password may do.</summary>
internal sealed record RightRow(string Mark, string Text, Brush Brush);
