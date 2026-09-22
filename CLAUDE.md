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
  pipeline abstractions (`IEntityKind` and friends), **and the portable ports** `IOcrEngine` + `CapturedImage`/
  `Rect`/`OcrLine`/`OcrWord` (`Core.Ocr` namespace). Anything here must stay portable and free of MediaWiki syntax
  knowledge — see "Wiki mapping layer" below. Interfaces the pipeline depends on live here even though their real
  implementations are Windows-only, since `Core` can't reference the Windows-only projects.
- `src/EQLWikiAssistant.Capture` (`net10.0-windows10.0.19041.0`) — global hotkey + Windows Graphics Capture of the
  game window.
- `src/EQLWikiAssistant.Ocr` (`net10.0-windows10.0.19041.0`) — `WindowsOcrEngine : IOcrEngine`, wrapping
  `Windows.Media.Ocr`. Kept swappable (via the `Core`-side interface) in case accuracy requires Tesseract/ONNX
  instead — see the milestone 1 findings below on where it currently falls short.
- `src/EQLWikiAssistant.Wiki` (`net10.0`) — MediaWiki API client (bot-password auth), wikitext parsing/rendering,
  the local icon file cache, and the checked-items ledger.
- `src/EQLWikiAssistant.App` (`net10.0-windows10.0.19041.0`, WPF) — UI: capture trigger, review/diff screen,
  settings/mapping editor, ledger view.
- `tests/EQLWikiAssistant.Tests` (`net10.0-windows10.0.19041.0`) — unit and golden-file tests across all projects.
- `tools/EQLWikiAssistant.TestSupport`, `tools/OcrSpike` (`net10.0-windows10.0.19041.0`, dev-only, not shipped) —
  `TestSupport.ImageFile` loads a screenshot file from disk into a `CapturedImage` (the real app only ever captures
  a live window, never reads a file — this exists for tests/tooling); `OcrSpike` is a CLI for iterating on
  OCR/locate accuracy against real sample screenshots (crop, upscale, run `WindowsOcrEngine`, dump recognized
  lines+bounding boxes). Keep using it — don't recreate an ad hoc version — when tuning milestone 2's locate logic.

Note: WinRT namespaces like `Windows.Media.Ocr` and `Windows.Graphics.Capture` are only projected on a Windows-SDK-
versioned TFM (`net10.0-windows10.0.19041.0`), not plain `net10.0-windows` — every project that touches them must
use the versioned form.

## Key architectural ideas

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
warning rather than silently processed as if complete.

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

**OCR is lossy in specific, known ways — the milestone 2 parser must compensate, not trust it verbatim.** Confirmed
against real screenshots via `tools/OcrSpike` (see the plan's milestone 1 writeup for full detail): always upscale
crops before recognition (`CapturedImage.Resize`, 3x+); different scales drop different fields, so a missing
expected value means "OCR uncertain," not "absent from the game" — surface it for user confirmation rather than
silently accepting emptiness. `rn`->`m` and `ti`->`b` are consistent, scale-independent misreads on label words
(`Ornamentation`, `Worn`, `Description`, `Exaltation`) — fix via a small fixed-vocabulary lexicon with edit-distance
correction, not by tuning OCR further. Payload text (item/exaltation names) can also take single-character hits, so
the native-vs-foreign exaltation name check and any wiki-page-title lookup by OCR'd name must use fuzzy/edit-distance
comparison, never exact equality. The item name usually appears twice (title bar + content) — reconcile both rather
than trusting one.

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
Windows OCR also needs a language pack installed (Settings -> Time & Language -> Language -> Optical character
recognition).

```powershell
dotnet build                                                    # build everything
dotnet test                                                     # run all tests
dotnet test --filter "FullyQualifiedName~StatsBlockParserTests" # run one test class
dotnet run --project src/EQLWikiAssistant.App                   # run the WPF app

# OCR/locate tuning against a real sample screenshot:
dotnet run --project tools/OcrSpike -- "samples/some item.jpg" --crop x,y,w,h --scale 3.0 --save out.png
```

Real screenshots for manual testing/tuning go in `samples/` (gitignored, never commit game screenshots).
