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
  `OcrLine`/`OcrWord` (`Core.Ocr`), **`ItemWindowLocator`** (`Core.Locate` — groups a screenshot's OCR lines into
  per-window clusters; see "Locating item windows" below), and `EditDistance` (`Core.Text` — fuzzy string
  matching, used by locate and destined for the future field-label lexicon and exaltation name checks). Anything
  here must stay portable and free of MediaWiki syntax knowledge — see "Wiki mapping layer" below. Interfaces the
  pipeline depends on live here even though their real implementations are Windows-only, since `Core` can't
  reference the Windows-only projects.
- `src/EQLWikiAssistant.Capture` (`net10.0-windows10.0.19041.0`) — `GlobalHotKey` (Win32 `RegisterHotKey`, its own
  message-only window/thread, no UI-framework dependency), `WindowFinder` (find a window by title), `WindowCapturer`
  (Windows Graphics Capture of a specific window, via `Vortice.Direct3D11`/`Vortice.DXGI` for the D3D11 device).
  **Read the `[GeneratedComInterface]` note below before touching `Interop/`** — it documents a real, confirmed
  runtime failure mode, not a style preference.
- `src/EQLWikiAssistant.Ocr` (`net10.0-windows10.0.19041.0`) — two `IOcrEngine` implementations: **`RapidOcrEngine`
  (the default) wrapping `RapidOcrNet`** (PaddleOCR PP-OCRv5 via ONNX, local/offline), and `WindowsOcrEngine`
  wrapping `Windows.Media.Ocr`, kept as a fallback/comparison option. See "OCR engine choice" below — this wasn't
  arbitrary, Windows OCR was tried first and replaced after real testing showed it meaningfully less accurate.
- `src/EQLWikiAssistant.Wiki` (`net10.0`) — MediaWiki API client (bot-password auth), wikitext parsing/rendering,
  the local icon file cache, and the checked-items ledger.
- `src/EQLWikiAssistant.App` (`net10.0-windows10.0.19041.0`, WPF) — UI: capture trigger, review/diff screen,
  settings/mapping editor, ledger view.
- `tests/EQLWikiAssistant.Tests` (`net10.0-windows10.0.19041.0`) — unit and golden-file tests across all projects.
- `tools/EQLWikiAssistant.TestSupport`, `tools/OcrSpike`, `tools/CaptureSpike`, `tools/LocateSpike`
  (`net10.0-windows10.0.19041.0`, dev-only, not shipped) — `TestSupport.ImageFile` loads a screenshot file from
  disk into a `CapturedImage` (the real app only ever captures a live window, never reads a file — this exists
  for tests/tooling), `TestSupport.RepoPaths` for finding `samples/` reliably from a test/tool's output
  directory, and `TestSupport.DebugDraw` for drawing debug rectangle overlays; `OcrSpike` iterates on OCR
  accuracy against real sample screenshots (crop, upscale, `--engine windows|rapid`, dump recognized
  lines+bounding boxes); `CaptureSpike` exercises `WindowFinder`/`WindowCapturer`/`GlobalHotKey` (list windows,
  capture one to a PNG, test-fire a hotkey); `LocateSpike` runs whole-screenshot OCR + `ItemWindowLocator` and can
  `--save` a debug overlay (green/red by `PossiblyOccluded`) for eyeballing results. Keep using these — don't
  recreate ad hoc versions — when tuning parse logic or debugging capture/locate.
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
warning rather than silently processed as if complete. Implemented as `ItemWindowLocator.PossiblyOccluded` (no
title-bar line found above the window's `Description` anchor) — a real but **unvalidated** heuristic, since no
genuinely occluded sample exists yet (see the plan's "Needed from the user"). Don't treat it as more trustworthy
than that without testing it against a real occluded window first.

**Locating item windows (`ItemWindowLocator`, `Core.Locate`).** Run OCR on the *whole* screenshot (see "Full-frame
OCR" below), then cluster its lines into per-window groups — no pixel-level text detection of our own is needed,
RapidOCR's detector already finds everything, locate just groups it correctly. Two simpler approaches were tried
and failed for specific, informative reasons (full writeup in the plan's milestone 2 section — read it before
reimplementing this from scratch, the failure modes are the actual design constraint):
- Pure bounding-box-gap clustering bridges across *different*, merely-nearby UI panels on a busy real screenshot
  (character sheet, buffs, inventory grid all densely packed).
- Adding a pixel "dark bridge" check (window interiors are near-black, ~RGB(16,16,16); real gaps are a distinctly
  lighter color, ~RGB(150+,120+,70+) — a wide, reliable margin) fixes that, but *two different item windows*
  sitting close together are both dark UI too, so pure darkness still can't tell "same window" from "next one over."
- What works: each `Description` tab line seeds one window; unclaimed lines join the nearest anchor whose (a)
  fixed, anchor-relative size envelope contains them (this — not cluster-relative growth — is what actually caps
  runaway spread into a neighbor) and (b) dark-pixel bridge reaches them from an already-claimed line.
- A real bug worth remembering if this code gets touched: sampling the "bridge" between two boxes that already
  touch/overlap (common for adjacent text rows) can land the sample *inside* one box's own text glyph — a bright
  pixel, falsely read as "not dark," wrongly splitting two lines that belong together. Fix was to compute the
  actual gap rectangle (never sample inside either box) and skip the check entirely when boxes already touch.
- Validated against 6 real screenshots (`tools/LocateSpike`, golden tests in `Tests/Locate/`): single windows,
  3 adjacent windows (correctly separated, no cross-contamination), and a tooltip sitting against two real
  windows (tooltip fully excluded even though it visually overlaps one). `LocatedWindow.Bounds` is the union of
  its lines' boxes, not pixel-exact chrome — fine for grouping, but add a margin if pixels are ever cropped from it.

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

**OCR engine choice: `RapidOcrEngine`, not `WindowsOcrEngine` — this was tested, not assumed.** Windows OCR was the
original default; testing against real item windows (`tools/OcrSpike`) found it unreliable on the game's ~9-11px
UI text even after upscaling (dropped numeric values, `rn`->`m`/`ti`->`b` misreads on labels, occasional
single-character corruption in payload text like item/exaltation names). Ruled out JPEG compression as the cause
(reproduced identically against a live, lossless capture). Tried `RapidOcrNet` (PaddleOCR PP-OCRv5 via ONNX) next
and it was dramatically better on the *same* crops **at native resolution, with no upscaling** — nearly everything
came back correct, including roman numerals and payload text Windows OCR had corrupted; upscaling it actually made
results slightly worse. Full before/after comparison is in the plan's milestone 1 writeup — read it before
second-guessing the engine choice or reverting to Windows OCR.

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
RapidOCR's models are bundled with its NuGet package, nothing to install. `WindowsOcrEngine` (fallback only) needs
an OCR language pack (Settings -> Time & Language -> Language -> Optical character recognition).

```powershell
dotnet build                                                    # build everything
dotnet test                                                     # run all tests
dotnet test --filter "FullyQualifiedName~StatsBlockParserTests" # run one test class
dotnet run --project src/EQLWikiAssistant.App                   # run the WPF app

# OCR tuning against a real sample screenshot (engine defaults to rapid; pass --engine windows to compare):
dotnet run --project tools/OcrSpike -- "samples/some item.jpg" --crop x,y,w,h --save out.png

# Locate tuning against a real full screenshot:
dotnet run --project tools/LocateSpike -- "samples/some screenshot.png" --save out.png
```

Real screenshots for manual testing/tuning go in `samples/` (gitignored, never commit game screenshots).
