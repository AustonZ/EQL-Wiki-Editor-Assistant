# EQLWiki Editor Assistant — Plan (iteration 1: items)

## Context
Contributing to the EverQuest Legends Wiki (eqlwiki.com) currently means viewing an item in-game on a desktop, then
manually looking it up on a laptop, checking template/format currency, comparing values, and hand-editing wikitext.
The tool automates the generic parts: capture the in-game item window locally, OCR it, fetch the wiki page, diff, and
propose edits for the user to approve. Iteration 1 covers **items only**, but the design must allow spells, monsters
and quests to be added later.

Hard constraints: no network-traffic interception, no game-memory access; screenshots and OCR stay 100% local (no
cloud OCR); only wiki text/images are fetched (inbound) and only user-approved edits are sent (outbound). The repo has a
GitHub remote, so real screenshots (which may contain private info) are committed only once audited (`samples/`
stays gitignored, and audited frames are added deliberately).

## Decisions
- Stack: C# / .NET WPF, Windows-only, runs on the gaming desktop (no laptop needed).
- OCR: **RapidOCR** (PaddleOCR PP-OCRv5 models via ONNX, `RapidOcrNet` NuGet, local/offline) behind an `IOcrEngine`
  interface, updated from the original Windows-built-in default after milestone 1 testing showed it dramatically
  more accurate on the game's small UI text — see the milestone 1 writeup below for the comparison.
  The `WindowsOcrEngine` (`Windows.Media.Ocr`) implementation was kept for a while as a fallback/comparison and
  has since been **removed** (2026-09-23) — see the milestone 1 writeup's closing note for why.
- Game display: windowed/borderless -> Windows Graphics Capture of the game window.
- Wiki write: MediaWiki API with a bot password (user must create one at Special:BotPasswords; stored via Windows
  Credential Manager / DPAPI, never in the repo).
- Format rules: built up iteratively as small, individually testable checks (+ optional auto-fixes), starting from
  `Template:Itempage` and a reference page the user nominates.
- Icon ID for new pages: user types `lucy_img_ID`. For existing pages, compare the cropped in-game icon to the wiki's
  `File:item_<ID>.png` (perceptual hash / normalized correlation) and only **flag** mismatches (no fix flow in v1).
- **Icon image cache:** wiki icon files are static, so each `File:item_<ID>.png` is fetched at most once and stored in a
  local on-disk cache (app-data folder, keyed by icon ID; icons are shared across many items so expect many hits). The
  cache is checked before any network call; a precomputed perceptual hash can be stored alongside each file. No
  expiry by default; a "clear icon cache" / "re-download this icon" action lives in Settings for the rare case an icon
  file is replaced on the wiki. Missing-file responses (icon not on wiki) are recorded as negative entries with a short
  TTL so they aren't re-requested on every capture.
- **A wiki-source prettifier is part of v1 (user, 2026-09-25 — promoted from "parked future feature").** It always
  produces **its own separate edit**, never riding along with a data change: *"A single 'automatically reformatted'
  edit with no actual data changes is much easier to work with when reviewing diff history."*
  - **It runs second, after the data edit, and this is settled rather than arbitrary.** Running it first would not
    stay pretty: the data edit adds new content as unformatted lines (see the minimal-edit rule below), so the page
    would need reformatting again afterwards — prettify-first collapses into prettify-first-and-last. It would also
    force a purely syntactic pass to understand legacy forms with a very short remaining life (the `<ul><li>`
    merchant-value blocks, legacy flag lines, `{{Item Lore Missing}}`, stale era banners) that the semantic pass
    deletes anyway. Semantics before syntax.
  - **WIRED IN 2026-09-28.** `CommitAsync` runs the formatter against the page as re-read from the wiki and hands
    back a `FormattingProposal`; the review screen shows its own diff and its own "Save the formatting" button, under
    its own summary. Never a second automatic write, and a failure preparing it never fails the data commit. The
    plan's "a brand-new page goes through it before its first commit" **arrived with page creation on 2026-10-01**,
    in the stronger form: the formatter runs over a generated page before the proposal is shown, so a creation needs
    one commit rather than two.
  - **v1 runs it automatically as a follow-up step after any change**, and prompts the user to commit the
    reformatting if it finds any needed — modelled on a CI formatting gate, with the twist that this codebase starts
    messy and the reformat must not bury the data change. The goal is to make leaving a page pretty the
    lowest-friction option.
  - **A brand-new page must be pretty from its first revision.** There is no reason for a page's history to start
    dirty when it doesn't have to, so a new page goes through the prettifier *before* the first wiki commit rather
    than being committed and then tidied.
  - **MVP milestone**: the flag-only version — the tool marks a page as needing reformatting (it knows, because it
    knows when it added an unformatted line) and the user runs the prettifier separately.
  - **BUILT 2026-09-28** as `Wiki.Formatting.ItemPagePrettifier`. Its licence to rearrange pages is that it *proves*
    it preserved the content: every format is verified by re-parsing and comparing field by field, and a mismatch
    returns the original untouched. That caught a real bug on 14 pages (a line carrying a flag *and* a field). It
    only touches the inside of the template call, and it leaves alone — completely, not even the spacing — anything
    it does not understand.
    - **Legacy flags stop it dead** (user, 2026-09-28): *"I don't want the prettifier to get into the business of
      understanding/reformatting obsolete flags and fields."* Discarding those is the data pass's decision, made
      against a live capture. Legacy is told from current by *shape* (ALL-CAPS versus Title Case), measured across
      1,183 pages rather than by a vocabulary list — the same reasoning that forbids a known-flags list on the
      capture side.
    - **One bug class the content check cannot catch, and the plan should record it**: a `*` is a bullet only at the
      start of a line, so folding a one-line `|relatedquests = * [[Quest]]` onto the parameter's line changes the
      *rendering* while leaving the value byte-identical. Layout rules of that kind have to be right rather than
      verified.
    - Baseline: 744 real item pages, 0 refusals, 478 statsblocks left alone (469 of them for legacy flags — the
      wiki is still mostly P1999-imported), median size change +5 bytes.
  - Two design constraints this has always implied, both already honoured and both still binding: **the data edit
    never reformats incidentally** (no whitespace normalization, no tidying re-render, no "while we're here" fixes —
    which is why the wikitext layer treats raw source as the source of truth), and **non-compliance is reported
    rather than fixed** by the data pass, so parsing must be able to describe what is off without changing it.
- **The minimal-edit rule (user, 2026-09-25)**, which is what lets the data edit stay surgical against a page nobody
  formatted:
  - **Editing a flag or value that already exists: change it in place.** Nothing else on the line moves.
  - **Adding something entirely new: put it on its own line** and let the follow-up reformatting place it nicely.
    Refinement: insert **before the `Class:` line** rather than at the very end, so the diff reads naturally — the
    blueprint puts `Class` and `Race` last, so this is derived rather than invented, and it falls back to appending
    when there is no `Class:` line.
  - **A template parameter that does not exist at all** is a third case the rule needs, since there is no line to
    add it to: insert it in the blueprint's parameter order (`ItemPageDocument.WithParameter` currently refuses to
    invent one, deliberately, because placement is a judgement).
  - The rule's whole virtue is that it never requires the tool to understand a layout it did not create, which is
    where any cleverer placement rule would go wrong on a messy page.
- **Expected wiki-side change (user, 2026-09-24): most of `statsblock` promoted to explicit `Itempage`
  parameters.** The user intends to make that case to the other editors. Until it lands — and after, unless a
  programmatic bulk pass converts every existing page — the tool must still read the legacy free-text form.
  Design implication for milestone 6: a field's *location* is part of the mapping, not a constant. "AC lives in
  the statsblock" and "AC is `|ac=`" must both be expressible, so the migration becomes a config change rather
  than a rewrite. Don't bake "stats come from statsblock" into anything above the mapping layer.
- **Template compliance is part of every edit (user, 2026-09-24)**: the user's manual process always includes
  bringing the item in line with the current official template — cleaning up duplicate parameters, discarding
  legacy flags. That is milestone 4 scope and is distinct from the prettifier above: compliance changes what the
  page *says*, formatting changes only how it reads.
- Out of scope for v1: icon-fix workflow, spells/monsters/quests, drops/soldby/quests/recipes sections (preserved
  untouched on existing pages).
- **UI scale and UI skins: out of scope for v1, but a likely feature if this is ever shared with other players.**
  EQL supports both. The tool is currently built and validated against one player's fixed UI scale and default
  skin, and that assumption is baked deeper than it looks: **every constant in `WindowBoundsFinder` is a measured
  pixel value** — the content outline's 50-62 grey, the 0-8 outer frame, the ~16 interior, the probe distances,
  the size ceilings (600x700), the line-verification run length. A different UI scale changes the distances; a
  different skin can change the colours outright, and would break outline tracing completely rather than
  degrading gracefully. `ItemParser`'s 8px row-grouping tolerance is scale-dependent too.
  - Design implication accepted for now: keep those constants together, named, and documented with their measured
    values (already done) so they can later be lifted into a per-skin/per-scale profile rather than hunted down.
    Don't add a profile abstraction yet — there's exactly one profile and no second data point to design against.
  - If this is shared later, the honest first step is a calibration pass (capture a known window, measure the
    chrome, derive the profile) rather than trying to make the thresholds scale-invariant by guesswork.
  - Sample screenshots are being re-taken through the tool's own lossless capture path (see "Sample screenshot
    set" below); one capture at a different UI scale is included purely to measure how badly scale-locked this is.

## "Verified for EQLegends" (found by the user 2026-09-29; v1 reports, v2 may write)
A step this plan missed entirely. The wiki shows a toast on every page not on its `VerifiedPages` list, and an editor
clears it by typing "Verified" into it. **v1 reads that list and warns when a page is unverified, and does nothing
else** — see CLAUDE.md for the format, the caching and the measurements.
- **The tool must never claim a verification**, and this is the durable part of the decision: verification attests
  that a *whole page* is accurate, including the drops, quests and recipes this tool never reads. That is also why it
  is not a blocker — it would be demanding something the tool cannot assess. Measured support: only **48 of 744**
  cached item pages are verified, so blocking would leave ~93% of items permanently unsettled.
- **Only the unverified branch speaks** (user). An alert on every page, either "not yet verified" or "already
  verified", is an alert everyone learns to ignore.
- **v2 write path is understood and cheap**: a plain `action=edit` with `appendtext`, exactly as the toast does. The
  trap is that it does not dedupe (the live list has 13 duplicate lines), so a writer must check the list first. The
  open question is not *how* but *whether* — the tool would be asserting something broader than it checks, so it
  probably belongs behind an explicit user action rather than riding along with a commit.
- **Verification is per-title and sticky, not per-revision**, so this tool's own edits to an already-verified page
  leave it marked verified with nobody told. Worth raising with the wiki's maintainers rather than working around.

## Creating pages for new items — BUILT 2026-10-01 (was out of scope, promoted by the user)

This plan listed page creation nowhere except as the thing `nocreate` deliberately prevented. The user asked for it
after reviewing where v1 stood, and the measurement justified it immediately: **7 of the 113 items in their live
ledger have no wiki page**, and they are EQL-native things a Project1999-derived wiki was never going to carry
(`Armor Ornamentation Token`, `Primary Class Unlock Token`, `Potion of Amnesia`, `Token of Reclamation`, ...). Before
this the tool's only offer on such an item was "Skip".

- **It reuses the data pass instead of adding a renderer**, which is the decision worth carrying forward: generate
  an empty blueprint skeleton, let the existing analyzer and editor fill it, then format it. A page renderer would
  have been a second home for every rule about how item data is written, and this project has paid for a duplicated
  rule three times already (the ledger fingerprint, the login gate, the flag dialect).
- **This plan's "a brand-new page must be pretty from its first revision" is now implemented**, and in the stronger
  form: the formatter runs over the generated text *before* the proposal is shown, so creation lands a finished page
  in **one** commit rather than a commit plus a reformat. A formatting prompt still follows the save, because the
  text is editable and a hand-edit can mangle it.
- **The permanence constraint shaped the gate more than anything else.** An ordinary editor on this wiki cannot
  delete a page, so creation is offered *only* where the lookup says `NotFound` — never for a quote-variant
  candidate (which would create the duplicate the variant search exists to prevent) and never for a title-illegal
  name (which has no title to create at). Both still warn; neither gets a button. The confirmation names the title,
  since that is the part that cannot be taken back.
- **`lucy_img_ID` is the one field no capture can supply**, and the user chose to warn rather than block. What makes
  that safe is the ledger: the outcome is read off the text actually saved, and a page still missing its icon is
  recorded `Flagged`, so it keeps coming back. A settled row would have lost it forever.
- See CLAUDE.md for the rest — the `createonly` guard and why it replaces `basetimestamp`, the new
  `CheckOutcome.Created`, the enum-ordering hazard that would have silently rewritten the user's live ledger, and
  the two bugs `WikiSpike preview` found the first time it ran against a real capture.

## Automatic icon identification — BUILT 2026-10-02 (the plan said a human types the id)

This plan's decision read: *"Icon ID for new pages: user types `lucy_img_ID`."* That was right while the only
reference artwork was the wiki's own 796 files. The user then extracted **every icon from the game's asset files** —
11,592 PNGs named by id — which makes the id something the tool can read off the capture instead.

- **Measured before it was built**, because the question is not "can icons be compared" but "can one be picked out of
  11,562": `WikiSpike iconsearch` scores **87/90 (96.7%) top-1** against the items' own live wiki pages.
- **The gate is the margin to the runner-up, not the absolute distance**, and that reversed the obvious choice — a
  *correct* match reaches 0.2977 because the game resamples each icon ~1.10x, so an absolute cutoff loses right
  answers while catching nothing extra. Full figures in CLAUDE.md.
- **No threshold separates right from wrong perfectly**, so the user confirming by eye is the safety argument, not
  the number: the matched artwork is shown beside the captured one, green when written and amber when not.
- **It only ever runs on creation.** An existing page's icon id is still compared and flagged. The asymmetry is the
  same one this plan already draws everywhere: there is nothing to overwrite on a page that does not exist.
- **Uploading turned out to be the common case**, not a trimming: the wiki has 796 of 11,592 icons, so 93% are
  missing. The tool writes the id and uploads the matching file as a pair, which keeps a new page correct even for
  the 2-in-90 items where the wiki's historical numbering disagrees with the game's.
- **The one genuinely new risk is a stale fingerprint index**, and it is a correctness problem rather than a
  performance one — a cached list of which icons exist that misses newly added ones would match those items against
  the closest *older* icon, confidently. The index records what it was built from and rebuilds when that changes.

## Offline behaviour (user, 2026-09-29)
**A wiki outage aborts the chain of actions that hit it, with a critical dialog** — it does not degrade. The tool
exists to compare captures against the wiki, so without it there is nothing useful left to do, and the previous
behaviour (one `Failed` row per window under a "3 item windows checked" status line) read like a tool malfunction
rather than an outage. `WikiUnavailableException` carries this and is deliberately distinct from `MediaWikiException`,
which means the wiki *answered* about one page and stays per-window. See CLAUDE.md for the split and its traps.
- **An explicit offline mode is a future feature**, not a fallback to slide into: caching captures for processing once
  the wiki is back, with its own visible state. Until then, failing loudly beats half-working.

## Items that share a name — v2 design consideration (user, 2026-10-01)

**The whole tool keys on the item's name, and for a small real set of items that key is not unique.** Raised by the
user for v2; measured against the live wiki the same day, and the measurement changed what the problem looks like.
Full detail and the live-risk analysis are in CLAUDE.md — the parts that belong in a *plan* are these:

- **It is small but real: 6 pairs out of 23,577 non-redirect pages**, marked on the wiki by a trailing `*`
  (`Shimmering Pearl`/`Shimmering Pearl*`, `Club`/`Club*`, `Dagger`/`Dagger*`, ...), plus a separate `(qualifier)`
  convention (`Tanned Split Paw Skin (lore)`, whose own notes say *four* in-game items share that name).
- **This is not a reporting gap, it is the silently-wrong failure mode this project is built to avoid.** v1 looks up
  the captured name, finds the unmarked page, and proposes rewriting it with the other item's stats — a diff that
  reads like an ordinary stale-page correction. **The icon check cannot catch it: all six pairs share one
  `lucy_img_ID`.** That is the only place in this design where the independent signal is structurally blind.
- **The capture cannot resolve it, which inverts the pipeline's order.** Both items show the same name and the same
  artwork, so the only discriminator is the stats — and the tool currently finds the page *by name* and then
  compares the stats to it. v2 has to be able to go the other way: gather the candidate titles (the name, plus its
  `*` and `(qualifier)` variants), and let the stats choose, or refuse and hand the user both. Either way the ledger
  key stops being the item name alone.
- **The wiki has no settled convention, so this needs the editors before it needs code.** Three incompatible shapes
  are live, and one of them — the qualifier copied into `itemname` — makes title and itemname agree, so the tool
  sees nothing wrong at all. Agreeing a single marker is a precondition for handling it reliably, and belongs with
  the user's other template conversations.
- **Methodological note worth keeping, because this number was wrong twice before it was right.** Counting titles
  that differ only in capitalisation gives 1,778 groups; excluding redirects drops it to 64; checking which are item
  pages gives 39; and spot-checking those finds they are **duplicate pages for one item**, not two items sharing a
  name. Three successive measurements, each an order of magnitude apart, and only the last one answers the question
  that was asked. The duplicates are a real cleanup job, but a different one.

## v1 polish round (user, 2026-10-07)

The last work before calling v1 done. Grouped and ordered by agreement:

1. **Quick fixes**: tab close buttons vertically centred (and taller), 650px minimum window width, in-game and wiki
   icons at the same size (the wiki's current size), icon comparison reads "appears to be correct" / "may be
   incorrect" with no distance figure, hotkey hint beside Capture, "N need attention" opens History filtered to
   them, "Checked items" renamed "History", the progress step split into "Taking game screenshot" and "Finding item
   windows in screenshot", the content scroller flush with its border, a "Close all" for the tabs.
2. **Wording pass**: shorter labels and messages throughout, and nothing that assumes the user's own setup (the UI
   font description called EQL Wiki Assistant "your own modification of Arial").
3. **Layout**: the item-window screenshot in its own fixed-width pane **left of the edit pane** (no border on the
   shared side; above the edit pane when the window is very narrow), so it never scrolls away and the warnings no
   longer sit under it; a large red "not verified" badge beside the item title; the item's icon on its tab; a
   per-item Re-check button only where the ledger let a capture skip the wiki, replacing the global checkbox; a
   borderless cog for Settings in the top-right corner. Restyle: anything that changes the wiki stays a clear,
   solid button; elsewhere links and icons with tooltips (History: an X to forget, the item name as the page link).
4. **The step-by-step review**, sliding between pages under a timeline, with the screenshot pane always visible:
   1. **Differences**: the findings, the icon comparison, and the wikitext editor expanded at the bottom. Next, or
      mark as already correct.
   2. **Preview**: the wiki's own rendering in an embedded browser (`action=parse`, sent only when the user reaches
      this page — see the clarified constraint in CLAUDE.md). Back / Next.
   3. **Submit**: the final diff against the page, the summary, Save, Back.
   4. **Formatting**: if nothing needs doing, a brief "formatting looks good" and on to 5 automatically. Otherwise
      the formatting diff with Accept, Skip, or Make another edit (page 1 again, diffed against the updated page).
      The raw wikitext editor is here but collapsed, to discourage hand formatting — the formatter is meant to be
      deterministic and trusted.
   5. **Done**: the page as it now renders, Done (closes the tab) and Edit again (page 1 again, freshly diffed).
   In-app dialogs replace the Windows message boxes, drawn over the window's content area.
   **BUILT 2026-10-07, not yet tried by the user.** Settled with them before building: the Submit page *is* the
   confirmation (no dialog after Save; an icon upload keeps its own); Done shows the live page; "Skip for now" is a link
   on page 1; History and Settings use the in-app dialog too, and only the crash handler keeps a Windows message box.
   Found while building: `action=parse` omits the skin's stylesheet (read once per session from `Special:BlankPage`),
   and the embedded browser runs with scripts off so the wiki's "not verified" toast cannot make an anonymous edit.
   The hotkey hint beside Capture is now a link to Settings > Capture hotkey.
5. Preview arrives with group 4 rather than separately. **Built with it.**
6. **Keep the mouse cursor out of captures** (user, 2026-10-07, after a cursor over an item window's text looked
   like a glyph-reading regression). Do this after 1-5, not before. First try `GraphicsCaptureSession.IsCursorCaptureEnabled = false`,
   which needs Windows 10 2004 and so fits the current target. It only removes the cursor Windows draws, so if the
   game draws its own cursor into its frames, the flag does nothing. The fallback is to park the cursor in the
   bottom-right corner for the capture and put it back afterwards. It is unknown whether the game redraws its cursor
   there when its window is in the background.
   **DONE 2026-10-08**: the flag alone works — the game's pointer is the one Windows composites, verified by the user
   with the pointer resting on an item name. No fallback needed.

## Versions and releases (user, 2026-10-08)

**The app is "the Assistant"** (or "our app") from here on; "the tool" is kept for other tools.

### Versioning: Semantic Versioning, adapted to an app that writes to a wiki
- **Patch** (`1.0.1`): a fix that changes nothing the Assistant would write to a page, other than correcting a bug.
- **Minor** (`1.1.0`): a new feature, *or any change to what the Assistant would write to a page* — which includes
  every `WikiMapping.CurrentVersion` bump (the 2026-10-08 attribute reorder would have been one). The promise: updating
  within a minor version never makes the Assistant start editing pages differently.
- **Major** (`2.0.0`): a redesign, or an improvement big enough to bring back people who passed on 1.x.
- **Pre-releases** go `-alpha.N` → `-beta.N` → `-rc.N`; the first release is **`1.0.0-alpha.1`**. Each release is a git
  tag (`v1.0.0-alpha.1`), and the version is visible in the app so a tester can quote it.
- **Older terms in this plan map across as**: "v1" = 1.0.0; "v2" items are 1.1.0 candidates unless found necessary for
  1.0.0 (items sharing a name, writing the verified list, scrollbar detection, asking the era); stat scaling for levelled
  items (milestone 8) may be big enough for 2.0.0. The mapping version stays separate and internal.

### 1.0.0-alpha.1 — for the first tester, a fellow wiki editor
**Order agreed** (user, 2026-10-08): (1) Arial default, version shown, edit-summary tag — **DONE 2026-10-08**; (2) tester
README, licence, RapidOCR notice, icon marking — **DONE 2026-10-08** (see "Licensing as built" below); (3) history and screenshot audit, results to the user before anything is
public — **DONE 2026-10-08**; (4) the installer and the update notice — **built 2026-10-08** (`tools/release.ps1`,
`Pipeline.ReleaseCheck`; see CLAUDE.md, "Releases"); install, repair and uninstall tested by the user 2026-10-09, after
which the updater and the uninstall question were added (below) and tested by the user 2026-10-09 between two local
builds — in-app update, a newer Setup over an older install, a downgrade warning, and both uninstall answers;
(5) the user makes the repo public;
(6) tag and publish
`v1.0.0-alpha.1`.

Same resolution and UI scale as the user, default Arial font, so the Assistant should work for them as it stands once the
font default changes.
- **Default UI font becomes Arial** (user, 2026-10-08). EQL Wiki Assistant is the user's own font and is never
  distributed; a tester on Arial would otherwise have every capture refused as `WrongFont`. Expect this to push for a
  better first-run experience before 1.0.0.
- **A real installer.** `tools/install.ps1` is a developer convenience. **Velopack, agreed** (user, 2026-10-08) (open source, MIT) — a
  per-user `Setup.exe` with no admin prompt, built for .NET apps, publishing to GitHub Releases, and with an updater
  built in that can simply stay switched off until testing shows interest (see 1.0.0 below). A self-contained build, so
  the tester needs no .NET install. To confirm when building: RapidOCR's `.onnx` models and the WebView2 runtime check.
- **The icon library ships** (user, 2026-10-08): the publisher has let item icons be used on third-party wikis and
  databases for 27 years, it helps them, and they can be removed on request. 46 MB on disk. Extracting from the player's
  own game files (the user's extractor is open source) would let the library pick up new icons, but the devs mostly
  reuse existing ones and it is not worth the complexity for 1.0.
- **The repository goes public before the installer is handed over** (user, 2026-10-08), so the tester can download it
  there and read the code rather than trust an executable. Before that:
  - **Audit the whole history, not just the tree**: making a repo public publishes every commit. Check for screenshots
    (`samples/` is gitignored, but verify no commit ever added one), the Arial-derived font in `fonts/`, credentials,
    and personal details (the commit author's email, paths naming the developer's machine, the developer's wiki
    account). Decide what is fine to keep.
  - **Licence: MIT** (user, 2026-10-08), and **RapidOCR's model notice** (Apache-2.0 requires it shipped alongside).
  - **The icons must be marked as not MIT** (user, 2026-10-08), chiefly so an AI agent that finds them later does not
    take them for permissively licensed and reuse them in a way the publisher would object to — humans rarely reach
    them without the context already. Standard machine-readable route: a **REUSE** `REUSE.toml` at the root declaring
    `game_assets/**` as the publisher's copyright under a `LicenseRef-` licence whose text sits in `LICENSES/`. Plus a
    plain `game_assets/README.md` saying what they are and that they are not covered by the MIT licence, a line in the
    root `LICENSE` excepting the folder, and a line in CLAUDE.md, the first thing an agent here reads.
  - **Audit the screenshots too, aiming to publish everything the automated tests use** (user, 2026-10-08). The
    golden and corpus tests skip on a fresh clone today because `samples/` is gitignored — which is how three top-edge
    bugs shipped. Each sample is a full frame that can show character names, other players and chat, so each needs
    looking at, and where something private is visible, a decision: redact it (blanking regions the tests do not read,
    then re-running the corpus to prove nothing moved) or leave that sample out. The "never commit real screenshots"
    rule in CLAUDE.md changes to "only audited ones" once this is done.
  - **DONE 2026-10-08.** History: no screenshot, font, log or credential was ever committed; the commit author became
    `AustonZ` with the GitHub noreply address, and the developer's machine path, wiki account and the other editor's
    name were rewritten out of every revision and message (`git filter-repo`; the final tree was byte-identical
    before and after). The plan moved into the repository as this file. Screenshots: the user reviewed all 52 and the
    only redaction was the private chat channel's name in five frames, blacked out losslessly; character names were
    judged fine. The corpus scored identically afterwards (120 windows, 2442 correct, 0 everywhere). The 52 frames
    are committed as plain git (~145 MB) rather than LFS. The GitHub repository is recreated empty before the first
    push, so no copy of the old history survives there.
  - **A README** for testers: what the Assistant does and never does (local screenshots only, no game memory, nothing
    sent to the wiki without pressing a button), creating a bot password and the rights it needs, the Settings it needs
    (font, hotkey), the SmartScreen warning to expect if unsigned, and how to report a problem.
- **The Assistant's edits are marked**: the edit summary ends with the app and its version, e.g.
  `(Editor Assistant 1.0.0-alpha.1)` (**DONE**, step 1). If a release ever ships a bad rule, every edit it made can be found in Recent
  Changes, and other editors can see where an edit came from.
- **Reporting problems**: GitHub issues, plus a "copy diagnostics" link in the app (version, settings, the end of
  `errors.log` — never screenshots). The README warns that the saved-captures folder holds full-screen screenshots.
- **Code signing, if it can be had for $0**: an unsigned installer gets SmartScreen's "unknown publisher" warning. To
  look into: SignPath Foundation's free signing for open-source projects (needs the public repo and an OSI licence) and
  the Microsoft Store's terms for individual developers. Otherwise ship unsigned and say so in the README.
- **An "update available" notice** (in the alpha, user, 2026-10-08): on start, ask GitHub's releases API for the latest
  version and show a link if it is newer. One small request to GitHub, nothing sent; the README says so.
- **Updating in place, brought forward from 1.0.0** (user, 2026-10-09). Shipping it in the first release is what makes it
  useful at all: an updater only helps from the release after the one that ships it, and an alpha is when fixes come
  fastest. The notice gains *Update*: one confirmation (which says the open items will close), the download behind a
  cancellable progress dialog, then Velopack restarts the app on the new version. Never without a click.
- **Uninstalling asks whether to delete the user's data too** (user, 2026-10-09: "I'm not a fan of programs that leave
  trash behind"). Default No, since the history is real work and a reinstall wants it back.

- **Name and tag** (user, 2026-10-08): the app is "EQL Wiki Editor Assistant"; wiki edits carry the shorter
  `(Editor Assistant 1.0.0-alpha.1)`, "EQL Wiki" being implied on the wiki. The user's font was renamed in code from
  "EQL Wiki Assistant" to "EQL Wiki Editor Assistant" to match, and the `.ttf` rebuilt under the new family name
  (byte-identical apart from its name records). No compatibility code: the user's own settings file was edited instead.

### Licensing as built (2026-10-08)
- `LICENSE` is plain MIT, copyright "EQL Wiki Editor Assistant contributors" (user's choice). **The exception is not
  written into `LICENSE` itself**, departing from the plan above: GitHub recognises a licence by near-exact text, and an
  added paragraph would lose the MIT badge. The exception lives in `REUSE.toml` (lints compliant, 11,796 files),
  `LICENSES/LicenseRef-*.txt`, `game_assets/README.md`, the README's Licence section and a CLAUDE.md hard constraint.
- **The wiki fixtures needed marking too**: they are wiki contributors' text, and the wiki names no content licence
  (its edit notice says "released under the" and leaves the licence blank — worth raising with the wiki's admins).
- `THIRD-PARTY-NOTICES.txt` covers the app's whole package closure. **For step 4, the installer must ship**: `LICENSE`,
  `THIRD-PARTY-NOTICES.txt`, and in a `licenses/` folder ONNX Runtime's `ThirdPartyNotices.txt` (as
  `onnxruntime-ThirdPartyNotices.txt`) and SkiaSharp.NativeAssets.Win32's `THIRD-PARTY-NOTICES.txt` (as
  `skiasharp-THIRD-PARTY-NOTICES.txt`), both from the NuGet cache; plus the .NET runtime's own notices if the build is
  self-contained. The README must gain a line on the update check once it exists.
- **For step 3's audit**: the glyph atlas is bitmaps of game-rendered Arial (and three glyphs of the user's modified
  font) — rendered pixels, not font software, so probably fine, but it is the one tracked file derived from Arial.

### Revisit before beta
- **Whether Settings should offer "EQL Wiki Editor Assistant" to testers** (user, 2026-10-08: not for the alpha). No
  tester can have it, since it is never distributed, so choosing it only gets every capture refused. Options then: show
  it only when it is already the saved choice, or publish the build instructions so anyone can make it.

### 1.0.0 (full release)
- **The Assistant adapts to each player's setup** — priority for 1.0.0 (user, 2026-10-08). Resolution should not matter
  (everything is measured relative to the window); UI scale and skin do, since every edge-finding constant and the glyph
  atlas belong to one scale and skin. Likely shape: a one-time calibration on a known window that measures the frame and
  learns the font, as the atlas was first built. The font alone could also be read from the game's UI `.ini`.
- **Updating without being asked**, in the background on close, if testers want it. Updating on a click is in the alpha
  (above).
- Everything above from the alpha, settled.

### Keeping up with the wiki's conventions (user, 2026-10-08)
With more users, a template or blueprint change has to reach everyone as a release. Watching for one needs no code:
- Each page's history has an Atom feed — `https://eqlwiki.com/index.php?title=Help:Contents&action=history&feed=atom`,
  and the same for `Template:Itempage` — checked working 2026-10-08. The API's `feedwatchlist` (with the private
  watchlist token from Preferences) gives one feed for everything on the user's watchlist.
- A feed-to-email service (Blogtrottr, Feedrabbit) turns those into email. MediaWiki's own "email me when a page on my
  watchlist changes" may also be on — it cannot be checked anonymously; look in Special:Preferences. The wiki has no Echo
  notifications extension.
- `Help:Contents` also holds the NPC and merchant blueprints; edit summaries name the section
  (`/* Item Page Blueprint */`), which is the filter.

### Template parameters in place of `statsblock` (user and the first tester, 2026-10-08)
Both want most of `statsblock` promoted to real parameters; someone has been experimenting, and the tester is following
up. The Assistant was built for this (a field's location is part of the mapping). If it goes ahead:
- **A bulk conversion** could reuse the Assistant's parser and the formatter's prove-nothing-moved check — but it is a
  different kind of edit from a capture-backed one: it reshapes what a page already says rather than correcting it.
  Needs community/admin agreement, a bot-flagged account so it does not flood Recent Changes, a polite rate, and pages it
  cannot read cleanly (the 469 still carrying legacy flags, for a start) left to humans.
- **Old and new templates can run side by side**; the open question is whether the Assistant converts a page when it
  touches one or only edits pages already converted. To settle with the tester and the template's author.

### The blueprint's effect line
Its 2026-10-06 wording is still being thought about by the user and its author; **the Assistant keeps its existing effect
behaviour until they decide** (user, 2026-10-08).

## Future features (parked deliberately, revisit after real use)
- **Warn when the item window's effects/info section is scrollable** (user, 2026-09-28). The game makes that section
  scrollable when it cannot fit the content, and past that simply clips it with no scrollbar at all. The clipped case
  has no reliable signal and the game normally expands the window to avoid it, so it stays the user's
  responsibility — but the *scrollbar* is detectable, and its presence means the capture is missing content. The user
  weighed the added complexity against the benefit and chose to revisit once the tool has been in use for a while.
- *(Done 2026-09-28: the window's trailing region is handled by text rules — known lines acted on, unknown ones
  warned. Two of this plan's own claims about it were wrong; see CLAUDE.md.)*
- **Ask which era an item belongs to** once the first expansion ships and `Classic` stops being a constant.
- **Syntax highlighting in the raw wikitext editor** (user, 2026-10-07): template names, parameter names, links and
  `<br>` picked out, so a hand edit is easier to read and harder to break.
- **A per-skin/per-scale calibration profile**, if the tool is ever shared with other players.

## Wiki findings (verified against eqlwiki.com)
- MediaWiki 1.45.3; `api.php` at site root; `login`/`clientlogin` available; **no OAuth extension**. OATHAuth (2FA)
  is installed, so bot passwords (which bypass 2FA) are the right route. ConfirmEdit/Turnstile is present, so
  anonymous edits may be blocked; verify logged-in edits work in the wiki spike.
- Item page shape (blueprint at `Help:Contents`; examples `10 Dose Adrenaline Tap`, `Earring of Bashing`):
  - Era template at top (`{{Classic Era}}` / Kunark / Velious ...)
  - Optionally a `[[File:...]]` 3D-world screenshot between the era template and `<onlyinclude>` — **preserve if present, never generate**
  - `<onlyinclude>{{Itempage |notes= |itemname= |lucy_img_ID= |statsblock= |focus_effect= |merchant_value= |bookcontents= |dropsfrom= |relatedquests= |playercrafted= |recipe= |soldby= |foraged=}}</onlyinclude>`
  - Trailing `[[Category:...]]` lines. **Categories are conditional, not fixed**: one per allowed class (`Bard Equipment` ... `Wizard Equipment`), one per slot (`Ear`, `Fingers`, `Primary`...), one per weapon skill (`1H Blunt`, `Piercing`...), plus misc (`Containers`, `Fashion:?`, `Inventory`, `Light source`, `Quest Items`, `Potion`, zone name). `Inventory Items` seen on some pages but is not universal. Class/slot/skill categories can therefore be *derived* from parsed `statsblock` fields and checked/auto-added (mind naming quirks, e.g. slot `FINGER` -> `Category:Fingers`).
- **v1 scope is the fields the tool can verify from the game window:** `itemname`, `lucy_img_ID`/icon, `statsblock`, `focus_effect`, `notes` (lore only — see below), `merchant_value`. Everything else on the page (dropsfrom, soldby, relatedquests, recipes, images, etc.) is preserved byte-for-byte.
- Lore lives in `notes` as `{{Item Lore|lore text}}` (e.g. `Earring of Bashing`). `notes` may also hold other human-written text, so the tool edits only the `{{Item Lore|...}}` call and preserves the rest. `Template:Item Lore Missing` is a placeholder that the tool **always removes** on any item it edits: lore is added iff the item has a lore tab, otherwise the item has no lore and no placeholder should remain.
- The blueprint's `[[File:?.png|200px|thumb|'''?''']]` line is the optional in-game 3D model screenshot — unrelated to `lucy_img_ID` (the inventory icon). Preserve only.
- `statsblock` is a **free-text, EQ-style block with `<br>` line breaks**, e.g. `EXPENDABLE  Charges: 10<br>`,
  `Effect: [[Flurry]] (Any Slot, Casting Time: 8.0)<br>`, `WT: 0.4  Size: SMALL<br>`, `Class: ALL<br>`, `Race: ALL<br>`.
  Effects/spells are wikilinked. So diffing = parse `statsblock` line-by-line into fields, compare to OCR fields, and
  re-render the block (not param-by-param template diffing).
- Icon: `lucy_img_ID` -> `File:item_<ID>.png`.
- Related templates: Itembox, Itempage, Item Lore, Item Lore Missing, ItemStats, ItemWhereRow/Table.
- `statsblock` line vocabulary (from blueprint): flags line (Attunable, Lore Equipped, Lore Item, No Trade, Placeable,
  Quest, EXPENDABLE ...), `Slot:`, `Skill:`/`Atk Delay:`, `DMG:`/`DMG Bonus:`/`AC:`/`BACKSTAB:`, stat lines
  (STR..END), `SV ...` resists, `Attack/HP Regen/Mana Regen/Haste/...`, `Recommended/Required level`, `Effect: [[Spell|<span class='itemeff'>Spell</span>]] (Combat/Clicky/Must Equip/Casting Time) at Level N`,
  `Charges:`, `Size:/WT:`, `Weight Reduction/Capacity/Size Capacity`, `Class:`, `Race:`. Order and spacing conventions matter for re-rendering.

## Wiki mapping layer & configuration (from user review)
The wiki is young and its templates/conventions will keep changing, so **internal models must not know wiki syntax**.
- Internal `Item` model (name, icon, flags, slots, stats, effects, size, weight, classes, races, lore, focus effect,
  merchant value, extensible bag) is wiki-agnostic.
- A **wiki mapping** (data, not code) translates model <-> wikitext: template name (`Itempage`), param names
  (`itemname`, `lucy_img_ID`, `statsblock`, `notes`, `focus_effect`, `merchant_value`), lore wrapper (`Item Lore`),
  placeholder templates to strip (`Item Lore Missing`), statsblock line layout/order/labels/flag spellings,
  effect-link markup, category rules (class/slot/skill -> category names), era templates, and the icon file pattern
  (`File:item_<ID>.png`). Stored as versioned JSON in the user's app-data folder with sensible built-in defaults.
- A **Settings/mapping window** in the WPF app to view and edit the mapping (template/param names, category rules,
  statsblock line order, flag list, placeholder templates to remove), with reset-to-default and import/export. Compliance
  checks and renderers read from this mapping so a wiki change is a config edit, not a code change.
- Code-side extension point stays `IEntityKind`; each kind ships its own default mapping.

## Checked-items ledger (from user review)
Maintain a **local list of items already checked** so we don't hit the wiki needlessly.
- Storage: a small local SQLite DB (or JSON file) in the app-data folder, one row per item, keyed by normalized
  (level-`+X`-stripped) item name (with `entity kind` as part of the key so spells/monsters/quests reuse it). Fields:
  last-checked time, outcome (`matched` / `edited` / `flagged` / `skipped` / `not-on-wiki`), wiki revision id seen or
  written, a fingerprint (hash) of the parsed, level-0-normalized in-game item data (incl. lore + icon hash + captured
  level `X`), mapping/rules version used, optional user note.
- **Items ruled ineligible for processing (foreign exaltation, unsupported `+X>0` level in v1) or canceled by the
  user never get a ledger row at all** — not `flagged`, not `skipped`. They weren't actually checked, so the next
  capture (a pristine copy, or a future version that supports leveling) is treated as new.
- Flow: after capture+parse, look up the ledger *before* any network call. If the item is `matched`/`edited`, the game
  fingerprint is unchanged, and the mapping/rules version is unchanged, show "already checked <date>" and skip the wiki
  fetch. Otherwise proceed normally and update the ledger afterwards.
- Overrides: a "Re-check anyway" action; ledger entries invalidated when the game fingerprint changes (game patch),
  the mapping/rules version changes (new compliance rules), or the entry is older than a configurable age (default: no
  expiry). `flagged` (e.g. icon wrong, occluded/partial capture) and `skipped` items are never treated as done.
- Other editors may change a page after we checked it; the ledger deliberately does not poll for that. A manual re-check
  (or an optional expiry) covers it.
- UI: ledger view (search/filter by outcome, clear/reset entries, export) inside the app; exposed in Settings.
- Only lookups that reach the wiki are skipped — OCR/parsing still runs locally each time (needed to compute the fingerprint).

## Item leveling / "+X" — DEFERRED, v1 handles level-0 only (from user review)
Items (and spells) can be leveled up in-game, shown as a `+X` suffix on the item name (`Robe of the Ishva +2`); `X=0`
(base) shows no suffix. **The wiki only stores level-0 data**; its page JS lets viewers preview higher levels, but our
source of truth must be level-0. Since players frequently already hold a leveled copy, the tool must not silently
mistreat one as level-0.

**Decision: v1 processes `+0` items only.** A parsed item with `X>0` is treated the same as an ineligible item (see
Augmentations below) — the tool tells the user leveling isn't supported yet and skips it, **writing no ledger row**,
so it's picked up automatically once leveling support ships. No stat-scaling math is attempted before then; a wrong
guess would be worse than refusing.
- **Name handling** (do this part now, it's simple and needed regardless): always strip the `+X` suffix before using
  the item name to look up the wiki page or key the ledger — base name only. The captured level `X` is retained on
  the parsed `Item` (used to decide "supported: proceed" vs. "unsupported: skip").
- **Placeholder normalizer**: a `ILevelNormalizer` (or similar) with one real implementation for now —
  `X=0` -> identity; `X>0` -> explicitly "unsupported," surfaced to the pipeline as ineligible rather than guessed at.
  This keeps the seam ready for milestone **2c** (below) to drop real logic in without reshaping the pipeline.

**What we found investigating "can we glean the formula from the wiki" (research for milestone 2c, not yet acted on):**
- The wiki ships dedicated compiled extensions **`ItemLevelSlider`** and **`SpellLevelSlider`** (confirmed via
  `action=query&meta=siteinfo&siprop=extensions`) — this is presumably what drives the in-page level-preview UI. As a
  compiled MediaWiki extension it has no wikitext/JS source reachable through the API, so its per-stat rules aren't
  directly inspectable.
- `MediaWiki:ItemLevelMultipliers` (a wiki page, fetched via `action=raw`) holds a JSON tier->multiplier lookup table:
  `{0:1.0, 1:1.3, 2:1.6, 3:1.9, 4:2.15, 5:2.4, 6:2.6, 7:2.8, 8:2.95, 9:3.05, 10:3.15}`. Likely part of the real
  formula, but per the user, different stats scale by different rules, so this table alone is not the whole story.
- `MediaWiki:Common.js` does **not** reference this table or implement the slider — the slider must be entirely
  extension-side (PHP), reinforcing that we can't fully reverse-engineer it from readable wiki assets alone.
- Also noted, unexplored: an `EQLClientData` extension exists — unknown relevance, worth a look during milestone 2c.
- **Known caveat from the user**: even the wiki's own rounding doesn't always match the real game (the game holds
  stats as floats and never reveals the decimal part in its UI), so *any* formula we land on — wiki-derived or
  reverse-engineered from real items — will need validation/tolerance-tuning against real in-game screenshots at
  multiple levels of the same item, not just against the wiki's preview output.

## Augmentations / exaltations (from user review)
Items can have "exaltations" attached — like attachments, usually granting extra effects. Confirmed from samples
`item with its native click effect (not removable).jpg` (Crystal Mask +1, Tier 1/2) and
`item with its native focus exaltation (removable).jpg` (Robe of the Ishva +2, Tier 2/4):
- The item window shows up to several exaltation slot rows (`Ornamentation`, `Focus Exaltation`, `Click Exaltation`,
  `Worn Exaltation`, `Proc Exaltation`), each `empty` or holding an entry named `<Name> (Exaltation)`.
- **A slot only appears once its tier/level unlocks it.** Below that tier, an exaltation the item natively shipped
  with (e.g. a click effect) isn't shown as a slot at all — it's baked into the base stats as a plain `Click Effect:`/
  `Focus Effect:` line, same as the old non-exaltation format. (Crystal Mask +1 example: Tier 1/2, no exaltation
  slots shown yet, `Click Effect: Gather Shadows (Can Equip)` appears as a flat stat line instead.)
- **Native vs. foreign, once a slot is unlocked and filled**: compare the slot entry's `<Name>` (stripping the
  ` (Exaltation)` suffix) against the item's own base (level-0) name. If they match (`Robe of the Ishva +2` ->
  `Robe of the Ishva (Exaltation)` in its Focus Exaltation slot), it's the item's own native exaltation, now shown as
  removable — treat this the same as an unremovable native effect and proceed normally. If the name refers to a
  *different* item (see `bladstopper +7 with exaltations.jpg`: `Golden Efreeti Boots (Exaltation)`, `Bloodmoon
  (Exaltation)`, etc.), the item has been augmented with someone else's exaltation.
  - Note: the `(Augmented)` suffix the game adds to the window title fires whenever *any* slot is filled — including
    the item's own native one — so it is **not** a sufficient "foreign modification" signal by itself; the per-slot
    name-vs-base-name check is what matters.
  - **`Ornamentation` is never native** (user, 2026-09-23) — it's purely something the player applies, so a filled
    Ornamentation slot is by definition foreign and the name check will always say so. **Open question before
    relying on that**: ornamentation is understood to be cosmetic, so if it can't change any field the wiki
    actually stores, blocking the whole item on it would be over-strict — the current "foreign exaltation ->
    ineligible" rule exists to stop *stat* contamination. Decide whether Ornamentation is exempt (cosmetic-only)
    or genuinely blocking before the eligibility step ships. Not urgent: the user doesn't currently have one, so
    there's no sample and no way to verify what it does to the window either way.
  - **Each exaltation has its own restrictions on which items it can be applied to** (user, 2026-09-23), so a
    single item can't necessarily show every slot filled — sample coverage for the filled-slot cases has to be
    spread across several items rather than sought on one.
- **On a foreign exaltation**: this item is **not eligible for automated processing**. Warn the user and let them
  cancel processing for that item. **Do not write a ledger entry in this case** — it's neither `matched`, `edited`,
  `flagged`, nor `skipped`; the item was never actually checked, and the user will likely retry with a pristine copy.
- The tool can't be 100% certain an exaltation wasn't swapped out and back (e.g. can't prove a slot's history) — this
  is an inherent trust boundary handed to the user, same as the Attunable/No Trade note below. The cancel-without-
  ledger-write flow is the mitigation; there's no stronger technical guarantee to build.

## Known flag ambiguity — Attunable vs. No Trade (FYI, no new requirement)
An item's flag line can show `Attunable` (before first equip/trade) or `No Trade` (after) if the item is natively
Attunable — but some items are natively `No Trade` and never show `Attunable` at all. The window alone can't
disambiguate these, so a flag mismatch here may be a false positive the user has to judge, not something the tool can
resolve automatically. This falls under the same "user must be careful, can override/cancel" pattern already covered
by the augmentation case above; it doesn't add new pipeline behavior, just something to keep in mind when writing the
flag-line parser/diff so it doesn't over-claim confidence.

## Robustness requirements (from user review)
- **Race restrictions must be supported — CLOSED 2026-10-03, a restricted item finally turned up.** This was
  written as a requirement with no evidence behind it: every capture showed `Race: ALL`, the user had never met a
  restricted item, but the game supports it so the tool had to (user, 2026-09-23). It was already satisfied without
  special-casing — `ItemParser` splits `Race:` on whitespace exactly like `Class:` — and the note's own
  instruction was "add a sample if a restricted item ever turns up".
  - One did: `Feir`Dal Fletching Kit`, `Race: ELF DEF VAH`, now `samples/14-race-restricted-item.png` with ground
    truth in the tracked corpus. It parsed correctly first time, so the gap really was evidential rather than
    behavioural — but it is the difference between believing that and knowing it, and there is now a corpus entry
    that fails if anyone simplifies the field to a single string.
  - **Worth repeating as a method note**: the sample came out of the Debug build's own capture archive rather than
    a fresh capture. An interesting item the user has already inspected is usually sitting in
    `%APPDATA%\EQLWikiEditorAssistant\debug-captures` under its own name, which makes closing a gap like this cost
    nothing on their part.
- **Wiki data is untrusted/dirty.** Pages are human-written and partly imported from the old Project1999 wiki: expect wrong
  template usage, missing/extra params, typos, mixed casing, stray whitespace, legacy templates. The wikitext parser must be
  tolerant (never throw on bad input; degrade to "couldn't parse this part, leaving it untouched, flagged for user") and
  edits must be minimal, surgical patches so unrelated human content survives.
- **Multiple item windows per screenshot**: locate and process every item window found, presenting each as its own
  result in the review UI.
- **Occlusion**: if an item window is partially covered by another window/UI element, detect it (e.g. missing/clipped
  border or chrome, truncated text) and alert the user rather than emitting partial data as if complete; suggest moving the
  window and re-capturing.
- Remove on rewrite: `{{Item Lore Missing}}` placeholder (always).
- Preserve on rewrite: era template, optional 3D-world screenshot `[[File:...]]`, all non-v1 params, non-lore `notes` text,
  unrelated categories.

## Architecture
Shared pipeline with per-entity plug-ins (`IEntityKind`: Item now; Spell/Monster/Quest later). Each kind supplies its
window locator, parser, wikitext renderer/template knowledge, and checks.

1. **Capture** — global hotkey (`RegisterHotKey`) -> Windows Graphics Capture -> in-memory bitmap.
2. **Locate** — find *all* item window(s)/tabs (template-match window chrome + OCR of title/tab labels); flag occluded ones.
3. **Extract** — OCR crops to raw lines; crop icon; detect lore tab and prompt for a second capture.
4. **Parse** — raw lines -> `Item`: fixed core (name incl. captured `+X` level, icon) + extensible field bag (flags,
   lore, slots, stats, effects, size, weight, classes, races, exaltation slots, ...). New field = one parser rule +
   one render rule + tests.
4b. **Eligibility check** — inspect filled exaltation slots (foreign exaltation -> warn, let user cancel, no ledger
   write) and captured level (`X>0` -> v1: unsupported, same treatment, no ledger write). Otherwise continue.
4c. **Normalize** — strip `+X` from the name for lookup/keying purposes. Stat scaling is identity in v1 (only `X=0`
   items reach this step); real level-0 downscaling for `X>0` items lands in milestone 2c.
5. **Ledger check** — keyed on the normalized base name; consult the checked-items ledger and skip the wiki entirely
   if the item is already verified and unchanged.
5b. **Fetch** — MediaWiki client: wikitext (`action=query&prop=revisions`) and icon file (`imageinfo`), the icon
   served from the local icon cache whenever present.
6. **Analyze** — format-compliance checks (incl. derived class/slot/skill categories), statsblock field diff, name / lore / focus_effect / merchant_value checks, icon comparison (flag only).
7. **Review & commit** — WPF side-by-side diff; user approves/tweaks; edit submitted with summary and base-revision
   timestamp (edit-conflict safe).

Solution layout: `src/EQLWikiEditorAssistant.App` (WPF), `.Core` (models, pipeline, `IEntityKind`), `.Capture`, `.Ocr`,
`.Wiki` (API client, wikitext/statsblock parsing); `tests/EQLWikiEditorAssistant.Tests`; `samples/` gitignored. All
Windows-API-touching projects (`Capture`, `Ocr`, `App`, `Tests`) target the WinRT-projected `net10.0-windows10.0.19041.0`
TFM (plain `net10.0-windows` doesn't project modern WinRT namespaces like `Windows.Media.Ocr`/`Windows.Graphics.Capture`);
`Core`/`Wiki` stay portable `net10.0`.

**Refinement from milestone 1**: `IOcrEngine` and its portable result types (`CapturedImage`, `Rect`, `OcrLine`,
`OcrWord`) live in **`Core`** (pure data/interface, no Windows types in the signature), not in `Ocr` — `Core` hosts
the pipeline abstractions and can't depend on the Windows-only `Ocr` project, so the port has to live where the
pipeline lives. Only the concrete engine (`RapidOcrEngine`) lives in `Ocr`. Apply the same
split to `Capture` later (a portable capture-source interface in `Core`, the Windows Graphics Capture implementation
in `Capture`) — still open; `Capture`'s `WindowCapturer`/`GlobalHotKey`/`WindowFinder` are concrete, standalone
classes for now (built and verified in milestone 1's capture half), not yet wired behind a `Core` port. Do that when
milestone 2 builds the actual pipeline orchestration, same as how the `Ocr` split only happened once something
needed to depend on it.

`Capture` also takes a NuGet dependency on `Vortice.Direct3D11`/`Vortice.DXGI` (thin, maintained D3D11/DXGI
bindings) — needed to create the `ID3D11Device` that Windows Graphics Capture's frame pool requires; there's no
WinRT-native way to get one.

**`tools/` (dev-only, not shipped)**: `EQLWikiEditorAssistant.TestSupport` (loads a screenshot file from disk into a
`CapturedImage`, via `Windows.Graphics.Imaging.BitmapDecoder` — the real app never loads from files, only used by
tests/tooling; also has `RepoPaths` for finding `samples/` from wherever a test/tool's output directory lands, and
`DebugDraw` for drawing rectangle overlays onto a `CapturedImage` for visual verification), `OcrSpike` (load a
sample screenshot, crop/upscale, `--engine windows|rapid`, dump recognized lines+bounding boxes), `CaptureSpike`
(`list` visible windows, `capture <titleSubstring> <outPngPath>` one real window, `hotkey <modifiers> <vkHex>` to
test `GlobalHotKey` live), and `LocateSpike` (OCR a full screenshot + run `ItemWindowLocator`, `--save` draws a
debug overlay per found window, green/red by `PossiblyOccluded`). All are kept around, not deleted after use —
useful for ongoing parse-logic tuning and any future capture/OCR/locate debugging.

## Milestones (riskiest first)
0. Repo scaffold: solution, `.gitignore` (incl. `samples/`), CI-less `dotnet build/test`, initial CLAUDE.md. DONE.
1. **Capture + OCR spike** — DONE (both halves). Decide whether Windows OCR is accurate enough; tune preprocessing
   (upscale/threshold) or swap engine; get hotkey + live window capture working.

   **Verdict: proceed with Windows.Media.Ocr, with mitigations — do not treat raw OCR output as ground truth.**
   Tested via a throwaway `tools/OcrSpike` console tool (kept in the repo — loads a sample screenshot, crops to a
   region, optionally upscales, runs the real `WindowsOcrEngine`, dumps recognized lines+bounding boxes) against
   several real item windows (Water Flask, Bladestopper +7 with 4 exaltation rows, Leatherfoot Sandals +6 with a
   foreign exaltation, Greater Potion of Accuracy) at native res and at 3x/5x upscale. Findings that should shape
   milestone 2's parser:
   - **Upscaling before OCR matters a lot.** At native resolution (UI text ~9-11px tall), many short numeric values
     were dropped entirely (not misread — just absent from the output) and label words were frequently garbled. At
     3x, almost all of that recovered. **Always upscale crops before recognition** (2-3x floor; Core's new
     `CapturedImage.Resize` does bilinear upscaling for this).
   - **No single scale is failure-free**, and different scales drop *different* fields (3x recognized "Accuracy" but
     dropped "0.4"; 5x recovered "0.4" but then dropped "Accuracy" on the same window). If a field the parser expects
     (per known grammar) is simply absent after OCR, **do not silently treat it as "not present in-game"** — treat it
     as "OCR uncertain," and either re-run at another scale automatically or ask the user to confirm/enter that one
     value, the same way occlusion is already handled. Consider a routine multi-scale pass (e.g. 3x + 5x, reconciled)
     for milestone 2 rather than a single fixed scale.
   - **Consistent, reproducible character-level substitutions**, independent of scale: `rn` -> `m` (`Ornamentation`
     -> `Omamentation`, `Worn` -> `Wom`) and `ti` -> `b` (`Description` -> `Descripbon`, `Exaltation` ->
     `Exaltabon`) recur across multiple different screenshots. These are font-shape ambiguities, not noise — **a
     small fixed-vocabulary lexicon with edit-distance correction for known field labels** (Description, Lore,
     Slot, Class, Race, Size, Weight, AC, all stat/resist names, Ornamentation, Focus/Click/Worn/Proc Exaltation,
     Focus/Click Effect, Cast Time, Required Level, Cooldown, Charges, Tier, Modified/Unmodified, ...) should fix
     nearly all of these; this is a milestone 2 parser task, not something to solve by tuning OCR further.
   - **Payload text (not just labels) can also take single-character hits**: `Golden Efreeti Boots` -> `Golden
     Efreetj Boots`, an item's own name mid-window `Leatherfoot Sandals` -> `Leat)erfoot Sandals`. **The
     native-vs-foreign exaltation name check (see Augmentations above) must use fuzzy/edit-distance comparison, not
     exact string equality** — this was a hypothetical concern before, now confirmed as a real failure mode with
     real data. The same applies to matching an OCR'd item name back to a wiki page title.
   - **Redundancy helps**: the item name typically appears both in the window title bar and again in the content —
     when one is garbled the other is often clean. Parsing should read both and reconcile/vote rather than trusting
     only one.
   - **Roman numerals in effect names are unreliable** (`III` -> `Ill` or dropped entirely; `IV` -> `W` or dropped;
     `II` read fine). Low-effort mitigation: cross-reference recognized effect/spell names against known wiki data
     once fetched, rather than trusting the numeral OCR gave verbatim.
   - Minor, low-effort cleanup items: decorative punctuation gets substituted (`.`->`-`/`_`), fraction slashes in
     `Tier N X/Y` are sometimes dropped/merged, and empty-checkbox icons occasionally OCR as a stray single
     character — all easy to filter/normalize in the parser, not real risks.
   - Requires an OCR language pack for the Windows user profile (Settings -> Time & Language -> Language -> add
     "Optical character recognition"); `WindowsOcrEngine` throws a clear error if none is installed.

   **Confirmed these mitigations are still needed after switching to live (lossless) capture** — the user
   reasonably suspected the errors above were JPEG compression artifacts from the original sample screenshots,
   since the game's UI is plain Arial, not something inherently blurry. Tested directly: captured the same
   `Bladestopper +7` window live (lossless PNG, via `WindowCapturer`) and re-ran `OcrSpike` at both native res and
   3x. The error pattern was **pixel-for-pixel identical** to the JPEG version at both scales (`Descripbon`,
   `Omamentabon`/`Exaltabon` i.e. `rn`->`m`/`ti`->`b`, dropped AC/Weight values at native res, `Omamentation` still
   wrong and Weight's `2.4` still dropped even at 3x). Not a compression artifact — the UI text is genuinely
   ~9-11px cap height even losslessly, and at that size anti-aliased `rn` and `m` (etc.) become close to
   indistinguishable to a general-purpose OCR engine tuned on scanned/printed documents, regardless of image
   format.

   **SUPERSEDED — switched default engine to RapidOCR (PP-OCRv5 via ONNX), dramatically better.** The user
   (reasonably) still felt uneasy shipping on an engine with this many known failure modes and asked for a brief
   look at free local alternatives. Found `RapidOcrNet` (NuGet, MIT-ish, targets net8.0/net10.0, bundles
   PaddleOCR's PP-OCRv5 detector+recognizer models as ONNX, fully local/offline) and implemented it as a second
   `IOcrEngine` (`EQLWikiEditorAssistant.Ocr.RapidOcrEngine`), then ran it through `OcrSpike --engine rapid` against the
   *exact same crops* that had produced the Windows OCR errors above. Result, at **native resolution, no
   upscaling**:
   - Bladestopper +7: `+7` correct everywhere (title *and* content), `Description` correct, `AC: 43`/`Weight:
     2.4`/`HP: 87`/`Stamina: 26` all present (previously dropped at native res), the "Modified" row's duplicate
     item name correct (was badly mangled by Windows OCR), `Tier 7 61/128` with the slash intact, and — notably —
     `Focus Effect Improved Healing III` / `Click Effect: Rune IV` both correct **including the roman numerals**,
     which Windows OCR never got right at any scale.
   - Leatherfoot Sandals +6: `Focus Exaltation: Golden Efreeti Boots (Exaltation)` — the foreign-exaltation payload
     text — came back **exactly correct** (Windows OCR corrupted it to `Efreetj`, a single-char hit that would
     have broken a naive equality check on the native-vs-foreign test). All stat rows (`AC: 18`, `Mana: 49`, `SV.
     Magic: 32`, `SV. Void: 6`, ...) correct too.
   - **Upscaling RapidOCR actually made things slightly worse** in a couple of spots (tested 2x: `III` regressed to
     `II`, a colon got dropped elsewhere) — feed it native-resolution crops, not upscaled ones. This also makes the
     pipeline simpler (skip the resize step for this engine).
   - **One residual, consistent error remains**: `Ornamentation` -> `Omamentation` and `Worn` -> `Wom`/`Womn` (the
     `rn`->`m`-ish confusion) still happens with RapidOCR too, at native res and upscaled — this specific
     letter-pair is apparently a genuinely hard case at this exact font/pixel-size combination, not an
     engine-specific flaw. Also saw one isolated dropped single digit (`SV. Void: 7`'s `7`) in one run.
   - **Broader census (asked by the user: "any other character combinations to watch out for?"), 6 real windows,
     ~100+ recognized lines total** (Bladestopper +7, Leatherfoot Sandals +6, Water Flask, Selo's Drums of the
     March +4, Gloomwater Arrow +1, Prayers of Life — weapons, armor, ammo, a charge item, a consumable, a quest
     token, spanning class lists, slots, stats, resists, exaltations, effects, currency): the `orn`->`om`-ish
     cluster (`Ornamentation`/`Worn`, 4 occurrences across 3 different screenshots) is the **only** recurring
     substantive character-level error found. Everything else tested reliably: digits (including lone `1`, e.g.
     `Tier1 0/2`, and multi-digit values), fractions (`0/16`, `61/128`, `19/64`, `0/2`), apostrophes (`Selo's Drums
     of the March` — both as the item name and inside its own exaltation label), decimals (`0.4`, `0.1`), and new
     vocabulary not seen in the first pass (`Ammo`, `Base Dmg:`, `Range:`, `Skill:`, `Charge Effect:`, `Container:
     CLOSED`, `Primary Secondary`). The general OCR-literature confusions worth keeping in mind if more errors turn
     up later — `0`/`O`, `1`/`l`/`I`, `5`/`S`, `cl`/`d`, `vv`/`w` — are **not** confirmed problems here; don't
     pre-emptively build correction rules for them without evidence, just don't be surprised if one shows up.
     Only cosmetic/non-substantive noise seen otherwise: a colon after a label is dropped unpredictably (`Charge
     Effect` vs `Charge Effect:`) and `Tier1`/`Tier 1` spacing is inconsistent — neither changes the actual data,
     easy to normalize away, not worth tracking as "confusions."
   - **Where the lexicon correction belongs** (asked by the user): not inside `RapidOcrEngine`/`WindowsOcrEngine` —
     that vocabulary is game-domain-specific, not an OCR-engine concern, and per-`IEntityKind` (spells/monsters/
     quests will have their own label sets later). It's a milestone 2 **Parse**-step component, e.g.
     `Core.Ocr.FieldLabelLexicon` (or similar) living alongside the Item parser in `Core`: a small fixed list of
     expected field labels for the current entity kind, with edit-distance (Levenshtein ≤1-2) correction applied to
     each OCR'd label token before it's matched to a field. Deliberately separate from the wiki mapping config
     (that's user-editable MediaWiki-side vocabulary that evolves with the wiki; this is stable game-UI vocabulary)
     and testable on plain strings, no image/OCR round-trip needed. Given the narrow, confirmed scope (really just
     `Ornamentation`/`Worn` so far), this can start as a tiny hardcoded table and grow only as real evidence
     demands — no need to front-load a large lexicon.
   - **Net effect on the milestone 2 parser mitigations above**: still needed, but much lighter weight than
     originally scoped. Keep: the small lexicon/edit-distance correction (`FieldLabelLexicon` above — now really
     just needs to cover `Ornamentation`/`Worn`, not a long list), fuzzy/edit-distance comparison for the
     native-vs-foreign exaltation check and wiki-name lookups (still cheap insurance, and this engine isn't proven
     flawless either), and "missing expected field -> ask the user, don't assume absent" (still good practice
     generally). Drop: the mandatory upscale-before-recognition step and the multi-scale-pass idea — not needed for
     this engine, and upscaling actively hurt in testing. Roman numeral cross-referencing is now optional insurance
     rather than a near-certain necessity.
   - **Decision: `RapidOcrEngine` is the production `IOcrEngine`** once milestone 2 wires up the pipeline.
   - **Follow-up (2026-09-23): `WindowsOcrEngine` deleted.** It was originally kept on the reasoning that there's
     no cost to leaving working, tested code in the repo. That turned out to be wrong — it was a second
     implementation of a core interface to keep compiling, documenting and reasoning about, and it earned nothing
     back: it needs an OS language pack the user has to install, needs 3x upscaling to be usable at all, and
     still misreads on clean lossless captures (`Race: ALL` -> `Race: Al I` on a current sample). The comparison
     that justified the original choice is written down above, which is the part actually worth keeping; the code
     wasn't. `IOcrEngine` stays regardless — `Core` can't reference the Windows-only `Ocr` project, so the port is
     required for layering, not just for swappability. `OcrSpike` lost its `--engine` flag with it.
   - RapidOCR loads 3 ONNX models per `RapidOcrEngine` instance (`InitModels()` in the constructor) — construct it
     once and reuse it (e.g. as a singleton in the app), not per-capture, to avoid repeated model-load cost.
   - Packaging note: `RapidOcrNet`'s bundled `.onnx` model files (content items) must be present next to the
     consuming executable's output — they're copied automatically by the SDK when the executable project
     references the package directly, but did **not** propagate transitively through just a `ProjectReference` to
     `EQLWikiEditorAssistant.Ocr` in testing (`tools/OcrSpike` needed its own direct `PackageReference` to `RapidOcrNet`
     even though it already referenced `EQLWikiEditorAssistant.Ocr`, which itself references the package). Make sure
     `EQLWikiEditorAssistant.App`'s csproj ends up with the models copied to its own output in milestone 2/5 — verify
     with a real run, don't assume the transitive reference is enough.

   **Capture half: DONE, verified against the live game window.** `GlobalHotKey` (Win32 `RegisterHotKey` + a
   dedicated message-only window/thread, no UI-framework dependency) and `WindowCapturer` (Windows Graphics
   Capture) both live in `EQLWikiEditorAssistant.Capture`. Validated with a `tools/CaptureSpike` CLI (kept, like
   `OcrSpike`) against the real, running EverQuest Legends window (it happened to be open during development) —
   captured a live 2560x1440 frame with 3 real item windows visible — and the hotkey was confirmed to fire via a
   simulated keypress (PowerShell `SendKeys`) while a different window had focus. Both also have real (tolerant,
   skip-if-no-window) tests in `EQLWikiEditorAssistant.Tests/Capture`.

   **Important interop gotcha, worth knowing before touching this code again**: getting Windows Graphics Capture
   working from C# required `[GeneratedComInterface]` (the .NET 8+ source-generated "built-in COM" marshaler), not
   the classic `[ComImport]` attribute, for the two interop interfaces this needs
   (`IGraphicsCaptureItemInterop`, `IDirect3DDxgiInterfaceAccess`). `[ComImport]` compiles fine but **fails at
   runtime** with `InvalidCastException` ("Specified cast is not valid") when calling through an interface obtained
   from a CsWinRT `ComWrappers`-based object (which is what `SomeWinRtType.As<T>()` returns) — this is a known,
   independently-documented incompatibility between classic COM interop and CsWinRT, not a mistake specific to this
   code. Also: `IGraphicsCaptureItem`'s IID must be the literal GUID `79C3F95B-31F7-4EC2-A464-632EF5D30760` —
   `typeof(GraphicsCaptureItem).GUID` is a *different*, wrong GUID and causes the native call to fail with
   `E_NOINTERFACE` (which .NET also surfaces as `InvalidCastException`, easy to conflate with the first issue).
   And: `Direct3D11CaptureFramePool.Create(...)` requires a `DispatcherQueue` pumped on the calling thread to ever
   raise `FrameArrived` (fine in the WPF app's UI thread, not fine in a console tool/background thread) — use
   `Direct3D11CaptureFramePool.CreateFreeThreaded(...)` instead (available on the user's Windows 11; would need a
   version check + fallback to target Windows 10 too, which this app doesn't need to). `WinRT.MarshalInterface<T>
   .FromAbi(ptr)` / `SomeType.FromAbi(ptr)` takes ownership of the reference — don't also `Marshal.Release` it
   (double-release bug, separate from the InvalidCastException issue but easy to introduce at the same time).
2. **Locate + parse — BOTH HALVES DONE.** Locate uses a pixel border-tracing design (see below) that **replaced**
   an earlier OCR-line-clustering design after real occluded samples exposed it as unfixable. Parse (OCR lines ->
   `Item`, `+X` suffix, exaltation slot rows) is detailed in the "2. Parse — DONE" entry further down this list.

   **Key finding that shaped everything below: don't OCR hand-picked crops, OCR the whole screenshot.** Locate
   has to work from a full, uncropped capture in practice — there's no crop to hand it, finding the crop is the
   whole problem locate solves. Running RapidOCR on a raw 2560x1440 screenshot with default options initially
   found almost nothing (33 garbled lines, no `Description` tokens at all) — same root-cause pattern as
   milestone 1's OCR findings, but at the *detector* stage this time: `RapidOcrOptions.Default.ImgResize` (1024)
   downsamples anything larger before running the text detector, and at 2560px that shrinks our ~9-11px UI text
   below a usable threshold. Fixed in `RapidOcrEngine` itself (not just for locate): `ImgResize` is now
   `Math.Max(1024, Math.Max(image.Width, image.Height))` — the 1024 floor keeps small per-window crops behaving
   exactly as before (milestone 1's results unaffected, reverified via the existing golden tests), the dynamic
   ceiling fixes full-screenshot detection (361 lines found post-fix on the same screenshot, `Description`
   anchors present and correctly positioned on every real window). Costs ~4s for a full frame — acceptable for a
   hotkey-triggered, non-realtime action.

   **SUPERSEDED — attempt 1, OCR-line clustering.** First design: run whole-screenshot OCR once, then cluster
   the recognized *lines* into per-window groups by text proximity (+ a pixel "dark bridge" check between
   candidate lines, evolving through several fixes — kept only as history below). This worked well enough on the
   samples on hand at the time (6 screenshots, no genuine occlusion among them) to look done, and was committed
   as such. **It wasn't.** Once the user added real occluded-window samples and pushed back on the whole
   approach — *"I don't think clustering the OCR data is going to be a reliable way to isolate items. The way a
   human does it is by looking at what's inside the window border... Programmatically crop down to non-occluded
   windows only... locate a window via the title bar method, then determine the bounds of the Description/Lore
   tab by following their gray borders. If the border is broken at any point, error out as an occluded case"* —
   testing against those samples showed the clustering design simply could not reliably tell "this window's own
   content" from "a different, adjacent dark window" when the two were genuinely adjacent with no lighter gap
   between them (which is exactly what occlusion by another window looks like): a real occluded window and the
   character sheet panel both bled into the wrong cluster, and there was no way to fix that within a
   text-proximity model — the underlying information (where the window's real edge is) simply isn't present in
   a list of recognized text lines. Historical detail on what was tried, since the *reasons* it failed are the
   actual design lesson: pairwise transitive clustering by bounding-box gap alone bridged across different UI
   panels sitting close together; adding a pixel darkness check between candidate lines fixed that but still
   failed to separate two *different*, similarly-dark item windows from each other, since both look equally
   "dark" with no distance-based signal to tell them apart.

   **SUPERSEDED — attempt 2, tracing the edge of the dark interior.** Kept because the reason it failed is the
   design lesson. This traced "the border" as the point where the near-black interior stops being dark, since
   inspection had concluded there was no distinctly-coloured border line to follow. That conclusion was simply
   wrong, and the approach silently assumed whatever sits *outside* a window is brighter than the window. Often
   it isn't: the player's own 3D character model standing behind a window measures ~33-75, and an adjacent dark
   UI panel measures about the same as the interior, so scans tunnelled straight through the real edge and ran
   to the scan limit. That produced two visible symptoms and one invisible one: a window with the Lore tab
   active failed outright; "The Tenderizer +7" against a Bank window was written off as an unresolvable hard
   case; and, worst because nobody noticed, bounds routinely ran past the true edge into neighbouring UI — one
   sample's window was traced 137px too far left, so the *neighbouring* window's Class/Race/Size/Weight rows were
   parsed as if they belonged to the item. That last one also manufactured the "partial title occlusion" gap
   that Parse was then required to close: the title had never been occluded at all, the bounds were just wrong.

   **Current design — trace the content area's own outline, per the user's correction.** The user pushed back on
   the "no distinctly-coloured line" conclusion with a zoomed corner screenshot showing four clearly distinct
   regions — window interior, a thin grey line around the tab contents, the window's outer frame, and the world
   behind — and directed: *"Just follow that thin grey line and contain the OCR to the region within it."* Direct
   pixel measurement (which is why `tools/LocateSpike` gained `--probe x,y,dx,dy,count`, dumping raw RGB along a
   ray) confirmed it exactly:
   - Window interior `R=G=B≈16` (10-25 with JPEG noise); **content outline a 1px neutral grey line at 50-62**,
     essentially constant along its own length (20 consecutive pixels all reading 59-60); the outer frame just
     beyond it at **0-8**, i.e. *darker* than the interior; world 150-170; text up to 255.
   - The outline is drawn by the window, so unlike a brightness transition it does not depend on what is behind
     the window. That is the whole reason this works where attempt 2 didn't.
   - **Brightness alone still can't identify it** — anti-aliased text edges and the character model both land in
     the same 50-62 band. The discriminator is that the outline is a long uniform straight line, so every
     candidate is confirmed by requiring a long run of same-brightness line pixels *perpendicular* to the scan
     (a glyph edge spans a few px, a stat value-box outline a few tens). That run check is also what implements
     the user's original "if the border is broken at any point, error out as an occluded case".
   - **Requiring the outer frame just beyond a candidate** is what separates the content area's real boundary
     from the internal divider rules the window also draws (identical grey lines, but with more window beyond
     them). It must be tested on the **minimum** channel: JPEG bleed from a bright neighbour lifts individual
     channels of that thin frame unevenly — against a red element below one real window it reads `(11,0,0)` then
     `(34,0,0)`, which a max-channel test rejects — but never lifts all three at once, so the minimum stays at 0
     while the interior's neutral grey keeps a minimum of ~16. This cost one wrong iteration to find.
   - **Both tab states matter.** An *active* `Description` tab merges into the content area (no chrome line below
     the label); an *inactive* one (Lore selected) is a raised box with a short stack of chrome lines below it,
     all of which must be stepped past — stopping between them makes the content area's top outline itself look
     like the window's bottom on the next downward scan. Getting this wrong broke every Description-active window
     for one iteration.
   - **The top edge is deliberately left on the old brightness scan**: there is no grey line at the window's
     outer top (the title bar is pure black meeting the world), and the title bar must stay in the crop for
     Parse's title-vs-content check. It has been unanimous across every real sample; the edges the dark-neighbour
     problem actually broke are left/right/bottom.
   - **Results**: all 15 real samples validated. Traced widths are now consistently ~394-404px (the true content
     width) against 414-546px before, i.e. the old design was over-reaching on most windows, not just the
     obviously-broken ones. The Lore-tab window and the zero-gap Tenderizer/Bank case both now resolve. Merchant
     values, previously missing on several items, now parse because the windows are no longer truncated.
   - **Retired claims** (both were load-bearing and both were wrong): "the border isn't a distinctly-coloured
     line", and the "known residual gap" about a ~20%-occluded title. Parse's title-vs-content reconciliation is
     still implemented and still worth keeping — on the 4-window sample it correctly rejects a Bank/Tradeskill
     panel that OCR'd a literal "Description" and became a false-positive window — but it is no longer propping
     up a geometry hole.

   **Historical detail of the superseded attempt 2**, kept for the same reason as attempt 1. `WindowBoundsFinder`
   (`Core.Locate`) finds a window's *actual pixel bounds* by tracing outward from its `Description` tab anchor,
   and returns null (no bounds) rather than a guess when the edges aren't self-consistent. `ItemWindowLocator`
   then crops to those bounds and re-OCRs just that crop for the real field data — reusing milestone 1's
   already-validated crop-based accuracy — rather than reusing the whole-frame pass's own line detections.
   - **The actual border isn't a distinctly-colored line** (checked by eye and by sampling real pixels — see the
     zoomed screenshots taken during this work): the transition from the tan game-world background to a
     window's interior is a sharp, ~1px cliff straight from ~RGB(150-170,120-140,70-90) to near-black, no
     separate lighter outline color to chase. So "the border" is operationalized as *the edge of the near-black
     region* (same darkness signal as before, ~16,16,16 interior vs. the tan values above), not a colored line.
   - **A single ray per edge doesn't work**, for two reasons that pull in opposite directions: (1) a window's own
     content is full of bright text (its title, the tab label, stat lines, class lists) that a plain
     "stop-at-first-non-dark-pixel" scan trips over almost immediately — confirmed empirically, a naive scan
     straight down a real window stopped within ~15px of the top every time, nowhere near the true bottom
     several hundred px further down; (2) tolerating brief brightness to solve (1) then risks tolerating straight
     through a genuine gap into a *different* adjacent window, since that gap can be just as brief.
   - **The fix**: every edge (top, bottom, left, right) is found by probing several rows/columns — not one — and
     taking the *largest same-value cluster* (plurality, not a plain majority: a wide title can legitimately claim
     more probes than the true edge does, so the threshold is deliberately ~40%, not 50%+). Each individual probe
     tolerates a bright run shorter than ~26px (skips over one line of text/a glyph) but stops at a sustained one
     (a real exit). This does two jobs: harmless in-window obstructions (text, the icon) get outvoted by probes
     that dodge them; genuine occlusion — something covering *part* of a side — shows up as probes disagreeing,
     which is what "the border is broken" means numerically.
   - **One thing consensus alone can't catch, and needed an extra hard cap**: when the occluding panel (e.g. the
     character sheet) is adjacent along the *entire* side, not just part of it, every probe agrees on the same
     wrong, oversized answer — there's no disagreement for consensus to notice. Fixed with sanity ceilings on the
     final width/height (600 / 700px, generous over the largest real window measured, ~550x655) — an implausibly
     large result is itself the signal, independent of whether the probes agreed.
   - **A real, separate bug worth remembering**: an early version of the per-probe scan used "nearest edge
     midpoint between the two boxes" to decide where to sample; for two boxes that already touch/overlap (common
     for adjacent text rows), that path can land *inside* one box's own text, sampling a bright glyph and falsely
     reporting "not dark." Fixed by computing the actual empty-gap rectangle explicitly and skipping the check
     entirely when boxes already touch in both axes.
   - **Tuning was genuinely iterative and adversarial against real samples** — every threshold above (bright-run
     tolerance, probe count/agreement fraction, size ceilings) was arrived at by testing against real screenshots
     that broke the previous version, not derived up front. Anyone revisiting these constants should retest
     against the full real-sample set (`tools/LocateSpike`), not just tweak in isolation.
   - *(The "known residual gap" and the "Tenderizer is unresolvable" note that used to sit here have been
     retired — see the current design above for why both were artifacts of this superseded approach.)*
   - Also detects the `Lore` tab from the re-OCR'd crop (drives the two-capture lore flow).
   - Golden tests: `EQLWikiEditorAssistant.Tests/Locate/ItemWindowLocatorTests.cs`.
2. **Parse — DONE.** `Core.Items.ItemParser.Parse(IReadOnlyList<OcrLine>)` turns a clean `LocatedWindow.Lines`
   list into a `ParsedItem` (`Core.Items`): name/level (from the content-area copy, not the title), flags,
   classes, races, an optional bare-word slot, an ordered extensible `Stats` label/value bag, `ExaltationSlots`,
   `Effects`, merchant value, and a `Warnings` list for anything that couldn't be parsed (degrade-gracefully, per
   the repo's wiki-data robustness requirement — applied here to OCR data too).

   **Ground truth came from real `LocateSpike` dumps** (verbatim captures reproduced in the milestone 2 section
   above) against 4 structurally different real windows: a heavily-augmented armor piece with a foreign
   exaltation (Lustrous Russet Bracer +6), a weapon with its own native (removable) exaltation plus two foreign
   ones and three effect types (Bloodmoon +10), a quest/lore item (Slime Blood of Cazic-Thule +10), and a plain
   consumable with no slot/exaltations/tier chrome at all (Water Flask). Real item windows follow a consistent
   line order (title -> Description[/Lore] tab -> content-area name repeated -> unlabeled comma-separated flags
   -> `Class:` -> `Race:` -> an optional bare slot word, no "Slot:" label in-game unlike the wiki's own convention
   -> UI chrome -> a two-column stat block -> "Modified" chrome (name a third time) -> optional exaltation rows ->
   optional effect rows -> optional merchant value), so the header is parsed positionally (fixed order) and the
   body by pattern-matching each reconstructed row, since the body's actual field set varies a lot by item type
   (weapons show `Base Dmg`/`Delay`/`Skill`/`Ratio` where armor shows `AC`/resists; Water Flask has neither).

   **A real OCR-layout quirk this depends on**: unlike almost every other label:value line (which OCR returns as
   one self-contained `OcrLine`, e.g. `"Class: WAR CLR PAL..."`), the classic two-column stat block (Size/Weight/
   AC/stats/resists/etc.) comes back as *separate* fragments for the label and its value even on the same row —
   apparently because the game renders the value in a visually distinct box. `ItemParser` first reconstructs rows
   by Y-proximity (`GroupIntoRows`, 8px tolerance — cf. `WindowBoundsFinder`'s similar consensus tolerances), then
   pairs fragments within a row generically (a self-contained "Label: Value" fragment, or a bare "Label:"/"Label."
   fragment immediately followed by a separate value fragment, including two such pairs sharing one row like
   `Size:` `SMALL` `AC:` `15`) rather than assuming either shape specifically.

   **`Core.Ocr.FieldLabelLexicon`** (small, fixed vocabulary + edit-distance correction, exactly per the milestone
   1 writeup's placement decision) fixes the one confirmed recurring OCR error (`Ornamentation`->`Omamentation`,
   `Worn Exaltation`->`Wom`/`Womn Exaltation`) before a label is matched to a field or an exaltation/effect kind.
   Verified against real data: `Correct("Omamentation")` -> `"Ornamentation"`, `Correct("Wom Exaltation")` ->
   `"Worn Exaltation"`.

   **Required title-vs-content name reconciliation — implemented and verified closing the real gap.**
   `ParsedItem.TitleContentNameMismatch` compares the title-bar name against the content-area name (fuzzy,
   threshold scaled to name length) and is set whenever they diverge beyond ordinary OCR noise. Verified two ways:
   (1) a synthetic unit test reproducing the documented gap (title truncated to `"s Russet Bracer +6
   (Augmented)"` vs content `"Lustrous Russet Bracer +6"`) correctly flags a mismatch; (2) a **golden test against
   the actual real sample** (`1 item occluded by another.png`) that exposed the gap in the first place: `Locate`
   reports `PossiblyOccluded=False` (clean bounds — the occluder isn't a large enough share of any edge to break
   consensus) and OCR read the title as `"Lustrou s Russet Bracer +6 (Augmented) ? x"` (worse than originally
   estimated — a second overlapping window's own text bled into the crop), yet `Parse` still correctly sets
   `TitleContentNameMismatch=true`. This is the concrete, tested proof that Parse closes Locate's documented
   residual gap, not just a plausible-sounding design.

   **Native-vs-foreign exaltation check** (`ItemParser.IsForeignExaltation`, fuzzy `EditDistance` comparison
   against the item's own parsed base name) verified against real data too: on the real 3-window sample,
   Bloodmoon's own `Focus Exaltation: Bloodmoon (Exaltation)` slot is correctly identified as native (not
   foreign) — the exact "removable native exaltation" case from the plan's Augmentations section — while its
   `Click Exaltation: Golem Metal Wand` and `Proc Exaltation: Khyldom the Blood Drinker` are correctly flagged
   foreign, and Lustrous Russet Bracer's `Focus Exaltation: Runed Mithril Bracer` is also correctly flagged
   foreign.

   **Superseded (2026-09-23): effect sub-lines are now attached to their effect.** This previously landed
   `Cast Time`/`Cooldown`/`Required Level` in the generic `Stats` bag; the user flagged that as wrong on review —
   they're properties of the effect, not of the item. `EffectEntry` now carries `Name` (the magenta-drawn part
   that identifies the effect), `Conditions` (trailing parentheticals like `Must Equip`/`Can Equip`), and
   `Modifiers` (the sub-lines). A required level arrives two ways — its own sub-line on click effects, folded
   into the parenthetical (`Ykesha (Req Level 37)`) on proc/combat ones — and both normalize into
   `Modifiers["Required Level"]` so nothing downstream has to know which style the game used.

   **New dev tool**: `tools/ParseSpike` (mirrors `OcrSpike`/`LocateSpike`) runs the full Locate -> Parse pipeline
   against a real screenshot and dumps every parsed field per window, including a `[FOREIGN]` marker on
   foreign exaltations — use this, don't recreate an ad hoc version, when tuning parser rules against new samples.

   Tests: `tests/EQLWikiEditorAssistant.Tests/Core/Ocr/FieldLabelLexiconTests.cs` (plain-string unit tests, no
   image/OCR round-trip) and `tests/EQLWikiEditorAssistant.Tests/Items/ItemParserTests.cs` (deterministic unit tests
   built from a verbatim real capture, plus golden tests against real samples per the skip-if-missing pattern).
   62/62 tests passing repo-wide after this work landed.
2d. **Extraction accuracy — accuracy harness, then glyph matching for the window-crop pass.** See the
   "Extraction accuracy" section below for the full plan and the measurements behind it. Short version: the game's
   UI font is pixel-deterministic (two `7`s are byte-identical; the anti-aliasing ramp is identical across text
   colours), so the window-crop pass is exact template matching rather than a recognition problem. Build the
   accuracy harness first — there is currently no repeatable way to score extraction, so any tuning or rewrite is
   unmeasurable. `RapidOcrEngine` stays for the full-frame locate pass.
2b. **Eligibility check — DONE (2026-09-25)**, unblocked by the user resolving the Ornamentation question.
   `Core.Items.ItemEligibility.Check` reports every blocker (not just the first): a foreign exaltation, or a
   levelled item (`X>0`). `ShouldWriteLedgerEntry` carries the no-ledger-row rule on the result itself rather than
   leaving each caller to remember it, because getting that backwards makes an unchecked item look handled forever.
   - **Ornamentation is treated as a foreign exaltation** (user, 2026-09-25). This plan left it open on the grounds
     that ornamentation is cosmetic and blocking on it might be over-strict; the user's call is to block.
     `IsForeignExaltation` now short-circuits for that slot rather than relying on the name comparison — an
     ornamentation named like the item itself would otherwise read as "native", and for this one slot that
     inference is knowably false since ornamentation is never native.
   - The pipeline orchestration that calls this landed with milestone 5 (`ItemCheckPipeline`). The `ILevelNormalizer`
     seam was never built and is not needed for v1: a levelled item is simply ineligible. It belongs with milestone 8.
     No stat-scaling math — v1 only fully processes `+0` items.
3. **Wiki client — read half DONE (2026-09-24); the live login/edit check is waiting on the user's bot password.**
   `Wiki.MediaWiki` has `MediaWikiClient`/`IMediaWikiClient` (anonymous fetch, bot-password login,
   conflict-guarded edit) and `ICredentialStore`/`WindowsCredentialStore`; `Wiki.Wikitext` has the scanner,
   `ItemPageDocument` and `StatsBlock`. `tools/WikiSpike` is the spike tool. 129 new tests, all green.

   **The plan's "parse the statsblock and re-render it" is superseded — re-rendering cannot meet the repo's
   byte-for-byte constraint.** Measuring 662 real item pages (two independent samples) showed they agree on the
   line grammar and disagree on nearly every whitespace decision within it: alignment padding, single vs double
   spaces between stats, `AC: 15 <br>` with a stray space before the break, blank lines mid-block. A wholesale
   re-render rewrites lines whose data never changed, which turns a one-value correction into a whole-page diff
   no reviewer can skim — defeating the review step the tool is built around. So the raw source is the source of
   truth and the parse exists only to compare: parameters carry their exact byte spans and an edit splices into
   one, lines keep their verbatim text and render by concatenation. Round trip is identity by construction, and
   measured at 0 failures over all 662 pages.

   **Findings worth carrying into milestones 4 and 6:**
   - **Legacy flags are discarded, not translated** (user, 2026-09-24 — "EQL completely redid flags"). The wiki
     speaks two dialects: imported Project1999-era pages write `MAGIC ITEM  LORE ITEM  NO DROP`, while the game's
     own vocabulary across all 101 verified windows is exactly seven values (`No Trade`, `Lore Equipped`,
     `Placeable`, `Quest`, `Attunable`, `No Destroy`, `No Storage`). Discarding is also the only *safe* rule,
     since no faithful mapping exists — `MAGIC ITEM`/`TEMPORARY`/`EXPENDABLE` have no counterpart, and classic
     `LORE ITEM` (carry one) is a different property from `Lore Equipped` (equip one). **This retires an earlier
     note here calling flags "a translation problem, bigger than the plan assumed"** — replace-wholesale is
     simpler, and it also makes the nine sampled pages with single-spaced legacy flags a non-issue, since the line
     is discarded whether or not it could be split.
   - **The flag vocabulary is open-ended; copy flags through blindly and build no known-flags list** (user,
     2026-09-24). Whatever the game displays is what the wiki should say, *whether or not the tool knows what it
     means*. The 101 verified windows happen to hold only seven values, but the user has separately seen `No Pet`,
     `Heirloom` and `Free Storage` on rare items, and the devs keep adding more. So an unfamiliar flag is ordinary
     data — not a warning, not an unparsed field. Validating against a list would reject exactly the rare items
     most worth recording. (Glyph matching suits this perfectly: it reads characters, not words, so it has no
     vocabulary to be surprised by.)
   - **Attunable/No Trade, resolved: keep the wiki's `Attunable` and alert** (user, 2026-09-24 — "I always err on
     the side of trusting an 'Attunable' flag on the wiki when mine says 'No Trade'"). The page was written by
     someone who saw the item before it was attuned, so it is the better-informed source. Keep the comparison and
     the alert separable: the user may later want this exact pair treated as *matching* via a setting, if the
     alert becomes noise.
   - **`This is a meal!` and friends: alert, never act** (user, 2026-09-24). On the flags line of 11 sampled pages
     and in no captured window — EQL removed them. Not meaningless, though: in original EverQuest `This is a
     hearty meal!` meant the food lasted longer, and that may still hold with the UI simply no longer exposing it.
     The user's practice is to move the text into `notes` by hand. The tool must neither perform that move nor
     silently drop the line — it raises it and leaves it to the human.
   - **`merchant_value` is always normalized (new requirement, user 2026-09-24)**, target `"1p 2g 3s 4c"` with
     zero denominations dropped and `absolutely nothing` as the literal no-value string. The transform is well
     defined because both sides were measured: the game emits the right content in the wrong spelling
     (`22 platinum 8 gold 5 silver 7 copper`) and has *already* dropped its own zeros, so the job is just
     `N platinum|gold|silver|copper` → `Np|g|s|c`. Corroborated on `Peridot`, where the window's
     `9 platinum 5 gold 2 silver 4 copper` matches the page's existing `9p 5g 2s 4c`. The wiki side is the mess:
     of 64 sampled values, 16 are a full HTML `<ul>/<span style="color:silver">` block (sometimes under a
     `VALUE TO VENDOR with CHA : 80` heading) and the plain ones drift through `2.6pp`, `~3pp`, `1pp 7gp`,
     `1gp to vendor.`, `1.3 gold` and `0p 0g 1s 0c with 111 Charisma`.
     - **Resolved (user, 2026-09-24): the captured figure always wins, and the CHA annotations are legacy.** In
       legacy EverQuest players guessed and checked to learn a value, hence the recorded conditions. EQL's window
       states the **maximum** directly, independent of Charisma and faction — so an annotated legacy figure is
       often *wrong*, not merely misformatted (`187p1g9s1cp (68 CHA @ Kindly)` is sub-maximum), and replacing it
       is a correction. This also means **no legacy-format parser is needed at all**: the page's value is never a
       source of truth, so the tool renders the captured value and compares strings; an HTML block or a `2.6pp`
       simply differs and is overwritten.
     - Implemented ahead of the rest of milestone 4 as `Core.Items.MerchantValue`, since it was fully specified
       and unblocked. A test asserts every merchant value in the verified corpus parses, so a change in the game's
       phrasing fails the build rather than writing a wrong figure. Unreadable input reports failure, never zero —
       zero would publish `absolutely nothing` for an item whose price could not be read.
   - **Duplicate parameters are real and the last wins.** Two pages carry two `|notes=`, the first empty. Reading
     the first (as the code did initially) meant reading a blank value off a page that visibly has content.
   - **`recipes`, not `recipe`** — 64 of the 65 sampled pages with one spell it plural; the odd page out writes
     `recipe` and is therefore ignored by the template. Outside the v1 write scope either way.
   - **An empty parameter's padding is a trap, and only the live sweep found it.** 343 of 662 sampled pages have a
     parameter written and left blank, whose raw value is *entirely* whitespace — so the leading and trailing
     padding are the same characters, and emitting both duplicated them. The ten hand-picked fixtures all happened
     to have non-empty parameters, so the offline suite was green while every second real page would have gained a
     blank line. The lesson generalizes: fixtures chosen to cover *interesting* cases systematically miss *boring*
     ones, and the live sweep is what covers those.
   - **"Zero unparsed lines" does not mean the parse is right**, which is the same lesson the OCR side learned
     with silent-wrong: a bad split still produces a field, just one with a nonsense label, so the unparsed count
     read zero while `MEDIUM WT` and `Blunt Atk Delay` were being produced. The label census in
     `WikiSpike grammar` is what catches it. Two grammar bounds came out of that and only that — digits end a
     label (otherwise a backward scan runs through a preceding value and `STR: +10 WIS: +10` reads as one field),
     and a label is at most two words (three made every single-spaced `Skill: Archery Atk Delay: 0` mis-split).
   - Item pages are structurally cleaner than feared: of 414, every one uses the literal `{{Itempage` spelling
     with named parameters only, and none contains an HTML comment, a `<nowiki>` or a wikitable. The messiness is
     entirely inside `statsblock`.

   **Write path verified against the live wiki (2026-09-25).** A bot password stored in Credential
   Manager; `WikiSpike whoami` confirms the session and its granted rights read-only, and `WikiSpike edit` ran
   against the developer's own sandbox page (revisions 179273 and 179274), restoring the page byte for byte — confirmed by an
   independent API read of the page and its history, not just from the tool's own report.
   - **ConfirmEdit/Turnstile does not block a logged-in API edit.** This plan flagged that as something to verify
     in the wiki spike, since the extension is installed and could have blocked automated edits outright. It
     doesn't, for an authenticated bot-password session. Also confirmed working: the CSRF token flow, `assert=user`,
     and `nocreate` not interfering with editing a page that already exists.
   - **One thing the test did *not* prove, and it is worth not overclaiming: that `basetimestamp` actually
     *rejects* a concurrent edit.** It proved only that the parameter is accepted in the format sent. MediaWiki
     attempts a three-way merge when a base timestamp is supplied, so a non-conflicting concurrent edit is merged
     rather than refused — meaning the guard catches *unmergeable* concurrency, not all of it. Proving the
     rejection needs a genuinely unmergeable conflict, which costs real revisions on a real page. Left unverified
     deliberately; milestone 5's review flow re-fetches before writing regardless, so the guard is defence in
     depth rather than the only protection.
   Eleven real pages are tracked as fixtures in `tests/EQLWikiEditorAssistant.Tests/Wiki/Fixtures/` (public wikitext —
   nothing private, unlike screenshots).
4. **Analyze**: format-compliance rule set (start small), field diff (level-0 items only in v1), icon compare.
   **Compliance rule set and category derivation DONE 2026-09-25** (`Wiki.Analysis.ComplianceChecker`) — six rules, each kept or dropped on
   measured frequency across 744 real pages rather than on plausibility: era template missing (232 pages, the tool
   can never fix it since the window does not say which expansion an item is from), `{{Item Lore Missing}}` (35,
   fixed), `<onlyinclude>` wrapper missing (4, reported), duplicate parameter (3, fixed), unrecognized parameter
   name (3, reported — one page writes `recipe` for `recipes`, so its recipe section does not render at all),
   required parameter missing (0, a guard). `ToolWillFix` splits "part of the edit" from "reported and left alone",
   with nothing in between.

   **Icon comparison DONE 2026-09-25** (`Core.Icons`, `Wiki.MediaWiki.IconCache`), flag-only as planned. The
   in-game icon has no frame — the sprite is drawn with transparency straight onto the window background — so its
   strip was measured across all 43 screenshots rather than traced. Comparison resamples each side's ink bounding
   box onto a 12x12 colour signature, which is what makes a 40x40 wiki PNG comparable to a sprite the game draws
   ~1.1x larger.

   **Three designs failed before this one, and the negative control is the only reason that was noticed.** A check
   that answers "match" to everything looks perfect on same-item pairs, so `WikiSpike icons` also compares each
   capture against the *other* items' wiki icons. That caught: alpha being discarded, so a transparent PNG margin
   read as ink and every icon mismatched at the random baseline; an ink floor of 70 that reduced a dark pauldrons
   icon to a 27x10 sliver of a 38x14 sprite; and a luminance-only 64-bit hash that could not tell small similar
   icons apart, because what distinguishes a brown band from a silver one is hue, not brightness.

   **The user asked whether an exact pixel diff would work, and it was tested rather than argued** (2026-09-25). It
   will not, against these files: the game draws every icon at a consistent ~1.10x the wiki file, and only 2-7% of
   the captured colours appear anywhere in the source — nearest-neighbour cannot invent a colour, so the game is
   interpolating. Crucially this is *not* a UI-scale setting: glyph matching proves the UI text is a byte-identical
   blit, so nothing global is resampled. Either the game renders a 40x40 asset into a larger cell, or the wiki files
   were downscaled to 40x40 during extraction — and if it is the latter, re-extracting at native size would make an
   exact diff possible and every alert a real defect. That is on the user's list.

   The measurement did improve the perceptual check: correlation beats mean absolute difference (4 false alerts
   against 7 at the best zero-false-match threshold), and an icon with too little contrast is now not judged at all
   — `Nightmare Hide` is nearly black and produced a confident mismatch against its own correct icon. Final: **2
   false alerts out of the 75 pairs the check is willing to judge, and zero false matches.**

   **The era rule changed from "unfixable" to an automatic fix once the user explained what it is for** (2026-09-25):
   the banner records that an item has actually been *seen in game*, which a capture is precisely the proof of. So
   the tool sets `{{Classic Era}}` unconditionally, including over a legacy `Velious Era` inherited from the P1999
   import (176 sampled pages). When the first expansion ships this stops being a constant and needs a way to ask the
   user which era an item belongs to — parked as a future feature.

   **The Item Page Blueprint on `Help:Contents` supplied the statsblock line order and the category tables**, and
   **contradicted one of my measurement-based choices**: canonical resist labels are `SV Fire`, not the `SV FIRE`
   that appears on 31 pages against 3. The blueprint wins — frequency only measures how many pages predate it. The
   category tables were taken from it rather than censused for the same reason: `Golden Efreeti Boots` says
   `Class: ALL` but lists only 14 of the 16 class categories, so a census would have recorded an incomplete
   convention as the convention.
   **Field diff DONE 2026-09-25** (`Wiki.Analysis.ItemPageAnalyzer` + `Wiki.Mapping.WikiMapping`), along with
   `Wiki.Wikitext.PageTitle` and `Core.Items.MerchantValue`. The compliance rule set and the icon comparison are
   still open; effect lines are not yet diffed (their wikilink markup is its own grammar). *(All three landed later
   the same day — see the paragraphs below.)*

   **The mapping layer landed here, as this plan anticipated it might.** `WikiMapping` is data with built-in
   defaults, built by censusing 37 game stat labels against 49 wiki ones. Most of it is renaming
   (`Weight`→`WT`, `SV. Fire`→`SV FIRE`, `Delay`→`Atk Delay`); the interesting parts are that an **unmapped stat is
   reported rather than dropped** (a stat the tool has never seen is how a game patch announces itself, and
   `Accuracy`/`Container`/`Type`/`Items` are live examples awaiting a decision), and that `Ratio` is explicitly
   *derived* and ignored since the wiki stores both its inputs.

   **`WikiSpike analyze` is the wiki-side `AccuracySpike`, and building it immediately paid for itself** by finding
   three things no unit test would have:
   - **Two mapping gaps that would have pushed regressions to the wiki**: the game writes `Weight Red: 100` where
     the wiki writes `100%` (the diff called them different and would have stripped every `%`), and the wiki's
     `FINGER` is the game's `Fingers`. Both now in the mapping; the slot exception list was censused across all 18
     slot names in the corpus so it is known complete.
   - **A case where the wiki legitimately holds more detail than the window** — `Range: 50 / 75 / 100` against a
     captured `50`. Now `NeedsReview` rather than an overwrite that discards the alternatives.
   - **A measurement error of my own**: the first run analyzed levelled items too and reported 300 differing fields,
     which would have read as "the wiki is badly stale". A levelled item's stats are *legitimately* higher than the
     wiki's level-0 figures (Bladestopper +7 shows AC 43 against a correct 25). Eligibility has to run before
     analysis, in the tool *and* in the measurement. Filtering dropped it to 19.

   **Mapping decisions the user settled after seeing that run (2026-09-25):** `Container` (open/closed) is ignored,
   being implied by Capacity and Size Capacity; `Type: Shield` and `Items: Arrows` map into the statsblock, beside
   `Slot` and `Size Capacity` respectively (a rendering concern for later; the user is getting both added to the
   template vocabulary so the tool is not writing unsanctioned fields); and the wiki signs attributes and resists
   (`STR: +5`) where the game does not. A **sign-only** difference is deliberately not an edit — rewriting `STR: 5`
   to `STR: +5` is the incidental reformatting this tool must never do, so the sign applies only to a value already
   being written. `Accuracy` remains the sole unmapped stat, awaiting a home.

   **Effects landed 2026-09-25** (`Wiki.Wikitext.EffectLine`), to the convention the user supplied. Focus effects go
   to the `focus_effect` parameter; everything else is an `Effect:` line with the `itemeff` span link. The span is
   *functional* — it carries a tooltip a bare `[[Name]]` does not — so modernizing a legacy link is a real fix the
   user makes on every item, unlike the cosmetic sign case. An effect the convention cannot express is refused rather
   than written incomplete: `Charge` and `Consumable` kinds have no agreed token and `Cooldown` has no agreed place,
   both now on the user's TODO.

   **The effect questions were settled 2026-09-25**: `Charge` and `Consumable` render as `Charge Clicky` /
   `Consumable Clicky` (interim wording, community conversation pending); `Cooldown` and `Cooldown Group` go at the
   end of the parenthetical; a rebuilt effect line completely replaces the old one, including parts the window does
   not show. A sign-only stat difference is corrected only when the edit already writes another signed stat, so the
   tool never leaves a line it made inconsistent — implemented as a post-pass, since the answer depends on what
   every other field concluded.

   Baseline, eligible items only (re-measured 2026-09-28): 41 of 90 distinct captures eligible, 38 have pages,
   **5 already correct and 33 would change**; 260 fields match, 28 differ, 49 missing on the wiki, 2 unverifiable,
   and **2 need review** — the two food-prose lines, which are human-by-design. (The "9 already correct and 29 would
   change" recorded here before was stale rather than a regression — confirmed by re-running `analyze` at the commit
   preceding milestone 5's pipeline work and getting the same 5/33.) It also caught a typo on a live page (`Lore Equpped`) and, by being
   run, two bugs of its own: the cast-time unit and the `Weight Reduction` percent.

   Rules the user settled up front:
   - **A template field absent from a page is fine** unless the capture has data for it. Absence only matters when
     it would hide something being added or changed; don't demand a full parameter set.
   - **`No Trade` makes merchant value unverifiable — preserve the wiki's and warn.** Measured: all 63 `No Trade`
     windows in the corpus show no merchant-value row, and all 12 `Attunable` windows show a real value. The
     absence is a property of tradeability, not evidence of worthlessness, and overwriting would destroy a figure
     no future capture of an attuned item can recover. **Broader than the Attunable framing it came from**: any
     `No Trade` capture is unverifiable. `absolutely nothing` never co-occurs with `No Trade`, so "verified
     worthless" and "couldn't see it" stay distinguishable.
   - **`itemname` must equal the page title, and any mismatch is a defect.** 10 of 538 real item pages mismatch.
     **Corrected 2026-09-25**: an earlier note here said those pages render fine and treated the parenthesised-title
     pattern as a legitimate convention. Both were wrong — the user pointed at a live broken page and inspection
     confirmed the mechanism. `Itempage` renders the item as a hover box anchored on a link to `[[itemname]]`, so a
     mismatch links elsewhere: either a red "page does not exist" where the item should be
     (`Essence of Barbarian (Wormwood)`), or — worse, because it is invisible — a silent link to an unrelated
     article (`Tailoring (Item)` points at the Tailoring *skill*). 8 of the 10 are the parenthesised shape, which
     is kept as its own outcome only to tell the user what kind of problem it is.
     - **The fix is not obvious, so the tool only reports.** The in-game item genuinely shares one name across its
       craft-material/deity/quest variants, so setting `itemname` to the qualified title would render a name the
       game never shows. Properly resolving it likely needs a template change — one for the user's template-
       improvement campaign.
     - **Methodological note worth keeping**: a first pass reported 33 mismatches, a third of them artifacts of the
       measuring script sanitizing `:` and `*` out of filenames (both are legal in MediaWiki titles). Measure
       against titles as the API returns them.
   - **A name with a title-illegal character (`# [ ] { } | < >`) is referred to the user, never rewritten.** A
     `Cell Key #5` cannot have a page at its own name; the editors chose `Cell Key No. 5` by hand, and the choice
     is permanent and becomes the URL. Lookup of such a name also fails, which is treated as "page does not exist"
     plus a loud warning that it probably exists under a hand-picked name. **Consequence for 4b: the ledger has to
     record the wiki's name alongside the in-game one**, or a deliberately-renamed item looks unhandled forever.
   - **The grave/apostrophe confusion runs both ways on real pages and this tool can fix it** rather than
     perpetuate it: glyph matching keeps `'` and `` ` `` distinct, so the in-game character is known.
     **Handling settled (user, 2026-09-25)**, and it is shaped by a constraint worth recording: *the user cannot
     rename a page.* Their remedy is to create a correctly-named page, copy the content, and redirect the old one
     (which is desirable anyway, since searchers type the wrong character) — losing edit history, which they
     dislike but have no way around. So a missed candidate does not merely inconvenience: it produces a duplicate
     page nobody can delete. `ItemPageLookup` therefore retries the quote variants on a miss and returns
     `FoundMisnamedCandidate`: the item is still treated as new, because acting on the wrong page is worse than
     treating a real item as unlisted, but the user is shown the candidate and prompted about the redirect. Left to
     the user whether the tool should ever automate the create-and-redirect dance; for now it only informs.
4b. **Ledger — DONE 2026-09-25** (`Wiki.Ledger.CheckedItemsLedger`, `Core.Items.ItemFingerprint`). JSON rather
   than SQLite: a few thousand small records, written whole and rarely, with no query beyond a keyed lookup. Saved
   through a temporary file, and a corrupt ledger loads as empty rather than throwing — every row is reconstructible
   by capturing the item again, so losing it beats refusing to start.
   - Only `Matched`/`Edited` mean done; `Flagged`, `Skipped` and `NotOnWiki` never let a capture skip the wiki, and
     the outcome is checked before the fingerprint so an unchanged flagged item is still flagged.
   - The fingerprint sorts lists (a class list is a set) and includes the captured level, but **excludes parser
     warnings** — they quote OCR fragments and churn with every tuning change, so including them would expire every
     row on every release.
   - `WikiPageTitle` is stored alongside the item name, closing the `Cell Key #5` -> `Cell Key No. 5` problem this
     plan raised under milestone 4.
   - The plan's named tests are asserted on request *counts* against a counting fake client, since "makes zero wiki
   requests" is the actual property. The pipeline orchestration that calls it landed with milestone 5, and the
   "re-check" action is a per-item "Refresh wiki data" link (2026-10-07) where the ledger let a capture skip the wiki.
5. **Review UI + commit — MOSTLY DONE 2026-09-28.** The renderer (`Wiki.Analysis.ItemPageEditor`), the pipeline
   orchestration (`Pipeline.ItemCheckPipeline`) and the WPF review screen all landed; what remains is the lore
   *write* path, noted at the end.

   **The pipeline got its own assembly, which this plan did not anticipate and should have.** It is the only thing
   that depends on both the game side (`Core`: locate, parse, eligibility, icons) and the wiki side (`Wiki`: lookup,
   analysis, editor, ledger), and neither may depend on it. Keeping it out of the WPF project is what makes the whole
   sequence testable with fakes on a portable target — 17 tests that assert on wiki request *counts* and ledger
   contents, which is how the plan always framed the ledger's own acceptance criteria.

   **Two ports came out of making this real rather than dev-only**, and both close gaps this plan had left open:
   - `Core.Icons.IImageDecoder` / `Capture.WindowsImageDecoder` — the `byte[]` → `CapturedImage` decoder the icon
     comparison needed. `TestSupport.ImageFile` now delegates to it, so the measured icon thresholds transfer.
   - `Core.Locate.IItemWindowLocator` — this is the "portable capture-source interface in Core" split the plan
     parked under Architecture, arriving for the reason it predicted: something finally needed to depend on it.
     Note it is the *locator* that needed the seam, not the capturer; the capturer's output (`CapturedImage`) was
     already portable, so an interface there would have had one implementation and one caller.

   **Rules the pipeline enforces that were scattered across this plan and are now in one place**: an occluded or
   ineligible window writes no ledger row; a page that agrees is `Matched` immediately but only if nothing wants a
   human, otherwise `Flagged`; `NotOnWiki` is recorded and never counts as done; a wiki failure is reported per
   window rather than abandoning the frame.

   **Committing re-fetches and refuses a page that changed since the check.** This plan left `basetimestamp`'s
   rejection behaviour deliberately unverified, on the grounds that a collision is effectively impossible here —
   that reasoning stands, but it is not the whole guard: MediaWiki merges what it can, and this tool's edits are
   wholesale parameter replacements, exactly the shape that merges cleanly while discarding somebody's work. So the
   re-fetch is the real protection and the parameter is defence in depth, which is the opposite of how the plan
   framed it.

   **The UI's one non-negotiable**: what is on screen is what gets saved. The proposed wikitext is editable and the
   commit writes *that*, not the renderer's output — a review screen whose approve button saved something else would
   make the review meaningless. Warnings sit above everything, saving is confirmed explicitly (a public wiki, under
   the user's account, with no way for an ordinary editor to delete a revision), and the diff is a real LCS one
   rather than the spike tool's set subtraction, which collapsed duplicate lines.

   **The lore two-capture flow landed the same day**, with one rule this plan did not anticipate and which the user
   confirmed (2026-09-28): **lore is added when the page has none and never overwritten when it has some.** Every
   other field in this tool treats the capture as authoritative. Lore should not, because it is a paragraph rather
   than a short token — a misread word would pass review invisibly — and because a page's lore can carry wikilinks
   the item window cannot show, so "differs" does not mean "stale". Comparison ignores line breaks (the game wraps
   prose to its window) and nothing else.
   - **The user asked for a warning on top of that**, since the page's wording has been right nearly every time in
     their experience but not every time. So a difference warns in the review screen's strip *and* gets its own panel
     showing both texts in full — lore is the one field a findings grid cannot display.
   - **Which exposed a hole worth recording**: a flagged item that gets committed for some *other* reason would have
     been recorded `Edited` and never raised again, silently settling a judgement nobody made. A commit on an item
     that still needs a human now records `Flagged`, and `RecordCheckedByHand` ("Looks right — mark as checked") is
     the only thing that settles it. Without that action such an item would re-fetch on every capture forever, and
     the only other escape would be overwriting the very thing the user just approved.

   The in-app credential dialog this listed as still open landed on 2026-10-05, as Settings > Wiki account.
6. Mapping config: JSON mapping schema with built-in defaults wired through parser/renderer/checks from milestone 3
   onward, plus the Settings window to edit it (basic version can land with milestone 4).
7. Polish: credential storage, error handling. (A tray icon was planned here and **dropped by the user, 2026-10-08**:
   the hotkey already brings the window to the front, so it would add nothing.)
   **Ledger view DONE 2026-09-29** (`App.LedgerWindow`), pulled ahead of milestone 6 by the user's agreement: over a
   long session the unsettled rows are the state that accumulates silently, and nothing else surfaced them. It
   delivers this plan's "search/filter by outcome, clear/reset entries" — per-row "Forget" rather than a bulk clear,
   since forgetting one item is the action that actually comes up — and **drops the export**: the ledger is already
   readable JSON in the user's own app-data, so the window links to the file instead of making a second copy of it.
   - **The view judges nothing.** "Would the next capture skip this?" is `CheckedItemsLedger.Consult` called with the
     row's own fingerprint, which is literally that question. The one rule it needed in its own right — "which
     outcomes mean done" — was **extracted** as `MeansDone` and is now shared with `Consult`, rather than copied;
     a test pins that the two agree across every `CheckOutcome`, because a drifted copy would quietly start calling
     a flagged item done.
   - Searching/filtering went into `Wiki.Ledger.LedgerQuery`, not the view model, since the tests project does not
     reference the WPF app. That constraint pushed the logic somewhere it belonged anyway.
7b. **A real installer and an alpha release** — moved to "Versions and releases" above (2026-10-08), where it is
   planned as `1.0.0-alpha.1`.
8. **[Deferred, post-v1] Item-level stat-scaling investigation**: figure out the real per-stat `+X` -> level-0
   formula and replace the milestone 2c placeholder. Starting leads: the `MediaWiki:ItemLevelMultipliers` tier table,
   the (unreadable) `ItemLevelSlider`/`SpellLevelSlider` extensions, the unexplored `EQLClientData` extension. Needs
   real in-game screenshots of the same item at multiple levels (see below) and rounding-tolerant validation, since
   neither the wiki's nor our own guess is guaranteed to match the game's hidden float precision. Once landed, `+X`
   items flow through the same pipeline `+0` items already use — no ledger/pipeline redesign needed, just replacing
   `ILevelNormalizer`'s behavior for `X>0`.

## Extraction accuracy — glyph matching for window text (planned 2026-09-23)

### Context
With 99 real item windows in the corpus, the extraction error profile is narrow and well measured: 2 windows
correctly reported occluded, 0 name mismatches, 31 warnings. About 20 of those warnings are a **single class** —
RapidOCR's *detector* fails to find isolated single digits (a lone `7` in a stat column), so roughly 1 item in 5
loses at least one stat value. The rest are character substitutions in item names (`Tarnished`->`Tamished`,
`Ornamentation`->`Omamentation`), roman numerals (`III`->`/II`), and long effect lines wrapping.

The user asked whether OCR is even the right technology, and whether a local AI approach would be more reliable.
Investigating that produced a decisive measurement:

- **The game renders text with a deterministic bitmap font.** Two separate `7` glyphs in the same window are
  **byte-identical**, offset by exactly 16px: `16 16 16 255 16 16 16 112 191 255 223 223 16`. Even the
  anti-aliasing intermediates match exactly.
- **Anti-aliasing uses a fixed quantized coverage ramp — `16, 64, 112, 159, 191, 223, 255` — and it is identical
  for white, yellow and green text.** Only the channel the ramp is applied to changes. There is no ClearType /
  subpixel fringing (yellow moves R and G together; green keeps R=B).
- Text rows sit on an exact 16px grid, on a constant `R=G=B=16` background.

**So this is not a machine-learning problem.** We are running a general-purpose ML text recognizer — trained on
photographs and scanned documents, with all the robustness machinery that implies — against what is actually
exact template matching. The answer to "would AI help" is that the correct direction is *less* inference, not more.

**Why a local VLM was rejected** (not merely "not now"): its failure mode is wrong for this tool. Today a lost
value is *missing and flagged*; a VLM's wrong value is *plausible and confident*. This tool writes to a public
wiki, digits are exactly where VLMs are weakest, and the wiki cannot correct a wrong number the way fuzzy
page-title matching can correct a misspelt name. The whole design is built on "flag uncertainty, never guess" —
a VLM inverts that. Secondary objections: VRAM contention with the running game, and non-determinism against the
ledger's fingerprint design. A VLM remains plausible much later as a *fallback* for windows the deterministic
path flags, never as the primary reader.

**Answers to the two questions the user raised about a glyph atlas:**
- **Font colour: no separate atlas needed.** Normalize each glyph region to a coverage map,
  `(value - background) / (peak - background)`; the ramp above is colour-independent, so white/yellow/green/magenta
  all collapse to the same pattern.
- **Font size: yes, one atlas per size** — but this is not a new coupling. Every constant in `WindowBoundsFinder`
  is already pixel-measured at this UI scale, and UI scale/skinning is already recorded above as out-of-scope for
  v1 and likely future work. The atlas joins that same "one UI profile" bucket, and because it is *generated* it
  can be rebuilt for a new scale or skin by re-running the generator, not by hand.

### Design
Implement glyph matching as a second `IOcrEngine` and select per call site. The port kept for layering reasons
when `WindowsOcrEngine` was deleted now earns its keep properly.

- **`OcrIntent.FullFrame` -> keep `RapidOcrEngine`.** The locate pass scans a whole 2560x1440 screenshot including
  3D world content; general OCR is the right tool there and it already finds every `Description` anchor reliably.
  Do not touch its settings — `WindowBoundsFinder` depends on those anchors, and the `ImgResize` value is a
  documented, measured fix.
- **`OcrIntent.WindowCrop` -> new `GlyphOcrEngine`.** Inside a traced window the background is a known constant,
  the font is fixed, and rows are on a 16px grid. This is the pass that feeds `ItemParser`, and where all the
  errors are.

`ItemParser` needs **no changes at all** — it consumes `OcrLine`s either way, and its whole grammar test suite
(built from verbatim captures, runs without `samples/`) stays valid.

### Stages

**Stage 1 — accuracy harness (build first; needed whichever direction wins).**
Nothing can be compared without a repeatable score; today we have eyeballed warning counts.
- Scorer in `tools/EQLWikiEditorAssistant.TestSupport/Accuracy/` (dev-only shared helper, like `ImageFile`/`RepoPaths`):
  load/save, field-level diff of `ParsedItem` vs expected, report aggregation.
- Driver `tools/AccuracySpike/` (mirror `tools/ParseSpike.csproj`, incl. its **own** direct `PackageReference` to
  `RapidOcrNet` — the `.onnx`-copy trap). Modes: `--bootstrap`, `--diff`, `--json`.
- Ground truth `tests/EQLWikiEditorAssistant.Tests/Accuracy/expected-items.json`, **tracked in git**. Safe to commit:
  it holds only parsed item-window fields — the same public game data this tool publishes to the wiki. Hard rule,
  stated in the file header and `samples/README.md`: *never* raw full-frame OCR text, never anything outside a
  window crop, no coordinates, and store warning **counts/categories** rather than verbatim warning strings
  (those quote OCR fragments and are tuning-unstable).
- Bootstrap emits `?TODO` for every field the parser flagged, so the ~20 known misses become ground truth the
  user fills in by eye rather than absences that freeze today's bugs in as "correct". User has agreed to verify
  the full corpus (~1-2 hours, once).
- Scoring is **exact string match, never fuzzy** — fuzzy is right at runtime and wrong for measurement; it would
  hide the entire character-substitution class. Verdicts: `correct` / `missing` / `wrong` / `extra`, cross-tabbed
  by *was it flagged?*. Headline gates: `structural` (window-count mismatch) and **`silent-wrong`** (a wrong value
  the parser did not flag) must both stay 0.
- Regression test `tests/.../Accuracy/CorpusAccuracyTests.cs` gated behind `EQLWIKI_ACCURACY=1` (precedent:
  `EQLWIKI_LOCATE_DIAG`) — a corpus pass is ~5 minutes and must not sit in the default `dotnet test`. Pure
  comparer unit tests run always.
- **Acceptance**: the harness must reproduce the already-measured numbers (99 items, 2 occluded, 31 warnings). If
  it doesn't, the harness is wrong, not the pipeline.
- Also add `tools/ParseSpike` and `tools/AccuracySpike` to `EQLWikiEditorAssistant.slnx` — `ParseSpike` is missing, so a
  root `dotnet build` never compiles it.

**Stage 2 — glyph atlas generator.**
- New `tools/GlyphSpike/`: segment every text run across the corpus (rows by the 16px grid and foreground runs;
  columns by background gaps), normalize each glyph to a coverage map, then **cluster by exact coverage equality**.
  Because glyphs are byte-identical this clustering is unambiguous.
- Expect roughly 70-90 distinct glyphs (A-Z, a-z, 0-9, punctuation). The user labels each *cluster* once — a
  one-time pass of ~15 minutes, not per-glyph drudgery. `--report` prints cluster count and a sample of each.
- **First thing to validate**: that one character yields exactly one cluster. If a character produces several
  (sub-pixel positioning, kerning variants), the approach still works but the atlas grows — measure before
  committing to the rest of the stage.
- Atlas committed as a small file under `src/EQLWikiEditorAssistant.Ocr/` (coverage bitmaps + labels; no screenshot
  content, so safe to track). Non-text shapes — the item icon, the tier progress bar, checkboxes — will form
  clusters that match no character; they are simply left unlabelled and discarded at match time, with a count
  surfaced for diagnostics.

**Stage 2 — DONE (2026-09-24). Atlas built, 90 characters, 89 distinct shapes.**
- Font identity confirmed first, as required: an 'A' from a real item window and an 'A' from the Notes Window
  sheet are byte-identical including every anti-aliased intermediate, just translated. The sheet is usable.
- The planned question "one character, one cluster?" is answered **yes, with exactly one exception**: lowercase
  'l' and uppercase 'I' are the same bare 2x9 vertical bar. Recorded in the atlas as a two-label entry and left
  for context to resolve; guessing either would be a silent substitution.
- Colour handling landed as predicted (one colour-blind atlas), but the normalization was subtler than the plan
  assumed and took two corrections, both caught by an off-ramp diagnostic rather than by eye: peak must be
  per-colour (a 2px 'i' never reaches full coverage, so its own maximum mis-calibrates it) and per-band (one row
  carries a white label beside a magenta effect name). Baseline detection needed a third fix — see CLAUDE.md;
  the `<>?|:~` row cannot determine its own baseline, and getting it wrong made every `Race:` read `Race.`.
- The estimate of "70-90 distinct glyphs" and "user labels each cluster, ~15 minutes" both held, except the
  labelling pass turned out to be unnecessary: the sheet spells known strings, so labels are assigned
  positionally and the builder refuses on any glyph-count mismatch.
- Atlas is an embedded resource of `Core` rather than a loose file (`GlyphAtlas.Bundled`), so it cannot go
  missing the way the RapidOCR models have.
- Against a real item window, every cleanly-segmented text row reads exactly right, including the three error
  classes this work exists to remove: `Ornamentation` (not `Omamentation`), `Worn Exaltation` (not `Wom`), and
  `SV. Void: 7` - an isolated digit RapidOCR drops entirely.
- **Quote characters, closed same day.** The sheet originally had a grave accent but neither `'` nor `"`, so an
  apostrophe read as nothing. The user added both and recaptured: the atlas is now **90 characters / 89 distinct
  shapes**, `l` = `I` still the only collision, and apostrophe and grave are separate entries — `Kilva's Skin of
  Flame` and `Kavruul`s Mystic Pouch` both read correctly, where RapidOCR collapsed every grave into an
  apostrophe. A test pins that they stay distinct.
- **Carried into stage 3:** *chrome contaminates bands.* A band that merges with the item icon, a divider rule or
  the tier bar loses its background/peak calibration and its whole row reads with gaps (the `Class:` and title-bar

**Stage 3 — DONE (2026-09-24). Every field in the verified corpus now reads exactly.**
- Final A/B on the same corpus and the same ground truth: **2094 correct, 0 wrong, 0 missing, 0 extra,
  0 silent-wrong, 0 structural, 0 warnings**, against RapidOCR's 24 missing / 32 wrong / 13 silent-wrong /
  24 warnings. All three `CorpusAccuracyTests` ratchets are now 0. `--rapid` on `AccuracySpike` and `ParseSpike`
  keeps the old configuration runnable for comparison.
- `GlyphOcrEngine` + `RoutingOcrEngine` live in `Core.Glyphs`, not the `Ocr` project as this plan assumed: the
  reader needs nothing Windows-specific (no Skia, no ONNX, no WinRT), so keeping it in `Core` means plain
  `net10.0` tests exercise it with no capture or model files. `OcrIntent` landed on `IOcrEngine` as planned and
  `ItemWindowLocator` passes `FullFrame` for the locate pass and `WindowCrop` for the crop.
- `ItemParser` needed **no changes at all**, as the plan predicted — but only because the reader was shaped to
  match RapidOCR's fragment contract (splitting a row at the stat block's column gap rather than emitting one
  string per line). That was not free, and is the reason the parser's whole grammar suite stayed green.
- The chrome problem carried from stage 2 was solved by deleting the assumption rather than patching it:
  per-candidate self-calibration removes any need to segment text bands before matching. See CLAUDE.md for that
  and for the four measurement-driven corrections it took — descenders under divider rules, a degenerate
  flat-colour match, cell-width-based spacing, and always-spaced glyphs learning a too-wide cell.
- **Two judgement calls worth knowing about**, both documented at their call sites:
  - `l` versus `I` is resolved from the surrounding word rather than reported as ambiguous. They are the same
    pixels, so reporting both corrupts every word containing either; the fallback for a wrong guess is a *name*,
    where fuzzy wiki lookup already backs us up. Digits are never guessed.
  - 9 windows of ground truth had their stat *order* corrected. The bootstrap had recorded RapidOCR's detection
    order, which is not reading order, and the user verified values rather than order. `--adopt-reading-order`
    rewrites a window only when the stats are the same multiset, so it can never change a value.
- Remaining known behaviour, accepted: a glyph the game itself clips is not read. A divider rule is drawn *over*
  the 'p' descenders in the "Modified" chrome row, so that row reads "Bladesto" + "er". The engine declining a
  partially-covered glyph is the correct call, and that row is chrome the parser discards anyway.
  rows do this). Clean text rows are unaffected. This is the main integration problem for stage 3, and is a
  segmentation concern, not a matching one.

**Stage 3 — `GlyphOcrEngine` + integration.**
- `src/EQLWikiEditorAssistant.Ocr/GlyphOcrEngine.cs` implementing `IOcrEngine`: segment, normalize, match each glyph
  against the atlas, group into words/lines, emit `OcrLine`s with the same bounding-box contract so `ItemParser`
  and its row-grouping are unaffected.
- Add `OcrIntent` to `IOcrEngine` (defaulted, so no call site breaks) and select the engine in
  `Core/Locate/ItemWindowLocator.cs` (full-frame pass line ~27, crop pass line ~44).
- **Unmatched glyphs must never be guessed.** An unmatched run degrades to the existing behaviour: emit nothing
  and let `ItemParser` raise its orphaned-label warning. Optionally fall back to `RapidOcrEngine` for that region
  only.
- A/B against RapidOCR through the harness on the same corpus — this is the payoff of building Stage 1 first.

### Verification
- `dotnet test` green throughout, including the locate golden tests (window widths 388-404, the occlusion case
  still occluded, the tooltip still excluded).
- `AccuracySpike --diff` before/after: `missing` should collapse toward 0, with `silent-wrong` and `structural`
  staying at 0. Ratchet `CorpusAccuracyTests`' baseline constants down in the same commit as each improvement.
- Split reporting to catch overfitting: tune/validate on the `12*` slot-and-category batch, hold out `01`-`11`
  (the geometry and negative cases, each of which exists because it broke an earlier design).
- `tools/ParseSpike` spot-checks on real samples stay the human-eyeball path, as now.

### Explicitly rejected
- A local VLM as the primary reader — see Context.
- Growing `FieldLabelLexicon` to chase item-name substitutions: it contradicts the lexicon's documented scope
  (stable game-UI *labels*), and names are unbounded payload text whose right defence is fuzzy wiki page-title
  matching plus user confirmation, in a later milestone. Glyph matching should remove the problem anyway.
- Lowering RapidOCR's `TextScore` to recover values: trades *missing* (flagged) for *wrong* (potentially silent),
  the wrong direction for a tool that edits a public wiki.
- A generic hyperparameter-search framework for the 18 `RapidOcrOptions` knobs. If glyph matching lands, the
  window-crop pass stops using them; the full-frame pass stays frozen deliberately.

## Needed from the user before milestone 1–3
- A handful of real item-window screenshots (with and without lore tab; weapon, armor, expendable, quest item).
- One reference "ideal" item page and one legacy-format/bad-data page (lore example already provided: `Earring of Bashing`).
- (Resolved: `merchant_value` and `focus_effect` both appear in the item window, so they're in the OCR scope; samples should include items that have them.)
- A Special:BotPasswords credential (later, for milestone 3).
- **[For milestone 8, later]** Once we circle back to leveling: real screenshots of the *same* item at several
  levels (e.g. +0/+low/+high), across a few different stat types, to validate whatever formula we derive.
- (Resolved: the user provided 2 real occluded-window screenshots — `1 item occluded by another.png`,
  `single item occluded by bag windows.png` — plus `3 items.png`/`3 lvl 0 items.png`. These drove the pixel
  border-tracing redesign above and exposed the residual title-truncation gap that Parse must close.)

## Verification
- Unit/golden tests: OCR-line parsing and statsblock parse/render round-trip on sample data; format-check rules.
- Locate tests (DONE): single/multi-window separation with no cross-contamination, tooltip exclusion, both real
  occluded samples handled per their expected outcome (see the milestone 2 writeup) —
  `EQLWikiEditorAssistant.Tests/Locate/ItemWindowLocatorTests.cs`.
- Ledger tests: second capture of an unchanged, already-matched item makes zero wiki requests (assert via a fake wiki
  client); changed fingerprint / rules version / "re-check" all force a fetch; flagged items are never skipped.
- Icon cache tests: two items sharing an icon ID trigger exactly one icon download; a cached icon needs zero requests;
  negative-cache entries expire.
- Eligibility tests: a foreign exaltation blocks processing and writes no ledger row; a native exaltation (slot name
  == base item name) proceeds normally; `+0` items normalize as identity and proceed; `+X>0` items are routed to
  "unsupported," blocked, and write no ledger row (v1). Once milestone 8 lands: leveled items normalize to level-0
  stats within tolerance across a range of real sample items.
- Manual end-to-end: press hotkey on an in-game item, confirm the parsed fields, see the proposed diff, approve, and
  verify the resulting revision on eqlwiki.com (use a sandbox page first).
