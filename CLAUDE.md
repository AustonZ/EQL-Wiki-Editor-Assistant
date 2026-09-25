# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows desktop tool (C# / .NET, WPF) that helps maintain item pages on the EverQuest Legends Wiki
(eqlwiki.com). The user plays EverQuest Legends and cross-references items against wiki pages by hand; this tool
automates the repetitive parts: capture the in-game item window via a hotkey, OCR it locally, fetch and parse the
corresponding wiki page, diff the two, and let the user approve/tweak/commit the edit.

Iteration 1 covers **items only**. The design is meant to extend to spells, monsters, and quests later via an
`IEntityKind` abstraction — don't hardcode item-specific assumptions into the shared pipeline (capture, OCR
plumbing, wiki client, review UI).

The full design rationale, wiki research findings, and milestone plan live in
[docs/PLAN.md](docs/PLAN.md) — read it for background on *why*, not just *what*.

## Hard constraints (non-negotiable)

- **No network traffic interception, no game memory access.** Both violate the game's EULA. The only inputs are
  local screenshots (via Windows Graphics Capture) and the public MediaWiki API.
- **Screenshots and OCR never leave the local machine.** No cloud OCR/vision APIs, ever. Only wikitext and wiki
  images are fetched over the network (inbound), and only user-approved edits are sent (outbound).
- **Never commit real screenshots.** They may contain private info (character/player names, chat). They belong in
  `samples/`, which is gitignored except for `samples/README.md`.
- **Wiki data is untrusted and often malformed.** Item pages are human-edited and partly imported from the old
  Project1999 wiki fork: expect wrong template usage, typos, missing/extra params, inconsistent casing. Wikitext
  parsing must degrade gracefully (flag "couldn't parse this part" rather than throw or silently corrupt), and
  generated edits must be minimal, surgical patches — untouched parts of a page must survive byte-for-byte.

## Solution layout

- `src/EQLWikiAssistant.Core` (`net10.0`, no Windows APIs) — wiki-agnostic domain models (`Item` etc.), shared
  pipeline abstractions (`IEntityKind` and friends), the portable ports `IOcrEngine` + `CapturedImage`/`Rect`/
  `OcrLine`/`OcrWord` and `FieldLabelLexicon` (`Core.Ocr`), **`ItemWindowLocator`/`WindowBoundsFinder`**
  (`Core.Locate` — finds each item window's real pixel bounds in a full screenshot by tracing its border, not by
  clustering text; see "Locating item windows" below), **`ItemParser`/`ParsedItem`** (`Core.Items` — turns a
  located window's OCR lines into item data; see "Parsing item windows" below), and `EditDistance` (`Core.Text` —
  fuzzy string matching, used by locate, the field-label lexicon, and the exaltation name/title-reconciliation
  checks). Anything here must stay portable and free of MediaWiki syntax knowledge — see "Wiki mapping layer"
  below. Interfaces the pipeline depends on live here even though their real implementations are Windows-only,
  since `Core` can't reference the Windows-only projects.
- `src/EQLWikiAssistant.Capture` (`net10.0-windows10.0.19041.0`) — `GlobalHotKey` (Win32 `RegisterHotKey`, its own
  message-only window/thread, no UI-framework dependency), `WindowFinder` (find a window by title), `WindowCapturer`
  (Windows Graphics Capture of a specific window, via `Vortice.Direct3D11`/`Vortice.DXGI` for the D3D11 device).
  **Read the `[GeneratedComInterface]` note below before touching `Interop/`** — it documents a real, confirmed
  runtime failure mode, not a style preference.
- `src/EQLWikiAssistant.Ocr` (`net10.0-windows10.0.19041.0`) — the general-OCR `IOcrEngine`: **`RapidOcrEngine`
  wrapping `RapidOcrNet`** (PaddleOCR PP-OCRv5 via ONNX, local/offline). See "OCR engine choice" below — this
  wasn't arbitrary: the OS-provided `Windows.Media.Ocr` was tried first and replaced after real testing showed it
  meaningfully less accurate, then removed outright once it had no remaining use.
- `src/EQLWikiAssistant.Wiki` (`net10.0`) — **`MediaWikiClient`/`IMediaWikiClient`** (`Wiki.MediaWiki` — bot-password
  auth, read-only fetch, conflict-guarded edit), **`ICredentialStore`/`WindowsCredentialStore`** (same namespace —
  Windows Credential Manager), **`WikitextScanner`/`TemplateCall`/`ItemPageDocument`/`StatsBlock`**
  (`Wiki.Wikitext` — locating a template's parameters by exact source span and editing one surgically; see
  "Reading and editing wiki pages" below), the local icon file cache, and the checked-items ledger.
  `WindowsCredentialStore` is the one Windows-only type in this otherwise portable assembly. That is deliberate:
  it is plain Win32 P/Invoke (`advapi32`), which needs no WinRT projection and so no versioned TFM, unlike the
  `Capture`/`Ocr` projects. It uses `[DllImport]` rather than `[LibraryImport]` because the latter's generator
  emits unsafe code, and enabling `<AllowUnsafeBlocks>` across a domain assembly for four P/Invokes is the worse
  trade — unrelated to the `[GeneratedComInterface]` rule below, which is about CsWinRT COM objects.
- `src/EQLWikiAssistant.App` (`net10.0-windows10.0.19041.0`, WPF) — UI: capture trigger, review/diff screen,
  settings/mapping editor, ledger view.
- `tests/EQLWikiAssistant.Tests` (`net10.0-windows10.0.19041.0`) — unit and golden-file tests across all projects.
- `tools/EQLWikiAssistant.TestSupport`, `tools/OcrSpike`, `tools/CaptureSpike`, `tools/LocateSpike`,
  `tools/ParseSpike` (`net10.0-windows10.0.19041.0`, dev-only, not shipped) — `TestSupport.ImageFile` loads a
  screenshot file from disk into a `CapturedImage` (the real app only ever captures a live window, never reads a
  file — this exists for tests/tooling), `TestSupport.RepoPaths` for finding `samples/` reliably from a
  test/tool's output directory, and `TestSupport.DebugDraw` for drawing debug rectangle overlays; `OcrSpike`
  iterates on OCR accuracy against real sample screenshots (`--crop`, `--scale`, `--save`, dump recognized
  lines+bounding boxes); `CaptureSpike` exercises `WindowFinder`/`WindowCapturer`/`GlobalHotKey` (list
  windows, capture one to a PNG, test-fire a hotkey); `LocateSpike` runs whole-screenshot OCR + `ItemWindowLocator`
  and can `--save` a debug overlay (green/red by `PossiblyOccluded`) for eyeballing results, or
  `--probe x,y,dx,dy,count` to dump raw pixel RGB along a ray — that's how the window-chrome colour profile in
  "Locating item windows" was measured, so use it rather than guessing before changing anything in
  `WindowBoundsFinder`; `ParseSpike` runs the full Locate -> Parse pipeline and dumps every parsed field per
  window; `AccuracySpike` scores that pipeline against tracked ground truth (see "Measuring extraction accuracy");
  `GlyphSpike` measures raw pixels, builds/verifies the UI font atlas and reads a region through the real glyph
  engine (see "Glyph matching"); `WikiSpike` is the wiki-side equivalent — `fetch` dumps a real page and its
  parsed fields, `roundtrip`/`grammar` validate the wikitext layer against a live sample of item pages (`grammar`
  additionally prints the label and flag census that is the only thing which catches a *wrong* split, see
  "Reading and editing wiki pages"), and `login`/`whoami`/`logout`/`edit` exercise the credential and write paths.
  `ParseSpike` and `AccuracySpike` both take `--rapid` to run the superseded RapidOCR-everywhere configuration for
  comparison.
  Keep using these — don't recreate ad hoc versions — when tuning parse logic or debugging capture/locate.
  **Note**: any executable project that uses `RapidOcrEngine` needs its own direct `PackageReference` to
  `RapidOcrNet`, not just a transitive one via `EQLWikiAssistant.Ocr` — the package's bundled `.onnx` model files
  only reliably copy to an executable's own output directory that way (confirmed the hard way: `tools/OcrSpike`
  failed at runtime with a missing-model-file error until given its own direct reference). `EQLWikiAssistant.App`
  will need the same treatment in milestone 2/5 — verify with a real run.

Note: WinRT namespaces like `Windows.Media.Ocr` and `Windows.Graphics.Capture` are only projected on a Windows-SDK-
versioned TFM (`net10.0-windows10.0.19041.0`), not plain `net10.0-windows` — every project that touches them must
use the versioned form.

## Key architectural ideas

**`[GeneratedComInterface]`, never `[ComImport]`, for the Windows Graphics Capture interop.**
`src/EQLWikiAssistant.Capture/Interop/` declares two hand-written COM interfaces
(`IGraphicsCaptureItemInterop`, `IDirect3DDxgiInterfaceAccess`) that aren't exposed by the plain WinRT projection.
Declaring these with the classic `[ComImport]` attribute **compiles fine but throws `InvalidCastException`
("Specified cast is not valid") at runtime** the moment you call through them on an object obtained from a CsWinRT
`ComWrappers`-based type (e.g. anything from `SomeWinRtType.As<T>()`) — confirmed by testing against the live game
window, not a hypothetical. Use `[GeneratedComInterface]` (`System.Runtime.InteropServices.Marshalling`,
.NET 8+ source-generated COM interop) instead; it's compatible. Two more traps in the same area: `IGraphicsCaptureItem`'s
IID must be the literal `79C3F95B-31F7-4EC2-A464-632EF5D30760` — `typeof(GraphicsCaptureItem).GUID` is a different,
wrong value and fails the same way (`E_NOINTERFACE` → `InvalidCastException`, easy to conflate with the first
issue); and `Direct3D11CaptureFramePool.Create(...)` silently never raises `FrameArrived` without a `DispatcherQueue`
pumped on the calling thread — use `CreateFreeThreaded(...)` instead (fine on this app's Windows 11 target).

**Formatting is somebody else's edit — never ours** (user, 2026-09-24). The user wants a separate wiki-source
prettifier eventually, possibly launched from this tool but always as its own edit: *"A single 'automatically
reformatted' edit with no actual data changes is much easier to work with when reviewing diff history."* Two
standing design consequences, both of which the current wikitext layer already satisfies:
- **This tool never reformats incidentally.** No whitespace normalization, no re-rendering a block to tidy it, no
  "while we're here" fixes. That is why the raw source is the source of truth (see "Reading and editing wiki
  pages"), and it is a constraint to preserve, not an implementation detail that happened to fall out.
- **Non-compliance is reported, not fixed.** The eventual flow flags "this page needs reformatting" in the UI and
  the user runs the prettifier separately (expected to be the same component in a `check` mode). So parsing needs
  to be able to *describe* what is off without changing it — which `StatsBlockLine` already does by keeping both
  the verbatim text and the parse. Out of scope for now; don't build it, don't design against it.

**The `statsblock`-to-real-template migration is expected, and both shapes must work** (user, 2026-09-24). The
user intends to make a case to the other editors for promoting most of `statsblock`'s contents to explicit
`Itempage` parameters. Until that lands — and afterwards too, unless a programmatic bulk pass converts every
existing page — the tool has to read the legacy free-text form. Design implication for the mapping layer
(milestone 6): a field's *location* is part of the mapping, not a constant. "AC lives in the statsblock" and "AC
is `|ac=`" must both be expressible, so a wiki-side migration is a config change rather than a rewrite. Don't bake
"stats come from statsblock" into anything above the mapping.

**Template compliance is part of every edit** (user, 2026-09-24): *"one of my steps is always to ensure the item
complies with the current official template."* So the edit the tool proposes is not only a data diff — it also
brings the page in line with the current template. Confirmed examples: duplicate parameters get cleaned up (see
"Reading and editing wiki pages" for the two real pages with two `|notes=`), and legacy flags are always discarded
(see below). This is milestone 4 work and is distinct from the prettifier above: compliance changes *what the
page says*, formatting changes only how it reads.

**Wiki mapping layer.** The wiki's templates and conventions are expected to keep changing (it's a young wiki for
a young game), so `Core` domain models must never encode MediaWiki syntax directly. A "wiki mapping" — versioned
JSON stored in app-data, with built-in defaults — translates between the internal `Item` model and wikitext:
template/param names, the `statsblock` line grammar (order, labels, flag spellings), lore wrapper syntax
(`{{Item Lore|...}}`), placeholder templates to strip (`{{Item Lore Missing}}` is always removed when the tool
touches a page), and class/slot/skill → category name rules. A wiki-side convention change should mean editing this
mapping, not shipping new code. There's a Settings window in the app for viewing/editing it.

**`statsblock` is free text, not template params.** Most item data (flags, slot, stats, resists, effects, class/race
restrictions, etc.) lives in the `Itempage` template's `statsblock` parameter as `<br>`-joined lines
(`STR: +8  WIS: +8<br>`), not as separate template arguments. Diffing means parsing this block line-by-line into
fields and comparing those — not comparing template parameters directly. **Note the plan said "and re-rendering
it", and that half is wrong** — see "Reading and editing wiki pages" below for what measuring against real pages
showed instead.

**Reading and editing wiki pages (`Wiki.Wikitext`).** Measured against 662 real item pages fetched from
eqlwiki.com (2026-09-24) in two independent samples; `tools/WikiSpike -- grammar <n>` reproduces the measurement.
- **The raw source is the source of truth; the parse exists only to compare.** `TemplateParameter` records each
  value's exact byte span and an edit splices into it, so everything else in the page is preserved by construction
  rather than by care. `StatsBlockLine` likewise keeps its verbatim source and renders by concatenation, so a
  round trip is exactly identity. **This replaces the plan's "parse and re-render the block"**, which cannot meet
  the repo's byte-for-byte constraint: real pages agree on the line grammar and disagree on nearly every
  whitespace decision inside it (alignment padding, single vs double spaces between stats, `AC: 15 <br>` with a
  stray space, blank lines mid-block), so re-rendering rewrites lines whose data never changed and turns a
  one-value correction into an unreviewable whole-page diff. Verified as a test on every fixture and measured at
  0 failures across all 662 live pages.
- **Duplicate parameters are real, and the *last* one wins.** Two sampled pages carry two `|notes=`, an empty one
  near the top and the real one further down; MediaWiki renders the last. Reading the first — which the code did
  at first — meant reading a blank value off a page that visibly has content, and would then have "corrected" it.
  `ItemPageDocument` still warns, because a duplicate is a page defect worth a human's attention.
- **The wiki speaks two flag dialects, and the legacy one is discarded rather than translated** (user, 2026-09-24:
  "EQL completely redid flags, so legacy flags should always be discarded"). Imported Project1999-era pages write
  `MAGIC ITEM  LORE ITEM  NO DROP`; the game writes its own, current set. Discarding is not just the user's
  preference, it is the only safe rule, because no faithful mapping exists: `MAGIC ITEM`, `TEMPORARY` and
  `EXPENDABLE` have no modern counterpart at all, and classic `LORE ITEM` (carry only one) is a different property
  from `Lore Equipped` (equip only one), so "translating" one would invent an equivalence. The flags line is
  regenerated from the captured window, not reconciled with what the page had.
  - **The flag vocabulary is open-ended, and flags are copied through blindly — do not build a known-flags list**
    (user, 2026-09-24). Whatever the game displays is exactly what the wiki should say, *whether or not the tool
    knows what it means*. The 101 verified windows happen to contain only `No Trade`, `Lore Equipped`,
    `Placeable`, `Quest`, `Attunable`, `No Destroy` and `No Storage`, but that is a sample, not the vocabulary:
    the user has separately encountered `No Pet`, `Heirloom` and `Free Storage` on rare items, the devs keep
    adding more, and there is no way to enumerate the rest. So a flag the tool has never seen is **not** a warning
    and **not** an unparsed field — it is ordinary data. Validating against a list would reject exactly the rare
    items most worth recording. (This is also why glyph matching is the right reader here: it reads characters,
    not words, so it has no vocabulary to be surprised by.)
  - This retires an earlier note here that called flags "a translation problem, bigger than the plan assumed". It
    is the opposite: replace-wholesale is simpler, and it makes the nine sampled pages whose legacy flags are
    single-spaced (`MAGIC ITEM LORE ITEM NO TRADE`, which no separator rule can split without a vocabulary) a
    non-issue — the grammar reports them as one unrecognized token, and the tool discards the line regardless.
  - **Attunable beats No Trade: trust the wiki, and alert** (user, 2026-09-24). An item natively `Attunable` shows
    `No Trade` in the window once equipped, so the capture genuinely cannot distinguish it from a natively
    `No Trade` item. When the page says `Attunable` and the capture says `No Trade`, **keep `Attunable`** and tell
    the user — do not let blind regeneration silently downgrade a correct page. The user may later ask for this
    exact pair to be treated as *matching* (i.e. stop alerting), probably as a setting, so keep the comparison and
    the alert separable rather than hard-coding one behaviour.
  - **`This is a meal!` and friends: alert, never act** (user, 2026-09-24). These appear on the flags line of 11
    sampled pages and in no captured window — EQL removed them. They are not meaningless: in original EverQuest
    `This is a hearty meal!` meant the food lasted longer, and that may still be true under the hood with the UI
    simply no longer exposing it. The user's own practice is to move the text into `notes` by hand to preserve the
    history. **The tool must not do that move itself and must not silently drop the line** — it raises it in the
    UI and leaves it to the human.
- **A label is at most two words, and digits end it.** Both bounds came from a failing measurement, not from
  reasoning. Allowing digits let a backward label scan run through a preceding value, so
  `STR: +10 WIS: +10 INT: +10` (single-spaced, real) read as one field. Allowing three words then made every
  single-spaced `Skill: Archery Atk Delay: 0` read as a label "Archery Atk Delay"; at two words no mis-split
  survived and no real label was lost. One line needs a second rule — `Size: MEDIUM WT: 3.0` splits wrongly into
  a two-word label, and what gives it away is that doing so leaves `Size` with an *empty* value, which no real
  page has.
- **"Zero unparsed lines" is not evidence the split is right.** A wrong boundary still produces a field, just one
  with a nonsense label, so the unparsed count read zero while `MEDIUM WT` and `Blunt Atk Delay` were being
  produced. What caught them is the label census (`WikiSpike grammar`, which prints every distinct label and flag
  token it produced with an example) — the wikitext equivalent of the OCR corpus's silent-wrong count, and worth
  reaching for the same way: eyeball the vocabulary, not the error count.
- **Pages that are genuinely broken stay broken.** One page writes `SV FIRE +5 SV COLD +5` with no colons at all;
  it is reported as one unrecognized token rather than repaired into fields. Same principle as the OCR side: a
  flagged gap beats a silent guess.
- Parsing is tolerant by contract — unterminated braces, a stray `}}`, an unclosed `[[` and a missing parameter
  all yield "couldn't read this", never an exception and never a confidently wrong span (a wrong span is an edit
  spliced into somebody else's prose). `ItemPageDocument.Parse` returning null just means "not an item page".
- Every mutation re-parses the edited text and returns a new document. Spans are byte offsets, so one edit
  invalidates every later one; re-parsing costs microseconds and removes the whole bug class.
- **Correction to the notes below and to the plan: the recipes parameter is `recipes`, not `recipe`.** 64 of the
  65 sampled pages that have one spell it plural; exactly one page writes `recipe`, which the template therefore
  ignores. Outside the v1 write scope either way, but worth knowing before anyone "fixes" a spelling.
- **A present-but-empty parameter is not an absent one, and its padding is a trap.** 343 of 662 sampled pages have
  at least one parameter written and left blank (`|notes       = ` then a newline). Its raw value is *entirely*
  whitespace, so "the padding before the value" and "the padding after it" are the same characters — computing
  each independently emits them twice and silently adds a blank line to every such page. Caught only by the live
  sweep, because no fixture had an empty parameter until one was added for it.
- **`merchant_value` is always normalized to the compact coin form** (user, 2026-09-24). Target shape is
  `"1p 2g 3s 4c"`, dropping any denomination that is zero (`"2g 1c"`), and the no-value case is written as the
  literal string `absolutely nothing`. Measured on both sides, so the transform is well defined:
  - The game already emits the right *content* in the wrong *spelling* — 36 captured windows give
    `22 platinum 8 gold 5 silver 7 copper`, `1 platinum 5 silver 8 copper`, `8 copper`, `350 platinum`,
    `absolutely nothing`. Note it has **already dropped the zero denominations** itself, so the tool's job is
    purely `N platinum|gold|silver|copper` → `Np|g|s|c`, with `absolutely nothing` passed through verbatim.
    Corroborated end to end on `Peridot`: the window says `9 platinum 5 gold 2 silver 4 copper`, the page already
    says `9p 5g 2s 4c`.
  - The wiki side is the mess. 48 of 64 sampled values are plain text but only loosely canonical —
    `2.6pp`, `1.5p`, `18pp`, `~3pp`, `3.3p`, `1pp 7gp`, `1gp to vendor.`, `1.3 gold`, `2sp`, `1s`, plus
    annotations like `with 111 Charisma` and `Max`. The other 16 are a full HTML block:
    `<ul><li> 5 <span style="color:silver"><b>Silvers</b></span></li></ul>`, sometimes under a
    `<p><b>VALUE TO VENDOR with CHA : 80 and faction at Ally</b></p>` heading. One real value is
    `0p 0g 1s 0c with 111 Charisma`, which normalizes to `1s`.
  - **The captured figure always wins, and those CHA annotations are a legacy artifact** (user, 2026-09-24). In
    legacy EverQuest players had to guess and check to learn an item's value, which is why pages record the
    conditions a figure was seen under (`with 111 Charisma`, `(68 CHA @ Kindly)`, `Max`). EQL removed the
    guesswork: the window states the **maximum** value directly, independent of Charisma and faction. So an
    annotated legacy figure is frequently *wrong* rather than merely misformatted — `187p1g9s1cp (68 CHA @
    Kindly)` records a sub-maximum price — and replacing it is a correction. The annotations go with it.
  - **Consequently there is deliberately no parser for the legacy forms.** Since the page's value is never a
    source of truth, nothing has to be understood about it: the tool renders the captured value and compares
    strings, and an HTML block or a `2.6pp` simply differs and is overwritten. Writing a tolerant reader for all
    of that would be work in service of a value that gets discarded anyway.
  - Implemented as `Core.Items.MerchantValue` (`TryParseGameText` / `ToWikiText`), with a test asserting that
    *every* merchant value in the verified corpus parses — so a future change to the game's phrasing fails the
    build instead of quietly writing a wrong figure. An unreadable value reports failure rather than zero;
    returning zero would publish `absolutely nothing` for an item whose price merely could not be read. The `p/g/s/c`
    spelling is a wiki convention and belongs in the mapping layer once that exists (milestone 6); it lives in
    `Core` for now because there is no mapping layer yet and one convention does not justify an abstraction.
- Fixtures: eleven real pages in `tests/EQLWikiAssistant.Tests/Wiki/Fixtures/`, **tracked in git** — unlike
  screenshots these are public wikitext with nothing private in them. That folder's README says what each one is
  there to prove; each exists because it broke a plausible simplifying assumption.

**Item name vs page title (`Wiki.Wikitext.PageTitle`).** The `Itempage` template needs `itemname` to equal the
page's title. **Any mismatch is a defect** (`PageTitle.IsDefect`), measured at 10 of 538 real item pages — so
expect a few hundred broken pages wiki-wide.
- **Verified mechanism, because an earlier note here claimed mismatches render fine and that was wrong.**
  `Itempage` renders the item as a hover box whose visible anchor is a link to `[[itemname]]`, so when `itemname`
  is not the page's own title the page shows a link to somewhere else. Two failure modes, both observed live:
  - no page has that name, so the reader gets a red "page does not exist" link where the item should be —
    `Essence of Barbarian (Wormwood)`, `Imbued Dwarven Chain Gorget (Bristlebane)`;
  - a page *does* have that name, so the box silently anchors to an unrelated article — `Tailoring (Item)` links
    to the Tailoring *skill* page. Worse for being invisible.
- **`TitleMatch.DisambiguatedTitle` is a defect with a recognizable cause, not an exception to the rule.** 8 of the
  10 mismatches are a title of the form `<itemname> (<qualifier>)`, arising where one in-game item needs several
  wiki pages (craft material, deity, quest variant, left/right book page) and the editor left `itemname` as the
  shared in-game name. It is reported separately only so the user can be told what kind of problem it is.
- **The fix is not obvious, which is why the tool only reports.** The in-game item really does share one name
  across its variants, so setting `itemname` to the qualified title would render a name the game never shows.
  Resolving it properly probably needs a template change — relevant to the user's plan to improve the template.
- **The grave/apostrophe confusion appears in both directions, and the tool has authoritative data.** One real page
  titles itself `Engraved Di\`Zok Deathbringer` with `itemname = Engraved Di'Zok Deathbringer`; another is the
  reverse. Glyph matching reads the true in-game character (the atlas keeps `'` and `` ` `` distinct, which a test
  pins), so unlike a general recognizer this tool knows which is right rather than guessing.
- **Beware measuring this with sanitized filenames.** A first pass reported 33 mismatches including `Summoned_
  Arrow` and `Crimson training tunic_`; those were an artifact of the measuring script replacing `:` and `*` (both
  legal in MediaWiki titles) to make safe filenames. The real figures come from comparing against titles as the
  API returns them. `_` in a URL is just MediaWiki's rendering of a space and means nothing here.

**Finding an item's page (`Wiki.MediaWiki.ItemPageLookup`).** More than one API call, because of two measured
hazards.
- **A quote-character mismatch must be offered as a candidate, not reported as a new item** (user, 2026-09-25). The
  user cannot rename a page; their remedy is to create a correctly-named one and redirect the old, so a missed
  candidate becomes a duplicate nobody can delete. On a miss the lookup retries the name's quote variants and, if
  one exists, returns `FoundMisnamedCandidate` — the item still counts as new (`TreatAsNew`), because acting on the
  wrong page is worse than treating a real item as unlisted, but the user is told what was found and prompted to
  consider a redirect.
  - **The variant ordering is what makes the request cap safe.** Four quote characters over two positions is
    sixteen combinations, more than the cap, so an arbitrary order could discard a plausible apostrophe/grave swap
    in favour of a curly-quote form nobody has observed. Variants are ordered by how many curly quotes they use, so
    the cap only trims the tail. A name with no quotes costs no extra requests at all.
  - Redirects are followed by the client, so an already-redirected misspelt page resolves to the right one and the
    lookup never sees it — desired, not a gap.
- **A title-illegal name cannot be queried**, so it returns `NameUnusable` rather than "not found" and never issues
  the request. The page very likely exists under a name a human chose, and creating a second would be the wrong
  move.
- **An in-game name containing a title-illegal character must be referred to the user, never rewritten** (user,
  2026-09-25). MediaWiki forbids `# [ ] { } | < >` in titles (derived from this wiki's own `legaltitlechars`), and
  `#` is the one that turns up on real items — a `Cell Key #5` cannot have a page at its own name. The wiki's
  editors resolved that by hand as `Cell Key No. 5`; `No. 5`, `Number 5`, `5` and dropping the `#` are all
  defensible, the choice is permanent, it becomes the URL, and a wrong guess creates a page nobody can delete. So
  `PageTitle` only ever reports.
  - **Such a name also breaks lookup**, which matters as much as the write side: the API returns an error or
    nothing. Treat that as "the page does not exist" — but never quietly, because it very likely *does* exist under
    a hand-chosen name. Warn loudly before creating anything.
  - **The ledger must then record the wiki's name alongside the in-game name** (user, 2026-09-25), or an item whose
    page was deliberately renamed looks unhandled on every future capture. Milestone 4b.
  - No sample in the 101-window corpus contains an illegal character, so this is implemented from MediaWiki's rule
    rather than from measured data.

**The wiki mapping (`Wiki.Mapping.WikiMapping`).** The game↔wiki translation, as **data with built-in defaults**
(`WikiMapping.Default`, `Load`/`SaveAsync` for a user-edited copy in app-data) — the plan's mapping layer arriving
with milestone 4 as it predicted, covering the subset the diff needs. Built by censusing both sides: 37 distinct
stat labels across the 101 verified windows against 49 across 744 real pages.
- The bulk is straightforward renaming (`Weight`→`WT`, `Strength`→`STR`, `SV. Fire`→`SV FIRE`, `Base Dmg`→`DMG`,
  `Delay`→`Atk Delay`, `Dmg Bon`→`DMG Bonus`, `Size Cap`→`Size Capacity`). Canonical wiki casing is caps
  (`SV FIRE` on 31 pages against `SV Fire` on 3).
- **An unmapped stat is reported, never dropped.** A stat the tool has never seen is how a game patch announces
  itself, and discarding it silently would lose real data from a public wiki with nobody the wiser. `Accuracy` is
  the one remaining example: real game data (on 12 of 101 windows) with no agreed wiki home, left unmapped so it
  surfaces as a question rather than being invented a field.
- **Two stats are `NotStored` and ignored silently, for different reasons** — the mapping carries a note on each, so
  a future reader does not have to guess:
  - `Ratio` is Base Dmg over Delay, both of which the wiki stores. Recording the quotient would be a third value to
    keep consistent for no gain, and it is the one stat where a rounding difference between the game and a
    recomputation would look like a data error.
  - `Container` (open/closed) is already implied by the container fields the wiki does store — Capacity and Size
    Capacity — so it adds nothing (user, 2026-09-25).
- **`Type` and `Items` go into the statsblock** (user, 2026-09-25). `Type: Shield` marks an item usable for bashing
  and belongs with `Slot`; `Items: Arrows` restricts what a container may hold and belongs with `Size Capacity`.
  Those positions are a *rendering* concern for whenever the renderer is built — the diff only needs the labels.
  The user is separately getting both added to the template's documented vocabulary, so the tool is not writing
  fields the template does not sanction.
- **The wiki signs attributes and resists; the game does not.** The wiki writes `STR: +5` where the window says
  `5`. Measured rather than assumed: `STR` is signed on 126 pages against 4 plain and `SV FIRE` 68 against 0, while
  `WT` is plain on all 721, and `AC`/`DMG`/`Atk Delay`/`Range`/`Capacity`/`Weight Reduction` are never signed.
  `StatMapping.Signed` carries it.
  - **A sign-only difference is deliberately not an edit.** `STR: 5` on a page reads unambiguously, so rewriting it
    to `+5` would be precisely the incidental reformatting this tool is not allowed to do — that belongs to the
    prettifier. The sign is applied to the value the tool *proposes*, and only when that value is being written
    anyway for some other reason. `ValuesAgree` ignores a leading `+` accordingly.
- **Two mapping gaps were found only by running the analyzer over the corpus**, and both would have pushed a
  regression to the wiki:
  - **Units differ on `Weight Reduction`**: the game shows `100`, the wiki `100%`. The comparison called them
    different and would have stripped the `%` off every such page. Hence `StatMapping.WikiSuffix`.
  - **Slot names need their own mapping**: the wiki writes slots in caps, which is mechanical, but its `FINGER` is
    the game's `Fingers`. Censused across all 18 slot names in the corpus, so the exception list is known to be
    complete rather than guessed at.

**Analyzing a capture against a page (`Wiki.Analysis.ItemPageAnalyzer`, milestone 4).** Produces per-field
`FieldFinding`s and changes nothing, so each judgement is reviewable on its own. Rules confirmed by the user
(2026-09-25):
- **A template field absent from a page is fine unless the capture has data for it.** Absence is only a defect when
  it would hide something the tool is adding or changing; don't demand a full parameter set.
- **`No Trade` makes merchant value unverifiable — preserve whatever the wiki has, and say so.** Measured across
  the verified corpus: **all 63 `No Trade` windows show no merchant-value row at all**, and **all 12 `Attunable`
  windows show a real value**. So the absence is a property of tradeability, not evidence the item is worthless,
  and overwriting the wiki's figure with "nothing" would destroy a value no future capture of an attuned item can
  ever recover. Note this is **broader than the Attunable-vs-No-Trade case the rule was first framed around**: any
  `No Trade` capture is unverifiable, natively-No-Trade items included.
  - **`absolutely nothing` and "no row at all" must stay distinguishable**, and they are: `absolutely nothing`
    never co-occurs with `No Trade` in the corpus. The first is a verified worthless item and gets written; the
    second is a gap and gets a "couldn't verify merchant value" warning.
- **The wiki sometimes holds more detail than the window shows, and that is not staleness.** A wiki value that is a
  slash-separated list containing the captured one is `NeedsReview`, not `Differs`, since overwriting would discard
  the alternatives. Only two pages do this, both ammo — `CLASS 1 Bone Point Arrow` has `Range: 50 / 75 / 100` and
  `CLASS 3 Wood Point Arrow` has `5 / 25 / 50`. Note both also carry a parallel triple in their recipe line
  (`Fletching (Trivial: 68 / 68 / 82)`), so these pages appear to aggregate several arrow variants; the game shows
  the single range of whichever variant is in hand. Unexplained as of 2026-09-25 and left to the user.
- **Only a leading `+` is ignored when comparing values.** The wiki writes a bonus as `+8` and the game as `8` and
  neither is more correct; everything else compares exactly, because this is the comparison that decides whether a
  number on a public wiki gets overwritten and a tolerant one would hide the errors it exists to find.
- See "Reading and editing wiki pages" for the flag rules (legacy discarded, vocabulary open-ended, wiki
  `Attunable` beats captured `No Trade`, `This is a meal!` alerted but never acted on) and the `merchant_value`
  normalization.
**Effects (`Wiki.Wikitext.EffectLine`).** The wiki splits them two ways, per the convention the user supplied
(2026-09-25) as the template documents it:
- **Focus effects get their own template parameter** — `| focus_effect = Improved Vampirism III`. No line, no link,
  no parenthetical.
- **Everything else is a statsblock line**:
  `Effect: [[?|<span class='itemeff'>?</span>]] (Combat / Clicky / Worn / Can Equip / Must Equip / Casting Time: ?) at Level ?`
  Both `?` in the link are the effect name; the parenthetical keeps whichever parts apply, comma-separated; the
  `at Level ?` carries a required level or is omitted.
- **The `<span class='itemeff'>` wrapper is functional, not decorative** (user, 2026-09-25): it is what gives the
  effect a tooltip, which a bare `[[Name]]` does not. So converting a legacy link is a real correction the user makes
  on every item they touch, and the analyzer reports it as `Differs` with that reason attached. **Contrast the
  `STR: 5` versus `STR: +5` case, which is purely cosmetic and deliberately left alone** — the line between them is
  whether it changes what the page *does* or only how it looks. `EffectLine.HasTooltipLink` is what distinguishes
  them; `TryReadName` reads the link target so "same effect written the old way" is never confused with "a different
  effect".
- **A cast time is written without its unit.** The game says `12.0 seconds`, every real page says
  `Casting Time: 12.0`. Found by the corpus run flagging `Careless Lightning` as differing when only the unit did.
- **An effect the convention cannot express is refused, not written incomplete.** `EffectRender.IsComplete` is false
  when any part had no wiki representation, and the analyzer reports `NeedsReview` rather than emitting a line that
  looks finished while having quietly dropped real game data. Two live cases, both on the user's TODO: `Charge` and
  `Consumable` kinds have no agreed parenthetical token, and `Cooldown`/`Cooldown Group` have no agreed place at all
  (10 of the 48 corpus effects have a cooldown).
- **Open question recorded for the user**: every line is rebuilt from the capture, so a parenthetical part the window
  does not show disappears — a live `Burn` page reads `(Combat, Casting Time: Instant)` where the game shows no cast
  time for that proc. Probably harmless, but it is a deletion the capture cannot justify.
- **`WikiSpike analyze [--detail]` runs the analyzer over every verified capture against the live wiki** — the
  wiki-side equivalent of `AccuracySpike`, and the only thing that finds a rule this wrong. **It must apply
  eligibility first**, which is a mistake worth not repeating: an initial run analyzed levelled items too and
  reported 300 differing fields and "76 of 85 pages are stale", when a levelled item's stats are *legitimately*
  higher than the wiki's level-0 figures (Bladestopper +7 shows AC 43 against a correct 25). Filtering to eligible
  items dropped that to 19.
- Baseline on the verified corpus (2026-09-25), eligible items only: 41 of 90 distinct captures are eligible (the
  rest are levelled), 38 have pages, **9 pages already correct and 29 would change** — 258 fields match, 19 differ,
  45 are missing on the wiki, 2 unverifiable, 11 need review. The differences are dominated by legacy flag lines
  being dropped, plus real staleness and four `merchant_value` corrections. It also caught a typo on a live page
  (`Lore Equpped`).

**Eligibility (`Core.Items.ItemEligibility`, pipeline step 4b).** A foreign exaltation or a levelled item (`+X>0`)
blocks automated processing. **The load-bearing rule is the ledger one and it is easy to get backwards: an
ineligible item gets no ledger row at all** — not `flagged`, not `skipped`. It was never actually checked, so the
next capture must be treated as new; any row would make it look handled and quietly exclude it from ever being
checked properly. `ShouldWriteLedgerEntry` says so on the result rather than leaving each caller to remember.
- **Ornamentation counts as a foreign exaltation** (user, 2026-09-25, resolving the plan's open question). It is
  never native — only ever applied by a player — so a filled slot blocks regardless of its name. That last part
  matters: `IsForeignExaltation` short-circuits for Ornamentation instead of relying on the name comparison, which
  would read an ornamentation named like the item itself as "native". Its explanation also avoids claiming a name
  mismatch, which would be misleading for a slot that is foreign by nature.
- Every blocker is reported, not just the first, so the user sees the whole picture in one pass.

**Checked-items ledger.** To avoid hitting the wiki unnecessarily, a local store (keyed by item name + entity kind)
records the outcome of each check (`matched`/`edited`/`flagged`/`skipped`/`not-on-wiki`) along with a fingerprint of
the parsed in-game data. A capture that reproduces an already-`matched`/`edited`, unchanged fingerprint skips the
wiki fetch entirely. `flagged` items (icon mismatch, occluded capture) are never treated as done.

**Icon cache.** Wiki icon files (`File:item_<ID>.png`) are static and reused across many items, so each is
downloaded at most once into a local on-disk cache before any icon comparison.

**Multi-window / occlusion handling.** A single screenshot may contain more than one item detail window; all of
them must be located and processed. A partially obscured window must be detected and surfaced to the user as a
warning rather than silently processed as if complete. Implemented as `ItemWindowLocator`/`WindowBoundsFinder`
(`Core.Locate`), validated against real occluded samples — see "Locating item windows" below for the design.
Parse adds a second, content-level check (reconciling the title-bar name against the content-area name) as
defence in depth.

**Locating item windows (`ItemWindowLocator` + `WindowBoundsFinder`, `Core.Locate`).** Run OCR on the *whole*
screenshot (see "Full-frame OCR" below) to find each window's `Description` tab, then trace the window's **own
content-area outline** — the thin neutral-grey line the game draws around the tab contents — outward from that
anchor. **Before touching this code, read the plan's milestone 2 section in full**: two earlier designs failed,
every constant here was measured against real screenshots rather than derived, and re-tuning in isolation without
retesting `tools/LocateSpike` against the full real-sample set is very likely to reintroduce a bug this history
already found and fixed.
- **Trace the grey outline, not a brightness transition.** Two superseded designs: (1) clustering OCR lines by
  text proximity, which can't tell a window's own content from an adjacent window's; (2) tracing the edge of the
  near-black interior — "scan outward until it stops being dark" — which silently assumed whatever is *outside*
  the window is brighter than it. Often it isn't, and then those scans tunnelled straight through the real edge:
  the player's own 3D character model standing behind a window measures ~33-75, and an adjacent dark UI panel
  measures about the same as the interior. The outline is drawn by the window itself, so it doesn't depend on
  what's behind it — which is the whole point.
- **Measured colour profile** (via `tools/LocateSpike --probe x,y,dx,dy,count`, which dumps raw pixel RGB along a
  ray — use it before changing any constant): interior `R=G=B≈16` (10-25 with JPEG noise); content outline a 1px
  neutral grey line at **50-62**, essentially constant along its length; the outer window frame just beyond it at
  **0-8**, i.e. *darker* than the interior; title bar the same **0**; world background 150-170; text up to 255.
  Verified identical on lossless Graphics Capture frames and on saved screenshots, so the capture path doesn't
  shift these. `EQLWIKI_LOCATE_DIAG=1` additionally prints what every individual probe answered, which is how the
  agreement thresholds were set — use both rather than guessing.
- **Brightness alone can't identify the outline** — anti-aliased text edges and the character model both land in
  the same 50-62 band. What separates them is that the outline is a long uniform straight line, so every
  candidate is confirmed by requiring a long run of same-brightness line pixels *perpendicular* to the scan
  direction (a glyph edge spans a few px; a stat value-box outline a few tens). That run check is also what
  implements "if the border is broken, treat it as occluded".
- **The outer frame is tested on the _minimum_ channel, not the maximum.** Requiring frame just beyond a
  candidate is what separates the content area's real boundary from the internal divider rules the window also
  draws (identical grey lines, but with more window beyond them). JPEG bleed from a bright neighbour lifts
  individual channels of that thin frame unevenly — against a red element below one real window it reads
  `(11,0,0)` then `(34,0,0)`, which a max-channel test rejects — but never lifts all three, so the minimum stays
  at 0 while the interior's neutral grey keeps a minimum of ~16.
- **The top edge traces a different piece of chrome**: there is no grey outline at the window's outer top, and
  the title bar must stay in the crop for Parse's title-vs-content name check, so the top is traced from the
  **title bar's own pure-black band** — its topmost row is the window's outer top. This also began as a
  brightness scan and failed identically to the side edges: with other dark UI directly above a window, there is
  no bright run to stop at and the scan ran to its limit, failing the window outright.
  - That band's black is tested on the **maximum** channel, unlike the frame. The active tab's label is yellow,
    `(191,191,4)`, whose *minimum* channel is 4 — a minimum-channel test reads bright yellow text as black and
    latches the top edge onto the tab label.
  - Inside the band, tolerate a short run of *any* non-black rather than only bright rows: the title's glyphs are
    anti-aliased (one real stroke reads 192, 115, 77, 38 down a column), so a "black or bright, else stop" test
    stops on the glyph's own soft edge and cuts the title bar out of the crop.
  - A real band measures ~16px, so a **much taller band means it has merged with adjacent black chrome** (another
    window's title bar, another dark panel) and that probe returns nothing rather than a wrong answer, leaving
    consensus to the columns that didn't merge. Measure that cap from the band's *start*, not from the last black
    row found — measuring from the latter lets a continuous band drag the limit along and never trip. Without
    this, a window sitting under another dark panel had 8 of 11 probes walk ~200px up into it and agree with each
    other, failing only later via the overall height ceiling: 13 of 26 real captures lost a readable window.
  - **Stop at interior grey, but tolerate glyphs.** A title bar contains only its own black plus its text, so an
    interior-grey pixel means the band has ended. Tolerating *any* short non-black run instead is not safe: with
    one window overlapping another the two title bars can sit ~7px apart, which such a rule bridges into the
    neighbour's chrome — that made a **fully visible** window report as occluded, because every probe then
    overran the band-height cap. Glyph rows are bright and still tolerated; interior grey ends the band.
- **Both tab states must work.** When `Description` is the *active* tab it merges into the content area, so there
  is no chrome line below the label. When it's *inactive* (the Lore tab is selected) it's drawn as its own raised
  box, so a stack of chrome lines sits below it and all must be stepped past — stopping between them makes the
  content area's top outline look like the window's bottom. The step-in point also has to land on an actual
  interior row, not a fixed offset: a real Lore capture starts its first line of text 4px below the outline, so
  a fixed clearance lands inside a glyph and every interior test downstream fails.
- **Probe agreement cannot decide occlusion; rectangle closure does.** Measured across the set, a clean window's
  edge agreement (64-100%) overlaps a genuinely part-covered edge's (55-67%), so no threshold separates them —
  strict enough to reject the covered edge also rejects a clean window touching a neighbour or sitting at the
  screen edge. So agreement stays permissive and the traced rectangle is instead required to **close**: the
  outline must be present at the corners, not just where probes crossed it. A covered edge's consensus lands on
  the *occluding* window's outline, and this window's own bottom outline doesn't reach that corner. Without this,
  a partly-covered window came back confidently 587px wide, silently merged with its neighbour.
- The absolute size ceilings (600x700) are retained and still catch an occluder adjacent along an *entire* side,
  where every probe agrees on the same wrong answer.
- Validated against the full real-sample corpus (43 screenshots, 101 located windows, 1 correctly occluded;
  `tools/LocateSpike --save` draws a debug overlay, green/red by `PossiblyOccluded`; golden tests in
  `Tests/Locate/`). Traced widths are consistently 388-404px — the window's true content width — where the
  previous design returned 414-587px because it ran past the real edge into neighbouring UI. Cases that now
  resolve and previously could not: windows flush against the inventory/bank panels, windows at all four screen
  edges, a window with the Lore tab active, touching windows, and one window overlapping another (`06c`, whose
  front window is fully visible and now parses cleanly — see the interior-grey stop above).
- **Corrections to earlier notes in this file, since the claims were load-bearing and are now disproven**: the
  border *is* a distinctly-coloured line (the earlier "it isn't, it's just the edge of the dark interior" was
  wrong); and the "known residual gap" about a window whose title was ~20% occluded never existed — that sample's
  title was fully readable, and the garbled title was an artifact of the old tracer running 137px past the real
  left edge into the neighbouring window. Parse's title-vs-content reconciliation is still implemented and still
  worth keeping as defence in depth, but it is no longer propping up a known geometry hole.

**Parsing item windows (`ItemParser`, `Core.Items`; `FieldLabelLexicon`, `Core.Ocr`).** Turns a clean
`LocatedWindow.Lines` list into a `ParsedItem`. Ground truth came from real `LocateSpike` dumps against four
structurally different real windows (armor with a foreign exaltation, a weapon with both a native and foreign
exaltations plus three effect types, a quest item, and a plain consumable with no slot/exaltations at all) — see
the plan's milestone 2 writeup for the verbatim captures. Real windows follow a consistent line order (title bar
name -> Description[/Lore] tab -> content-area name repeated -> unlabeled comma-separated flags -> `Class:` ->
`Race:` -> an optional bare-word slot row with **no "Slot:" label in-game**, unlike the wiki's own statsblock
convention -> UI chrome -> a two-column stat block -> "Modified" chrome (the name a third time) -> optional
exaltation rows -> optional effect rows -> optional merchant value), so the header is parsed positionally and the
body by pattern-matching each row, since the body's actual field set varies a lot by item type (a weapon shows
`Base Dmg`/`Delay`/`Skill`/`Ratio` where armor shows `AC`/resists).
- **A real OCR-layout quirk the row-reconstruction logic depends on**: unlike almost every other label:value line
  (which comes back as one self-contained `OcrLine`, e.g. `"Class: WAR CLR PAL..."`), the classic two-column stat
  block (Size/Weight/AC/stats/resists/etc.) is recognized as *separate* fragments for a label and its value even
  on the same row — apparently because the game renders the value in a visually distinct box. `ItemParser` groups
  lines into rows by Y-proximity first (8px tolerance, same reasoning as `WindowBoundsFinder`'s consensus
  tolerances), then pairs fragments within a row generically (self-contained "Label: Value", or a bare
  "Label:"/"Label." fragment immediately followed by a separate value fragment — including two such pairs on one
  row, e.g. `Size:` `SMALL` `AC:` `15`) rather than assuming either shape specifically.
- **A long `Class:` list wraps onto a second, unlabeled row** (real capture: `Class: WAR RNG SHD MNK BRD ROG NEC
  WIZ MAG` then `ENC BST BER`). Because the header is parsed positionally, that shifted everything by one row —
  truncated class list, empty races, and the continuation consumed as the item's *slot*. Continuation rows are
  absorbed into the list above them; class/race codes are short and ALL-CAPS, which is what distinguishes them
  from the bare slot row that can also follow (slots read `Range Ammo`, `Primary Secondary`, `Ear` — mixed case).
- **`Slots` is a list, not a string.** An item can be equippable in several slots, and the game lists them
  space-separated on that one unlabeled row: `Primary Secondary` and `Range Ammo` are common, and odder pairings
  exist (a shield usable in Secondary *or* Back; an item usable in Chest *or* Waist). Empty for items with no slot
  at all — consumables, containers, tradeskill materials, which are ~30% of the corpus.
- **Effect kinds seen in real captures**: Focus, Click, Combat, Proc, Charge, **Worn**, **Consumable**. The last
  two only turned up once a broad slot/category sample set existed, so treat the list as "what's been observed",
  not "what exists" — an unrecognized `X Effect` line degrades to an unparsed-line warning, which is the signal
  to add it.
- **An effect line is three separate things, not one string.** `EffectEntry` splits them: `Name` (the part the
  game draws in magenta — the only part that identifies the effect, and what a wiki lookup keys on),
  `Conditions` (trailing parentheticals like `Must Equip` / `Can Equip`), and `Modifiers` (the sub-lines that
  follow it — `Cast Time`, `Cooldown`, `Required Level`).
- **A required level reaches us two different ways, and is normalized to one.** Click effects put it on its own
  sub-line (`Required Level: 40`); proc/combat effects fold it into the parenthetical (`Ykesha (Req Level 37)`).
  Both land in `Modifiers` under `Required Level`, so nothing downstream has to know which style the game used
  for a given effect. Only that one qualifier is hoisted out of the parentheses; anything else stays a condition.

**The Lore tab is a different view, not a variant of the Description layout.** It has no repeated content-area
name and no stat block — just the lore prose — so a Lore capture yields a `ParsedItem` with `Name` (from the
title bar, the only place it appears there) and `Lore` set, and everything else empty. The two captures are
combined by the two-capture lore flow. Lore content varies: genuinely descriptive prose for some items, just the
item's own name again for others.
- **Which tab is showing is read from the label's colour, in Locate, not inferred in the parser.** The selected
  tab's text is yellow (measured `191,191,4` / `159,159,6` / `255,255,0` — red≈green, blue near zero), unselected
  is neutral white. `LocatedWindow.ActiveTab` carries it. This is a question about pixels, and Locate is the layer
  holding the image — OCR output carries no colour at all. Structural guesses ("no `Class:` row, so it must be
  lore") were rejected: OCR does sometimes drop a `Class:`/`Race:` row, and that would silently reinterpret a
  whole Description capture as lore.
- `HasLoreTab` (the window *offers* a Lore tab — drives the two-capture flow) and `ActiveTab` (which one is on
  screen) are different things; both captures of a lore-bearing item have `HasLoreTab = true`.
- Lore wrapped across rows is joined with spaces — the game breaks it purely to fit the window.
- **OCR renders the label separator as `.` often enough to matter** (`Accuracy. +13.6%`, `Container. CLOSED.`,
  and `Weight.`/`Dexterity.` as bare labels). Splitting on `.` unconditionally would cut decimal values in half,
  so it only applies when the text before the dot is a label the lexicon knows.
- **`FieldLabelLexicon`** is deliberately the small, fixed vocabulary the milestone 1 writeup scoped it to: it
  fixes only the confirmed recurring corruption (`Ornamentation`->`Omamentation`, `Worn Exaltation`->
  `Wom`/`Womn Exaltation`) before a label is matched to a stat field or an exaltation/effect kind. It only
  contains full labels as they actually appear in-game (e.g. `"Worn Exaltation"`, not a bare `"Worn"` — there's
  no bare "Worn" field), so don't add bare-word entries without a real line that needs one.
- **Occlusion safety net (defence in depth).** `ParsedItem.TitleContentNameMismatch` fuzzy-compares the
  title-bar name against the content-area name (threshold scaled to name length: tight enough that a truncated
  title trips it, loose enough that ordinary single-character OCR noise doesn't). A caller must treat a set flag
  as "don't trust this capture", not as advisory. It no longer covers a known geometry hole — the sample that
  supposedly proved one turned out to be a bounds bug, now fixed (see "Locating item windows") — but it still
  earns its place: on the 4-window sample it correctly rejects a Bank/Tradeskill panel that OCR'd a literal
  "Description" and got picked up as a false-positive window, catching it via a title/content name mismatch plus
  a pile of "expected row not found" warnings.
- **Native-vs-foreign exaltation check** (`ItemParser.IsForeignExaltation`, fuzzy `EditDistance` against the
  item's own parsed base name) is verified against real data, not just a plausible design: on the real 3-window
  sample, Bloodmoon's own `Focus Exaltation: Bloodmoon (Exaltation)` is correctly identified as native (the
  "removable native exaltation" case from the Augmentations section above), while its Click/Proc exaltations
  (different items) and Lustrous Russet Bracer's Focus exaltation are all correctly flagged foreign.
- **An exaltation or effect row is matched across a whole row, not only a single-fragment one.** Requiring
  `row.Count == 1` (what this used to do) silently demoted the row to an ordinary *stat* whenever OCR fragmented
  it, which dropped the exaltation or effect from its own list entirely. All three shapes are real, measured on
  the corpus:
  - the label split from its value, with the separator duplicated across the break — `"Click Exaltation:"` +
    `": Earthshaker's Mantle (Exaltation)"` (so a second leading `:` has to be trimmed off the value);
  - the effect's trailing parenthetical detected as its own fragment, leaving a dangling open paren behind —
    `"Click Effect Careless Lightning ("` + `"(Can Equip)"` (so `BuildEffect` trims a trailing `(` after peeling
    the real parenthetical);
  - the exaltation slot's own **icon** recognized as a stray fragment *before* the label — `"0"` +
    `"Click Exaltation: Bladestopper (Exaltation)"`.
  `TryMatchRowLabel` scans from each fragment in turn, which covers leading junk and rejoins a split label with
  its value without assuming either shape. Safe because these labels are long and specific — a stat value won't
  fuzzy-match "Focus Exaltation". This is also why 08a and 08b disagreed on the *same* item window: identical
  pixels, but OCR happened to split the Combat Effect line in one and not the other.
- **A punctuation-only junk row shifts the whole positional header.** The window's own chrome occasionally reads
  as text — a real capture had the tab-bar corner, clipped at the crop's left edge, recognized as `()` on its own
  row between the tab row and the content-area name, which made the name parse as `()` and pushed the real name
  row into the flags field. Rows with no letters or digits at all are dropped before parsing. Note the safer fix
  is dropping junk, *not* identifying the name row by similarity to the title — that would quietly defeat the
  title-vs-content occlusion check, whose whole job is to notice when those two genuinely differ.
- **Closed: RapidOCR dropped isolated stat digits, and it was the dominant data gap.** Measured over 100 real
  item windows it lost ~24 stat values — roughly 1 item in 5 missing at least one stat — with the digits plainly
  present in the pixels (a window showing four `7`s returned no `7` fragment at all) while `AC: 6` and `HP: 55`
  in the same window read fine. Glyph matching reads all of them; the count is now 0. Kept here because it is the
  clearest illustration of *why* the window-crop pass stopped using a general recognizer: the failure was not
  tuning, it was the wrong tool.
- `tools/ParseSpike` (mirrors `OcrSpike`/`LocateSpike`) runs the full Locate -> Parse pipeline against a real
  screenshot and dumps every parsed field per window (including a `[FOREIGN]` marker on foreign exaltations) —
  use this, don't recreate an ad hoc version, when tuning parser rules against new samples.

**Glyph matching (`Core.Glyphs`, atlas in `Glyphs/eql-ui-font.atlas`) — the UI font is a deterministic bitmap
blit, so reading it is exact template matching, not recognition.** Measured, not assumed: the same 'A' in an item
window and in the in-game Notes Window is byte-identical, anti-aliasing intermediates included. Build and inspect
with `tools/GlyphSpike` — `dump` prints a region's raw intensities (the `--probe` of this work; measure before
changing a constant), `segment` shows the bands/runs/glyphs found, `cluster` groups by exact equality, `atlas`
builds the labelled atlas, `verify` reads a real region back.
- **Anti-aliasing is one fixed ramp in every colour, once normalized.** Absolute values differ per colour because
  the ramp is scaled to that colour's peak — white on a content area runs `16 64 112 159 191 223 255`, title-bar
  grey on black runs `0 38 77 115 141 166 192`, magenta effect text runs `16 58 100 141 169 196 224` — but
  divided by `(peak - background)` all three are the same seven steps, exactly `k/15` for k in 0,3,6,9,11,13,15.
  So **one colour-blind atlas covers the whole UI**; no per-colour atlas is needed. Intensity is the **maximum**
  channel (the opposite of `WindowBoundsFinder`'s frame test, which needs the minimum — different question).
- **The off-ramp counter is the check on all of it.** Captures are lossless, so a pixel that doesn't land on a
  ramp step means the model no longer describes the image (rescaled? another skin or UI scale?) and the tool
  refuses to build an atlas rather than rounding. It caught both calibration bugs below before inspection did.
- **Background is per band, peak is per colour within a band.** A 2px-wide `i` or `l` tops out at ramp level 4
  (191 where white's peak is 255) because its stem is never fully covered, so a per-run peak mis-normalizes it;
  and a single row routinely carries two colours (a white `Focus Effect:` label beside a magenta effect name), so
  a per-band peak is wrong too. Runs are grouped by which channels their brightest pixel uses.
- **Baseline is the modal glyph bottom per band, ties broken _downward_.** On a line of letters this is
  unambiguous; on the sheet's `<>?|:~` row only `?` and `:` sit on the baseline while `<` and `>` float above it,
  and breaking that 2-2 tie upward put the atlas's `:` 2px out so it never matched — every real `Race:` read as
  `Race.`, a silent substitution. A tempting global rule ("text is on a 16px grid, take the modal phase across
  all bands") is **wrong**: the pitch is 16px through the stat block but 23px down the exaltation rows.
- **Glyphs inside a word touch** (a real `ALL` has 'A' ending at x=869 and 'L' beginning at x=870), so gap
  splitting cannot separate them — the reader walks a run left to right taking the largest exact match. Largest
  by width *then height*: a `.` legitimately matches a `:`'s lower dot, so a width-only rule reintroduces the
  `Race.` bug from the other direction.
- **Atlas labelling is positional, not a manual pass.** The Notes Window sheet spells known strings with every
  character space-separated, so `GlyphAtlas.FromLabelledBands` labels by position and **refuses** if any row's
  glyph count disagrees — a miscount would shift every later label and bake wrong characters in.
- **Result: 90 characters, 89 distinct shapes.** The single collision is `l` = `I`, both a bare 2x9 bar with no
  serif or crossbar. That is irreducible at the glyph level: the atlas records both labels and leaves the choice
  to context. Guessing one would be a silent substitution, which is the failure this engine exists to remove.
- The atlas is an **embedded resource** of `Core` (`GlyphAtlas.Bundled`), not a file beside the executable —
  contrast the RapidOCR models, whose loose-file dependency has already caused a real runtime failure twice.
- **The apostrophe and the grave accent are distinct entries, and both occur in real item names** — `Kilva's Skin
  of Flame` against `Kavruul`s Mystic Pouch`. RapidOCR read every grave as an apostrophe; glyph matching keeps
  them apart, which a test pins. The sheet originally had only the grave, so both quote characters were added to
  it in a second capture — if the sheet is ever recaptured, it must keep them.
- **Reading a window is `GlyphReader`, and every candidate calibrates itself.** The obvious design — segment into
  text bands, measure each band's background and peak, match within it — was built first and fails: a band that
  merges with the item icon, a divider rule or the tier bar takes its calibration from the chrome, and the whole
  row becomes unreadable (measured on one real window, clean text bands are 9-13px tall while merged ones run to
  22, 40, 44 and 65px). Instead a candidate solves both unknowns from its own pixels — background from the
  glyph's level-0 pixels, peak from the brightest level its bitmap uses — after which every remaining pixel has
  exactly one permitted intensity. Chrome does not satisfy that, and text reads identically on the content area's
  16-grey, the title bar's black, or beside a bright icon.
  - **Background comes from the glyph's own interior, not a ring around it.** The game draws divider rules flush
    under a line of text: a rule at intensity 50 sits one pixel below the 'p' descenders of a real "Bladestopper",
    so a uniform-ring requirement drops exactly those glyphs and the word reads "Bladesto" + "er". Only shapes
    with no level-0 pixels at all (the bare `l`/`I` bar, `-`, `|`) use the ring, and for those it must be strict —
    relaxing it let the window's own frame read as a column of `|` and `!`.
  - **A minimum contrast of 64 is required, because self-calibration has a degenerate solution.** A flat-coloured
    scroll-bar arrow solves to background 149 and peak 150, and with a one-unit span every ramp level rounds to
    the same intensity so the whole bitmap "matches". Real text spans 192 at the narrowest.
  - **Search is anchored on each glyph's brightest pixel**, not swept over every position: a full sweep measured
    11.8s for one window against 0.5s anchored, and cannot find anything the anchored search misses. Note this
    only helps where ink is sparse — on a *full screenshot* the 3D world sits at 150-170 so nearly every pixel is
    ink, which is why advances are learned inside located windows rather than whole frames.
- **Spaces are decided by the glyph's cell width, never by the pixel gap.** Two adjacent '1's (4px of ink on a
  6px cell) sit 3 background columns apart with no space between them — exactly as far as a space puts some other
  pairs. A gap threshold of 3 turned every "11" into "1 1"; 4 merged hundreds of real spaces instead. Bucketing
  all ~26,000 adjacent pairs by how far past the preceding cell the next glyph starts is cleanly bimodal with
  **nothing at 2** (`0: 23641, 1: 217 | 3: 2650, 4: 93, ...`), which is the separation a gap rule never had.
  - Advances are learned by `GlyphSpike advances` from real windows, because the sheet the atlas is built from
    spaces every character out by design and so cannot show them. 67 of 89 shapes get one; the rest are
    characters item windows never use and fall back to the gap.
  - A learned cell wider than the ink plus a real side bearing (measured: 0 or 1 for all but two glyphs) means
    the glyph is *always* followed by a space, so the measurement contains one. `:` and the class codes are like
    this. Left alone, the too-wide cell then suppresses the very spaces that produced it — "Class: RNG" read as
    "Class:RNG", which merged "Time: Instant" into one word and let the `l`/`I` rule resolve it "lnstant". Such a
    cell is clamped to the ink width; clamping to ink+1 instead leaves `Z` landing in the empty bucket and
    "WIZ MAG" merges.
- **`l` versus `I` is the one place the reader chooses rather than reports.** They are the same pixels, so
  "don't guess" would mean emitting both and corrupting every word containing either. It resolves from the word:
  initial means `I` (this UI is Title Case), otherwise it follows the word's other letters, and a word of nothing
  but bars is `I` (a roman numeral). A human reading the screen does the same. Anything it gets wrong is a *name*,
  where fuzzy wiki lookup is the existing backstop — never a digit, which is guessed at nowhere.
- **A fragment with no letter or digit is chrome and is dropped.** The title bar's decorations match `.` and `_`
  exactly, and since the parser joins a row's fragments, that turned a title of "Spit" into `_ . Spit .. .. . . .`
  — enough to trip the title-vs-content occlusion check and condemn a good capture.
- **Output shape deliberately mirrors `RapidOcrEngine`'s**, so `ItemParser` needed no change to switch engines: a
  row is split into separate `OcrLine`s at the stat block's *column* gap (measured 19-67px against 4-5px between
  words), because emitting "Size: MEDIUM   AC: 43" as one string makes the parser read the value as
  "MEDIUM AC: 43". Baselines within 2px are one visual row, since the two stat columns are not always rendered on
  exactly the same baseline and grouping on the exact value emitted the right column before the left.
- **Result on the verified corpus: every field exact.** 2094 correct, 0 wrong, 0 missing, 0 extra, 0 silent-wrong,
  0 structural, 0 warnings — against RapidOCR's 13 silent-wrong, 24 missing and 24 warnings on the same corpus
  and the same ground truth. `AccuracySpike --rapid` and `ParseSpike --rapid` still run the old configuration for
  comparison.

**Measuring extraction accuracy (`tools/AccuracySpike`, scorer in `TestSupport/Accuracy/`).** Any change to OCR
settings or parser rules must be judged by a number, not by eyeballing warning counts — 18 tunable OCR parameters
against ~100 item windows is unmeasurable by eye, and the failure that matters most (a *silently* wrong value) is
invisible that way by definition.
- Ground truth lives in `tests/EQLWikiAssistant.Tests/Accuracy/expected-items.json`, **tracked in git**. It holds
  only parsed item-window fields — the same public game data this tool publishes to the wiki. The private-info
  risk in `samples/` is everything *outside* a window, so the hard rule (documented in `ExpectedCorpus.cs`) is:
  never whole-frame OCR text, never a line the locator didn't attribute to a window crop, no coordinates, and
  warning **counts** rather than verbatim warning strings (those quote OCR fragments and churn with every tuning
  change). It lives under `tests/` rather than beside the screenshots because `samples/` is gitignored — a sidecar
  there would silently vanish on a fresh clone and take the regression guard with it.
- `AccuracySpike --bootstrap` writes a candidate to `.local-data/` (never over the tracked file). Every field the
  parser flagged is emitted as `?TODO` rather than as its absent value — that's what turns the known misses into
  ground truth a human must supply, instead of freezing today's bugs in as "correct". `verified: false` until a
  human has checked an entry against the screenshot.
- **Scoring is exact string match, never fuzzy.** Fuzzy is right at *runtime* (`EditDistance`, for wiki page-title
  lookup) and wrong for *measurement* — a tolerant comparer would score `Tarnished`->`Tamished` as a pass and hide
  an entire error class.
- Verdicts are cross-tabbed by *did the parser flag it?* The two hard gates are **`structural`** (window count
  mismatch — a lost or invented window, reported as one loud failure rather than a cascade of field errors) and
  **`silent-wrong`** (a wrong value on an item carrying no warning). Everything else is a manual-review cost;
  `silent-wrong` is a wiki-corruption risk, and it is the number that must never move off zero.
- Compare configurations **lexicographically**, not by a weighted score: a weighted total lets a tuner buy five
  recovered digits with one corrupted value, which is exactly the trade this project must never make.
- `CorpusAccuracyTests` gates the baseline, behind `EQLWIKI_ACCURACY=1` (precedent: `EQLWIKI_LOCATE_DIAG`). A
  corpus pass is ~3 minutes; in the default `dotnet test` path it would get muted within a week. The pure comparer
  tests run always and need no samples.
- Baseline against the **verified** corpus (all 43 samples checked against the screenshots by the user,
  2026-09-24): **101 windows (1 correctly occluded), 2094 correct fields, and 0 for every error count —
  structural, silent-wrong, wrong, missing, extra and parser warnings alike.** All three ratchets in
  `CorpusAccuracyTests` are therefore 0 and must stay there; a regression is now a real defect rather than a
  known gap being re-measured. Under the previous configuration (RapidOCR reading window crops) the same corpus
  and the same ground truth scored 24 missing, 32 wrong, 13 silent-wrong and 24 warnings — every one of them a
  glyph-level failure that exact template matching removed outright. `AccuracySpike --rapid` still scores the old
  configuration, so the comparison stays reproducible.
- **Ground-truth ordering was a latent defect, fixed once and mechanically.** The bootstrap recorded RapidOCR's
  *detection* order, which is not the window's reading order; the user verified values, never order, and the plan
  makes order part of the contract because it is how the wikitext gets laid back out. 9 windows were reordered by
  `AccuracySpike --adopt-reading-order`, which rewrites a window only when its stats are the **same multiset** in
  both — so it can reorder entries and can never add, remove or alter a value, and it reports anything it refuses.
  Reach for it only when an engine change moves reading order legitimately, never to make a failing value pass.
- **Verified ground truth can still be wrong, and a wrong entry hides a real error.** One entry kept an OCR
  artifact through review (`Bumning Affliction III` — the `rn`->`m` cluster; corrected to `Burning` only after
  reading the pixels at 6x). While it stood, the window that reproduced that same artifact scored as *correct*.
  Fixing one word moved `silent-wrong` from 12 to 13. When a ground-truth value looks like a known OCR
  corruption, check it against the image (`OcrSpike --crop ... --scale 6 --save`) rather than trusting the review.

**Full-frame OCR needs `ImgResize` raised, or the detector finds almost nothing.**
`RapidOcrOptions.Default.ImgResize` (1024) downsamples any larger image before detection; at a real 2560x1440
screenshot that shrinks our ~9-11px UI text below a usable threshold (confirmed: default settings found 33
garbled lines and zero `Description` tokens on a real screenshot with 3 real item windows). Fixed inside
`RapidOcrEngine` itself: `ImgResize = Math.Max(1024, Math.Max(image.Width, image.Height))` — the 1024 floor keeps
small per-window crops (milestone 1's use case) behaving identically to before; the dynamic ceiling fixes
full-frame recognition (361 lines, all real `Description` anchors found, on the same screenshot post-fix). Costs
~4s for a full frame — fine for a hotkey-triggered, non-realtime action, but don't be surprised by it.

**Model files resolve against the assembly directory, not the working directory.** `RapidOcr.InitModels()` with
no arguments looks for `models/v5/*.onnx` relative to the *current* directory, which made every documented
`dotnet run --project tools/...` command fail with "Detector model file does not exist" — `dotnet run` sets the
working directory to the project folder, while the models are copied next to the binary. `RapidOcrEngine` now
passes explicit paths built from `AppContext.BaseDirectory`. This is separate from (and in addition to) the
direct-`PackageReference` requirement noted in the solution layout: one controls whether the files are *copied*,
this controls whether they're *found*.

**Item leveling (`+X`) — v1 only processes `+0`.** Items (and spells) can be leveled up in-game (`Robe of the Ishva
+2`); the wiki only stores level-0 data. The `+X` suffix is always stripped before using the name to key the ledger
or look up the wiki page. Real per-stat level-0 downscaling is **deferred** (the actual formula isn't safely
reverse-engineerable from the wiki — see the plan's "Item leveling" and milestone 8 sections): a parsed item with
`X>0` is treated as ineligible in v1 — warn the user, skip it, write no ledger row — via a placeholder
`ILevelNormalizer` seam rather than guessed-at math. Don't implement real stat scaling without re-reading that plan
section first; it documents what was already investigated (and ruled out) on the wiki side.

**Exaltation eligibility.** Item windows can show "exaltation" slots (Ornamentation/Focus/Click/Worn/Proc), each
either `empty` or holding `<Name> (Exaltation)`. A slot only appears once its tier unlocks it — below that, a native
effect the item shipped with (e.g. a click effect) shows as a plain stat line instead. When a slot is filled, compare
`<Name>` to the item's own base name: a match means it's the item's own (now-removable) native exaltation — proceed
normally. A mismatch means a *foreign* exaltation is attached (the item has been augmented from another item) — the
item is **not eligible for automated processing**; warn the user, let them cancel, and **write no ledger entry at
all** (not `flagged`, not `skipped` — it was never actually checked). Note the window title's `(Augmented)` suffix
fires for a filled native slot too, so it is not itself a foreign-modification signal.

**Known unresolvable ambiguity: Attunable vs. No Trade.** An item natively `Attunable` shows `No Trade` once
equipped/traded, but some items are natively `No Trade` with no `Attunable` state ever. The window can't
disambiguate these, so a flag mismatch here needs the user's judgment rather than being auto-corrected — same
"user can override/cancel" pattern as the exaltation case, no separate pipeline behavior. **Resolved behaviour
(user, 2026-09-24): keep the wiki's `Attunable` and alert.** The page is the more informed source here, because
it was written by someone who saw the item before it was attuned. See "Reading and editing wiki pages" for the
detail, including that the user may later want this pair treated as *matching* via a setting.

**OCR engine choice: RapidOCR — this was tested, not assumed, and the alternative has been deleted.** The
OS-provided `Windows.Media.Ocr` was the original default; testing against real item windows (`tools/OcrSpike`)
found it unreliable on the game's ~9-11px UI text even after upscaling (dropped numeric values, `rn`->`m`/
`ti`->`b` misreads on labels, occasional single-character corruption in payload text like item/exaltation names).
Ruled out JPEG compression as the cause (reproduced identically against a live, lossless capture). Tried
`RapidOcrNet` (PaddleOCR PP-OCRv5 via ONNX) next and it was dramatically better on the *same* crops **at native
resolution, with no upscaling** — nearly everything came back correct, including roman numerals and payload text
Windows OCR had corrupted; upscaling it actually made results slightly worse. Full before/after comparison is in
the plan's milestone 1 writeup — read that before reaching for a different engine.

`WindowsOcrEngine` was kept for a while as a fallback/comparison, then **removed** once it was clear it had no
remaining use: it needs an OS language pack the user must install, needs 3x upscaling to be usable at all, and
still misreads on clean lossless captures (it read `Race: ALL` as `Race: Al I` on a current sample). Two engine
implementations to maintain wasn't worth that. `IOcrEngine` stays — `Core` can't reference the Windows-only `Ocr`
project, so the port is needed for layering regardless, and it keeps the engine swappable.

**OCR is still not perfect — the milestone 2 parser must not trust it blindly, just with lighter mitigations than
originally planned.** With `RapidOcrEngine` at native res: `Ornamentation` -> `Omamentation` and `Worn` ->
`Wom`/`Womn` (an `rn`-ish confusion) persisted across every test regardless of engine or scale — treat it as a
genuinely hard case for this exact font/pixel-size, not something to keep chasing. A small lexicon/edit-distance
correction covering just the handful of known field labels handles it. Also keep: fuzzy/edit-distance comparison
(not exact equality) for the native-vs-foreign exaltation name check and any wiki-page-title lookup by OCR'd name
— cheap insurance, and an isolated dropped digit was seen even with the better engine; and "a field the parser
expects but doesn't find is 'OCR uncertain,' not 'absent from the game'" — ask the user rather than assume. Drop:
the mandatory upscale-before-recognition step and any multi-scale-pass plan — not needed for `RapidOcrEngine`, and
upscaling measurably hurt it in testing. Construct `RapidOcrEngine` once and reuse it (it loads 3 ONNX models in
its constructor) rather than per-capture.

A broader census across 6 real windows (~100+ recognized lines: weapons, armor, ammo, a charge item, a consumable,
a quest token) found the `orn`->`om`-ish cluster to be the **only** recurring substantive error — digits (incl.
lone `1`), fractions, apostrophes, and decimals all came through reliably. Don't pre-emptively guard against other
classic small-text OCR confusions (`0`/`O`, `1`/`l`/`I`, `cl`/`d`, etc.) without evidence; none showed up here.

**Where the label-correction fix belongs**: not inside either `IOcrEngine` implementation (that's game-domain
vocabulary, not an OCR-engine concern, and will differ per future `IEntityKind`) — it's a milestone 2 Parse-step
component, e.g. `Core.Ocr.FieldLabelLexicon`, living with the Item parser: a small list of expected field labels
with edit-distance (≤1-2) correction applied per recognized label token before matching it to a field. Keep it
separate from the wiki mapping config (that's user-editable MediaWiki vocabulary; this is stable game-UI
vocabulary) and testable on plain strings without an image/OCR round-trip. Start small (really just
`Ornamentation`/`Worn` today) and grow only as real evidence demands.

## Wiki reference (eqlwiki.com)

- MediaWiki 1.45.3, `api.php` at the site root (no script path). `login`/`clientlogin` API modules are available;
  there is **no OAuth extension**, so auth is via a bot password (Special:BotPasswords), which also bypasses the
  site's OATHAuth 2FA. Credentials are stored via Windows Credential Manager/DPAPI, never in the repo.
  - **Reads are anonymous; only writes need the credential.** That keeps the common path — check an item, find it
    already correct — free of credentials entirely.
  - **The client must carry a cookie container.** MediaWiki's login token, its session and the later CSRF token are
    tied together by cookies, so an `HttpClient` built without one fails at the second step with a `badtoken` that
    reads like a token-handling bug. `MediaWikiClient.Create` builds a correctly configured one.
  - **An API error arrives as HTTP 200 with an `error` object**, so the status code proves nothing; the client
    unwraps it into a `MediaWikiException` carrying the wiki's own code.
  - **Every edit sends `basetimestamp` and `assert=user`.** The first is the edit-conflict guard — without it an
    edit somebody else saved between our read and our write is silently reverted, which is the difference between
    a patch and a revert on a wiki other people are editing. The second turns an expired session into a loud
    failure instead of an anonymous IP edit. `nocreate` is set too: creating a page is a different feature with
    different review requirements, and doing it by accident is worse than failing.
  - **The password is never in a file, a command line or an environment variable.** `WikiSpike login` reads it
    from the console (unechoed) straight into Credential Manager. A command line would land in shell history and
    in the process list.
  - **Verify a credential with `WikiSpike whoami`, which writes nothing.** It logs in and then *asks* the wiki what
    the session may do (`meta=userinfo&uiprop=rights`). The alternative — attempting an edit to see whether it
    works — leaves a permanent revision in some page's history, and an ordinary editor on this wiki cannot delete
    a page or a revision. `whoami` also catches the failure that matters most with a fresh bot password: a login
    that silently didn't stick, which would otherwise edit as the user's IP address.
  - **Verified live (2026-09-25): ConfirmEdit/Turnstile does not block a logged-in API edit.** The extension is
    installed and could have blocked automated edits outright, so this was a real open question; it is now
    answered for an authenticated bot-password session. The CSRF token flow, `assert=user` and `nocreate` are all
    confirmed working against the real wiki too (`WikiSpike edit` on the developer's own sandbox page, restored byte for byte
    and checked by an independent read).
  - **Not verified, and deliberately left that way: that `basetimestamp` *rejects* a concurrent edit.** Only that
    it is accepted in the format sent. MediaWiki attempts a three-way merge when a base timestamp is supplied, so
    non-conflicting concurrency is merged rather than refused — the guard catches *unmergeable* concurrency, not
    all of it. **The user ruled out investigating further** (2026-09-25): the wiki has few editors and ~19,000 item
    pages, so a collision inside the seconds-wide window between this tool's read and its write is effectively
    impossible, and a user who does hit one can resolve it by hand. Keep sending the parameter — it costs nothing
    and is the right thing to send — but don't build machinery around it.
- Item pages follow this shape (see `Help:Contents` for the canonical blueprint, and e.g. `Earring of Bashing` for a
  real example with lore):
  - An era template at the top (`{{Classic Era}}`, `{{Kunark Era}}`, ...).
  - Optionally a `[[File:...]]` in-world screenshot before `<onlyinclude>` — preserve verbatim if present, never
    generate one.
  - `<onlyinclude>{{Itempage |itemname= |lucy_img_ID= |statsblock= |focus_effect= |notes= |merchant_value= |...}}
    </onlyinclude>`. `notes` holds lore as `{{Item Lore|...}}` plus possibly other human-written text — only the
    lore wrapper is machine-edited.
  - Trailing `[[Category:...]]` lines, conditionally present based on class/slot/skill (these are derived from
    parsed stats, not hardcoded).
- Iteration 1 only reads/writes the fields verifiable from the in-game item window: `itemname`, icon
  (`lucy_img_ID`), `statsblock`, `focus_effect`, lore (inside `notes`), `merchant_value`. Everything else on an
  existing page (`dropsfrom`, `soldby`, `relatedquests`, `recipes`, `bookcontents`, `foraged`, unrelated categories)
  is preserved untouched.

## Commands

No dependencies beyond the .NET SDK (net10.0 / net10.0-windows10.0.19041.0 targets — see Solution layout above).
RapidOCR's models are bundled with its NuGet package, so there is nothing to install and no OS OCR language pack
to configure.

```powershell
dotnet build                                                    # build everything
dotnet test                                                     # run all tests
dotnet test --filter "FullyQualifiedName~StatsBlockParserTests" # run one test class
dotnet run --project src/EQLWikiAssistant.App                   # run the WPF app

# OCR tuning against a real sample screenshot (feed it native resolution — upscaling hurts this engine):
dotnet run --project tools/OcrSpike -- "samples/some screenshot.png" --crop x,y,w,h --save out.png

# Locate tuning against a real full screenshot:
dotnet run --project tools/LocateSpike -- "samples/some screenshot.png" --save out.png

# Measure the window chrome's actual pixel colours before touching WindowBoundsFinder's thresholds
# (here: 60 pixels rightward from 1490,500, to cross a window's right-hand outline):
dotnet run --project tools/LocateSpike -- "samples/some screenshot.png" --probe 1490,500,1,0,60

# Full Locate -> Parse pipeline, dumping every parsed field per window:
dotnet run --project tools/ParseSpike -- "samples/some screenshot.png"

# Score the whole corpus against tracked ground truth (the number to judge any OCR/parser change by):
dotnet run --project tools/AccuracySpike                 # summary
dotnet run --project tools/AccuracySpike -- --diff       # plus every differing field
dotnet run --project tools/AccuracySpike -- --bootstrap  # regenerate ground truth after a new capture batch

# The corpus regression test (~3 min, opt-in so it can't get muted):
$env:EQLWIKI_ACCURACY=1; dotnet test --filter "FullyQualifiedName~CorpusAccuracyTests"

# Glyph matching: read a region's raw pixel intensities (the --probe of this work; measure before tuning),
# then read it back through the real engine:
dotnet run --project tools/GlyphSpike -- dump "samples/some screenshot.png" 864,373,12,12 [--raw]
dotnet run --project tools/GlyphSpike -- read "samples/some screenshot.png" 774,279,388,522

# Regenerate the atlas from the in-game Notes Window glyph sheet. The region only has to *contain* the character
# rows as consecutive bands — it is matched by glyph-count sequence, not by coordinates, because the sheet can
# never be reopened in the same place twice. Update SheetRows() in GlyphSpike if the sheet's contents change.
# Then relearn cell widths from real windows: the sheet spaces every character out, so it cannot show them.
dotnet run --project tools/GlyphSpike -- atlas "samples/notepad-with-all-glyphs.png" 986,700,570,250 \
  --out src/EQLWikiAssistant.Core/Glyphs/eql-ui-font.atlas
dotnet run --project tools/GlyphSpike -- advances "samples/any screenshot.png"   # updates the atlas in place

# Wiki side. Reads are anonymous, so fetch/roundtrip/grammar need no credential:
dotnet run --project tools/WikiSpike -- fetch "Earring of Bashing"       # page source + parsed v1 fields
dotnet run --project tools/WikiSpike -- roundtrip 400 --seed 4242        # byte-for-byte check on a live sample
dotnet run --project tools/WikiSpike -- grammar 400                      # plus the label/flag census
dotnet run --project tools/WikiSpike -- grammar --cached .local-data/wiki-pages   # re-run offline on the cache

# Diff every verified capture against its live wiki page (eligibility applied first — see the note above about
# levelled items, or the numbers lie). The wiki-side equivalent of AccuracySpike:
dotnet run --project tools/WikiSpike -- analyze
dotnet run --project tools/WikiSpike -- analyze --detail   # plus every non-matching field, per item

# Store the bot password (prompts; never pass it as an argument — that lands in shell history and the process
# list). Create one first at https://eqlwiki.com/Special:BotPasswords with "Edit existing pages" granted.
dotnet run --project tools/WikiSpike -- login
dotnet run --project tools/WikiSpike -- whoami     # confirm it logs in AND may edit; writes nothing
dotnet run --project tools/WikiSpike -- edit "User:YourName/sandbox"   # 2 revisions, self-reverting, confirms first
```

Real screenshots for manual testing/tuning go in `samples/` (gitignored, never commit game screenshots).

**The sample set is captured through the tool's own capture path**, not saved screenshots — lossless PNG via
Windows Graphics Capture, the same code the app uses, so the pixel values `WindowBoundsFinder` depends on are the
ones it will really see:

```powershell
dotnet run --project tools/CaptureSpike -- list EverQuest        # find the exact window title once
dotnet run --project tools/CaptureSpike -- capture "<title>" "samples/NN-description.png"
```

Arrange the game, alt-tab to a terminal, then capture — Graphics Capture reads an unfocused window fine. Samples
are named `NN-description.png` (with a sub-letter for variants of one scenario, e.g. `06a`/`06b`), and the golden
tests reference those names directly, so renaming one means updating the tests. Current coverage is the geometry
and negative cases; item-type, exaltation and eligibility captures are still to come.
