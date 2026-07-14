# Hearthstone Battlegrounds Recorder

A Windows desktop app that automatically records Hearthstone Battlegrounds games and organizes the clips into a browsable library.

The recorder tails the Hearthstone game log to detect lobbies and combats, captures video for each, and tracks metadata per game: hero, placement, rating, turns, and final board.

## Status

Early design stage. The `design/` folder contains an interactive UI prototype of the Library and Settings screens — open [`design/BG Recorder - Library.dc.html`](<design/BG Recorder - Library.dc.html>) in a browser to view it.

## Planned features

- Automatic recording per lobby and per combat, no manual start/stop
- Library with filtering by hero, placement, rating, and date
- Storage management: max storage cap, output folder selection, archive drives
- Game log parsing for match metadata

## License

MIT — see [LICENSE](LICENSE).
