# EQL Wiki Editor Assistant

A Windows app that helps keep item pages on the [EverQuest Legends Wiki](https://eqlwiki.com) accurate. Open an item's
window in the game and press a hotkey: the Assistant reads the window, finds the item's wiki page, shows you exactly what
differs, and proposes the edit. You review it step by step, see the wiki's own preview, and decide whether to save. For
an item the wiki has no page for, it proposes the whole page.

**This is an alpha** (1.0.0-alpha.1). It works well on the setup it was built on; your setup is what testing is for.

## What it does and never does

- **It only looks at your screen.** It takes a screenshot of the game window, the same way screen recorders do. It never
  reads the game's memory or its network traffic.
- **Screenshots stay on your computer.** The text in the item window is read on your machine; nothing about your screen
  is sent anywhere.
- **Nothing reaches the wiki unless you press a button that says so** — Save, Create, Upload. Checking an item only reads
  the wiki, and does so anonymously. Each edit it makes is marked in the page history with the app's name and version,
  e.g. `(Editor Assistant 1.0.0-alpha.1)`.
- **Your wiki password is a bot password**, kept in Windows Credential Manager, never in a file.
- **The only other request it makes is to GitHub**, once each time it starts, asking whether a newer release exists, so
  it can say so if one does. It sends nothing about you, and it downloads an update only when you click *Update*.

## Requirements

- Windows 10 (version 2004 or later) or Windows 11.
- EverQuest Legends in **windowed or borderless windowed** mode.
- The game's **default UI font (Arial), default skin and 100% UI scale.** The Assistant reads item windows by their exact
  pixels, and other settings are not supported yet. Developed at 2560x1440; other resolutions should work but are
  untested.

## Getting started

1. **Install it**: download `EQLWikiEditorAssistant-win-Setup.exe` from the newest release on the
   [Releases](https://github.com/AustonZ/EQL-Wiki-Editor-Assistant/releases) page and run it. It installs for your Windows
   account only, needs no administrator rights and no separate .NET install, and adds a Start menu and a desktop
   shortcut. The installer is not code-signed yet, so Windows SmartScreen may warn about an unknown publisher; choose
   *More info*, then *Run anyway*.
2. **Create a bot password** on the wiki at [Special:BotPasswords](https://eqlwiki.com/Special:BotPasswords), granting
   - *Edit existing pages* (required),
   - *Create, edit, and move pages* (to create pages for new items),
   - *Upload new files* (to upload an item's icon when the wiki lacks it).
3. **Enter it in the Assistant** under Settings (the cog) > Wiki account. It is checked against the wiki before it is
   saved. You only need it the first time you save something; checking items needs no login.

## Using it

1. In game, open an item's window (inspect it), with nothing covering the window.
2. Press **Ctrl+Shift+E** (change it under Settings > Capture hotkey). You can stay in the game; several open item
   windows are all read at once.
3. In the Assistant, each item gets a tab. Its review goes **Edit → Preview → Submit → Formatting → Done**:
   - *Edit* shows what differs and the proposed change. You can edit the wikitext yourself, or say the wiki is right.
   - *Preview* shows the page as the wiki will render it.
   - *Submit* is the final diff and the edit summary, and where you save.
   - *Formatting* offers a separate, data-free edit that lays the page out the way the wiki's Item Page Blueprint
     describes, kept separate so the page history stays easy to read.
4. If an item has a **Lore** tab, the Assistant asks you to switch to it in game and capture again.

History lists every item checked and what is still waiting for you. An item that hasn't changed since it was settled is
not looked up again.

### Not handled yet

- Levelled items (`+1` and up) and items carrying another item's exaltation are reported and skipped: their stats are not
  the base item's.
- Items only — not spells, NPCs or quests.
- Two different items sharing one name (`Shimmering Pearl` and `Shimmering Pearl*`) cannot be told apart, so check what
  it proposes for such items with care.

## Your data

Everything the Assistant keeps is in `%APPDATA%\EQLWikiEditorAssistant`: settings, the history of checked items, cached wiki
icons, and `errors.log`. **Settings > Saved captures** is off by default; when on, it keeps every screenshot it takes in
a `debug-captures` folder there. Those are full screenshots of your game, so they can show your character's name,
other players and chat.

**Updating**: when a newer version is out, the top of the window says so. Click *Update*: the Assistant downloads it,
closes, and opens again on the new version. Items you were still reviewing are closed, so finish or save them first. Your
data is kept. Running a newer release's `Setup.exe` over the installed one works too: it offers to update.

**Uninstalling**: Windows Settings > Apps > Installed apps > EQL Wiki Editor Assistant. It asks whether to delete your
data as well — settings, history, cached icons, saved screenshots and your wiki login. The answer is No unless you choose
Yes, and the question gives up after 30 seconds and keeps everything.

## Reporting a problem

Open an [issue](https://github.com/AustonZ/EQL-Wiki-Editor-Assistant/issues) and include the version (shown at the
bottom of Settings), what you did, and what happened. If the app reported an unexpected error, the end of `errors.log`
helps. Please don't attach a screenshot without checking it for names and chat first.

## Building from source

Needs the .NET 10 SDK on Windows.

```powershell
dotnet build
dotnet test
dotnet run --project src/EQLWikiEditorAssistant.App
```

[CLAUDE.md](CLAUDE.md) is the detailed design record: why each rule exists and what it was measured against.

## Licence

The Assistant's own code is [MIT licensed](LICENSE). **Three things in this repository are not**:

- the game's item icons in `game_assets/item_icons`, which belong to the game's publisher — see
  [game_assets/README.md](game_assets/README.md);
- item pages copied from the wiki as test fixtures, which belong to the wiki's contributors;
- the game screenshots in `samples/` that the tests run against, which show the publisher's artwork.

Licensing is recorded machine-readably in [REUSE.toml](REUSE.toml). The components the Assistant is built on, such as
RapidOCR and the PaddleOCR models it uses, are listed with their licences in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

EverQuest is a registered trademark of Daybreak Game Company LLC. This project is not affiliated with or endorsed by
Daybreak or the EverQuest Legends Wiki.
