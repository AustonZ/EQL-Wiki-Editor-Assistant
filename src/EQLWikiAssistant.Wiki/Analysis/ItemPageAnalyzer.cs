using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Wiki.Analysis;

/// <summary>The whole comparison of one capture against one page.</summary>
public sealed record ItemPageAnalysis(
    string PageTitle,
    IReadOnlyList<FieldFinding> Findings)
{
    public IEnumerable<FieldFinding> Changes => Findings.Where(f => f.IsChange);
    public IEnumerable<FieldFinding> Blockers => Findings.Where(f => f.Blocks);

    /// <summary>True when the page already agrees with the capture and nothing needs a human — the "matched"
    /// outcome the ledger records.</summary>
    public bool IsClean => !Findings.Any(f => f.IsChange || f.Blocks);

    public FieldFinding? Find(string field) =>
        Findings.FirstOrDefault(f => string.Equals(f.Field, field, StringComparison.Ordinal));
}

/// <summary>
/// Compares a captured item against its wiki page, field by field, and says what it concluded — without changing
/// anything. Rendering the actual edit is a separate step, so that every judgement here is reviewable on its own.
///
/// **The rules that are easy to get wrong, all confirmed by the user or by measurement:**
/// - **A field absent from the page is only a problem if the capture has data for it.** Don't demand a full
///   parameter set; absence matters only when it would hide something being added.
/// - **`No Trade` makes merchant value unverifiable, so the wiki's value is preserved.** Measured across the
///   verified corpus: all 63 `No Trade` windows show no merchant-value row and all 12 `Attunable` windows show a
///   real one. The absence is a property of tradeability, not evidence the item is worthless — and an attuned item
///   can never be re-captured with its price visible, so overwriting destroys the figure permanently.
/// - **`absolutely nothing` is a verified value and gets written**; no row at all is a gap and does not. The two
///   never co-occur in the corpus, so they stay distinguishable.
/// - **The wiki's `Attunable` beats a captured `No Trade`** (an item natively `Attunable` shows `No Trade` once
///   attuned, so the page's author saw something the capture cannot), but the user is told.
/// - **Legacy flags are discarded, not translated**, and the captured flag set is authoritative and open-ended — a
///   flag the tool has never seen is ordinary data, because the devs keep adding them.
/// - **Food/book prose on the flags line (`This is a meal!`) is raised, never acted on.** EQL dropped those strings
///   but they may still mean something under the hood, and the user preserves them in `notes` by hand.
/// </summary>
public static class ItemPageAnalyzer
{
    /// <summary>Field keys used in <see cref="FieldFinding.Field"/> for the non-stat fields.</summary>
    public const string ItemNameField = "itemname";
    public const string PageTitleField = "page title";
    public const string MerchantValueField = "merchant_value";
    public const string FlagsField = "flags";
    public const string LegacyFlagsField = "flags (legacy)";
    public const string FlagProseField = "flags (descriptive text)";
    public const string ClassesField = "Class";
    public const string RacesField = "Race";
    public const string SlotsField = "Slot";

    /// <summary>Flag strings the game no longer shows, which are food/book descriptions rather than flags. Raised for
    /// the user to move into `notes`; never moved automatically and never silently dropped.</summary>
    private static readonly string[] DescriptiveFlagMarkers =
        ["This is a meal", "This is a drink", "This is a snack", "This is a banquet", "The Book is", "The Note is"];

    public static ItemPageAnalysis Analyze(
        ParsedItem captured,
        ItemPageDocument page,
        string pageTitle,
        WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(pageTitle);
        mapping ??= WikiMapping.Default;

        var findings = new List<FieldFinding>();
        StatsBlock? block = page.ReadStatsBlock();

        AddNameFindings(findings, captured, page, pageTitle);
        AddMerchantValueFinding(findings, captured, page);
        AddFlagFindings(findings, captured, block);
        AddListFinding(findings, ClassesField, captured.Classes, block);
        AddListFinding(findings, RacesField, captured.Races, block);
        // Slots go through the mapping: the wiki writes them in caps, and its `FINGER` is the game's `Fingers`.
        AddListFinding(findings, SlotsField, [.. captured.Slots.Select(mapping.ToWikiSlot)], block);
        AddStatFindings(findings, captured, block, mapping);
        AddEffectFindings(findings, captured, page, block, mapping);
        NormalizeSignsIfTheEditWouldBeInconsistent(findings);

        return new ItemPageAnalysis(pageTitle, findings);
    }

    private static void AddNameFindings(
        List<FieldFinding> findings, ParsedItem captured, ItemPageDocument page, string pageTitle)
    {
        // The template needs itemname to equal the page's own title, and any mismatch visibly breaks the page — see
        // PageTitle.IsDefect for the verified mechanism. The fix is not obvious, so this only ever reports.
        TitleMatch match = PageTitle.Compare(page.ItemName, pageTitle);
        if (PageTitle.IsDefect(match))
            findings.Add(new FieldFinding(
                PageTitleField, FieldVerdict.NeedsReview, pageTitle, page.ItemName,
                match == TitleMatch.DisambiguatedTitle
                    ? $"The page is titled '{pageTitle}' but its itemname is '{page.ItemName}'. That breaks the " +
                      "item box, which links to a page named after itemname. It looks like one in-game item split " +
                      "across several pages, which the template cannot currently express — a human has to decide."
                    : $"The page is titled '{pageTitle}' but its itemname is '{page.ItemName}'. That breaks the " +
                      "item box, which links to a page named after itemname."));

        if (string.Equals(page.ItemName, captured.Name, StringComparison.Ordinal)) return;

        // The captured name is authoritative on the characters themselves (glyph matching keeps ' and ` distinct),
        // but a rename is a page move the user cannot perform, so this is never an automatic edit.
        findings.Add(new FieldFinding(
            ItemNameField, FieldVerdict.NeedsReview, captured.Name, page.ItemName,
            $"The game calls this '{captured.Name}'; the page's itemname is '{page.ItemName}'. Changing it means " +
            "the page title has to change too, which is a move rather than an edit."));
    }

    private static void AddMerchantValueFinding(List<FieldFinding> findings, ParsedItem captured, ItemPageDocument page)
    {
        string? onWiki = page.MerchantValue;
        bool untradeable = captured.Flags.Any(f => string.Equals(f, "No Trade", StringComparison.OrdinalIgnoreCase));

        if (captured.MerchantValue is null)
        {
            if (untradeable && !string.IsNullOrWhiteSpace(onWiki))
            {
                findings.Add(new FieldFinding(
                    MerchantValueField, FieldVerdict.Unverifiable, null, onWiki,
                    "This capture is No Trade, so the window shows no price — the item is untradeable, not " +
                    "worthless. The wiki's value is being kept, and cannot be re-verified from an attuned copy."));
                return;
            }

            // No captured value and nothing on the wiki either: nothing to say.
            if (!string.IsNullOrWhiteSpace(onWiki))
                findings.Add(new FieldFinding(
                    MerchantValueField, FieldVerdict.Unverifiable, null, onWiki,
                    "The window showed no merchant value, so the wiki's value is being kept."));
            return;
        }

        // A captured value is authoritative: EQL's window states the maximum price directly, independent of
        // Charisma and faction, so a legacy figure annotated "with 111 Charisma" is often simply wrong.
        if (!MerchantValue.TryParseGameText(captured.MerchantValue, out MerchantValue value))
        {
            findings.Add(new FieldFinding(
                MerchantValueField, FieldVerdict.NeedsReview, captured.MerchantValue, onWiki,
                $"'{captured.MerchantValue}' is not a merchant value this tool recognizes, so it was not used."));
            return;
        }

        string wanted = value.ToWikiText();
        if (string.IsNullOrWhiteSpace(onWiki))
            findings.Add(new FieldFinding(MerchantValueField, FieldVerdict.MissingOnWiki, wanted, null));
        else if (string.Equals(onWiki, wanted, StringComparison.Ordinal))
            findings.Add(new FieldFinding(MerchantValueField, FieldVerdict.Matches, wanted, onWiki));
        else
            findings.Add(new FieldFinding(
                MerchantValueField, FieldVerdict.Differs, wanted, onWiki,
                "The window states the maximum price directly, so it replaces whatever the page held — including " +
                "legacy figures recorded at a particular Charisma, which are usually below maximum."));
    }

    private static void AddFlagFindings(List<FieldFinding> findings, ParsedItem captured, StatsBlock? block)
    {
        IReadOnlyList<string> onWiki = block is null ? [] : ReadFlagLine(block);
        string[] legacy = [.. onWiki.Where(IsLegacyFlag)];
        string[] prose = [.. onWiki.Where(f => DescriptiveFlagMarkers.Any(m => f.StartsWith(m, StringComparison.OrdinalIgnoreCase)))];

        if (prose.Length > 0)
            findings.Add(new FieldFinding(
                FlagProseField, FieldVerdict.NeedsReview, null, string.Join(", ", prose),
                "EQL no longer shows this text, but it may still mean something the UI stopped exposing — in " +
                "original EverQuest 'This is a hearty meal!' meant the food lasted longer. Move it into notes by " +
                "hand if you want to keep it; this tool will not move or discard it for you."));

        if (legacy.Length > 0)
            findings.Add(new FieldFinding(
                LegacyFlagsField, FieldVerdict.Differs, null, string.Join(", ", legacy),
                "These are pre-EQL flags with no current equivalent, so they are dropped rather than translated. " +
                "'LORE ITEM' (carry one) is not the same property as 'Lore Equipped' (equip one), and 'MAGIC ITEM' " +
                "has no counterpart at all."));

        // The captured set is authoritative and open-ended: the devs keep adding flags (No Pet, Heirloom, Free
        // Storage), so an unfamiliar one is ordinary data, never a warning.
        var wanted = new List<string>(captured.Flags);

        // An item natively Attunable shows No Trade once attuned, so the page's author saw a state this capture
        // cannot. Keep theirs — but say so, because the user may later want this pair treated as simply matching.
        bool wikiSaysAttunable = onWiki.Any(f => string.Equals(f, "Attunable", StringComparison.OrdinalIgnoreCase));
        bool capturedSaysNoTrade = wanted.Any(f => string.Equals(f, "No Trade", StringComparison.OrdinalIgnoreCase));
        if (wikiSaysAttunable && capturedSaysNoTrade)
        {
            wanted[wanted.FindIndex(f => string.Equals(f, "No Trade", StringComparison.OrdinalIgnoreCase))] = "Attunable";
            findings.Add(new FieldFinding(
                FlagsField, FieldVerdict.Unverifiable, string.Join(", ", captured.Flags), string.Join(", ", onWiki),
                "The page says Attunable and this copy says No Trade, which is what an Attunable item shows once " +
                "attuned. Keeping the page's Attunable, since it cannot be re-observed from this copy."));
            return;
        }

        string[] current = [.. onWiki.Where(f => !IsLegacyFlag(f) && !prose.Contains(f))];
        if (current.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
            findings.Add(new FieldFinding(FlagsField, FieldVerdict.Matches, string.Join(", ", wanted), string.Join(", ", current)));
        else if (current.Length == 0 && wanted.Count > 0)
            findings.Add(new FieldFinding(FlagsField, FieldVerdict.MissingOnWiki, string.Join(", ", wanted), null));
        else if (wanted.Count > 0 || current.Length > 0)
            findings.Add(new FieldFinding(FlagsField, FieldVerdict.Differs, string.Join(", ", wanted), string.Join(", ", current)));
    }

    /// <summary>A pre-EQL flag: the imported dialect is ALL CAPS, the current one is Title Case. Crude on purpose —
    /// the line is discarded either way, so this only has to decide what to *tell* the user, and it never has to
    /// split a legacy line into individual flags (which single-spaced pages make impossible anyway).</summary>
    private static bool IsLegacyFlag(string flag) =>
        flag.Any(char.IsLetter) && flag.Where(char.IsLetter).All(char.IsUpper);

    /// <summary>The flags line is the first line of the block carrying unlabelled tokens and no fields.</summary>
    private static IReadOnlyList<string> ReadFlagLine(StatsBlock block) =>
        block.Lines.FirstOrDefault(l => l.Kind == StatsLineKind.Flags)?.Flags ?? [];

    private static void AddListFinding(
        List<FieldFinding> findings, string wikiLabel, IReadOnlyList<string> capturedValues, StatsBlock? block)
    {
        string? onWiki = block?.Find(wikiLabel)?.Value;
        string wanted = string.Join(' ', capturedValues);

        if (capturedValues.Count == 0)
        {
            // Nothing captured. An absent wiki field is fine (the item has no slot, say); a present one is a value
            // the capture cannot speak to, so it stays.
            if (!string.IsNullOrWhiteSpace(onWiki))
                findings.Add(new FieldFinding(
                    wikiLabel, FieldVerdict.Unverifiable, null, onWiki,
                    $"The window showed no {wikiLabel} row, so the wiki's value is being kept."));
            return;
        }

        if (onWiki is null)
            findings.Add(new FieldFinding(wikiLabel, FieldVerdict.MissingOnWiki, wanted, null));
        else if (NormalizeList(onWiki) == NormalizeList(wanted))
            findings.Add(new FieldFinding(wikiLabel, FieldVerdict.Matches, wanted, onWiki));
        else
            findings.Add(new FieldFinding(wikiLabel, FieldVerdict.Differs, wanted, onWiki));
    }

    /// <summary>Order and spacing are not meaningful in a class or race list, so compare as a set.</summary>
    private static string NormalizeList(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.ToUpperInvariant())
            .OrderBy(v => v, StringComparer.Ordinal));

    private static void AddStatFindings(
        List<FieldFinding> findings, ParsedItem captured, StatsBlock? block, WikiMapping mapping)
    {
        foreach ((string gameLabel, string capturedValue) in captured.Stats)
        {
            StatMapping? statMapping = mapping.FindStat(gameLabel);

            if (statMapping is null)
            {
                // Never dropped. An unmapped stat is how a game patch announces itself, and discarding it would
                // lose real data with nobody the wiser.
                bool known = mapping.UnmappedGameLabels.Contains(gameLabel, StringComparer.OrdinalIgnoreCase);
                findings.Add(new FieldFinding(
                    gameLabel, FieldVerdict.NeedsReview, capturedValue, null,
                    known
                        ? $"The game shows '{gameLabel}' but the wiki has no agreed field for it, so this tool will " +
                          "not invent one. Decide where it belongs and add it to the mapping."
                        : $"'{gameLabel}' is a stat this tool has no mapping for at all — possibly new. It has been " +
                          "left alone rather than guessed at; add it to the mapping."));
                continue;
            }

            // Stats the wiki deliberately does not record (Ratio, Container) are ignored rather than reported;
            // the mapping carries a note saying why for each.
            if (statMapping.Disposition == StatDisposition.NotStored || statMapping.WikiLabel is null) continue;

            string wikiLabel = statMapping.WikiLabel;
            string? onWiki = block?.Find(wikiLabel)?.Value;
            string wanted = ToWikiValue(capturedValue, statMapping);

            bool signed = statMapping.Signed;

            if (onWiki is null)
                findings.Add(new FieldFinding(wikiLabel, FieldVerdict.MissingOnWiki, wanted, null, SignedStat: signed));
            else if (ValuesAgree(wanted, onWiki))
                findings.Add(new FieldFinding(wikiLabel, FieldVerdict.Matches, wanted, onWiki, SignedStat: signed));
            else if (IsOneAlternativeOf(wanted, onWiki))
                // A slash-separated value means somebody combined several items onto one page — the two real cases
                // are ammo pages carrying three arrow variants at once, with a parallel triple in their recipe line.
                // The user's call (2026-09-25) is that this is a genuine mismatch to surface, not something to hold
                // back: the right fix is usually splitting the page, which only a human can do.
                findings.Add(new FieldFinding(
                    wikiLabel, FieldVerdict.Differs, wanted, onWiki,
                    $"The page lists several values for {wikiLabel} where the window shows one. That usually means " +
                    "several items were combined into one page, which is worth splitting up rather than editing."));
            else
                findings.Add(new FieldFinding(wikiLabel, FieldVerdict.Differs, wanted, onWiki, SignedStat: signed));
        }
    }

    /// <summary>
    /// Whether a captured stat value and the wiki's agree.
    ///
    /// The wiki writes a stat bonus as `+8` and the game as `8` (or the reverse), and neither is more correct, so a
    /// leading `+` is ignored. Everything else is compared exactly: this is the comparison that decides whether a
    /// number on a public wiki gets overwritten, and a tolerant one would hide the very errors it exists to find.
    /// </summary>
    private static bool ValuesAgree(string captured, string onWiki) =>
        string.Equals(Trim(captured), Trim(onWiki), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Promotes sign-only matches to changes, but **only when the edit already touches another signed stat**
    /// (user, 2026-09-25).
    ///
    /// The reasoning is about what the page looks like afterwards. On its own, a page saying `STR: 5` is fine and
    /// rewriting it to `+5` is the incidental reformatting this tool must leave to the prettifier. But if the edit
    /// is already writing `WIS: +8` on that page, leaving `STR: 5` beside it produces a line the tool itself made
    /// inconsistent — so those get normalized too, and only then.
    ///
    /// Note this is a post-pass over the findings rather than a rule inside the comparison, because the answer
    /// depends on what *every other* field concluded. A per-field rule cannot see that.
    /// </summary>
    private static void NormalizeSignsIfTheEditWouldBeInconsistent(List<FieldFinding> findings)
    {
        bool editingASignedStat = findings.Any(f => f.IsChange && f.SignedStat);
        if (!editingASignedStat) return;

        for (int i = 0; i < findings.Count; i++)
        {
            FieldFinding finding = findings[i];
            if (!finding.SignedStat || finding.Verdict != FieldVerdict.Matches) continue;
            if (string.Equals(finding.Captured, finding.OnWiki, StringComparison.Ordinal)) continue;

            findings[i] = finding with
            {
                Verdict = FieldVerdict.Differs,
                Explanation = "Only the sign differs, which would normally be left to the prettifier — but this " +
                              "edit is already writing another signed stat on the page, so leaving this one " +
                              "unsigned would make the result inconsistent.",
            };
        }
    }

    /// <summary>
    /// Effects, which the wiki splits two ways: focus effects get their own template parameter
    /// (<c>focus_effect = Improved Vampirism III</c>) while everything else is an <c>Effect:</c> line in the
    /// statsblock.
    ///
    /// **A legacy link is a real correction, not reformatting.** The <c>&lt;span class='itemeff'&gt;</c> wrapper is
    /// what gives the effect a tooltip, which a bare <c>[[Name]]</c> does not (user, 2026-09-25), so a line naming
    /// the right effect in the old form still differs in a way worth fixing. That is the opposite call from the
    /// `STR: 5` versus `+5` case, and for a concrete reason: one changes what the page *does*, the other only how it
    /// looks.
    /// </summary>
    private static void AddEffectFindings(
        List<FieldFinding> findings, ParsedItem captured, ItemPageDocument page, StatsBlock? block, WikiMapping mapping)
    {
        // Existing Effect: lines, by the effect they name. A statsblock can carry several.
        var onWiki = new List<(string? Name, string Value)>();
        if (block is not null)
            foreach (StatsField field in block.AllFields()
                         .Where(f => string.Equals(f.Label, EffectLine.WikiLabel, StringComparison.OrdinalIgnoreCase)))
                onWiki.Add((EffectLine.TryReadName(field.Value), field.Value));

        foreach (EffectEntry effect in captured.Effects)
        {
            string field = $"{effect.Kind} Effect";

            if (mapping.IsFocusEffect(effect.Kind))
            {
                CompareFocusEffect(findings, field, effect, page);
                continue;
            }

            EffectRender render = EffectLine.Render(effect, mapping);
            (string? Name, string Value) existing = onWiki.FirstOrDefault(
                e => string.Equals(e.Name, effect.Name, StringComparison.OrdinalIgnoreCase));

            if (!render.IsComplete)
            {
                // Refuse rather than write a line that has quietly lost part of the effect.
                findings.Add(new FieldFinding(
                    field, FieldVerdict.NeedsReview, effect.Name, existing.Value,
                    $"This effect cannot be written in the wiki's convention yet: {string.Join(" ", render.Unsupported)}"));
                continue;
            }

            string wanted = render.Line!;

            if (existing.Value is null)
                findings.Add(new FieldFinding(field, FieldVerdict.MissingOnWiki, wanted, null));
            else if (string.Equals($"{EffectLine.WikiLabel}: {existing.Value}", wanted, StringComparison.Ordinal))
                findings.Add(new FieldFinding(field, FieldVerdict.Matches, wanted, existing.Value));
            else
                findings.Add(new FieldFinding(
                    field, FieldVerdict.Differs, wanted, existing.Value,
                    EffectLine.HasTooltipLink(existing.Value)
                        ? null
                        : "The existing link is the legacy [[Name]] form, which gets no tooltip. Rewriting it to " +
                          "the itemeff span form is a functional fix, not a style change."));
        }
    }

    private static void CompareFocusEffect(
        List<FieldFinding> findings, string field, EffectEntry effect, ItemPageDocument page)
    {
        string? onWiki = page.FocusEffect;

        if (string.IsNullOrWhiteSpace(onWiki))
            findings.Add(new FieldFinding(field, FieldVerdict.MissingOnWiki, effect.Name, null));
        else if (string.Equals(onWiki, effect.Name, StringComparison.Ordinal))
            findings.Add(new FieldFinding(field, FieldVerdict.Matches, effect.Name, onWiki));
        else
            findings.Add(new FieldFinding(field, FieldVerdict.Differs, effect.Name, onWiki));
    }

    /// <summary>
    /// A captured value written the way the wiki writes it: the unit suffix appended where the two sides differ
    /// (`Weight Red: 100` in-game is `Weight Reduction: 100%` here), and an explicit `+` on the fields the wiki
    /// signs (`STR: +5` where the game says `5`).
    ///
    /// **This is only the value the tool would write. It is deliberately not part of deciding whether two values
    /// match** — <see cref="ValuesAgree"/> ignores a leading `+`, so a page saying `STR: 5` counts as correct and
    /// generates no edit. A sign is cosmetic: `STR: 5` reads unambiguously, and rewriting it would be exactly the
    /// incidental reformatting this tool is not allowed to do (see the prettifier note in CLAUDE.md). The sign gets
    /// applied when the value is being written anyway, for some other reason.
    /// </summary>
    private static string ToWikiValue(string capturedValue, StatMapping mapping)
    {
        string value = capturedValue.Trim();

        if (mapping.WikiSuffix is { } suffix && !value.EndsWith(suffix, StringComparison.Ordinal))
            value += suffix;

        // Only a bare non-negative number gains a sign; a value already signed keeps its own, and something like
        // "SMALL" or "1H Slashing" is left alone.
        if (mapping.Signed && value.Length > 0 && char.IsAsciiDigit(value[0]))
            value = "+" + value;

        return value;
    }

    /// <summary>Whether the wiki holds a slash-separated list of which the captured value is one member.</summary>
    private static bool IsOneAlternativeOf(string captured, string onWiki) =>
        onWiki.Contains('/', StringComparison.Ordinal) &&
        onWiki.Split('/').Select(Trim).Contains(Trim(captured), StringComparer.OrdinalIgnoreCase);

    private static string Trim(string value) => value.Trim().TrimStart('+');
}
