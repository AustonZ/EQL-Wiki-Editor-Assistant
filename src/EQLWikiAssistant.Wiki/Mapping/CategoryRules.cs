namespace EQLWikiAssistant.Wiki.Mapping;

/// <summary>
/// Which `[[Category:...]]` lines an item's class and slot imply.
///
/// Both tables come verbatim from the Item Page Blueprint on `Help:Contents` (user, 2026-09-25), not from sampling
/// pages — a census would have recorded whatever existing pages happen to do, and several of them are incomplete
/// (`Golden Efreeti Boots` says `Class: ALL` but lists only 14 of the 16 class categories, predating Beastlord and
/// Berserker).
///
/// **The derived categories are only ever a subset of a page's categories.** Real pages also carry zone names,
/// `Quest Items`, `Focus Items`, `Inventory Items`, `Fashion:` entries and more — none of which is derivable from an
/// item window. So this answers "which categories does the capture imply?" and never "which categories should the
/// page have?"; anything outside these two families is preserved untouched.
/// </summary>
public static class CategoryRules
{
    /// <summary>Game class code -> the wiki's category name for it. The game abbreviates, the wiki spells out.</summary>
    private static readonly Dictionary<string, string> ClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BRD"] = "Bard",
        ["BST"] = "Beastlord",
        ["BER"] = "Berserker",
        ["CLR"] = "Cleric",
        ["DRU"] = "Druid",
        ["ENC"] = "Enchanter",
        ["MAG"] = "Magician",
        ["MNK"] = "Monk",
        ["NEC"] = "Necromancer",
        ["PAL"] = "Paladin",
        ["RNG"] = "Ranger",
        ["ROG"] = "Rogue",
        ["SHD"] = "Shadow Knight",
        ["SHM"] = "Shaman",
        ["WAR"] = "Warrior",
        ["WIZ"] = "Wizard",
    };

    /// <summary>
    /// Wiki slot name -> its category. Almost all are the slot in title case, and the blueprint's category list
    /// confirms the one documented exception: the slot is `FINGER`, the category is `Fingers`.
    ///
    /// `ANY` appears in the blueprint's slot list but has no category, so an item in that slot contributes none.
    /// </summary>
    private static readonly Dictionary<string, string> SlotCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRIMARY"] = "Primary",
        ["SECONDARY"] = "Secondary",
        ["RANGE"] = "Range",
        ["AMMO"] = "Ammo",
        ["ARMS"] = "Arms",
        ["BACK"] = "Back",
        ["CHEST"] = "Chest",
        ["EAR"] = "Ear",
        ["FACE"] = "Face",
        ["FEET"] = "Feet",
        ["FINGER"] = "Fingers",
        ["HANDS"] = "Hands",
        ["HEAD"] = "Head",
        ["LEGS"] = "Legs",
        ["NECK"] = "Neck",
        ["SHOULDERS"] = "Shoulders",
        ["WAIST"] = "Waist",
        ["WRIST"] = "Wrist",
    };

    /// <summary>The token the game uses for "every class", which expands to all of them.</summary>
    public const string AllClasses = "ALL";

    /// <summary>
    /// The category names implied by a capture's class list and slots, in the blueprint's order (classes
    /// alphabetically, then slots).
    /// </summary>
    /// <param name="classCodes">Class codes as the game writes them, or a single <c>ALL</c>.</param>
    /// <param name="wikiSlots">Slots as the *wiki* spells them — run them through
    /// <see cref="WikiMapping.ToWikiSlot"/> first, since the game says `Fingers` where the wiki says `FINGER`.</param>
    /// <param name="unrecognized">Codes and slots with no category, so the caller can report them rather than
    /// silently producing a short list. A new class or slot should surface, not vanish.</param>
    public static IReadOnlyList<string> Derive(
        IReadOnlyList<string> classCodes,
        IReadOnlyList<string> wikiSlots,
        out IReadOnlyList<string> unrecognized)
    {
        ArgumentNullException.ThrowIfNull(classCodes);
        ArgumentNullException.ThrowIfNull(wikiSlots);

        var categories = new List<string>();
        var unknown = new List<string>();

        bool allClasses = classCodes.Any(c => string.Equals(c, AllClasses, StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> classNames = allClasses
            ? ClassNames.Values
            : classCodes.Select(code =>
            {
                if (ClassNames.TryGetValue(code.Trim(), out string? name)) return name;
                unknown.Add(code.Trim());
                return null;
            }).Where(n => n is not null).Select(n => n!);

        categories.AddRange(classNames.Select(n => $"{n} Equipment").OrderBy(n => n, StringComparer.Ordinal));

        foreach (string slot in wikiSlots)
        {
            if (SlotCategories.TryGetValue(slot.Trim(), out string? category)) categories.Add(category);
            // "ANY" is a real slot with no category of its own, so it is neither emitted nor reported.
            else if (!string.Equals(slot.Trim(), "ANY", StringComparison.OrdinalIgnoreCase)) unknown.Add(slot.Trim());
        }

        unrecognized = unknown;
        return categories;
    }

    /// <summary>Whether a category name is one this tool derives, and therefore one it may add. Everything else on a
    /// page — zone names, `Quest Items`, `Focus Items`, `Fashion:` entries — is somebody else's and is left
    /// alone.</summary>
    public static bool IsDerivable(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        string trimmed = category.Trim();
        return SlotCategories.Values.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            || (trimmed.EndsWith(" Equipment", StringComparison.OrdinalIgnoreCase)
                && ClassNames.Values.Contains(trimmed[..^" Equipment".Length], StringComparer.OrdinalIgnoreCase));
    }
}
