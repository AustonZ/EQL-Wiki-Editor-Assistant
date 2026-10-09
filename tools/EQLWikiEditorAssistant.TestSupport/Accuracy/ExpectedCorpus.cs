using EQLWikiEditorAssistant.Core.Ocr;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLWikiEditorAssistant.TestSupport.Accuracy;

/// <summary>
/// Ground truth for the gitignored <c>samples/</c> corpus: what each screenshot's item windows *should* parse to.
///
/// **This file is tracked in git, so what may go in it is a hard rule, not a preference.** It holds only parsed
/// item-window fields — item names, flags, class/race lists, slots, stats, effects, merchant values. That is
/// public game data; it is literally the data this tool exists to publish to a public wiki. The private
/// information risk in <c>samples/</c> is everything *outside* an item window (character and player names, guild
/// tags, chat, zone), so:
/// <list type="bullet">
/// <item>Never serialize raw whole-frame OCR text, or any line the locator did not attribute to a window crop.
/// A convenient "dump everything the engine saw" mode is exactly how chat text ends up in a tracked file.</item>
/// <item>Never store pixel data or window coordinates.</item>
/// <item>Store warning <b>counts</b>, not verbatim warning text — warnings quote the offending OCR fragment, and
/// those strings are both in-scope-but-unnecessary and unstable across tuning, so they would churn every diff.</item>
/// </list>
/// </summary>
public sealed class ExpectedCorpus
{
    /// <summary>Placeholder written by the bootstrap wherever the parser flagged a field, so a human has to read
    /// the real value off the screenshot instead of the bootstrap silently freezing today's bug in as "correct".</summary>
    public const string TodoMarker = "?TODO";

    public const string FileNote =
        "Ground truth for the gitignored samples/ corpus. Item-window fields only - public game data, the same " +
        "data this tool publishes to the wiki. Never whole-frame OCR text, never anything outside a window crop, " +
        "no coordinates, and warning counts rather than verbatim warning strings. See ExpectedCorpus.cs.";

    public int Version { get; set; } = 1;
    public string Note { get; set; } = FileNote;
    public List<ExpectedSample> Samples { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },

        // Ground truth is hand-reviewed against screenshots, so it has to stay readable and diff cleanly. The
        // default encoder escapes '+', '\'' and other HTML-sensitive characters, turning "Accuracy: +2.5%" into
        // "\u002B2.5%" and "Kilva's" into "Kilva\u0027s" — which churns the whole file whenever it is
        // regenerated and makes a reviewer decode escapes to check a value. Nothing here is ever emitted into
        // HTML; it is a local file read back by this same tool.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ExpectedCorpus Load(string path) =>
        JsonSerializer.Deserialize<ExpectedCorpus>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} did not deserialize to an ExpectedCorpus.");

    public async Task SaveAsync(string path)
    {
        // Deterministic ordering so a re-bootstrap produces a reviewable diff rather than a reshuffle.
        Samples = [.. Samples.OrderBy(s => s.File, StringComparer.Ordinal)];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, Json));
    }

    public ExpectedSample? Find(string fileName) =>
        Samples.FirstOrDefault(s => string.Equals(s.File, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every field still awaiting a human reading. <c>--bootstrap</c> is not complete while any remain.</summary>
    [JsonIgnore]
    public int TodoCount => Samples.Sum(s => s.Windows.Sum(w => w.TodoCount));
}

public sealed class ExpectedSample
{
    public string File { get; set; } = "";

    /// <summary>True once a human has compared this entry against the screenshot. Unverified samples are scored
    /// but treated as advisory — they can't gate a build, because nobody has confirmed they're right.</summary>
    public bool Verified { get; set; }

    /// <summary>The UI font the screenshot was captured in. <b>Absent means Arial</b> — every sample captured before
    /// the user's custom font existed — so those entries never change; only a sample in another font names it.
    /// The reader is configured from this, and a sample whose pixels disagree is reported, because ground truth
    /// read in the wrong font would confuse I and l.</summary>
    public UiFont? Font { get; set; }

    [JsonIgnore]
    public UiFont FontOrArial => Font ?? UiFont.Arial;

    /// <summary>In the locator's own order (<c>OrderBy(Y).ThenBy(X)</c>), matched to actuals by index.</summary>
    public List<ExpectedWindow> Windows { get; set; } = [];
}

public sealed class ExpectedWindow
{
    /// <summary>Expected to be reported <c>PossiblyOccluded</c> and therefore not parsed at all.</summary>
    public bool Occluded { get; set; }

    public string? Name { get; set; }
    public int Level { get; set; }
    public bool TitleContentNameMismatch { get; set; }
    public List<string> Flags { get; set; } = [];
    public List<string> Classes { get; set; } = [];
    public List<string> Races { get; set; } = [];

    /// <summary>Every slot the item fits — "Primary Secondary" and "Range Ammo" are common, and rarer pairings
    /// exist. Empty for items with no slot at all.</summary>
    public List<string> Slots { get; set; } = [];
    public List<ExpectedField> Stats { get; set; } = [];
    public List<ExpectedExaltation> Exaltations { get; set; } = [];
    public List<ExpectedEffect> Effects { get; set; } = [];
    public string? MerchantValue { get; set; }

    /// <summary>Set only for a capture of the Lore tab, which is a different view with no stat block. Item lore
    /// is public game data and is one of the fields this tool writes to the wiki.</summary>
    public string? Lore { get; set; }

    /// <summary>Count only — see the type doc for why the text itself is deliberately not stored.</summary>
    public int WarningCount { get; set; }

    [JsonIgnore]
    public int TodoCount =>
        Stats.Count(s => s.Value == ExpectedCorpus.TodoMarker)
        + Effects.Sum(e => e.Modifiers.Count(m => m.Value == ExpectedCorpus.TodoMarker))
        + (Name == ExpectedCorpus.TodoMarker ? 1 : 0);
}

public sealed class ExpectedField
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ExpectedExaltation
{
    public string Kind { get; set; } = "";

    /// <summary>Null for an empty slot.</summary>
    public string? Name { get; set; }
}

public sealed class ExpectedEffect
{
    public string Kind { get; set; } = "";

    /// <summary>The effect/spell name alone — what the game draws in magenta, and what a wiki lookup keys on.</summary>
    public string Name { get; set; } = "";

    /// <summary>Parenthesised qualifiers such as "Must Equip" / "Can Equip". A "(Req Level N)" is normalized into
    /// <see cref="Modifiers"/> instead, so it matches the sub-line form other effect kinds use.</summary>
    public List<string> Conditions { get; set; } = [];

    public List<ExpectedField> Modifiers { get; set; } = [];
}
