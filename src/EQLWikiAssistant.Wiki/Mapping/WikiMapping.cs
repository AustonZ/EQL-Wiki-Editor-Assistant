using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
/// How a captured value is turned into the value the wiki writes, where a rename is not enough.
///
/// Deliberately a short list: every other stat's value is copied through verbatim, with at most the unit suffix and
/// the sign the mapping already carries. A format is for a value with its own small grammar, and each one here has
/// to be justified by both sides having been measured.
/// </summary>
public enum StatValueFormat
{
    /// <summary>Written as captured, give or take <see cref="StatMapping.WikiSuffix"/> and the sign rule.</summary>
    AsCaptured,

    /// <summary>A skill percentage: <c>Fishing 5 % (10 Max)</c> -> <c>Fishing +5% (10 Max)</c>.</summary>
    SkillModifier,
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
///
/// **The blueprint now documents this outright, and it agrees with the measurement exactly** (2026-09-30, oldid
/// 179818): it was changed from `STR: ?` to `STR: +?` across every attribute and every `SV` resist, and left
/// `Attack`, `HP Regen`, `Mana Regen`, `Haste`, `Clairvoyance`, `Spell Dmg`, `Heal Amount` and `END Regen`
/// unsigned — which is the split below, including the easy-to-miss pair where `END` is signed but `END Regen` is
/// not. So this set no longer rests on frequency alone, which matters because the repo's rule is that the
/// blueprint outranks what existing pages do most. Re-measured against it: **1,053 signed occurrences against 54
/// unsigned, the latter on 32 of 1,183 pages.**</summary>
/// <remarks>Those 32 pages are not reached by anything today: the data pass signs a stat only when it is already
/// writing another signed one, and the formatter preserves every value byte for byte. Raised with the user
/// 2026-09-30 rather than changed, since closing it means either reversing their sign-only decision or letting
/// the formatter edit a value — and the latter would break the content check that is its whole licence to
/// rearrange a page.</remarks>
public sealed record StatMapping(
    string GameLabel,
    string? WikiLabel,
    StatDisposition Disposition,
    string? WikiSuffix = null,
    bool Signed = false,
    string? Note = null,
    StatValueFormat Format = StatValueFormat.AsCaptured)
{
    /// <summary>
    /// The captured value as the wiki writes it: the format first, then the unit suffix, then the sign.
    ///
    /// **One home for the whole conversion**, so a caller cannot apply two of the three and miss the other. The
    /// order matters and is the only one that works: a format that rewrites the number has to run before the sign
    /// rule looks at whether the value is a bare number.
    /// </summary>
    public string ToWikiValue(string capturedValue)
    {
        ArgumentNullException.ThrowIfNull(capturedValue);
        string value = capturedValue.Trim();

        if (Format == StatValueFormat.SkillModifier) value = AsSkillModifier(value);

        if (WikiSuffix is { } suffix && !value.EndsWith(suffix, StringComparison.Ordinal))
            value += suffix;

        return WithWikiSign(value);
    }

    /// <summary>
    /// <c>Fishing 5 % (10 Max)</c> -> <c>Fishing +5% (10 Max)</c>: sign the percentage and close the space before
    /// the <c>%</c>, keeping the skill's name and the cap exactly as the game gave them.
    ///
    /// **A format rather than the ordinary sign rule, because the value is not a number** — it is a skill name, a
    /// percentage and a ceiling in one string, so <see cref="WithWikiSign"/> (which deliberately only signs a value
    /// that is wholly a positive number) can never apply. Measured against the only two sources there are: the game
    /// writes `Skill Mod: Fishing 5 % (10 Max)` on `Collapsible Fishing Pole`, the wiki's one page carrying this
    /// field writes `Skill Mod: Fishing +5%`, and the blueprint writes `Skill Mod: ?` — so the sign and the spacing
    /// come from the page and the cap is kept by the user's decision (2026-10-02), it being real game data that
    /// nothing else on the page records.
    ///
    /// Anything that does not match that shape is returned untouched: a value this does not understand is one to
    /// leave alone, not to reshape into something that looks tidy and says something else.
    /// </summary>
    private static string AsSkillModifier(string value)
    {
        Match match = SkillModifierPattern.Match(value);
        if (!match.Success) return value;

        string sign = match.Groups["sign"].Value is { Length: > 0 } given ? given : "+";
        return $"{match.Groups["skill"].Value} {sign}{match.Groups["amount"].Value}%{match.Groups["rest"].Value}";
    }

    private static readonly Regex SkillModifierPattern = new(
        @"^(?<skill>\S+(?: \S+)*?)\s+(?<sign>[+-]?)(?<amount>\d+(?:\.\d+)?)\s*%(?<rest>.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// The value as the wiki writes it, which for a signed stat means a leading <c>+</c>.
    ///
    /// **One rule, shared by both passes that write a value**, because two copies of it would drift and the two
    /// passes disagreeing about a sign is a diff that flaps back and forth. <c>ItemPageAnalyzer</c> calls it for a
    /// value it is writing from a capture; <c>ItemPagePrettifier</c> calls it for a value already on the page.
    ///
    /// **Only a value that is entirely a positive number is signed.** That is deliberately stricter than testing
    /// the first character, which is what the data pass used to do: a capture only ever yields a clean token, but
    /// the formatter is handed arbitrary human-written text, where <c>50 / 75 / 100</c> (two real ammo pages) and
    /// <c>0%</c> would both start with a digit and neither should gain a sign. Measured safe to tighten: all 264
    /// signed-stat values in the verified corpus are a bare non-negative number or already signed, so no captured
    /// value changes.
    ///
    /// **A negative keeps its own sign, and that is real rather than defensive** — the game emits
    /// <c>Dexterity: -1</c> on `Earthshaker` and <c>SV. Magic: -10</c> on `Adamantite Band`, and 29 values across
    /// 1,183 real pages are negative. Zero is left alone too: <c>+0</c> reads oddly, and no signed stat on any real
    /// page or in any capture is zero, so the case is unobservable either way.
    /// </summary>
    public string WithWikiSign(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string trimmed = value.Trim();
        return Signed && IsPositiveNumber(trimmed) ? "+" + trimmed : trimmed;
    }

    /// <summary>A bare positive number — all digits, at most one interior decimal point, and not zero.</summary>
    private static bool IsPositiveNumber(string value)
    {
        if (value.Length == 0) return false;
        if (!value.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
        if (value.Count(c => c == '.') > 1) return false;
        if (value[0] == '.' || value[^1] == '.') return false;
        return value.Any(c => c is >= '1' and <= '9');
    }
}

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
    /// <summary>
    /// Bumped whenever the built-in defaults change. The ledger records the version an item was checked under, so a
    /// mapping change invalidates stale rows rather than leaving them looking current.
    ///
    /// **Bump this in the same commit as any change to what the tool would write** — a new stat, a changed wiki
    /// label, a new category rule, a different statsblock line order. Forgetting means an item checked yesterday is
    /// reported as settled today while the tool would now propose something different, and the user never sees it:
    /// the ledger skips the wiki fetch entirely, so there is no later chance to notice.
    ///
    /// **2** (2026-09-28): categories are now derived and written at all, pet illusion became a mapped field, the
    /// parameter order and two statsblock line positions were added. Rows written under 1 would have been treated
    /// as settled while missing every category the tool would now add.
    ///
    /// **3** (2026-09-28): `Mount Speed` became a mapped stat, so mounts checked under 2 would be settled while
    /// missing it.
    ///
    /// **The blueprint's 2026-09-30 revision (oldid 179818) did not on its own warrant a bump**, which is worth
    /// recording because it looked like it should: the sign convention it newly states was already the mapping's and
    /// the `Recommended level` line it dropped was dead on both sides, so the formatter's census over all 1,183
    /// cached pages came out byte-identical. What earned the bump below was the *behaviour* change the user then
    /// asked for, not the mapping edit that preceded it.
    /// **4** (2026-09-30): the formatting pass now signs a positive attribute or resist (user: a sign is formatting,
    /// not data). This is the bump the note above is about, and it is the awkward kind — a *settled* row means the
    /// next capture skips the wiki entirely, so without it an item whose page has an unsigned stat would never be
    /// offered the fix. Measured cost of not bumping: 9 of 1,183 cached pages. Measured cost of bumping: every
    /// settled row re-checks once, finds its data still matching, and settles again.
    /// **5** (2026-09-30): the seven <see cref="BlockParameters"/> are always laid out as blocks, framed by blank
    /// lines. Same reasoning as 4 — a settled row skips the wiki, so the page would never be offered the new
    /// layout — and a wider reach: 721 of 744 cached pages carry at least one of those parameters.
    /// **6** (2026-10-01): a flags line is compared as the page wrote it, so a legacy flag the tool previously
    /// called a match is now removed. This is the bump that matters most of the three: the pages affected are
    /// exactly the ones a capture would have settled as `Matched`, so without it they would keep their legacy
    /// flags forever.
    /// **7** (2026-10-02): the <c>=</c> column is the blueprint's width rather than the longest name on the page —
    /// see <see cref="ParameterAlignmentWidth"/>. Same settled-row reasoning as 4 and 5, and the widest reach of
    /// any bump so far: **680 of 744 cached pages** are laid out at a narrower column than the new rule wants, and
    /// a settled row would never be offered the reflow.
    /// **8** (2026-10-02): `Skill Mod` is mapped, and a flags line is compared case-sensitively so a legacy `QUEST`
    /// stops matching a captured `Quest`. The second half is the one that needs the bump, and it needs it for
    /// exactly the reason 6 did: the affected pages are the ones a capture *settled* as `Matched`, so without it
    /// they keep their legacy flags forever and nobody is told. Measured at 5 of 1,183 cached pages (`NO TRADE` on
    /// 5, `LORE EQUIPPED` on 1). The user said no bump was needed for the item they reported, having removed it
    /// from their ledger by hand — this is for the ones they have not checked.
    /// </summary>
    public const int CurrentVersion = 8;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The template every item page transcludes.</summary>
    public string TemplateName { get; init; } = "Itempage";

    /// <summary>
    /// The era every item currently in the game belongs to.
    ///
    /// **The era banner is how the wiki records that an item has actually been seen in game** (user, 2026-09-25), so
    /// a capture is itself the confirmation and the tool sets this unconditionally. EQL has shipped no expansion
    /// yet, so everything in game is Classic. When the first one lands, `Kunark` becomes valid for in-game items and
    /// this stops being a constant — that needs a way to ask the user which era an item belongs to without being
    /// annoying about it, which is deliberately left as a future feature rather than guessed at now.
    /// </summary>
    public string CurrentEra { get; init; } = "Classic";

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
    /// effects, which the template has no wording for; the user's interim choice (2026-09-25) is
    /// `Charge Clicky` and `Consumable Clicky`, since both behave as clickies. **Interim is the operative word** —
    /// formalizing all of this with the wiki community is on the user's list, and when it lands this table is the
    /// only thing that changes. `Worn` is likewise in the game and on real pages but missing from the template's
    /// documented list.
    /// </summary>
    public IReadOnlyDictionary<string, string> EffectKinds { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Effect kinds the wiki stores in their own template parameter rather than a statsblock line.</summary>
    public IReadOnlyList<string> FocusEffectKinds { get; init; } = [];

    /// <summary>
    /// The statsblock's line order, each entry listing the wiki labels that share one line, in order. Taken verbatim
    /// from the Item Page Blueprint on `Help:Contents` (user, 2026-09-25), which is the authority the user's own
    /// compliance routine follows.
    ///
    /// **The formatting pass reads this; the data pass does not** — the diff compares fields and does not care where
    /// they sit. (An earlier note here said nothing read it yet, which stopped being true when
    /// <c>ItemPagePrettifier</c> landed. It matters: removing an entry is not free by default, it is free only when
    /// no page and no capture carries that label, which has to be measured.) A label named by no entry is not
    /// dropped — it keeps its own line just before <c>Class:</c> and is reported.
    ///
    /// Note the blueprint lists several labels no capture has produced (`Attack`, `Clairvoyance`, `Spell Dmg`,
    /// `Heal Amount`, `Magic DMG`, `Poison DMG`) and omits several the game does produce (`Range`, `Accuracy`,
    /// `Type`, `Items`). Both gaps are on the user's list. **`Skill Mod` was on the first list until 2026-10-02**,
    /// when a capture produced one — being listed here and nowhere else is exactly how it came to have a line
    /// position but no mapping, so the tool called a documented template field "possibly new".
    ///
    /// **Re-checked against the blueprint's 2026-09-30 revision** (oldid 179818). It dropped its
    /// `Recommended level of ? Required level of ?` line, which is dropped here too, and it settled the field
    /// separator at two spaces on every line — which is what the formatter already emitted. Nothing else here moved.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> StatsBlockLineOrder { get; init; } = [];

    /// <summary>
    /// A sentinel entry in <see cref="StatsBlockLineOrder"/> meaning "a blank line here, if anything below it is
    /// written". It separates fields that describe something *other than the item* — the horse a bridle summons,
    /// the illusion a wand puts on your pet — from the item's own stats.
    ///
    /// Dropped when nothing follows it, so a statsblock never ends in a stray blank.
    /// </summary>
    public const string BlankLine = "(blank)";

    /// <summary>
    /// The order the template's parameters are written in, for the formatting pass. A parameter the page carries
    /// that is not listed here keeps its relative position after the ones that are — an unrecognized parameter is
    /// somebody's content and gets preserved, not sorted into a place nobody chose.
    /// </summary>
    public IReadOnlyList<string> ParameterOrder { get; init; } = [];

    /// <summary>
    /// The column the formatting pass aligns every <c>=</c> to: the longest name the blueprint declares, whether or
    /// not a given page carries that parameter.
    ///
    /// **It is a property of the template, not of one page's parameter list** (user, 2026-10-02), and that is the
    /// whole point. Aligning to the longest name actually *present* made the column a function of which parameters a
    /// page happened to have, so adding or removing any one of them reflowed every line in the call. Measured on the
    /// 744 cached item pages: only **64 (9%) carry `merchant_value`**, the longest blueprint name — and adding a
    /// merchant value is the single most common thing the data pass does to a legacy page, because legacy EverQuest
    /// gave players no way to learn a value while EQL states it outright. So the old rule dragged a whole-column
    /// reflow behind **680 of 744 pages** the first time the tool touched them.
    /// The user found it from the other end (creation always offering a reformat afterwards): a generated page
    /// declares every blueprint parameter, so deleting the blank scaffolding left the column one width too wide.
    /// Both symptoms are the same instability, and fixing the width fixes both.
    /// </summary>
    public int ParameterAlignmentWidth =>
        ParameterOrder.Count == 0 ? 0 : ParameterOrder.Max(p => p.Length);

    /// <summary>
    /// Parameters whose value is always laid out as a block: on its own line, with one blank line between the
    /// <c>|name =</c> line and the content and another before whatever follows.
    ///
    /// **These read as sections rather than as values** — a drop table, a recipe list, a vendor table — and the
    /// blank lines are what make a long template call skimmable. The user asked for it on 2026-09-30 after the
    /// formatter condensed `Hematite`'s `relatedquests`, `recipes` and `soldby` into an unbroken wall, and chose
    /// the consistent rule over a length-dependent one: these are *always* blocks, even for a one-line value.
    ///
    /// **Purely source readability, with no rendering effect.** MediaWiki trims a named parameter's value, so the
    /// surrounding blank lines never reach the page — which is exactly why this belongs to the formatting pass and
    /// not the data pass. For the same reason the content check needs no special case: it compares
    /// <see cref="Wikitext.TemplateParameter.Value"/>, which is trimmed on both sides.
    ///
    /// This is the tail of <see cref="ParameterOrder"/> from <c>bookcontents</c> onward — every parameter after
    /// <c>notes</c>, and nothing else. Listed explicitly rather than derived from that order, because deriving it
    /// would mean a future reordering silently changed which parameters are blocks.
    /// </summary>
    public IReadOnlyList<string> BlockParameters { get; init; } = [];

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

    /// <summary>
    /// How a stat should be handled, looked up by the label the *wiki* writes rather than the one the game does.
    ///
    /// <see cref="Stats"/> is keyed by game label, which is what the data pass has in hand. The formatting pass only
    /// ever sees a page, so it has the wiki's spelling — `STR` where the game says `Strength`, `SV Fire` where it
    /// says `SV. Fire`. Case-insensitive for the same reason <see cref="Stats"/> is: real pages write both
    /// `SV FIRE` and `SV Fire`, and a page's case must not decide whether its value gets a sign.
    /// </summary>
    public StatMapping? FindStatByWikiLabel(string wikiLabel)
    {
        ArgumentNullException.ThrowIfNull(wikiLabel);
        return ByWikiLabel.TryGetValue(wikiLabel.Trim(), out StatMapping? mapping) ? mapping : null;
    }

    /// <summary>
    /// Built on first use rather than in a field initializer, because <see cref="Stats"/> is set by an
    /// <c>init</c> accessor and so is still empty while field initializers run. By the time anything can call
    /// <see cref="FindStatByWikiLabel"/> the object is fully constructed.
    ///
    /// Two threads racing here would each build an index and one would win; both are the same content, so the
    /// race is benign and not worth a lock on a lookup this hot.
    /// </summary>
    private IReadOnlyDictionary<string, StatMapping> ByWikiLabel
    {
        get
        {
            if (_byWikiLabel is not null) return _byWikiLabel;

            var index = new Dictionary<string, StatMapping>(StringComparer.OrdinalIgnoreCase);
            foreach (StatMapping stat in Stats.Values)
                if (stat.WikiLabel is { Length: > 0 } label)
                    index[label] = stat;

            return _byWikiLabel = index;
        }
    }

    private IReadOnlyDictionary<string, StatMapping>? _byWikiLabel;

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
        // left out. Canonical labels come from the Item Page Blueprint on Help:Contents, not from what is most
        // common on existing pages — see the SV note below.
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
            ("End Regen", "END Regen"),
            // Title case, per the Item Page Blueprint on Help:Contents. **This reverses an earlier choice made on
            // frequency** — real pages write `SV FIRE` 31 times against `SV Fire` 3 — because the blueprint is the
            // template the user's routine complies with, and frequency only measures how many pages predate it.
            // Harmless to switch: the comparison finds a stat case-insensitively, so this changes only what gets
            // written, never whether an existing line counts as matching.
            ("SV. Fire", "SV Fire"),
            ("SV. Cold", "SV Cold"),
            ("SV. Magic", "SV Magic"),
            ("SV. Disease", "SV Disease"),
            ("SV. Poison", "SV Poison"),
            ("SV. Void", "SV Void"),
            ("Base Dmg", "DMG"),
            ("Dmg Bon", "DMG Bonus"),
            ("Backstab Dmg", "BACKSTAB"),
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

        // The game encodes this as a bare `Effect: Pet Illusion: <appearance> (Casting Time: N)` line, which
        // `ItemParser` normalizes to a plain field before anything here sees it — the odd spelling is the game's,
        // not the wiki's. The wiki records just the appearance; the casting time is not part of the value
        // (user, 2026-09-28). Only three items in the game have this today.
        map[Core.Items.ItemParser.PetIllusionLabel] =
            new StatMapping(Core.Items.ItemParser.PetIllusionLabel, "Pet Illusion", StatDisposition.Stored);

        // A real stat that happens to live in the window's trailing region, below the effects, alongside developer
        // help text that is *not* data (user, 2026-09-28: "this seems like a legitimate stat for the statsblock").
        // Values seen: `Fast`, `AA Speed or Highest`. One wiki page already writes it by hand.
        map["Mount Speed"] = new StatMapping("Mount Speed", "Mount Speed", StatDisposition.Stored);

        // **In the blueprint all along, and missing here because no capture had ever produced it** (user,
        // 2026-10-02, on `Collapsible Fishing Pole`): this file's own note listed `Skill Mod` among the labels the
        // blueprint has and the game had not been seen to write, so the tool reported "no mapping at all — possibly
        // new" for a field the template documents. The game does write it: `Skill Mod: Fishing 5 % (10 Max)`.
        //
        // The value needs a format rather than a rename, because the game's spelling and the wiki's differ in three
        // ways at once — see StatMapping.AsSkillModifier for the measurement and for what happens to the cap.
        map["Skill Mod"] = new StatMapping(
            "Skill Mod", "Skill Mod", StatDisposition.Stored, Format: StatValueFormat.SkillModifier);

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
                // Interim wording pending a community decision — see EffectKinds.
                ["Charge"] = "Charge Clicky",
                ["Consumable"] = "Consumable Clicky",
            },
            FocusEffectKinds = ["Focus"],
            StatsBlockLineOrder =
            [
                // The flags line is unlabelled, so it is named here by convention rather than by a wiki label.
                ["(flags)"],
                // `Type` sits with `Slot` and `Items` with `Size Capacity` by the user's decision (2026-09-25);
                // the blueprint omits both, and the user is having them added to the template's vocabulary.
                ["Slot", "Type"],
                ["Skill", "Atk Delay"],
                ["DMG", "DMG Bonus", "AC", "BACKSTAB", "Magic DMG", "Poison DMG"],
                ["Skill Mod"],
                ["STR", "DEX", "STA", "CHA", "WIS", "INT", "AGI", "HP", "MANA", "END"],
                ["SV Fire", "SV Disease", "SV Cold", "SV Magic", "SV Poison", "SV Void"],
                ["Attack", "HP Regen", "Mana Regen", "Haste", "Clairvoyance", "Spell Dmg", "Heal Amount", "END Regen"],
                // The blueprint's `Recommended level of ? Required level of ?` line was removed from it on
                // 2026-09-30, so it is gone from here too. Provably a no-op either way: the game has never produced
                // either label, and of 1,183 real pages exactly one mentions a required level at all — as the prose
                // `Required level of 55.`, which is not `Label: Value` and which the formatter declines to touch.
                ["Effect"],
                ["Charges"],
                ["Size", "WT"],
                ["Weight Reduction", "Capacity", "Size Capacity", "Items"],
                ["Class"],
                ["Race"],

                // **Below a blank line, at the very bottom: properties of something the item summons or affects,
                // not of the item itself** (user, 2026-09-28, after seeing them rendered). `Mount Speed: Fast`
                // describes the horse the bridle summons; `Pet Illusion: Murderbee` describes what your pet turns
                // into. Separating them says so, and it is also — now understood — why the devs put them below the
                // effects in the window rather than among the stats.
                [BlankLine],
                ["Mount Speed"],
                ["Pet Illusion"],
            ],
            ParameterOrder =
            [
                "itemname", "lucy_img_ID", "statsblock", "focus_effect", "merchant_value", "notes", "bookcontents",
                "dropsfrom", "soldby", "foraged", "playercrafted", "recipes", "relatedquests",
            ],
            BlockParameters =
            [
                "bookcontents", "dropsfrom", "soldby", "foraged", "playercrafted", "recipes", "relatedquests",
            ],
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
