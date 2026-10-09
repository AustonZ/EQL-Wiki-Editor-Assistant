namespace EQLWikiEditorAssistant.Wiki.Mapping;

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

    /// <summary>Class code -> the category it earns (<c>BRD</c> -> <c>Bard Equipment</c>), for showing the rules.
    /// <see cref="Derive"/> is what applies them, including that only an item with a slot earns one.</summary>
    public static IEnumerable<KeyValuePair<string, string>> ClassCategories =>
        ClassNames.Select(c => KeyValuePair.Create(c.Key, $"{c.Value} Equipment"));

    /// <summary>Wiki slot name -> the category it earns, for showing the rules.</summary>
    public static IReadOnlyDictionary<string, string> SlotCategoryNames => SlotCategories;

    /// <summary>The token the game uses for "every class", which expands to all of them.</summary>
    public const string AllClasses = "ALL";

    /// <summary>The token for "no class can use this" — a real value with no category, like `ANY` for slots.</summary>
    public const string NoClasses = "NONE";

    /// <summary>
    /// Categories implied by a *field* rather than by a class or a slot, keyed by the wiki's label for it.
    ///
    /// `Pet Illusion Items` is the first (user, 2026-09-28). Only three items in the game carry a pet illusion
    /// today, which is exactly why the category matters: it is how anyone finds them.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FieldCategories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pet Illusion"] = "Pet Illusion Items",
        };

    /// <summary>
    /// The category names implied by a capture's class list and slots, in the blueprint's order (classes
    /// alphabetically, then slots).
    /// </summary>
    /// <param name="classCodes">Class codes as the game writes them, or a single <c>ALL</c>.</param>
    /// <param name="wikiSlots">Slots as the *wiki* spells them — run them through
    /// <see cref="WikiMapping.ToWikiSlot"/> first, since the game says `Fingers` where the wiki says `FINGER`.</param>
    /// <param name="unrecognized">Codes and slots with no category, so the caller can report them rather than
    /// silently producing a short list. A new class or slot should surface, not vanish.</param>
    /// <param name="wikiFieldLabels">The wiki's labels for the fields the capture produced, for categories implied
    /// by a property rather than by a class or slot — see <see cref="FieldCategories"/>. Unknown labels are ignored
    /// rather than reported: most fields imply no category, so reporting them would be pure noise.</param>
    public static IReadOnlyList<string> Derive(
        IReadOnlyList<string> classCodes,
        IReadOnlyList<string> wikiSlots,
        out IReadOnlyList<string> unrecognized,
        IReadOnlyList<string>? wikiFieldLabels = null)
    {
        ArgumentNullException.ThrowIfNull(classCodes);
        ArgumentNullException.ThrowIfNull(wikiSlots);

        var categories = new List<string>();
        var unknown = new List<string>();

        // **A class category is only earned by something that is actually equipment** — that is, something with a
        // slot to equip it in (user, 2026-09-28). The category is named `<Class> Equipment`, and a Water Flask is
        // not warrior equipment however many classes may drink from it.
        //
        // Measured before adopting it: of 326 class categories the tool wanted to add across the corpus, **323 were
        // on items with no slot at all** — food, drink, gems, tradeskill materials, containers, potions, quest
        // tokens — every one of them carrying `Class: ALL` and gaining all sixteen. Three were on real equipment.
        // The rule removes essentially all of the noise and almost none of the signal.
        bool isEquipment = wikiSlots.Any(s => s.Trim().Length > 0);
        bool allClasses = classCodes.Any(c => string.Equals(c, AllClasses, StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> classNames = allClasses
            ? ClassNames.Values
            : classCodes.Select(code =>
            {
                if (ClassNames.TryGetValue(code.Trim(), out string? name)) return name;
                // `NONE` is a real value meaning no class can use the item — it has no category of its own and is
                // not an unknown code, so it is neither emitted nor reported. Same shape as `ANY` for slots below,
                // and for the same reason: reporting it would be a false alarm on every such item.
                if (!string.Equals(code.Trim(), NoClasses, StringComparison.OrdinalIgnoreCase)) unknown.Add(code.Trim());
                return null;
            }).Where(n => n is not null).Select(n => n!);

        // The class list is still walked when the item is not equipment, so an unrecognized code is still reported —
        // a new class should surface whatever kind of item revealed it.
        IReadOnlyList<string> classCategories = [.. classNames.Select(n => $"{n} Equipment").OrderBy(n => n, StringComparer.Ordinal)];
        if (isEquipment) categories.AddRange(classCategories);

        foreach (string slot in wikiSlots)
        {
            // An unrecognized slot is reported, with no exceptions. There used to be one here for `ANY`, on the
            // stated grounds that it was "a real slot with no category" — it is not. `Any Slot` occurs only inside
            // an effect's parenthetical (`Effect: [[Levitation]] (Any Slot, Casting Time: 4.0)`), where it means the
            // effect works whichever slot the item is in; it is never a slot value. Measured 2026-09-28: 0 of 104
            // captured windows and 0 of 1,183 wiki pages have a slot `ANY`, and the blueprint's table has no such
            // entry. The exception silenced a value that does not exist, and would have silenced a genuinely new
            // slot if one ever appeared — the opposite of what an unrecognized value is supposed to do here.
            if (SlotCategories.TryGetValue(slot.Trim(), out string? category)) categories.Add(category);
            else unknown.Add(slot.Trim());
        }

        foreach (string label in wikiFieldLabels ?? [])
            if (FieldCategories.TryGetValue(label.Trim(), out string? category) && !categories.Contains(category))
                categories.Add(category);

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
            || FieldCategories.Values.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            || (trimmed.EndsWith(" Equipment", StringComparison.OrdinalIgnoreCase)
                && ClassNames.Values.Contains(trimmed[..^" Equipment".Length], StringComparer.OrdinalIgnoreCase));
    }
}
