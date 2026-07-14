# Technical notes

Externally verified facts that constrain the implementation. Sources checked 2026-07-13.

## Match detection: Power.log

- Battlegrounds has no live API; match detection works by tailing the game's own event log, the same way Hearthstone Deck Tracker and Firestone do.
- One-time setup: write `log.config` to `%LocalAppData%\Blizzard\Hearthstone\log.config` enabling the `Power` logger. Takes effect on next game launch. ([HDT wiki](https://github.com/HearthSim/Hearthstone-Deck-Tracker/wiki/Setting-up-the-log.config))
- **Modern clients (patch ~25.0.4, Dec 2022, and later) write logs to a per-session timestamped subfolder**: `<install>\Logs\Hearthstone_YYYY_MM_DD_HH_MM_SS\Power.log` — not `<install>\Logs\Power.log`. The tailer must enumerate `Logs\` subfolders, pick the newest by creation time (HDT HearthWatcher's `GetActualLogDir()` pattern), and handle a new subfolder appearing when the game restarts mid-session.

### What Power.log can and cannot provide

Derivable from the log:

| Metadata | Source |
|---|---|
| Hero | player HERO entities |
| Placement | `PLAYER_LEADERBOARD_PLACE` tag changes |
| Turns | `TURN` tag — **displayed tavern turn = `(TURN + 1) / 2`** (raw tag counts both halves of each round; see HDT `GameV2.GetTurnNumber()`) |
| Combat boundaries | even `TURN` transitions on GameEntity (verified against 17 real matches, 2026-07-14: coincides to the same log line-time with `BOARD_VISUAL_STATE=2`, usable as a cross-check; `STEP value=MAIN_COMBAT` does **not** appear in current BG logs despite older documentation) |
| Solo vs duos | GameType (`GT_BATTLEGROUNDS_DUO = 37`) |
| Final board | board-state entities at match end (HDT `BattlegroundsBoardState` pattern) |

**Not in any log: Battlegrounds rating (MMR).** See below.

## Rating (MMR): process-memory reading

BG rating never appears in Power.log or any local file. Trackers read it from the running game's memory:

- [HearthMirror](https://github.com/HearthSim/Hearthstone-Deck-Tracker) (C# library maintained by HearthSim, ships with HDT) attaches read-only (`ReadProcessMemory`, no injection) and walks the Unity/Mono managed heap.
- Field path: `BaconRatingMgr.s_instance → m_lastRatingResponse → Rating / LeaderboardPlace`.
- HDT usage: `HearthMirror.Reflection.Client.GetBattlegroundRatingInfo()` — see [GameV2.cs](https://github.com/HearthSim/Hearthstone-Deck-Tracker/blob/master/Hearthstone%20Deck%20Tracker/Hearthstone/GameV2.cs). **Solo `Rating` and `DuosRating` are separate fields** — solo and duos are independent ladders and must never be mixed into one series.
- Per-match delta: sample rating at match start (HDT does an explicit `CacheBattlegroundsRatingInfo()`), read again after the post-game rating update, store the difference.
- Fragility: game patches occasionally break memory reading until HearthSim ships a fix (e.g. [HSTracker #1419](https://github.com/HearthSim/HSTracker/issues/1419), June 2026 patch). MMR must therefore be an **optional rating-provider subsystem** — when it breaks, the app degrades to "recordings without MMR" and recording itself is unaffected.
- Stack implication: HearthMirror is C#. A .NET app consumes it directly; any other stack needs a small C# sidecar process that polls rating and emits JSON (proven pattern: [OpenDeckTracker](https://github.com/ZJUxjy/OpenDeckTracker) built exactly this bridge for Electron).
- **Licensing (unresolved)**: HDT's repository is ["All Rights Reserved"](https://github.com/HearthSim/Hearthstone-Deck-Tracker#license), and HearthMirror is published neither as a NuGet package nor as a standalone licensed repo (checked 2026-07-14). This MIT project cannot assume it may vendor or redistribute HearthMirror. Options, in preference order: ask HearthSim for permission, implement a minimal clean reader for the single documented field path above, or ship without MMR (manual entry / none). By contrast, [python-hearthstone/hslog](https://github.com/HearthSim/python-hearthstone) is MIT and safe as a log-parsing reference.

## Video capture

- Single-source recording of the Hearthstone window; no scene compositing needed. Realistic routes: Windows.Graphics.Capture + hardware encoder (NVENC / AMF / QSV, x264 fallback), an ffmpeg child process, or embedding libobs.
- Per-combat "clips" should be **timestamped markers into one VOD per match**, not separate files — the prototype's player scrubber already renders combat markers, which implies this design.
- Game-audio-only capture (excluding Discord/system sounds) uses WASAPI **process loopback** (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`), which requires [**Windows build 20348 or later**](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-audioclient_activation_type) — consumer Windows 10 tops out at build 19045, so game-only audio is effectively **Windows 11**. On Windows 10 the fallback is full system-output loopback (standard WASAPI), with the audio option labeled honestly.
- Capture licensing: [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) is MIT. **libobs is GPL-2.0+** — embedding/linking it would force this project off MIT, so it can only ever be an isolated-process last resort. An ffmpeg build with libx264 enabled is a **GPL binary**: running it as a separate child process keeps the app MIT, but distributing that ffmpeg.exe alongside the app requires GPL compliance (source offer) for the binary itself.

## Policy posture

Log tailing + screen capture is the same read-only footprint deck trackers have used for a decade and sits within what Blizzard has tolerated (Tournament Player Handbook permits deck-tracking software). Memory reading is grayer on paper but is the established norm (HDT, Firestone). No automation of gameplay of any kind.
