using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Icons;

/// <summary>One library icon's identity and fingerprint.</summary>
/// <param name="IconId">The icon's id, which is its filename in the extracted asset folder — and therefore the
/// value an item page's <c>lucy_img_ID</c> should carry.</param>
public sealed record LibraryIcon(string IconId, IconFingerprint Fingerprint);

/// <summary>A candidate answer to "which icon is this?", with how close it was and how clearly it won.</summary>
/// <param name="Distance">Correlation distance to the captured icon: 0 is identical, larger is worse.</param>
public sealed record IconMatch(string IconId, double Distance);

/// <summary>
/// Every item icon the game ships, fingerprinted, so a captured icon can be identified rather than merely checked.
///
/// **This inverts what the icon code was for.** <see cref="ItemIconReader"/> and <see cref="IconFingerprint"/> were
/// built to answer "does this page point at the right artwork?" — one capture against one known file. Here the
/// question is "which of 11,592 icons is this?", which is a harder question in a way worth stating: a comparison only
/// has to separate one right answer from one wrong one, while a search has to beat *every* wrong one. The same
/// signature is reused deliberately, because a second fingerprint would be a second home for a measured rule, but
/// the thresholds are its own — see <see cref="ConfidentMargin"/>.
///
/// **The ids are the game's own filenames**, which is what makes this usable at all: the extracted asset folder names
/// each icon by its id, so identifying the artwork identifies the id. Nothing here derives an id from the wiki, and
/// that matters — 2 of the 76 cached wiki icon files hold artwork the library files under a *different* id, so the
/// wiki is not a reliable authority on this mapping (see CLAUDE.md).
/// </summary>
public sealed class IconLibrary
{
    private readonly LibraryIcon[] _icons;

    public IconLibrary(IEnumerable<LibraryIcon> icons)
    {
        ArgumentNullException.ThrowIfNull(icons);
        _icons = [.. icons];
    }

    public int Count => _icons.Length;

    public IReadOnlyList<LibraryIcon> Icons => _icons;

    /// <summary>
    /// How much closer the best match must be than the runner-up before the id is written into the wikitext without
    /// a human choosing it.
    ///
    /// **A margin, and deliberately *not* an absolute distance — which was measured rather than reasoned, and the
    /// measurement went against the obvious choice.** `WikiSpike iconsearch` over 90 distinct captured items, each
    /// against all 11,562 indexed icons, with the item's own live wiki page as ground truth: top-1 is 87/90 (96.7%),
    /// nothing refused. Gating on <see cref="IconFingerprint.SameIconThreshold"/> (0.13) as well *loses* 5 correct
    /// answers and catches nothing the margin does not — a correct match reaches 0.2977 (`Cloak of Scales`), because
    /// the game draws each icon about 1.10x its stored size and interpolates, which moves the absolute score far more
    /// than it moves the ranking. So the second-place gap is the only signal worth gating on.
    ///
    /// Margin sweep, counting `Puppet Strings` as the one genuinely wrong answer (see below):
    ///
    /// <code>
    /// margin    accepted   wrong accepted   correct rejected
    ///  0.0020        88            0                1
    ///  0.0075        87            0                2
    ///  0.0100        86            0                2
    ///  0.0200        85            0                3
    ///  0.0300        82            0                6
    /// </code>
    ///
    /// 0.01 sits in the middle of the flat part: 8x above the one genuine error (`Puppet Strings`, margin 0.0013,
    /// whose best distance is 0.3754 — nothing in the library resembles it) and 2x below the closest correct answer
    /// it gives up (`Bloodstar Pendant`, 0.0050).
    ///
    /// **No threshold separates the two classes perfectly, and that is worth knowing rather than hiding**: the same
    /// shape of result as the icon check's own threshold. `Cloak of Scales` is a *correct* answer at a margin of
    /// 0.0002 — below the genuine error — so it is a coin flip this tool happened to win, and it is excluded here.
    /// Which is the right outcome: it becomes a shortlist for the user instead of a silent guess.
    ///
    /// What makes the residual risk acceptable is the same thing that made the icon check's thresholds acceptable —
    /// **the review screen shows the matched artwork beside the captured one**, so a wrong match is a glance rather
    /// than a silent edit.
    /// </summary>
    public const double ConfidentMargin = 0.01;

    /// <summary>
    /// The closest icons to <paramref name="captured"/>, nearest first.
    ///
    /// Returns an empty list for a fingerprint that is not worth comparing, rather than a confident wrong answer —
    /// the same refusal <see cref="ItemIconReader"/> makes, for the same reason. A near-uniform signature correlates
    /// with almost anything, and against 11,592 candidates "almost anything" will always contain a winner.
    /// </summary>
    public IReadOnlyList<IconMatch> Search(IconFingerprint captured, int take = 5)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        if (!captured.IsComparable) return [];

        var scored = new List<IconMatch>(_icons.Length);
        foreach (LibraryIcon icon in _icons)
        {
            if (icon.Fingerprint.Signature.Length != captured.Signature.Length) continue;
            scored.Add(new IconMatch(icon.IconId, captured.CorrelationDistanceTo(icon.Fingerprint)));
        }

        scored.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return scored.Count <= take ? scored : scored[..take];
    }

    /// <summary>
    /// Identifies the captured icon, or returns null when nothing can be said confidently.
    ///
    /// **Null is a normal and useful outcome**, not a failure to engineer away. A generated page with no
    /// <c>lucy_img_ID</c> is an honest gap the ledger keeps bringing back; a generated page with the *wrong* icon id
    /// is a silently-wrong edit to a public wiki, and worse, an invitation to upload the wrong artwork under a name
    /// nobody here can delete. The whole project prefers the flagged gap.
    /// </summary>
    public IconIdentification? Identify(IconFingerprint captured, int candidates = 5)
    {
        IReadOnlyList<IconMatch> matches = Search(captured, Math.Max(2, candidates));
        if (matches.Count == 0) return null;

        IconMatch best = matches[0];
        // The runner-up has to be a *different artwork*, not another id carrying the same picture: the library holds
        // 4 groups of byte-identical duplicates (15 ids in all), and for those the margin is legitimately zero. A
        // duplicate stealing the margin would make exactly those icons permanently unidentifiable.
        IconMatch? runnerUp = matches.Skip(1).FirstOrDefault(m => m.Distance > best.Distance);
        double margin = runnerUp is null ? double.PositiveInfinity : runnerUp.Distance - best.Distance;

        return new IconIdentification(best.IconId, best.Distance, margin, matches);
    }
}

/// <summary>What the search concluded about one captured icon.</summary>
/// <param name="Margin">How far behind the best match the nearest *different* artwork is. Infinite when every
/// candidate examined is a duplicate of the winner.</param>
/// <param name="Candidates">The ranked shortlist, so the UI can show what else it considered — the point of which is
/// that a human confirms the answer by eye rather than trusting the number.</param>
public sealed record IconIdentification(
    string IconId,
    double Distance,
    double Margin,
    IReadOnlyList<IconMatch> Candidates)
{
    /// <summary>Whether the winner is clear enough to put into the wikitext without a human choosing first.</summary>
    public bool IsConfident => Margin >= IconLibrary.ConfidentMargin;
}
