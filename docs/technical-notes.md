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

BG rating never appears in Power.log or any local file. Trackers read it from the running game's memory,
and this project ships its own clean-room, read-only, external reader (`src/BgRecorder.Rating/`, default
OFF — runbook and live findings in [`mmr-offset-verification.md`](mmr-offset-verification.md)):

- Technique: attach with `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ` (no injection, no writes)
  and walk the Unity/Mono managed heap via `ReadProcessMemory` — the same footprint deck trackers use.
- **Field path (corrected by the 2026-07-16 live probe):** the long-documented
  `BaconRatingMgr.s_instance → m_lastRatingResponse → Rating/DuosRating` is GONE — no `BaconRatingMgr`
  class exists anywhere in the current build. The rating actually lives in `NetCacheBaconRatingInfo`
  (`Rating` at 0x10 solo, `DuosRating` at 0x14 duos), reached through `NetCache.m_netCache`, and neither
  `NetCache` nor `ServiceLocator` exposes a static instance — with no known static root, automatic MMR
  stays off pending a field-path redesign. The Mono ABI offsets themselves are live-verified.
- **Solo `Rating` and `DuosRating` are separate fields** — solo and duos are independent ladders and must
  never be mixed into one series.
- Per-match delta: sample rating at match start, read again after the post-game update, store the
  difference — a deliberate follow-up; the reader currently surfaces health plus the latest values only.
- Fragility: game patches occasionally break memory reading (e.g.
  [HSTracker #1419](https://github.com/HearthSim/HSTracker/issues/1419), June 2026 patch) — and this
  project hit the stronger form: the documented class vanished outright. MMR is therefore an **optional
  rating-provider subsystem** — a break only degrades `IRatingProvider.Health`; recording is unaffected.
- **Licensing (resolved)**: HDT's repository is ["All Rights Reserved"](https://github.com/HearthSim/Hearthstone-Deck-Tracker#license)
  and HearthMirror has no usable public license; the HearthSim permission route was declined
  (2026-07-16). The shipped reader is therefore clean-room: every struct layout derives from Mono's own
  open-source headers (Unity-Technologies/mono), with no tracker source consulted — see
  `spikes/SpikeC.MmrRoute/LICENSING.md` and the provenance section of `docs/mmr-offset-verification.md`.
  [python-hearthstone/hslog](https://github.com/HearthSim/python-hearthstone) remains the MIT-safe
  log-parsing reference.

## Video capture

- Single-source recording of the Hearthstone window; no scene compositing needed. Realistic routes: Windows.Graphics.Capture + hardware encoder (NVENC / AMF / QSV, x264 fallback), an ffmpeg child process, or embedding libobs.
- Per-combat "clips" should be **timestamped markers into one VOD per match**, not separate files — the prototype's player scrubber already renders combat markers, which implies this design.
- Game-audio-only capture (excluding Discord/system sounds) uses WASAPI **process loopback** (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`), which requires [**Windows build 20348 or later**](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-audioclient_activation_type) — consumer Windows 10 tops out at build 19045, so game-only audio is effectively **Windows 11**. On Windows 10 the fallback is full system-output loopback (standard WASAPI), with the audio option labeled honestly.
- Capture licensing: [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) is MIT. **libobs is GPL-2.0+** — embedding/linking it would force this project off MIT, so it can only ever be an isolated-process last resort. An ffmpeg build with libx264 enabled is a **GPL binary**: running it as a separate child process keeps the app MIT, but distributing that ffmpeg.exe alongside the app requires GPL compliance (source offer) for the binary itself.

## Policy posture

Log tailing + screen capture is the same read-only footprint deck trackers have used for a decade and sits within what Blizzard has tolerated (Tournament Player Handbook permits deck-tracking software). Memory reading is grayer on paper but is the established norm (HDT, Firestone). No automation of gameplay of any kind.
