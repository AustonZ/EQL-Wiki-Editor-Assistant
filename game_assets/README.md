# Game assets — not covered by this project's MIT licence

`item_icons/` holds the EverQuest Legends item icons, one 40x40 PNG per icon, named by its icon id, extracted from the
game client. **They belong to the game's publisher, Daybreak Game Company LLC, or its licensors. The MIT licence at the
root of this repository does not cover them, and this project grants no licence to them.**

The Assistant uses them for one thing: identifying an item's icon by comparing the in-game artwork against them, so it
can fill in a page's `lucy_img_ID` and upload the matching file to the EverQuest Legends Wiki. The publisher has long
allowed EverQuest item icons on fan wikis and item databases; that is their tolerance, not a licence, and it covers
nothing beyond that use. Anyone wanting them for anything else — another project, a dataset, training a model — needs
the publisher's permission. If the rights holder asks for them to be removed, they will be.

The same statement, in machine-readable form, is in [`REUSE.toml`](../REUSE.toml) and
[`LICENSES/LicenseRef-EverQuest-Item-Icons.txt`](../LICENSES/LicenseRef-EverQuest-Item-Icons.txt).
