using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Pipeline;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.App;

/// <summary>
/// The UI font, the wiki login, and a read-only view of the wiki mapping (milestone 6).
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

        RefreshStoredLogin();
        ShowMapping(services.Mapping);

        PageList.SelectedIndex = 0;
        _loading = false;
    }

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before the pages exist as fields.
        if (FontPage is null) return;

        FontPage.Visibility = PageList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountPage.Visibility = PageList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        MappingPage.Visibility = PageList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
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
            "Your own modification of Arial: the capital I has serifs and the r is a pixel wider. Every bare stroke " +
            "is a lowercase l, so nothing is guessed.",
        _ =>
            "The game's default. Its capital I and lowercase l are the same pixels, so the tool guesses from the word: " +
            "a stroke that starts a word is read as I. That is right for item names and wrong in lore, where \"lost\" " +
            "reads \"Iost\" — a known limitation of this font.",
    };

    private void OnOpenSettingsFolderClick(object sender, RoutedEventArgs e)
    {
        string path = AppPaths.SettingsFile;
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{AppPaths.Root}\"",
            UseShellExecute = true,
        });
    }

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
