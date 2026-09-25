using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLWikiAssistant.Wiki.Ledger;

/// <summary>
/// What a check concluded. **Only <see cref="Matched"/> and <see cref="Edited"/> mean "done"** — see
/// <see cref="CheckedItemsLedger"/> for why the others must never let a capture skip the wiki.
/// </summary>
public enum CheckOutcome
{
    /// <summary>The page already said what the capture says. Nothing was written.</summary>
    Matched,

    /// <summary>The page was brought into line with the capture.</summary>
    Edited,

    /// <summary>Something needs a human — a suspected wrong icon, an unreadable field, a page defect the tool will
    /// not fix. **Never treated as done**, however old the row is.</summary>
    Flagged,

    /// <summary>The user chose not to act on it this time. Also never treated as done.</summary>
    Skipped,

    /// <summary>The item has no wiki page. Not done either: somebody may have created one since.</summary>
    NotOnWiki,
}

/// <summary>
/// One item's last check.
///
/// <see cref="WikiPageTitle"/> is recorded separately from <see cref="ItemName"/> because the two legitimately
/// differ: an item whose in-game name cannot be a MediaWiki title (`Cell Key #5`) lives at a name a human chose
/// (`Cell Key No. 5`), and without recording that, every future capture would look unhandled and send the user
/// hunting for a page that does not exist under the name they were given.
/// </summary>
public sealed record LedgerEntry
{
    /// <summary>The item's in-game name with any `+X` suffix stripped — the key, alongside <see cref="EntityKind"/>.</summary>
    public required string ItemName { get; init; }

    /// <summary>`item` today; the plan's `IEntityKind` extension point means spells and monsters will share this
    /// store, and a spell could share a name with an item.</summary>
    public required string EntityKind { get; init; }

    /// <summary>The page actually checked, when it is not simply <see cref="ItemName"/>.</summary>
    public string? WikiPageTitle { get; init; }

    public required CheckOutcome Outcome { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>The revision seen or written, so a later check can tell whether anyone else has edited since.</summary>
    public long? WikiRevisionId { get; init; }

    /// <summary>Hash of the captured item data — see `Core.Items.ItemFingerprint`.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The mapping version the check ran under. A mapping change can change what the tool would write, so
    /// it invalidates the row.</summary>
    public required int MappingVersion { get; init; }

    public string? Note { get; init; }
}

/// <summary>Why a capture does or does not need the wiki.</summary>
public enum LedgerVerdict
{
    /// <summary>No row: never checked, or checked and deliberately not recorded (an ineligible item).</summary>
    NotChecked,

    /// <summary>Checked, unchanged, and settled. The wiki fetch can be skipped entirely.</summary>
    AlreadyDone,

    /// <summary>Checked, but the outcome was never "done" — flagged, skipped, or not on the wiki.</summary>
    Unresolved,

    /// <summary>Checked and done, but the captured data has changed since — a game patch, or a different copy.</summary>
    DataChanged,

    /// <summary>Checked and done, but under an older mapping, so what the tool would write may differ now.</summary>
    MappingChanged,

    /// <summary>Checked and done, but longer ago than the configured maximum age.</summary>
    Stale,
}

/// <summary>
/// The local record of which items have been checked, so an unchanged item costs no wiki traffic.
///
/// **The point is to avoid network calls, so the check must happen before any of them** — `Consult` takes only the
/// captured fingerprint and answers from disk. A design where the ledger were consulted after fetching would be
/// pointless.
///
/// **An ineligible item must never reach this store at all.** A foreign exaltation or a levelled item writes *no
/// row* — not `Skipped`, not `Flagged` (see `Core.Items.ItemEligibility`). It was never actually checked, and any
/// row would make it look handled and quietly exclude it from ever being checked properly.
///
/// Stored as JSON rather than SQLite: a few thousand rows of small records, written whole and rarely, with no
/// queries beyond a keyed lookup. A database would be machinery without a question to answer.
/// </summary>
public sealed class CheckedItemsLedger
{
    /// <summary>The kind key items use. Spells, monsters and quests get their own when they arrive.</summary>
    public const string ItemKind = "item";

    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public CheckedItemsLedger(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>How long a settled row stays trusted. Null — the default — means forever, because a checked item
    /// does not drift on its own; the fingerprint and mapping version are what actually invalidate a row.</summary>
    public TimeSpan? MaximumAge { get; set; }

    public int Count => _entries.Count;

    public IEnumerable<LedgerEntry> Entries => _entries.Values;

    /// <summary>
    /// Whether this capture needs the wiki, and why. **Consult this before fetching anything.**
    /// </summary>
    /// <param name="reCheckAnyway">The user's "re-check anyway" override, which forces a fetch whatever the row
    /// says without the ledger having to be edited or cleared.</param>
    public LedgerVerdict Consult(
        string itemName,
        string fingerprint,
        int mappingVersion,
        string entityKind = ItemKind,
        bool reCheckAnyway = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        LedgerEntry? entry = Find(itemName, entityKind);
        if (entry is null) return LedgerVerdict.NotChecked;
        if (reCheckAnyway) return LedgerVerdict.DataChanged;

        // Flagged, Skipped and NotOnWiki are outcomes that left something undone, so they never skip the wiki
        // however recent they are. Checking this before the fingerprint matters: a flagged item whose data has not
        // changed is still flagged.
        if (entry.Outcome is not (CheckOutcome.Matched or CheckOutcome.Edited)) return LedgerVerdict.Unresolved;

        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal)) return LedgerVerdict.DataChanged;
        if (entry.MappingVersion != mappingVersion) return LedgerVerdict.MappingChanged;
        if (MaximumAge is { } age && _time.GetUtcNow() - entry.CheckedAt > age) return LedgerVerdict.Stale;

        return LedgerVerdict.AlreadyDone;
    }

    /// <summary>Convenience for the common question. See <see cref="Consult"/> for the reason behind the answer.</summary>
    public bool CanSkipWikiFetch(string itemName, string fingerprint, int mappingVersion, string entityKind = ItemKind) =>
        Consult(itemName, fingerprint, mappingVersion, entityKind) == LedgerVerdict.AlreadyDone;

    public LedgerEntry? Find(string itemName, string entityKind = ItemKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
        return _entries.GetValueOrDefault(KeyFor(itemName, entityKind));
    }

    /// <summary>Records a check, replacing any earlier row for the same item.</summary>
    public void Record(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[KeyFor(entry.ItemName, entry.EntityKind)] = entry;
    }

    /// <summary>Forgets an item, so the next capture treats it as new. The "re-check" action's permanent form, and
    /// what an ineligible item needs if a row was somehow written for it.</summary>
    public bool Remove(string itemName, string entityKind = ItemKind) =>
        _entries.Remove(KeyFor(itemName, entityKind));

    public void Clear() => _entries.Clear();

    /// <summary>Keys are case-insensitive because the same item captured twice must key the same way, and OCR is
    /// not the only thing that varies case — the wiki's own pages disagree about it too.</summary>
    private static string KeyFor(string itemName, string entityKind) => $"{entityKind}\u0000{itemName.Trim()}";

    public static CheckedItemsLedger Load(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ledger = new CheckedItemsLedger(time);
        if (!File.Exists(path)) return ledger;

        LedgerFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            // A corrupt ledger costs re-checks, not correctness — every row is reconstructible by capturing the
            // item again. Losing it is strictly better than refusing to start.
            return ledger;
        }

        foreach (LedgerEntry entry in file?.Entries ?? []) ledger.Record(entry);
        return ledger;
    }

    /// <summary>Writes the ledger, via a temporary file so an interrupted save cannot leave a truncated one.</summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        string temporary = full + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(new LedgerFile { Entries = [.. _entries.Values] }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        File.Move(temporary, full, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class LedgerFile
    {
        public List<LedgerEntry> Entries { get; set; } = [];
    }
}
