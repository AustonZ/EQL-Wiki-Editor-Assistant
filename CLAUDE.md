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
- `src/EQLWikiAssistant.Ocr` (`net10.0-windows10.0.19041.0`) — the `IOcrEngine` implementation: **`RapidOcrEngine`
  wrapping `RapidOcrNet`** (PaddleOCR PP-OCRv5 via ONNX, local/offline). See "OCR engine choice" below — this
  wasn't arbitrary: the OS-provided `Windows.Media.Ocr` was tried first and replaced after real testing showed it
  meaningfully less accurate, then removed outright once it had no remaining use.
- `src/EQLWikiAssistant.Wiki` (`net10.0`) — MediaWiki API client (bot-password auth), wikitext parsing/rendering,
  the local icon file cache, and the checked-items ledger.
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
  window; `AccuracySpike` scores that pipeline against tracked ground truth (see "Measuring extraction accuracy").
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
fields and re-rendering it — not comparing template parameters directly.

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
- **A punctuation-only junk row shifts the whole positional header.** The window's own chrome occasionally reads
  as text — a real capture had the tab-bar corner, clipped at the crop's left edge, recognized as `()` on its own
  row between the tab row and the content-area name, which made the name parse as `()` and pushed the real name
  row into the flags field. Rows with no letters or digits at all are dropped before parsing. Note the safer fix
  is dropping junk, *not* identifying the name row by similarity to the title — that would quietly defeat the
  title-vs-content occlusion check, whose whole job is to notice when those two genuinely differ.
- **Open issue — OCR drops isolated stat digits, and it's the dominant remaining data gap.** Measured over 100
  real item windows: ~24 stat values are lost, so roughly 1 item in 5 is missing at least one stat. The parser
  surfaces each as an orphaned-label warning rather than guessing (never silently drop the field), but that's a
  manual-review cost, not a fix. Confirmed it is genuinely the OCR and not the capture or the parser: the digits
  are plainly present in the pixels (a window showing four `7`s returns no `7` fragment at all), while `AC: 6`
  and `HP: 55` in the same window read fine.
  - **A targeted re-OCR of just the value cell at 4x recovers most of them** (3 of 4 on the test case, vs 0 at
    native resolution). Note this is the *opposite* of the general "upscaling hurts RapidOCR" finding, which was
    measured on full window crops — for a tiny isolated-digit region, upscaling clearly helps.
  - Watch out when investigating: the lower stat block (`Strength`/`Wisdom`/…) puts its values in a **different
    column** from the upper two-column block (`Size`/`AC`/`Weight`/`HP`). Probing the wrong column reads as "the
    value isn't there at all" and sent this investigation down a false path once.
- `tools/ParseSpike` (mirrors `OcrSpike`/`LocateSpike`) runs the full Locate -> Parse pipeline against a real
  screenshot and dumps every parsed field per window (including a `[FOREIGN]` marker on foreign exaltations) —
  use this, don't recreate an ad hoc version, when tuning parser rules against new samples.

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
- Baseline at the time of writing: **43 samples, 101 windows (1 correctly occluded), 2072 correct fields, 24
  missing, 0 wrong, 0 silent-wrong, 29 warnings.** The 24 missing are the known dropped-digit issue above.

**Full-frame OCR needs `ImgResize` raised, or the detector finds almost nothing.**
`RapidOcrOptions.Default.ImgResize` (1024) downsamples any larger image before detection; at a real 2560x1440
screenshot that shrinks our ~9-11px UI text below a usable threshold (confirmed: default settings found 33
garbled lines and zero `Description` tokens on a real screenshot with 3 real item windows). Fixed inside
`RapidOcrEngine` itself: `ImgResize = Math.Max(1024, Math.Max(image.Width, image.Height))` — the 1024 floor keeps
small per-window crops (milestone 1's use case) behaving identically to before; the dynamic ceiling fixes
full-frame recognition (361 lines, all real `Description` anchors found, on the same screenshot post-fix). Costs
~4s for a full frame — fine for a hotkey-triggered, non-realtime action, but don't be surprised by it.

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
disambiguate these, so a flag mismatch here may need the user's judgment rather than being auto-corrected — same
"user can override/cancel" pattern as the exaltation case, no separate pipeline behavior.

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
  existing page (`dropsfrom`, `soldby`, `relatedquests`, `recipe`, `bookcontents`, `foraged`, unrelated categories)
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
