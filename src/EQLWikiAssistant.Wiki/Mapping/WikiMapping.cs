using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLWikiAssistant.Wiki.Mapping;

/// <summary>What the tool knows about a stat the game displays.</summary>
public enum StatDisposition
{
    /// <summary>The wiki stores it, under <see cref="StatMapping.WikiLabel"/>.</summary>
    Stored,

    /// <summary>The wiki deliberately does not record it. Ignored silently — reporting it would be noise on every
    /// affected item. <see cref="StatMapping.Note"/> says why, because the reasons differ and a future reader
    /// should not have to guess.</summary>
    NotStored,
}

/// <summary>
/// One game stat label and where it goes on the wiki.
///
/// <see cref="WikiSuffix"/> exists because the two sides do not always write the same units. The game shows
/// <c>Weight Red: 100</c> where the wiki writes <c>Weight Reduction: 100%</c> — identical data, and without this the
/// comparison called them different and would have stripped the <c>%</c> off every such page. Found by running the
/// analyzer over the corpus, not by reading either format.
///
/// <see cref="Signed"/> marks the fields the wiki writes with an explicit sign: `STR: +5` where the game says `5`.
/// Measured, not assumed — attributes and resists are overwhelmingly signed (`STR` 126 signed against 4 plain,
/// `SV FIRE` 68 against 0) while `WT`, `AC`, `DMG`, `Atk Delay`, `Range`, `Capacity` and `Weight Reduction` never
/// are (`WT` 0 signed against 721). **It affects only the value the tool proposes, never whether two values match**
/// — see <c>ItemPageAnalyzer</c> for why a sign-only difference is deliberately not an edit.
/// </summary>
public sealed record StatMapping(
    string GameLabel,
    string? WikiLabel,
    StatDisposition Disposition,
    string? WikiSuffix = null,
    bool Signed = false,
    string? Note = null);

/// <summary>
/// The translation between what the game window says and what the wiki records.
///
/// **This is data, not code** — the repo's standing rule, because the wiki's conventions are expected to keep
/// changing and a wiki-side change should be a config edit rather than a release. <see cref="Default"/> is the
/// built-in baseline; <see cref="Load"/> reads a user-edited copy from app-data. The plan puts the full mapping
/// (category rules, era templates, line ordering) in milestone 6 with a Settings window; this is the subset the
/// diff needs, landing with milestone 4 exactly as the plan anticipated.
///
/// **An unmapped stat is reported, never dropped.** That is the whole reason <see cref="StatDisposition"/> has no
/// "ignore" member: a stat the tool has never seen is how a game patch announces itself, and silently discarding it
/// would lose real data from a public wiki with nobody the wiser. The four in
/// <see cref="UnmappedGameLabels"/> are live examples awaiting a decision.
///
/// Every mapping below was derived by censusing both sides: 37 distinct stat labels across the 101 verified item
/// windows, against 49 across 744 real wiki pages.
/// </summary>
public sealed class WikiMapping
{
    /// <summary>Bumped whenever the built-in defaults change. The ledger records the version an item was checked
    /// under, so a mapping change invalidates stale entries rather than leaving them looking current.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The template every item page transcludes.</summary>
    public string TemplateName { get; init; } = "Itempage";

    /// <summary>Parameter names for the fields v1 reads and writes.</summary>
    public string ItemNameParameter { get; init; } = "itemname";
    public string IconIdParameter { get; init; } = "lucy_img_ID";
    public string StatsBlockParameter { get; init; } = "statsblock";
    public string FocusEffectParameter { get; init; } = "focus_effect";
    public string MerchantValueParameter { get; init; } = "merchant_value";
    public string NotesParameter { get; init; } = "notes";

    /// <summary>Game stat label -> wiki stat label. Keyed case-insensitively because the wiki is inconsistent about
    /// case (both `SV FIRE` and `SV Fire` occur on real pages) and the game is not.</summary>
    public IReadOnlyDictionary<string, StatMapping> Stats { get; init; } =
        new Dictionary<string, StatMapping>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Game stat labels that are real data with no agreed wiki home yet, listed so the diff can say something
    /// specific instead of "unknown field".
    ///
    /// Guessing a wiki label for one of these would invent a convention the wiki's editors never agreed to, so they
    /// are surfaced as questions instead. Empty as of 2026-09-25: the four that were here (`Accuracy`, `Container`,
    /// `Type`, `Items`) have all been resolved by the user. Kept because the next game patch will refill it.
    /// </summary>
    public IReadOnlyList<string> UnmappedGameLabels { get; init; } = [];

    /// <summary>
    /// Game slot name -> wiki slot name, for the ones that are not simply the game's name upper-cased.
    ///
    /// The wiki writes slots in caps (`EAR`, `PRIMARY`, `CHEST`) where the game uses Title Case, which
    /// <see cref="ToWikiSlot"/> handles without a table. Only one slot genuinely differs: the game's `Fingers` is
    /// the wiki's singular `FINGER`. Censused across all 18 slot names the corpus contains — so this is a real
    /// exception list, not a guess at one.
    /// </summary>
    public IReadOnlyDictionary<string, string> Slots { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Game effect kind -> the token the wiki puts in an effect line's parenthetical.
    ///
    /// The template documents `Combat`, `Clicky` and `Worn`. The game also produces `Charge` and `Consumable`
    /// effects, which have no agreed token — 4 and 3 respectively in the verified corpus — so they are absent here
    /// on purpose and the renderer refuses rather than picking one. `Worn` is in the game and on real pages but
    /// missing from the template's documented list, which is on the user's list to fix.
    /// </summary>
    public IReadOnlyDictionary<string, string> EffectKinds { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Effect kinds the wiki stores in their own template parameter rather than a statsblock line.</summary>
    public IReadOnlyList<string> FocusEffectKinds { get; init; } = [];

    /// <summary>The wiki's parenthetical token for a game effect kind, or null if it has none agreed.</summary>
    public string? FindEffectKind(string gameKind)
    {
        ArgumentNullException.ThrowIfNull(gameKind);
        return EffectKinds.TryGetValue(gameKind.Trim(), out string? token) ? token : null;
    }

    /// <summary>Whether this kind belongs in <see cref="FocusEffectParameter"/> instead of the statsblock.</summary>
    public bool IsFocusEffect(string gameKind)
    {
        ArgumentNullException.ThrowIfNull(gameKind);
        return FocusEffectKinds.Contains(gameKind.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The wiki's spelling of a game slot name.</summary>
    public string ToWikiSlot(string gameSlot)
    {
        ArgumentNullException.ThrowIfNull(gameSlot);
        string trimmed = gameSlot.Trim();
        return Slots.TryGetValue(trimmed, out string? wiki) ? wiki : trimmed.ToUpperInvariant();
    }

    /// <summary>The built-in baseline.</summary>
    public static WikiMapping Default { get; } = CreateDefault();

    /// <summary>How a game stat should be handled, or null when the tool has no idea — which the caller must
    /// surface rather than skip.</summary>
    public StatMapping? FindStat(string gameLabel)
    {
        ArgumentNullException.ThrowIfNull(gameLabel);
        return Stats.TryGetValue(gameLabel.Trim(), out StatMapping? mapping) ? mapping : null;
    }

    public static WikiMapping Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return JsonSerializer.Deserialize<WikiMappingFile>(File.ReadAllText(path), JsonOptions)?.ToMapping()
            ?? throw new InvalidOperationException($"'{path}' does not contain a wiki mapping.");
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(
            path, JsonSerializer.Serialize(WikiMappingFile.From(this), JsonOptions), cancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static WikiMapping CreateDefault()
    {
        // (game label, wiki label). Confident mappings only — see UnmappedGameLabels for what was deliberately
        // left out. The wiki's canonical casing is used (SV FIRE, not SV Fire: 31 pages to 3).
        (string Game, string Wiki)[] stored =
        [
            ("Size", "Size"),
            ("Weight", "WT"),
            ("AC", "AC"),
            ("Strength", "STR"),
            ("Stamina", "STA"),
            ("Agility", "AGI"),
            ("Dexterity", "DEX"),
            ("Wisdom", "WIS"),
            ("Intelligence", "INT"),
            ("Charisma", "CHA"),
            ("HP", "HP"),
            ("Mana", "MANA"),
            ("End", "END"),
            ("HP Regen", "HP Regen"),
            ("Mana Regen", "Mana Regen"),
            ("End Regen", "End Regen"),
            ("SV. Fire", "SV FIRE"),
            ("SV. Cold", "SV COLD"),
            ("SV. Magic", "SV MAGIC"),
            ("SV. Disease", "SV DISEASE"),
            ("SV. Poison", "SV POISON"),
            ("SV. Void", "SV VOID"),
            ("Base Dmg", "DMG"),
            ("Dmg Bon", "DMG Bonus"),
            ("Backstab Dmg", "Backstab"),
            ("Delay", "Atk Delay"),
            ("Skill", "Skill"),
            ("Range", "Range"),
            ("Haste", "Haste"),
            ("Capacity", "Capacity"),
            ("Size Cap", "Size Capacity"),
            // Container properties the user placed in the statsblock (2026-09-25): Type sits with Slot (it marks an
            // item as usable for bashing), Items sits with Size Capacity (it restricts what a container may hold).
            ("Type", "Type"),
            ("Items", "Items"),
            // Added to the game the week of 2026-09-22, so no legacy page has it and every item that shows one is
            // an addition. The game already writes the % and the sign ("+13.6%").
            ("Accuracy", "Accuracy"),
        ];

        var map = new Dictionary<string, StatMapping>(StringComparer.OrdinalIgnoreCase);
        foreach ((string game, string wiki) in stored)
            map[game] = new StatMapping(game, wiki, StatDisposition.Stored);

        // The fields the wiki writes with an explicit sign — attributes and resists, measured (see StatMapping).
        foreach (string game in new[]
                 {
                     "Strength", "Stamina", "Agility", "Dexterity", "Wisdom", "Intelligence", "Charisma",
                     "HP", "Mana", "End",
                     "SV. Fire", "SV. Cold", "SV. Magic", "SV. Disease", "SV. Poison", "SV. Void",
                 })
            map[game] = map[game] with { Signed = true };

        // The one field where the units differ: the game writes 100, the wiki 100%.
        map["Weight Red"] = new StatMapping("Weight Red", "Weight Reduction", StatDisposition.Stored, WikiSuffix: "%");

        map["Ratio"] = new StatMapping("Ratio", null, StatDisposition.NotStored, Note:
            "Base Dmg over Delay. The wiki stores both inputs, so recording the quotient would be a third value to " +
            "keep consistent for no gain — and a rounding difference between the game and a recomputation would " +
            "look like a data error.");

        map["Container"] = new StatMapping("Container", null, StatDisposition.NotStored, Note:
            "Whether the bag is open or closed. Already implied by the other container fields the wiki does store " +
            "(Capacity, Size Capacity), so it adds nothing (user, 2026-09-25).");

        return new WikiMapping
        {
            Stats = map,
            UnmappedGameLabels = [],
            Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Fingers"] = "FINGER" },
            EffectKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Combat"] = "Combat",
                ["Click"] = "Clicky",
                ["Worn"] = "Worn",
                // Charge and Consumable are deliberately absent — see EffectKinds.
            },
            FocusEffectKinds = ["Focus"],
        };
    }

    /// <summary>The on-disk shape. Separate from the model so the JSON stays a flat, hand-editable file rather than
    /// a serialization of whatever the model happens to look like.</summary>
    private sealed class WikiMappingFile
    {
        public int Version { get; set; } = CurrentVersion;
        public string TemplateName { get; set; } = "Itempage";
        public Dictionary<string, string> Parameters { get; set; } = [];
        public Dictionary<string, string> Stats { get; set; } = [];
        public List<string> NotStoredStats { get; set; } = [];
        public List<string> UnmappedGameLabels { get; set; } = [];

        public static WikiMappingFile From(WikiMapping mapping) => new()
        {
            Version = mapping.Version,
            TemplateName = mapping.TemplateName,
            Parameters = new Dictionary<string, string>
            {
                ["itemName"] = mapping.ItemNameParameter,
                ["iconId"] = mapping.IconIdParameter,
                ["statsBlock"] = mapping.StatsBlockParameter,
                ["focusEffect"] = mapping.FocusEffectParameter,
                ["merchantValue"] = mapping.MerchantValueParameter,
                ["notes"] = mapping.NotesParameter,
            },
            Stats = mapping.Stats.Values
                .Where(s => s.Disposition == StatDisposition.Stored && s.WikiLabel is not null)
                .ToDictionary(s => s.GameLabel, s => s.WikiLabel!, StringComparer.Ordinal),
            NotStoredStats = [.. mapping.Stats.Values
                .Where(s => s.Disposition == StatDisposition.NotStored)
                .Select(s => s.GameLabel)],
            UnmappedGameLabels = [.. mapping.UnmappedGameLabels],
        };

        public WikiMapping ToMapping()
        {
            var map = new Dictionary<string, StatMapping>(StringComparer.OrdinalIgnoreCase);
            foreach ((string game, string wiki) in Stats)
                map[game] = new StatMapping(game, wiki, StatDisposition.Stored);
            foreach (string game in NotStoredStats)
                map[game] = new StatMapping(game, null, StatDisposition.NotStored);

            return new WikiMapping
            {
                Version = Version,
                TemplateName = TemplateName,
                ItemNameParameter = Parameters.GetValueOrDefault("itemName", "itemname"),
                IconIdParameter = Parameters.GetValueOrDefault("iconId", "lucy_img_ID"),
                StatsBlockParameter = Parameters.GetValueOrDefault("statsBlock", "statsblock"),
                FocusEffectParameter = Parameters.GetValueOrDefault("focusEffect", "focus_effect"),
                MerchantValueParameter = Parameters.GetValueOrDefault("merchantValue", "merchant_value"),
                NotesParameter = Parameters.GetValueOrDefault("notes", "notes"),
                Stats = map,
                UnmappedGameLabels = UnmappedGameLabels,
            };
        }
    }
}
