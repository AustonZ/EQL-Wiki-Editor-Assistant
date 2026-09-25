using System.Globalization;

namespace EQLWikiAssistant.Core.Items;

/// <summary>
/// An item's vendor value, as four coin denominations.
///
/// **The item window's figure is authoritative and always replaces the wiki's** (user, 2026-09-24). In legacy
/// EverQuest players had to guess and check to learn an item's value, so wiki pages carry annotations recording the
/// conditions a figure was observed under — <c>with 111 Charisma</c>, <c>(68 CHA @ Kindly)</c>, <c>Max</c>. EQL
/// removed the guesswork: the window states the **maximum** value directly, independent of Charisma or faction. So
/// a legacy annotated figure is not merely formatted differently, it is frequently *wrong* — a value recorded at 68
/// Charisma is below the maximum — and replacing it is a correction, not a reformat.
///
/// That is also why there is deliberately **no parser for the legacy forms**. The page's existing value is never a
/// source of truth, so nothing needs to be understood about it; the tool renders the captured value and compares
/// strings. A page holding an HTML <c>&lt;ul&gt;/&lt;span style="color:silver"&gt;</c> block, <c>2.6pp</c>,
/// <c>~3pp</c>, <c>1gp to vendor.</c> or <c>1.3 gold</c> simply differs and gets overwritten. Writing a tolerant
/// reader for all of that would be work in service of a value we are going to discard anyway.
///
/// <see cref="Nothing"/> is a real, distinct state rather than zero-of-everything: the game writes
/// <see cref="NothingText"/> for a worthless item and the wiki records that exact string, so it round-trips as
/// itself instead of collapsing to an empty value.
/// </summary>
public readonly record struct MerchantValue(int Platinum, int Gold, int Silver, int Copper)
{
    /// <summary>What the game displays for an item no merchant will pay for, and what the wiki records verbatim.
    /// Confirmed in three captured windows (Vegetables, Fruit, Kavruul`s Mystic Pouch) and on a real page
    /// (Unfired Lined Poison Vial).</summary>
    public const string NothingText = "absolutely nothing";

    /// <summary>The worthless case. Distinct from <c>default</c> only in intent; both render as
    /// <see cref="NothingText"/>.</summary>
    public static MerchantValue Nothing => default;

    /// <summary>True when no denomination carries a value.</summary>
    public bool IsNothing => Platinum == 0 && Gold == 0 && Silver == 0 && Copper == 0;

    /// <summary>
    /// Reads the phrasing the item window uses — <c>"22 platinum 8 gold 5 silver 7 copper"</c>,
    /// <c>"1 platinum 5 silver 8 copper"</c>, <c>"8 copper"</c>, <c>"350 platinum"</c>,
    /// <c>"absolutely nothing"</c> — or returns false, leaving the caller to treat it as an unread field rather
    /// than as zero. Never throws.
    /// </summary>
    /// <remarks>
    /// The game already omits denominations worth nothing, so this does no zero-dropping of its own: every real
    /// captured value names only the coins the item is actually worth. Measured across all 36 merchant values in
    /// the verified corpus.
    /// </remarks>
    public static bool TryParseGameText(string? text, out MerchantValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        if (string.Equals(trimmed, NothingText, StringComparison.OrdinalIgnoreCase)) return true;

        string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length % 2 != 0) return false;

        int platinum = 0, gold = 0, silver = 0, copper = 0;
        for (int i = 0; i < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int amount)) return false;

            // Each denomination may appear at most once; a repeat means this is not the window's own phrasing and
            // silently summing it would invent a figure.
            switch (parts[i + 1].ToLowerInvariant())
            {
                case "platinum" when platinum == 0: platinum = amount; break;
                case "gold" when gold == 0: gold = amount; break;
                case "silver" when silver == 0: silver = amount; break;
                case "copper" when copper == 0: copper = amount; break;
                default: return false;
            }
        }

        value = new MerchantValue(platinum, gold, silver, copper);
        return true;
    }

    /// <summary>
    /// Renders the compact form the wiki uses: <c>"22p 8g 5s 7c"</c>, dropping any denomination that is zero
    /// (<c>"2g 1c"</c>), or <see cref="NothingText"/> when the item is worthless.
    /// </summary>
    /// <remarks>
    /// This spelling is a <em>wiki</em> convention, not a game fact, so it belongs in the mapping layer once that
    /// exists (milestone 6) — the same way flag spellings and statsblock labels do. It lives here for now because
    /// there is no mapping layer yet and a single convention does not justify inventing an abstraction to hold it;
    /// the plan makes the same call about UI-scale profiles. Nothing above this method should hard-code the letters.
    /// </remarks>
    public string ToWikiText()
    {
        if (IsNothing) return NothingText;

        var parts = new List<string>(4);
        if (Platinum != 0) parts.Add($"{Platinum}p");
        if (Gold != 0) parts.Add($"{Gold}g");
        if (Silver != 0) parts.Add($"{Silver}s");
        if (Copper != 0) parts.Add($"{Copper}c");
        return string.Join(' ', parts);
    }

    public override string ToString() => ToWikiText();
}
