# EQL Wiki Editor Assistant

A Windows app for contributors to the [EverQuest Legends Wiki](https://eqlwiki.com) that speeds up the process of
verifying that the items listed on the wiki match the game.

The EverQuest Legends (EQL) Wiki was created by forking the Project 1999 (aka "P99") wiki, which documented an
unofficial emulation of EverQuest as it existed up to the Velious era. EverQuest Legends, however, is a fork of the
official (aka "Live") EverQuest game, so the details that were carried over from the P99 wiki are not always accurate
for EQL. This means we need to check every item, NPC, quest, etc. to verify that it matches up with the game. This is
a time-consuming process currently done almost entirely by hand.

For items, this means reading every stat, flag and effect in the game's item window and comparing it with the
corresponding wiki page (if one even exists!). The Assistant aims to do much of that comparison for you. Open an item's
window in the game and press a hotkey. The Assistant reads the window, finds the item's wiki page, and shows you what
differs along with the edit it proposes. Nothing is saved until you have reviewed the edit and pressed Save. For an
item the wiki has no page for, it helps you create a whole new page.

## What it checks

From the item window, the Assistant checks:

- the item's **name**, its **icon** (`lucy_img_ID`), its **flags** (`No Trade`, `Lore Equipped`, `Attunable`, ...),
  **classes**, **races** and **slots**;
- every **stat and resist**, weapon damage and delay, and container capacity;
- **effects**, including the focus effect, with their casting time, cooldown and required level;
- the **lore** text and the **merchant value**.

It also brings the page in line with the wiki's
[Item Page Blueprint](https://eqlwiki.com/Help:Contents):

- it sets the `{{Classic Era}}` banner, since the item is in the game;
- it adds any **class and slot categories** the item earns that are missing. Pages imported from P99 often
  lack Beastlord and Berserker;
- it removes old P99 flags (`MAGIC ITEM`, `NO DROP`, ...) and the `{{Item Lore Missing}}` placeholder, and
  moves lines such as `This is a meal!` into the notes;
- it lays the page out the way the blueprint does. This layout change is shown to you separately from the data change,
  and the Assistant checks that it changes nothing the page says.

**What it leaves alone:** drops, vendors, quests, recipes, book text, any category it does not derive from the item
window, and anything you or others wrote in the notes. Lore is added when a page has none, but never overwritten.
When the page's lore differs from the game's, the Assistant shows you both versions and leaves the choice to you.

For an item the wiki has never heard of, the Assistant writes a complete new page from the blueprint. It usually
recognizes the item's icon from the game's own icons, and offers to upload the icon file to the wiki when the wiki
lacks it.

## What it does and never does

- **It only takes screenshots of the game.** It captures the game's window and nothing else, so it never sees any other
  program on your computer. It never reads the game's memory or its network traffic.
- **Screenshots stay on your computer.** The text in the item window is read on your machine; nothing about your screen
  is sent anywhere.
- **Nothing reaches the wiki unless you press a button that says so** — Save, Create, Upload. Checking an item only reads
  the wiki, and does so anonymously. Each edit it makes is marked in the page history with the app's name and version,
  e.g. `(Editor Assistant 1.0.0-beta.1)`, so other editors can see where it came from.
- **Your wiki login is a bot password**, kept in Windows Credential Manager, never in a file.
- **The only other request it makes is to GitHub**, once each time it starts, asking whether a newer release exists, so
  it can say so if one does. It sends nothing about you, and it downloads an update only when you click *Update*.
- **It is not AI.** The Assistant was written with the help of AI (Claude Code), but is itself an ordinary computer
  program. All processing happens on your computer.

## Requirements

- Windows 10 (version 2004 or later) or Windows 11.
- These game settings. The Assistant reads item windows by their exact pixels, so other settings are not supported yet:
  - the **Arial** UI font, the game's default;
  - the **`default_modern`** UI skin. In the `default` and `default_light` skins it finds item windows but cannot read
    them, and says so;
  - **UI Scaling of 1.00, 2.00, 3.00 or 4.00**, with **UI Text Filtering set to None**.

Any screen resolution _should_ work. It was developed at 2560x1440.

## Getting started

1. **Install it.** Download `EQLWikiEditorAssistant-win-Setup.exe` from the newest release on the
   [Releases](https://github.com/AustonZ/EQL-Wiki-Editor-Assistant/releases) page and run it. It installs for your
   Windows account only, needs no administrator rights and no separate .NET install, and adds a Start menu and a desktop
   shortcut. The installer is not code-signed yet, so Windows SmartScreen may warn about an unknown publisher. If it
   does, choose *More info*, then *Run anyway*.
2. **Create a bot password on the wiki.** A bot password is a second password for your wiki account, made for apps:
   it can do only what you allow it, and you can revoke it at any time without touching your real password. Go to
   [Special:BotPasswords](https://eqlwiki.com/Special:BotPasswords), give it a name (for example `Assistant`), and allow:
   - *Edit existing pages* (required);
   - *Create, edit, and move pages*, to create pages for new items;
   - *Upload new files*, to upload an item's icon when the wiki lacks it.

   The wiki then shows a user name such as `YourName@Assistant` and a password. Keep the page open for the next step.
3. **Enter it in the Assistant**, under Settings (the cog in the top right) > Wiki account. The Assistant checks it
   with the wiki before saving it, and lists what it is allowed to do. You can skip this until you first want to save
   something: checking items needs no login.

## Using it

### Checking an item

1. In the game, open an item's window (inspect the item), with nothing covering the window.
2. Press **Ctrl+Shift+E**, or click *Capture now* in the Assistant. You can stay in the game: the hotkey works from
   anywhere. If several item windows are open, they are all read at once. You can change the hotkey under Settings >
   Capture hotkey.
3. Each item appears in the list on the left of the Assistant, with what was found: *Already correct*, *Edit
   proposed*, *Not on the wiki*, *Needs attention*, and so on.

If an item has a **Lore** tab, the Assistant asks you to switch to that tab in the game and capture again, so it can
read the lore too.

### Reviewing an edit

Select an item to review it. The screenshot of its window stays on the left while you work through four steps:

1. **Edit** shows each difference between the game and the wiki, and the proposed edit as a diff. Below that, you can
   expand the layout changes, and you can edit the wikitext yourself. If you think the page is right and the game
   window is misleading, choose *The wiki is right — mark as checked*.
2. **Preview** shows the page exactly as the wiki will render it.
3. **Submit** shows the final diff and the edit summary. Saving here is the only confirmation; there is no "are you
   sure?" afterwards.
4. **Done** shows the page as it now reads on the wiki.

*Skip for now* leaves the item for later, and it comes back the next time you capture it.

A red **Not verified for EQL** badge next to an item's name means nobody has marked its page as verified on the wiki
yet. Verifying a page vouches for all of it, including drops and vendors, which the Assistant cannot check, so the
Assistant never does it for you. Click the badge to see how to verify a page yourself.

### When it can't read a window

The Assistant tells you, rather than guessing, when:

- **another window covers part of the item window.** It shows the whole screenshot with the window ringed in red.
  Move the window into the clear and capture again;
- **the item is leveled** (`+1` and up). The wiki records an item's stats at level 0, and the Assistant cannot work
  those out from a leveled item's window;
- **the game uses a font, skin or UI scale it does not support** (see Requirements).

None of these is recorded, so the item is treated as new the next time you capture it.

### History

*History*, at the top of the window, lists every item checked and what happened to it. The count of items needing
attention at the bottom of the window opens History showing just those. An item that has not changed since it was
settled is not looked up on the wiki again.

### Staying out of the way while you play

Settings > Window can minimize the Assistant to the system tray instead of the taskbar, minimize it automatically once
you close the last open item, and start it minimized. The capture hotkey brings it back.

## Things worth knowing

- **Merchant value**: the game shows no value for a `No Trade` item, so the Assistant keeps whatever the page says and
  tells you it could not check it.
- **`Attunable` and `No Trade`**: once equipped, an `Attunable` item shows `No Trade`, so the window cannot tell the
  two apart. When the page says `Attunable`, the Assistant keeps it and tells you.
- **Damage bonus** is removed from pages rather than written. The wiki calculates it for the reader's level, and the
  game shows it only for your character's level.
- **Two different items sharing one name** (`Shimmering Pearl` and `Shimmering Pearl*`) cannot be told apart, so the
  Assistant compares the item with whichever page has the plain name. Check what it proposes for such items with care.
- **Items only**, so far: not spells, NPCs or quests.

## Your data

Everything the Assistant keeps is in `%APPDATA%\EQLWikiEditorAssistant`: settings, the history of checked items, cached
wiki icons, and `errors.log`. **Settings > Saved captures** is off by default. When it is on, the Assistant keeps every
screenshot it takes in a `debug-captures` folder there, which helps when reporting a problem. These are full screenshots
of your game, so they can show your character's name, other players and chat.

**Updating**: when a newer version is out, the top of the window says so. Click *Update*: the Assistant downloads it,
closes, and opens again on the new version. Items you were still reviewing are closed, so finish or save them first.
Your settings, history and login are kept. Running a newer release's `Setup.exe` over the installed one works too.

**Uninstalling**: Windows Settings > Apps > Installed apps > EQL Wiki Editor Assistant. It asks whether to delete your
data as well: settings, history, cached icons, saved screenshots and your wiki login. The answer is No unless you
choose Yes, and if you don't answer within 30 seconds it keeps everything.

## Reporting a problem

Open an [issue](https://github.com/AustonZ/EQL-Wiki-Editor-Assistant/issues) and include the version (shown at the
bottom of Settings), what you did, and what happened. Naming the item helps most, since most problems are with a
particular item. If the app reported an unexpected error, the end of `errors.log` helps too. Please check a screenshot
for names and chat before attaching it.

## Building from source

Needs the .NET 10 SDK on Windows.

```powershell
dotnet build
dotnet test
dotnet run --project src/EQLWikiEditorAssistant.App
```

[CLAUDE.md](CLAUDE.md) is the detailed design record: why each rule exists and what it was measured against.

## License

The Assistant's own code is [MIT licensed](LICENSE). **Four things in this repository are not**:

- the game's item icons in `game_assets/item_icons`, which belong to the game's publisher. See
  [game_assets/README.md](game_assets/README.md);
- item pages copied from the wiki as test fixtures, which belong to the wiki's contributors;
- the game screenshots in `samples/` that the tests run against, which show the publisher's artwork;
- a word list taken from ENABLE, which its compilers released into the public domain.

Licensing is recorded machine-readably in [REUSE.toml](REUSE.toml). The components the Assistant is built on, such as
the .NET runtime and the Velopack installer, are listed with their licenses in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

EverQuest is a registered trademark of Daybreak Game Company LLC. This project is not affiliated with or endorsed by
Daybreak or the EverQuest Legends Wiki.
