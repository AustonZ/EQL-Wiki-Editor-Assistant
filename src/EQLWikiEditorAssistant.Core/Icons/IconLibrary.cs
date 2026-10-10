
namespace EQLWikiEditorAssistant.Core.Icons;

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
/// signature is reused deliberately, because a second fingerprint would be a second home for a measured rule. Since
/// 2026-10-10 the library also decides the check itself: a page's icon matches when it is as close to the capture as
/// the best icon here — see <see cref="SameArtworkMargin"/>.
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
    /// **A margin, and deliberately *not* an absolute distance.** An absolute cutoff loses correct answers: the game
    /// draws each icon 1.1x and interpolates, which moves the absolute score far more than it moves the ranking, so the
    /// gap to the runner-up is the signal worth gating on.
    ///
    /// Measured with the whole-cell fingerprint (2026-10-10, `WikiSpike iconsearch`): 101 distinct captured items,
    /// each against all 11,592 icons, the item's own wiki page as ground truth. **Top-1 is 100/101**, from 87/90 on the
    /// old ink-box fingerprint. The one miss is `Puppet Strings`, which the game draws unlike its file, at a margin of
    /// 0.0066.
    ///
    /// <code>
    /// margin    accepted   wrong accepted   correct rejected
    ///  0.006       100            1                1
    ///  0.008        99            0                1
    ///  0.010        98            0                2
    ///  0.030        95            0                5
    /// </code>
    ///
    /// 0.01 sits 1.5x above the one wrong answer; the two correct answers below it (`Bloodstar Pendant` 0.0054,
    /// `Mote of Grand Potential` 0.0080) become a shortlist for the user rather than a silent guess. What makes the
    /// residual risk acceptable is that **the review screen shows the matched artwork beside the captured one**, so a
    /// wrong match is a glance rather than a silent edit.
    /// </summary>
    public const double ConfidentMargin = 0.01;

    /// <summary>
    /// How much further a page's icon may be from the capture than the best icon in the whole library, and still be
    /// the same artwork (user, 2026-10-10).
    ///
    /// **Relative to the best match, not an absolute distance**, because the library already ranks correctly and an
    /// absolute cutoff cannot: two recoloured twins of one drawing (icons 590 and 603, both swords) sit closer to
    /// each other than some genuine pairs sit to themselves, while against the best match each twin is clearly the
    /// other's runner-up. And the game's own rendering quirks — the 1.1x interpolation, a grey outline it adds
    /// around dark sprites — penalise every candidate alike, so they cancel.
    ///
    /// Measured on the 16x16 grid over 96 captured items against 87 wiki icons: the worst genuine pair is 0.016 over
    /// the best, the closest different icon 0.043 over. The pairs above it are not false alarms: on four the page's
    /// id is right and the wiki's *file* for it is different artwork from the game's (`Obtenebrate Mithril Guard`,
    /// `Black Chain Bridle`, `Cloak of Scales`, `Void-Touched Potential`), and on `Puppet Strings` the game draws
    /// something unlike either file.
    /// </summary>
    public const double SameArtworkMargin = 0.03;

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

    /// <summary>How far <paramref name="captured"/> is from the library's icon <paramref name="iconId"/>, or null when
    /// the library has no such icon or the capture is not worth comparing.</summary>
    public double? DistanceTo(string iconId, IconFingerprint captured)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (!captured.IsComparable) return null;
        LibraryIcon? icon = _icons.FirstOrDefault(i => string.Equals(i.IconId, iconId, StringComparison.Ordinal));
        return icon is null || icon.Fingerprint.Signature.Length != captured.Signature.Length
            ? null
            : captured.CorrelationDistanceTo(icon.Fingerprint);
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
