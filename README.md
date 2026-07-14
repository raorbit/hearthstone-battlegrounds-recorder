# Hearthstone Battlegrounds Recorder

A Windows desktop app that automatically records Hearthstone Battlegrounds games and organizes the clips into a browsable library.

The recorder tails the Hearthstone game log to detect matches and combats, captures one video per match with combat markers on the timeline, and tracks metadata per game: hero, placement, rating, turns, and final board.

## Status

Design + planning stage. No app code yet.

- `design/` contains an interactive UI prototype of the Library and Settings screens — clone the repo and open [`design/BG Recorder - Library.dc.html`](<design/BG Recorder - Library.dc.html>) locally in a browser (GitHub shows only the source).
- [`docs/technical-notes.md`](docs/technical-notes.md) — verified facts constraining the implementation: Power.log lives in per-session timestamped subfolders; hero/placement/turns/combats/final board are log-derivable; **rating (MMR) is not in any log** and is read from game memory via HearthMirror (HDT's approach), so it's designed as an optional, degradable subsystem.
- [`docs/implementation-plan.md`](docs/implementation-plan.md) — stack decision (.NET 10 + WPF tray shell + WebView2 UI, ScreenRecorderLib capture), architecture, the resolved storage/retention policy, and six milestones starting with a de-risking spike sprint that includes a dependency-licensing gate.

## Planned features

- Automatic recording per match with combat markers on the timeline, no manual start/stop
- Library with filtering by hero, placement, rating, and date
- Rating (MMR) tracking per mode (solo/duos) via memory reading, degrading gracefully when game patches break it
- Storage management: max storage cap, output folder selection, archive drives, starred matches never auto-deleted
- Game log parsing for match metadata

## License

MIT — see [LICENSE](LICENSE).
