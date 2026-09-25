using System.Text.RegularExpressions;
using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Core.Text;

namespace EQLWikiAssistant.Core.Items;

/// <summary>
/// Turns a clean (non-occluded) <c>LocatedWindow.Lines</c> list into a <see cref="ParsedItem"/>. Ground truth for
/// this grammar came from real <c>LocateSpike</c> dumps against several real windows (armor, a weapon, a
/// consumable, augmented and non-augmented, with and without a Lore tab) — see the plan's milestone 2 writeup.
///
/// Real item windows follow a consistent line order: title bar name -&gt; Description[/Lore] tab -&gt; content-area
/// name (repeated) -&gt; flags (comma-separated, unlabeled) -&gt; Class: -&gt; Race: -&gt; an optional slot row, one or
/// more slots space-separated, with no "Slot:" label in-game unlike the wiki's own statsblock convention
/// (e.g. "Ear", "Primary Secondary", "Range Ammo") -&gt; UI chrome (Merge/Place/Item/Item/
/// Tier.../upgrade text) -&gt; a two-column stat block -&gt; a "Modified" chrome row (with the name a third time) -&gt;
/// optional exaltation slot rows -&gt; optional effect rows -&gt; optional merchant value row. This parser processes
/// the header positionally (fixed order) and the body by pattern-matching each row, rather than assuming a fixed
/// schema for the body — real windows vary a lot there (weapons show Base Dmg/Delay/Skill/Ratio where armor shows
/// AC/resists; a plain consumable like Water Flask has none of that and skips straight to Value:).
///
/// **Important OCR quirk this depends on**: unlike most label:value pairs, the classic two-column stat block
/// (Size/Weight/AC/stats/resists/etc.) is recognized as *separate* OcrLine fragments for the label and its value
/// even though they render on the same row — apparently because the game draws the value in a visually distinct
/// box. Everything else (Class:, Race:, exaltation rows, effect rows) comes back as one self-contained
/// "Label: Value" fragment. <see cref="ParseStatRow"/> handles both shapes generically by pairing fragments
/// within a reconstructed row rather than assuming either shape specifically.
///
/// **Required occlusion safety net**: <see cref="ParsedItem.TitleContentNameMismatch"/> reconciles the title-bar
/// name against the content-area name (see <see cref="NamesReconcile"/>) — this is what closes the known,
/// documented gap in <c>WindowBoundsFinder</c>/<c>ItemWindowLocator</c> where a small enough partial title
/// occlusion passes geometric bounds-checking cleanly but leaves the title text itself truncated. A caller must
/// treat a set flag as "don't trust this capture," not as merely advisory — see the plan's milestone 2 writeup.
/// </summary>
public static class ItemParser
{
    private static readonly string[] ExaltationLabels =
        ["Ornamentation", "Focus Exaltation", "Click Exaltation", "Worn Exaltation", "Proc Exaltation"];
    private static readonly string[] EffectLabels =
        ["Focus Effect", "Click Effect", "Combat Effect", "Proc Effect", "Charge Effect",
         "Worn Effect", "Consumable Effect"];

    // Sub-lines that describe the effect immediately above them, not a standalone item stat — confirmed by real
    // captures (Bladestopper, Bloodmoon, Crystal Mask) to always sit directly between their own effect line and
    // the next row that isn't one of these, so "attach to the most recently seen effect" is reliable.
    private static readonly string[] EffectModifierLabels =
        ["Cast Time", "Cooldown", "Cooldown Group", "Required Level", "Charges"];

    public static ParsedItem Parse(IReadOnlyList<OcrLine> lines, ItemWindowTab activeTab = ItemWindowTab.Description)
    {
        var warnings = new List<string>();
        List<List<OcrLine>> rows = GroupIntoRows(lines).Where(HasAnyAlphanumeric).ToList();
        int i = 0;

        if (rows.Count == 0)
        {
            warnings.Add("No OCR lines to parse.");
            return new ParsedItem("", 0, TitleContentNameMismatch: true, [], [], [], [], [], [], [], null, null, warnings);
        }

        // --- Header: fixed order ---
        string titleRaw = JoinRow(rows[i++]);
        (string titleName, int titleLevel) = ExtractNameLevel(titleRaw);

        if (i < rows.Count && RowLooksLikeTabLabels(rows[i])) i++;
        else warnings.Add("Expected a Description/Lore tab row after the title; none found where expected.");

        if (activeTab == ItemWindowTab.Lore)
            return ParseLoreView(rows, i, titleName, titleLevel, warnings);

        string contentName = "";
        int level = 0;
        bool nameMismatch = true;
        if (i < rows.Count)
        {
            string contentRaw = JoinRow(rows[i++]);
            (contentName, level) = ExtractNameLevel(contentRaw);
            nameMismatch = !NamesReconcile(titleName, contentName);
            if (nameMismatch)
            {
                warnings.Add($"Title-bar name (\"{titleRaw}\") doesn't match content-area name (\"{contentRaw}\") " +
                    "— possible partial occlusion; treat this capture as untrustworthy.");
            }
        }
        else
        {
            warnings.Add("Expected a content-area item name row; none found.");
        }

        var flags = new List<string>();
        if (i < rows.Count && !RowStartsWithLabel(rows[i], "Class"))
            flags.AddRange(SplitList(JoinRow(rows[i++])));

        var classes = new List<string>();
        if (i < rows.Count && RowStartsWithLabel(rows[i], "Class"))
        {
            classes.AddRange(SplitList(ValueAfterLabel(JoinRow(rows[i++]), "Class"), ' '));
            while (i < rows.Count && LooksLikeCodeListContinuation(rows[i]))
                classes.AddRange(SplitList(JoinRow(rows[i++]), ' '));
        }
        else
        {
            warnings.Add("Expected a \"Class:\" row; none found where expected.");
        }

        var races = new List<string>();
        if (i < rows.Count && RowStartsWithLabel(rows[i], "Race"))
        {
            races.AddRange(SplitList(ValueAfterLabel(JoinRow(rows[i++]), "Race"), ' '));
            while (i < rows.Count && LooksLikeCodeListContinuation(rows[i]))
                races.AddRange(SplitList(JoinRow(rows[i++]), ' '));
        }
        else
        {
            warnings.Add("Expected a \"Race:\" row; none found where expected.");
        }

        // One unlabeled row, space-separated, listing every slot the item fits: "Ear", "Primary Secondary",
        // "Range Ammo", and rarer pairings like Secondary/Back or Chest/Waist. Empty for items with no slot.
        var slots = new List<string>();
        if (i < rows.Count && LooksLikeBareSlotRow(rows[i]))
            slots.AddRange(SplitList(JoinRow(rows[i++]), ' '));

        // --- Body: pattern-matched, any order/mix, since it varies a lot by item type ---
        var stats = new List<KeyValuePair<string, string>>();
        var exaltations = new List<ExaltationSlot>();
        var effectBuilders = new List<(string Kind, string Description, List<KeyValuePair<string, string>> Modifiers)>();
        string? merchantValue = null;

        for (; i < rows.Count; i++)
        {
            List<OcrLine> row = rows[i];
            string joined = JoinRow(row);

            if (IsChromeRow(row, joined)) continue;

            if (TryMatchRowLabel(row, ExaltationLabels, out string exLabel, out string exValue))
            {
                exaltations.Add(ParseExaltation(exLabel, exValue));
                continue;
            }

            if (TryMatchRowLabel(row, EffectLabels, out string fxLabel, out string fxValue))
            {
                effectBuilders.Add((fxLabel.Replace(" Effect", ""), fxValue, []));
                continue;
            }

            if (TryMatchRowLabel(row, ["Value"], out _, out string merchant))
            {
                merchantValue = merchant;
                continue;
            }

            if (row.Count == 1 && effectBuilders.Count > 0 &&
                TryParseSelfContainedLabelValue(joined, out string modLabel, out string modValue) &&
                EffectModifierLabels.Contains(modLabel))
            {
                effectBuilders[^1].Modifiers.Add(new KeyValuePair<string, string>(modLabel, modValue));
                continue;
            }

            ParseStatRow(row, stats, warnings);
        }

        var effects = effectBuilders
            .Select(e => BuildEffect(e.Kind, e.Description, e.Modifiers))
            .ToList();

        return new ParsedItem(
            contentName, level, nameMismatch,
            flags, classes, races, slots,
            stats, exaltations, effects, merchantValue, Lore: null, warnings);
    }

    /// <summary>The Lore tab is a different view of the same window, not a variant of the Description layout:
    /// there is no repeated content-area name and no stat block, just the lore prose. So the item's name can only
    /// come from the title bar here, and with nothing to reconcile it against, the title-vs-content occlusion
    /// check simply doesn't apply.</summary>
    private static ParsedItem ParseLoreView(List<List<OcrLine>> rows, int i, string titleName, int titleLevel, List<string> warnings)
    {
        // Everything below the tab row is lore. Joined with spaces because the game wraps a long lore string
        // across rows purely to fit the window — the breaks aren't part of the text.
        string lore = string.Join(' ', rows.Skip(i).Select(JoinRow)).Trim();
        if (lore.Length == 0) warnings.Add("Lore tab is active but no lore text was found below it.");

        return new ParsedItem(
            titleName, titleLevel, TitleContentNameMismatch: false,
            Flags: [], Classes: [], Races: [], Slots: [],
            Stats: [], ExaltationSlots: [], Effects: [],
            MerchantValue: null, Lore: lore.Length == 0 ? null : lore, Warnings: warnings);
    }

    /// <summary>Splits an effect's raw text into its name and its parenthesised qualifiers, and normalizes the
    /// two ways a required level reaches us into one. See <see cref="EffectEntry"/>.</summary>
    private static EffectEntry BuildEffect(string kind, string rawDescription, List<KeyValuePair<string, string>> modifiers)
    {
        string text = rawDescription.Trim();
        var conditions = new List<string>();

        // Qualifiers are always trailing parentheticals, so peel them off the end until none remain.
        while (true)
        {
            int open = text.LastIndexOf('(');
            if (open < 0) break;

            int close = text.IndexOf(')', open);
            string inner = (close < 0 ? text[(open + 1)..] : text[(open + 1)..close]).Trim();
            string remainder = text[..open].TrimEnd();
            if (inner.Length == 0 || remainder.Length == 0) break;

            text = remainder;
            Match level = Regex.Match(inner, @"^Req(?:uired)?\.?\s*Level\s*:?\s*(?<level>\d+)$", RegexOptions.IgnoreCase);
            if (level.Success)
                modifiers.Insert(0, new KeyValuePair<string, string>("Required Level", level.Groups["level"].Value));
            else
                conditions.Insert(0, inner);
        }

        // OCR sometimes detects an effect's trailing parenthetical as its own fragment and leaves the open paren
        // behind with the name ("Click Effect Careless Lightning (" + "(Can Equip)"). The peel above consumes the
        // real parenthetical and can't match this orphan, so drop it — no real effect name ends in an open paren.
        return new EffectEntry(kind, text.TrimEnd(' ', '('), conditions, modifiers);
    }

    /// <summary>True if a filled exaltation slot's name does NOT match the item's own base name — a foreign
    /// exaltation, per the plan's Augmentations section, making the item ineligible for automated processing.
    /// Fuzzy, not exact: OCR'd payload text (names, not just labels) can take single-character hits too
    /// (confirmed in testing — see the plan's milestone 1 writeup), so exact equality would false-positive.</summary>
    public static bool IsForeignExaltation(ExaltationSlot slot, string itemBaseName)
    {
        if (slot.Name is null) return false;

        // A filled Ornamentation slot is foreign by definition: ornamentation is never something an item ships
        // with, only something a player applies (user, 2026-09-23), and the user has since confirmed it should be
        // treated exactly like any other foreign exaltation (2026-09-25). The name comparison below would usually
        // reach the same answer, but only usually — an ornamentation whose name happened to resemble the item's own
        // would slip through as "native", and this is the one slot where that inference is knowably wrong.
        if (slot.Kind == ExaltationKind.Ornamentation) return true;

        int threshold = Math.Max(2, itemBaseName.Length / 6);
        return !EditDistance.IsCloseMatch(slot.Name, itemBaseName, threshold);
    }

    private static List<List<OcrLine>> GroupIntoRows(IReadOnlyList<OcrLine> lines)
    {
        const int rowTolerancePx = 8; // see WindowBoundsFinder's similar tolerance constants for the same reasoning

        List<OcrLine> sorted = lines.OrderBy(l => l.BoundingBox.Y).ThenBy(l => l.BoundingBox.X).ToList();
        var rows = new List<List<OcrLine>>();
        foreach (OcrLine line in sorted)
        {
            // Anchor on the row's first (topmost) member, not the most recently added one, so tolerance can't
            // chain/drift across several rows of slightly-increasing Y.
            if (rows.Count > 0 && Math.Abs(rows[^1][0].BoundingBox.Y - line.BoundingBox.Y) <= rowTolerancePx)
                rows[^1].Add(line);
            else
                rows.Add([line]);
        }
        foreach (List<OcrLine> row in rows) row.Sort((a, b) => a.BoundingBox.X.CompareTo(b.BoundingBox.X));
        return rows;
    }

    /// <summary>Drops rows with no letters or digits at all. The window's own chrome occasionally reads as
    /// punctuation — a real capture has the tab-bar corner, clipped at the crop's left edge, recognized as
    /// "()" — and because the header is parsed positionally, one such junk row between the tab row and the
    /// content-area name shifts every following field by one: the name is read as "()", and the real name row is
    /// then consumed as the flags row. No legitimate field is punctuation-only, so dropping these outright is
    /// safer than trying to identify the name row by similarity to the title (which would quietly defeat the
    /// title-vs-content occlusion check, whose entire job is to notice when those two *don't* match).</summary>
    private static bool HasAnyAlphanumeric(List<OcrLine> row) =>
        row.Any(l => l.Text.Any(char.IsLetterOrDigit));

    private static string JoinRow(List<OcrLine> row) => string.Join(' ', row.Select(l => l.Text.Trim()));

    private static bool RowLooksLikeTabLabels(List<OcrLine> row) =>
        row.Count > 0 && row.All(l =>
            EditDistance.IsCloseMatch(l.Text.Trim(), "Description", maxDistance: 3) ||
            EditDistance.IsCloseMatch(l.Text.Trim(), "Lore", maxDistance: 1));

    /// <summary>Finds one of <paramref name="candidateLabels"/> anywhere in a row and returns the text following
    /// it, tolerating the ways OCR fragments a row that is visually one line.
    ///
    /// Matching only a single-fragment row (what this used to do) missed three shapes that are all real, and each
    /// one fell through to the generic stat pairing — which recorded an exaltation or an effect as an ordinary
    /// *stat* and dropped it from its own list entirely:
    /// <list type="bullet">
    /// <item>a label split from its value, with the separator duplicated across the break:
    /// <c>"Click Exaltation:"</c> + <c>": Earthshaker's Mantle (Exaltation)"</c>;</item>
    /// <item>an effect's trailing parenthetical detected as its own fragment, leaving a dangling open paren
    /// behind: <c>"Click Effect Careless Lightning ("</c> + <c>"(Can Equip)"</c>;</item>
    /// <item>the exaltation slot's own icon recognized as a stray fragment *before* the label:
    /// <c>"0"</c> + <c>"Click Exaltation: Bladestopper (Exaltation)"</c>.</item>
    /// </list>
    /// Scanning from each fragment in turn covers leading junk and rejoins a split label with its value without
    /// assuming either shape. These labels are long and specific, so a stat value is not going to fuzzy-match one.
    /// </summary>
    private static bool TryMatchRowLabel(
        List<OcrLine> row, IReadOnlyList<string> candidateLabels, out string label, out string value)
    {
        for (int k = 0; k < row.Count; k++)
        {
            string text = JoinRow(row.GetRange(k, row.Count - k));
            if (!FieldLabelLexicon.TryMatchPrefixLabel(text, candidateLabels, out label, out value)) continue;

            // The lexicon strips one separator after the label; a value carried on its own fragment can start
            // with a second one (the "Click Exaltation:" + ": Earthshaker's..." case above).
            value = value.TrimStart(':', '.', ' ');
            return true;
        }

        label = "";
        value = "";
        return false;
    }

    private static bool RowStartsWithLabel(List<OcrLine> row, string label) =>
        FieldLabelLexicon.TryMatchPrefixLabel(JoinRow(row), [label], out _, out _);

    private static string ValueAfterLabel(string text, string label)
    {
        FieldLabelLexicon.TryMatchPrefixLabel(text, [label], out _, out string remainder);
        return remainder;
    }

    private static IEnumerable<string> SplitList(string text, char separator = ',') =>
        text.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Extracts the name and "+X" level, tolerant of trailing OCR noise from a nearby title-bar
    /// checkbox/close icon — confirmed real on multiple captures, reading as "? x" (or similar) after the name,
    /// with or without a "+X"/"(Augmented)" present to anchor on (a plain, non-augmented level-0 item's title has
    /// neither, so the noise strip can't only run relative to those). Stripped unconditionally before the level
    /// search, so trailing junk never corrupts the name or hides the level.</summary>
    private static (string Name, int Level) ExtractNameLevel(string raw)
    {
        string s = StripTrailingIconNoise(StripParentheticalSuffix(raw.Trim(), "Augmented"));
        Match m = Regex.Match(s, @"\+(?<level>\d+)\b");
        if (m.Success && int.TryParse(m.Groups["level"].Value, out int lvl))
            return (StripTrailingIconNoise(s[..m.Index]).Trim(), lvl);
        return (s.Trim(), 0);
    }

    /// <summary>Strips a trailing title-bar checkbox/close-icon OCR artifact that is never part of the actual
    /// name — confirmed real as "?" followed by a close-button glyph, which OCR'd as the Unicode multiplication
    /// sign "×" (U+00D7), not the ASCII letter 'x' — easy to conflate by eye in a terminal, which is exactly what
    /// broke an earlier version of this pattern.</summary>
    private static string StripTrailingIconNoise(string s) =>
        Regex.Replace(s, @"\s*\?+\s*[x×]?\s*$", "", RegexOptions.IgnoreCase).TrimEnd();

    /// <summary>Strips a "(Word)" span if the parenthesized text closely matches <paramref name="word"/> — used
    /// for both "(Augmented)" on titles and "(Exaltation)" on exaltation slot values, tolerant of OCR noise
    /// around the parens/word itself. Finds the matching ')' rather than assuming the parenthetical runs to the
    /// end of the string, so trailing OCR noise after it (see <see cref="ExtractNameLevel"/>) doesn't prevent the
    /// match or get silently absorbed into the returned text.</summary>
    private static string StripParentheticalSuffix(string s, string word)
    {
        int openIdx = s.LastIndexOf('(');
        if (openIdx < 0) return s;
        int closeIdx = s.IndexOf(')', openIdx);
        if (closeIdx < 0) return s;

        string inner = s[(openIdx + 1)..closeIdx].Trim();
        if (!EditDistance.IsCloseMatch(inner, word, maxDistance: 2)) return s;

        return (s[..openIdx] + s[(closeIdx + 1)..]).Trim();
    }

    /// <summary>Fuzzy-compares the title-bar name against the content-area name. Threshold is generous enough to
    /// absorb ordinary single-character OCR noise, but a partial-title-occlusion truncation (missing a whole
    /// leading word or more) reliably exceeds it — that's the point; see the class-level doc comment.</summary>
    private static bool NamesReconcile(string titleName, string contentName)
    {
        if (titleName.Length == 0 || contentName.Length == 0) return false;
        int threshold = Math.Max(3, contentName.Length / 4);
        return EditDistance.IsCloseMatch(titleName, contentName, threshold);
    }

    /// <summary>True if a row is an unlabeled continuation of the `Class:`/`Race:` list above it. A long class
    /// list wraps onto a second row with no label of its own (a real capture: `Class: WAR RNG SHD MNK BRD ROG NEC
    /// WIZ MAG` then `ENC BST BER`), which otherwise threw the whole positional header out by one row — the class
    /// list came back truncated, races empty, and the continuation was consumed as the item's slot.
    ///
    /// Class and race codes are short and ALL-CAPS, which is what separates a continuation from the bare slot row
    /// that can also follow (slots read `Range Ammo`, `Primary Secondary`, `Ear` — always mixed case).</summary>
    private static bool LooksLikeCodeListContinuation(List<OcrLine> row)
    {
        if (row.Count != 1) return false;
        string text = row[0].Text.Trim();
        if (text.Length == 0 || text.Contains(':')) return false;

        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0
            && tokens.All(t => t.Length <= 4 && t.All(char.IsLetterOrDigit) && t == t.ToUpperInvariant());
    }

    private static bool LooksLikeBareSlotRow(List<OcrLine> row)
    {
        if (row.Count != 1) return false;
        string text = row[0].Text.Trim();
        if (text.Length == 0 || text.Contains(':') || text.Contains('.')) return false;
        if (IsChromeRow(row, text)) return false;
        if (FieldLabelLexicon.TryMatchPrefixLabel(text, ExaltationLabels, out _, out _)) return false;
        if (FieldLabelLexicon.TryMatchPrefixLabel(text, EffectLabels, out _, out _)) return false;
        if (FieldLabelLexicon.StartsWithLabel(text, "Value")) return false;
        return true;
    }

    private static bool IsChromeRow(List<OcrLine> row, string joined)
    {
        if (row.Count > 0 && EditDistance.IsCloseMatch(row[0].Text.Trim(), "Merge", maxDistance: 1)) return true;
        if (row.Count > 0 && EditDistance.IsCloseMatch(row[0].Text.Trim(), "Item", maxDistance: 1)) return true;
        if (row.Count > 0 && EditDistance.IsCloseMatch(row[0].Text.Trim(), "Modified", maxDistance: 2)) return true;
        if (FieldLabelLexicon.TryMatchPrefixLabel(joined, ["Tier"], out _, out _)) return true;
        if (FieldLabelLexicon.TryMatchPrefixLabel(joined, ["This item"], out _, out _)) return true;
        return false;
    }

    /// <summary>Splits a single self-contained "Label: Value" fragment (e.g. "Cast Time: 4.0 seconds"), applying
    /// label correction. Shared by <see cref="ParseStatRow"/> and the effect-modifier attachment check.</summary>
    private static bool TryParseSelfContainedLabelValue(string text, out string label, out string value)
    {
        int colonIdx = text.IndexOf(':');
        if (colonIdx > 0 && colonIdx < text.Length - 1)
        {
            label = FieldLabelLexicon.Correct(text[..colonIdx].Trim());
            value = text[(colonIdx + 1)..].Trim();
            return true;
        }

        // OCR also renders the separator as '.' on this UI (real captures: "Accuracy. +13.6%",
        // "Container. CLOSED."). Splitting on '.' unconditionally would cut decimals in half, so this only
        // applies when the text before the dot is a label the lexicon actually knows.
        int dotIdx = text.IndexOf('.');
        if (dotIdx > 0 && dotIdx < text.Length - 1)
        {
            string candidate = text[..dotIdx].Trim();
            if (FieldLabelLexicon.IsKnownLabel(candidate))
            {
                label = FieldLabelLexicon.Correct(candidate);
                value = text[(dotIdx + 1)..].Trim();
                return true;
            }
        }

        label = "";
        value = "";
        return false;
    }

    private static ExaltationSlot ParseExaltation(string label, string value)
    {
        ExaltationKind kind = label switch
        {
            "Ornamentation" => ExaltationKind.Ornamentation,
            "Focus Exaltation" => ExaltationKind.Focus,
            "Click Exaltation" => ExaltationKind.Click,
            "Worn Exaltation" => ExaltationKind.Worn,
            "Proc Exaltation" => ExaltationKind.Proc,
            _ => ExaltationKind.Ornamentation, // unreachable: label always comes from ExaltationLabels
        };

        string v = value.Trim();
        if (v.Length == 0 || EditDistance.IsCloseMatch(v, "empty", maxDistance: 1))
            return new ExaltationSlot(kind, null);

        return new ExaltationSlot(kind, StripParentheticalSuffix(v, "Exaltation"));
    }

    /// <summary>Reconstructs label/value pairs from a row's fragments. Handles both real shapes seen in practice:
    /// a single self-contained "Label: Value" fragment (Class:, Race:, exaltation/effect rows), and the
    /// two-column stat block's split shape (a bare "Label:"/"Label." fragment immediately followed by a separate
    /// value fragment) — including two such pairs sharing one row (e.g. "Size:" "SMALL" "AC:" "15").</summary>
    private static void ParseStatRow(List<OcrLine> row, List<KeyValuePair<string, string>> stats, List<string> warnings)
    {
        int i = 0;
        while (i < row.Count)
        {
            string text = row[i].Text.Trim();
            if (TryParseSelfContainedLabelValue(text, out string selfLabel, out string selfValue))
            {
                stats.Add(new KeyValuePair<string, string>(selfLabel, selfValue));
                i++;
                continue;
            }

            // A label fragment normally keeps its ':' (or an OCR'd '.'), but this engine drops that colon
            // unpredictably on this UI, which would otherwise strand the label and its value as two unparsed
            // fragments — so a fragment that corrects to a known field label counts as a label either way.
            string labelText = text.TrimEnd(':', '.').Trim();
            bool bareLabel = text.EndsWith(':') || text.EndsWith('.') || FieldLabelLexicon.IsKnownLabel(labelText);
            if (bareLabel && i + 1 < row.Count)
            {
                string nextText = row[i + 1].Text.Trim();
                // The next fragment must actually look like a value, not another bare label — if a value
                // fragment was dropped by OCR (confirmed real: a real capture had a numeric value missing
                // entirely), the next surviving fragment is the *next label*, and pairing them would silently
                // produce nonsense like "Strength: SV. Magic:". Flag the orphaned label instead.
                bool nextLooksLikeValue = nextText.Length > 0 && !nextText.EndsWith(':') && !nextText.EndsWith('.');
                if (nextLooksLikeValue)
                {
                    stats.Add(new KeyValuePair<string, string>(FieldLabelLexicon.Correct(labelText), nextText));
                    i += 2;
                    continue;
                }

                warnings.Add($"Unparsed line in stat block: \"{text}\" (no adjacent value found — likely an OCR-dropped value)");
                i++;
                continue;
            }

            if (text.Length > 0)
                warnings.Add($"Unparsed line in stat block: \"{text}\"");
            i++;
        }
    }
}
