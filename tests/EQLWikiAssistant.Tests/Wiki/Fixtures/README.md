# Wiki page fixtures

Verbatim wikitext of eleven real item pages, fetched from https://eqlwiki.com on 2026-09-24.

**Why these are tracked when `samples/` is not.** The screenshot rule exists because a screenshot captures whatever
else was on the player's screen — character names, chat, other players. These files are the opposite: public wiki
pages any visitor can read at `?action=raw`, written by the wiki's own editors. Nothing private can be in them.

**Why real pages rather than hand-written ones.** The wikitext layer's job is to survive input nobody designed. Every
fixture here was chosen because it breaks a plausible simplifying assumption, and several of those assumptions were
in the code before these pages disproved them:

| Page | What it is here to prove |
|---|---|
| `Earring of Bashing.txt` | The canonical shape: aligned parameters, `{{Item Lore\|...}}` inside `notes`. |
| `Shimmering Ruby Stiletto.txt` | Two `\|notes=` parameters — an empty one first, the real one last (MediaWiki uses the last). Also a leading `[[File:...]]` outside `<onlyinclude>`, and an `Effect:` line whose value contains both a piped link with an HTML span and its own `Casting Time:` colon. |
| `Water Flask.txt` | 21 KB of nested `{{ItemWhereTable}}`/`{{ItemWhereRow}}` with piped links in almost every cell — the parameter scanner's worst case for pipes it must not split on. Also carries `merchant_value`. |
| `10 Dose Greater Null Potion.txt` | `EXPENDABLE  Charges: 10` — an unlabelled flag and a labelled field sharing one line, separated only by a double space. |
| `Blackened Alloy Longsword.txt` | `{{Item Lore Missing}}` placeholder, which the tool always removes. |
| `Bladestopper.txt` | The placeholder followed by `<br>` and then a human's own commentary, including `<s>` markup — the removal must take the break and leave the prose. |
| `Golden Efreeti Boots.txt` | `focus_effect` as its own parameter; a stat line using single spaces between fields (`WIS: +9  INT: +9 SV POISON: +1`) where most pages use double. |
| `Cloak of Scales.txt` | Five stats on one line separated by single spaces; a trailing space before `<br>` (`AC: 15 <br>`). |
| `Fishbone Earring.txt` | `{{Item Lore \| Fishbone Earring.}}` — spaces around the pipe and inside the value. |
| `Arctic Mussels.txt` | `This is a meal!` as the whole flags line: no colon, and punctuation a label pattern must not claim. |
| `10 Dose Potion of Antiweight.txt` | A present-but-empty `\|notes=` — its raw value is nothing but padding, which an earlier version emitted twice. Common (343 of 662 sampled pages have an empty parameter) and absent from every other fixture here, so the live sweep caught it before the offline suite could. |

To refresh or extend the set:

```powershell
dotnet run --project tools/WikiSpike -- fetch "<page title>"
```

`WikiSpike roundtrip` checks the same properties against a much larger live sample; these eleven are the subset that
runs in the ordinary `dotnet test` with no network.
