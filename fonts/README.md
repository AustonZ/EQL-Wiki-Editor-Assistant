# fonts/

Everything in this folder except this README is gitignored, and must stay that way.

**EQL Wiki Assistant** is a personal modification of Windows' own Arial, made so the in-game item window is easier
for a human to read and exactly readable by the tool. Arial's licence forbids modifying it at all, let alone
distributing it; the user chose to make this copy for their own machine. So the font files never enter git, never
get attached to an issue, and never get shared. This folder is the one place they live.

## What is here

- `EQLWikiAssistant-Regular.ttf`, `EQLWikiAssistant-Bold.ttf` — the font in use (version 2.3). Install both, then
  select "EQL Wiki Assistant" in the game's font option.
- `archive/` — every earlier version, kept for comparison:
  - `v1-liberation` — Liberation Sans 1.05 with a serifed I and Arial's per-size line heights. Rejected: the user
    preferred Arial's look. Note it uses the **same family name** as the current font, so never install both.
  - `v2.0` — Arial with a serifed I. Its I dropped 2px below the baseline in Word and the Windows font viewer.
  - `v2.1` — v2.0 with that fixed.
  - `v2.2` — adds 1px of space after `r`, to stop `rn` reading as `m`. Too airy before round letters.
  - `v2.4`, `v2.5` — 2.2 with the `r` arm reaching ½ and ¾ of a pixel into that space. Too close to fusing `rn`.
    The chosen 2.3 reaches ¼.
- `build/` — the scripts that made them. `build2.py` builds the Arial-based fonts from the system's own Arial
  (`C:\Windows\Fonts\arial.ttf`, `arialbd.ttf`), so no Arial data is stored in them; `build.py` built v1.

## What the font changes, and nothing else

Measured through GDI, DirectWrite, GDI+ and FreeType when each version was built:

- **Capital I** gains serifs as thick as E's bars, hinted onto E's bar rows at every size from 8 to 48 px/em, and
  is 2px wider at the game's 12 px/em. The 35 letters built on I (Í, Ï, Ĳ, Greek and Cyrillic I...) follow it, with
  their accents re-measured onto Arial's rows.
- **r** is 1px wider at 12 px/em, its arm extended ¼ px into that space. Its outline is otherwise Arial's, and its
  hinting is untouched.
- **Everything else is Arial byte for byte**: every other glyph outline, every other width at every size, and
  Arial's own per-size line heights, so the game sizes text exactly as it sizes Arial (12 px/em on a 15px line).

## Rebuilding

```powershell
cd fonts/build
python build2.py "$(Get-Content spec2.json -Raw)" '{"family":"EQL Wiki Assistant","ps":"EQLWikiAssistant","version":"2.3","out":"final","widen":{"r":171},"stretch":{"r":43}}'
```

Needs `pip install --user fonttools freetype-py`. `spec2.json` holds the measured hinting choices (which of Arial's
control values the serifs use, and the per-size corrections that put the serifs and accents on Arial's rows).
