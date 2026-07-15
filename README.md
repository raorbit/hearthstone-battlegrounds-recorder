# Hearthstone Battlegrounds Recorder

A Windows desktop app that automatically records Hearthstone Battlegrounds games and organizes the clips into a browsable library.

The recorder tails the Hearthstone game log to detect matches and combats, captures one video per match with combat markers on the timeline, and tracks metadata per game: hero, placement, rating, turns, and final board.

## Status

M2 implementation baseline and the first M3 library/player vertical slice are built; M1 spike verdicts are in [`spikes/DECISIONS.md`](spikes/DECISIONS.md). Neither milestone's field exit is signed off yet.

The desktop path records unattended end-to-end: log watcher → BG parser (fixture-tested against 17 real matches) → session state machine → window capture (NVENC, fragmented MP4) + game-only process-loopback audio → Media Foundation mux at finalize → SQLite library row with combat markers, behind a WPF tray shell. Crash safety is test-proven: a hard-killed recording is recovered on next startup as a playable partial VOD registered as incomplete, including when the kill corrupts the staged audio. Disk checks now guard every arm/capture path, a persistent `StorageBlocked` state explains why recording is disarmed, and a minimized-only Hearthstone target is rejected instead of risking a black recording.

The tray now opens a bundled TypeScript/Preact library in WebView2 with all/solo/duos/starred buckets, search/placement/date filters, starring, recorder controls, match detail, marker seeking, and real local VOD playback through opaque match URLs with HTTP byte-range responses. Recovered rows remain visible and are labeled explicitly.

Still needed to close M2: wire the onboarding live test-feed verifier, run 3 consecutive hands-free real matches including a client restart, check A/V sync within ±100 ms over a long session, and complete Spike B's 30-minute PresentMon measurement. M3 still needs real multi-GB seek/scrub validation, marker timing checks across real matches, thumbnails, final-board/damage presentation, keyboard shortcuts, and virtual clips. v1 ships without automatic MMR (M1 licensing decision).

- `design/` contains an interactive UI prototype of the Library and Settings screens — clone the repo and open [`design/BG Recorder - Library.dc.html`](<design/BG Recorder - Library.dc.html>) locally in a browser (GitHub shows only the source).
- [`docs/technical-notes.md`](docs/technical-notes.md) — verified facts constraining the implementation: Power.log lives in per-session timestamped subfolders; hero/placement/turns/combats/final board are log-derivable; **rating (MMR) is not in any log** and is read from game memory via HearthMirror (HDT's approach), so it's designed as an optional, degradable subsystem.
- [`docs/implementation-plan.md`](docs/implementation-plan.md) — stack decision (.NET 10 + WPF tray shell + WebView2 UI, ScreenRecorderLib capture), architecture, the resolved storage/retention policy, and six milestones starting with a de-risking spike sprint that includes a dependency-licensing gate.

## Planned features

- Automatic recording per match with combat markers on the timeline, no manual start/stop
- Library with filtering by hero, placement, rating, and date
- Rating (MMR) tracking per mode (solo/duos): manual entry in v1; automatic memory-read tracking is post-v1, pending licensing (see `spikes/SpikeC.MmrRoute/LICENSING.md`)
- Storage management: max storage cap, output folder selection, archive drives, starred matches never auto-deleted
- Game log parsing for match metadata

## License

MIT — see [LICENSE](LICENSE).
