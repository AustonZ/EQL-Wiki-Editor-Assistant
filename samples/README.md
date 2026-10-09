# samples/

Real EverQuest Legends screenshots, captured through the Assistant's own lossless capture path, that the locate
golden tests and the accuracy corpus run against.

**Every frame committed here was audited by eye first.** A frame shows the whole game screen, so it can show
character names, other players and chat. The folder stays gitignored so that a new capture can never be committed by
accident; an audited one is added deliberately.

To add a sample:

1. Capture it (see CLAUDE.md, "The sample set") and name it `NN-description.png`.
2. Look at the whole frame. Black out anything private with a solid box (a private chat channel's name is the
   example so far), editing the PNG losslessly, never re-encoding it as JPEG.
3. Add its ground truth (`AccuracySpike --bootstrap --only <name>`) and re-run `AccuracySpike`: the scores must not
   move, which proves the redaction touched nothing the tests read.
4. `git add -f samples/NN-description.png`.

These images show the game's artwork and are not covered by the project's MIT licence; see `REUSE.toml`.
