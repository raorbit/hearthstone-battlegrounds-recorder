# Implementation plan

Plan for building the recorder described by the [design prototype](../design/) within the constraints in [technical-notes.md](technical-notes.md). Solo-dev paced: after M2 every milestone boundary leaves a shippable app.

## Stack

**C# / .NET 10 (LTS, supported through Nov 2028), single Windows process.** (.NET 8 was rejected: its support ends 2026-11-10, months after this project would ship.)

| Concern | Choice |
|---|---|
| Shell | WPF + tray icon (H.NotifyIcon.Wpf) |
| UI | WebView2 hosting a small TypeScript + Preact + Vite SPA ported from the prototype (markup/CSS transfer nearly 1:1); VOD playback via HTML5 `<video>` |
| Capture | ScreenRecorderLib (MIT; Windows.Graphics.Capture + Media Foundation hardware encoders NVENC/AMF/QSV, x264 fallback), behind an `IRecorder` interface; named pivot: ffmpeg child process (`ddagrab` + `h264_nvenc`; GPL binary distributed with source offer, app stays MIT); last resort: libobs in an isolated process (GPL — never linked in-proc) |
| Audio | Game-only WASAPI process loopback on **Windows build 20348+ (effectively Windows 11)**; on Windows 10, automatic fallback to full system loopback with the option labeled honestly; NAudio for mic/device handling |
| Log watching | Newest-subfolder discovery + tailer; clean implementation using MIT [hslog](https://github.com/HearthSim/python-hearthstone) as the behavioral reference (HDT/HearthWatcher code is All-Rights-Reserved — pattern only, no code reuse) |
| MMR | Optional `IRatingProvider`. **Decided (M1 exit, 2026-07-14): v1 ships without MMR** — HearthMirror is licensing NO-GO (see `spikes/SpikeC.MmrRoute/LICENSING.md`); a clean-room reader for the documented field path is the planned post-v1 route (attach feasibility proven by the Spike C probe). v1 ships a null provider behind the same interface |
| Metadata | SQLite (Microsoft.Data.Sqlite + Dapper) |
| Packaging | Velopack — Setup.exe + delta auto-updates from GitHub Releases |
| Logging | Serilog rolling files |

**Supported Windows matrix:** Windows 11 = full experience (game-only audio). Windows 10 22H2 = supported with system-loopback audio only. Nothing older.

Why .NET: the memory-reading route to MMR is C#-native and the reference ecosystem (HDT, hslog's C# siblings, HearthDb) is C#, so parsing-fidelity questions are answered by reading proven code — but note the licensing boundary: HDT source is **read for understanding, never copied** (All Rights Reserved). Why WebView2 for UI: the finished HTML prototype ports almost directly, and HTML5 video is a far better player substrate than WPF MediaElement (frame-accurate seeking, easy marker overlays). The prototype's dc-runtime and unpkg React are design-tool artifacts and are not shipped.

**Dependency licensing gate (M1 exit requirement).** This is an MIT project; every shipped dependency needs a compatible license and known binary provenance before it is treated as available. Current state: ScreenRecorderLib MIT ✅; NAudio MIT ✅; hslog MIT ✅ (reference only, Python); SQLite/Dapper/Serilog/Velopack ✅; **HearthMirror ❌** — HDT is All-Rights-Reserved and no separately-licensed package exists, so it is *not* usable without HearthSim's permission; **libobs ⚠️** GPL-2.0+ (isolated process only, never linked); **ffmpeg+libx264 ⚠️** GPL binary (child process OK; distributing it requires GPL compliance for the binary). M1 must record a GO/NO-GO per dependency in `spikes/DECISIONS.md`.

## Architecture

```
Power.log (game)              Hearthstone process memory
     |                                  |
 LogWatcher ──lines──► LogParser        |
     |                    |       RatingProvider (memory read, OPTIONAL)
     └──────────► GameEvents ◄──────────┘
                      |
              SessionCoordinator  (recording state machine)
                |            |
        CaptureEngine    MatchAssembler ──► SQLite (matches, markers, files)
        (IRecorder)          |                      |
                |            └──► StorageEngine ────┤  (archive/eviction pass)
             .mp4 files                             |
                                              UiBridge (WebView2 postMessage RPC)
                                                    |
                                              Library SPA (ported prototype)
```

- **LogWatcher** — finds the newest `<install>\Logs\Hearthstone_YYYY_MM_DD_HH_MM_SS\` subfolder, re-checks on game restart, tails Power.log with a polling `FileShare.ReadWrite` reader.
- **LogParser** — minimal BG-only tag set: CREATE_GAME, GameType (`GT_BATTLEGROUNDS_DUO=37`), hero entities, TURN (**displayed tavern turn = `(TURN+1)/2`**; combat boundaries = even TURN transitions, cross-checked by `BOARD_VISUAL_STATE=2` — Spike A verified `STEP=MAIN_COMBAT` no longer exists in BG logs), per-combat result + damage, `PLAYER_LEADERBOARD_PLACE`, final board. Fully fixture-testable.
- **SessionCoordinator** — state machine `Idle → Armed → Recording → Finalizing → Armed`, driving all three status states the prototype shows (recording / waiting-for-match / game-not-found). Two distinct user controls: **Stop this recording** (finalize early, stay armed for the next match) and **Pause auto-recording** (disarm; distinct tray glyph; resume affordance offers *resume now / auto-resume next match / stay off this session*).
- **CaptureEngine** — window capture + hardware encode behind `IRecorder`; fragmented MP4 so a crash mid-match leaves a playable file; marker clock = log-event wall-clock minus recording-start wall-clock. Startup crash-recovery detects orphaned fragmented MP4s and commits them as `video_status='incomplete'` rows.
- **RatingProvider** — optional/degradable; samples at match start, polls post-game until the rating changes (timeout tuned by Spike C); solo `Rating` and `DuosRating` stored strictly per mode; health states (OK / AttachFailed / PatchBroken) + feature-flag kill switch. Failure degrades to null MMR; recording never depends on it.
- **StorageEngine** — retention spec below; journaled mover with a `mover_journal` table (copy → fsync → size + xxHash verify → DB flip → delete source; startup reconciliation: source wins if verify incomplete, target wins if complete).
- **UiBridge** — JSON-RPC over `postMessage`; media served via virtual host mapping with an HTTP 206 range handler for multi-GB seeking.

## Retention policy (resolves the prototype's contradiction)

1. **Tiers.** One recording drive (cap **R**, the prototype's Max storage slider) and zero or more priority-ordered archive drives, each with its own cap. New recordings always land on the recording drive.
2. **Pinned hot set.** The newest **K** matches (default 5) always stay on the recording drive — "newest stay on the fast drive" is an invariant, not an accident of ordering.
3. **Over R → archive first.** Move oldest-finished-first (starred included — moves are lossless) to the highest-priority archive drive with headroom under both its cap and real free space.
4. **No archive headroom → delete oldest-unstarred-first.** This fires directly off R when no archive can take the move — so the single-drive default behaves exactly as the prototype copy promises ("oldest unstarred removed first when the cap is hit"). An optional total-library cap T (default = R + archive caps) additionally bounds the whole library.
5. **Starred is inviolable.** If space is needed and everything left is starred: delete nothing, raise a persistent notification, and refuse to arm for the *next* match (the current one always finishes) while below the floor.
6. **Free space is authoritative.** Effective budget per drive = min(cap, used + free − reserve). **Every managed volume keeps a configurable reserve floor** — recording drive: max(10 GB, 2× rolling-average match size); archive drives: default 5 GB each — the mover never fills any destination to zero free space. A low-space watchdog during recording finalizes early rather than corrupting. Caps are user intent; the disk is the truth.
7. **Offline archives** (unplugged drive) mark rows offline — playback disabled, metadata kept, nothing deleted.

## Milestones

### M1 — Spike sprint: kill the unknowns (time-boxed: ~1 weekend per spike)

Four independent `dotnet run` console apps under `/spikes`, each with a written GO/PIVOT verdict in `spikes/DECISIONS.md`. Spike code is written to be salvaged (A → LogParser, B's config → CaptureEngine), not discarded.

- **A — log fidelity**: subfolder discovery + tailer; parse ≥10 real matches (solo and duos, one concede, one mid-session client restart); build the permanent fixture corpus. **Privacy boundary:** raw logs are sanitized before entering Git — BattleTags, account IDs, and machine paths redacted by a scripted pass; unsanitized logs never leave the local `fixtures/raw/` directory, which is gitignored along with databases and dumps. *Pivot:* any missed boundary or wrong placement → port the HearthWatcher discovery *pattern* (no code reuse — All Rights Reserved) with MIT hslog as the parsing reference.
- **B — capture perf**: ScreenRecorderLib at 1080p60 NVENC for a 30-min session; measure game FPS impact with PresentMon. *Kill line:* >5 % FPS loss or WGC defects → ffmpeg child-process pivot; then isolated-process libobs; then descope to 30 fps.
- **C — MMR route + licensing**: resolve the licensing gate first — contact HearthSim about HearthMirror terms. If permitted: verify the assembly loads in-proc under .NET 10 (net48 TFM risk), read rating before/after 3 real matches, confirm solo/duos separation, measure post-game update latency. If not permitted: assess a minimal clean reader for the documented `BaconRatingMgr` field path, or record the decision to ship v1 without MMR. Can't kill the project — the provider is optional by architecture.
- **D — game-only audio** (requires build 20348+, i.e. Windows 11): process-loopback capture of Hearthstone-only audio mixed with mic. *Verdict rule:* GO → v1 default on Windows 11 with automatic system-loopback fallback on Windows 10; NO-GO → system loopback everywhere with the desktop-audio toggle relabeled honestly.

**Exit:** all four spikes run against the live game; DECISIONS.md holds measured numbers (FPS %, parse accuracy /10, MMR latency), a GO or named-pivot verdict each, **and a licensing GO/NO-GO per shipped dependency**.

### M2 — Walking skeleton: safe unattended recording

Solution structure (`Core` / `App` / `Tests`); productionize the parser with fixture-driven tests; recording state machine incl. stop-vs-pause semantics; CaptureEngine on the chosen route; SQLite schema v1; onboarding writes log.config (**merge, never clobber** an existing file) + the prototype's live test-feed verifier; tray icon with state.

Because "post-M2 boundaries are shippable" and this app records unattended, M2 also owns the safety floor:

- **Audio productionized, not just spiked**: the Spike D route muxed into recordings with acceptance criteria — A/V sync within ±100 ms over a 30-min session, audio-device loss mid-recording handled without killing the video, mic mixing per settings, and the Windows 10 system-loopback fallback path exercised.
- **Disk safety from day one** (pulled forward from M5): free-space floor check before arming; low-space watchdog during recording that finalizes early rather than corrupting; staging-dir writes so partial files never enter the library.
- **Crash recovery proven, not assumed**: startup pass detects orphaned fragmented MP4s and commits them as `video_status='incomplete'` rows — tested by force-killing the process mid-recording.

**Exit:** 3 consecutive real matches, including one game-client restart in between, produce 3 playable MP4s **with correct synced audio** and 3 correct DB rows (hero, placement, turns, combat markers) with zero manual interaction; a `kill -9` during a 4th recording leaves a playable partial VOD registered as incomplete on next launch; recording onto a nearly-full volume finalizes early instead of corrupting.

### M3 — Library UI and VOD player

Port the prototype to the SPA; RPC bridge; buckets/search/segment filters; design + build the date filter the prototype left unmodeled; player with combat/damage markers and turn ticks, final-board strip, keyboard shortcuts; clips as **virtual marker ranges** (no file copies); thumbnails on finalize. Validate multi-GB `<video>` range-seeking in week one (the milestone's risk item).

**Exit:** every recorded match browsable; smooth scrubbing on multi-GB VODs; markers within ±2 s of true combat start across 5 real matches; filters return correct subsets.

### M4 — Rating degradation UX and per-mode stats (shrunk by the M1 decision)

Per the M1 licensing verdict and the 2026-07-14 decision, **v1 ships without MMR**: this milestone is now the degradation UX + manual-entry stub only. `NullRatingProvider` behind `IRatingProvider`; null MMR renders as "—" with a non-blocking "MMR unavailable — recordings unaffected" note; optional manual rating entry per match; sidebar rating card strictly per-mode, **following the active solo/duos bucket**, driven by manual entries when present. The memory-reading implementation (clean-room reader, or HearthMirror if HearthSim grants permission) moves to the post-v1 roadmap; the provider interface, health states, and kill switch stay so it slots in without rework.

**Exit:** with the null provider, a full match records normally and the UI shows the degraded state everywhere MMR would appear; manual entries round-trip through the DB and render in the per-mode card.

### M5 — Storage, retention, and archive engine

Implement the spec above; journaled transactional mover that never contends with an active recording's disk bandwidth; startup reconciliation; "what would be evicted next" preview in settings; scripted **VHDX torture suite**: fill past cap → archive; archives full → delete oldest unstarred; all-starred → notify + zero deletions; yank drive mid-move; `kill -9` mid-move; **archive-drive reserve respected** — moves stop before any destination drops below its reserve floor, never filling a volume to zero.

**Exit:** the torture suite passes — DB and filesystem consistent in every scenario, zero starred files deleted, zero orphans, correct offline flagging.

### M6 — Settings, polish, packaging, updates

Full settings surface (resolution/fps/encoder/quality; per-source audio with mic + desktop default OFF; output folder; notifications); launch-at-login; toasts; crash handler + logs; Velopack Setup.exe with delta auto-updates; **idle resource budget**: Armed-state CPU < ~0.5 %, WebView2 torn down while the window is hidden.

**Exit:** on a clean VM: install → onboard → record a match hands-free → auto-update vN→vN+1 with settings and library intact — and the idle budget holds.

## Top risks

1. **Capture performance/compatibility** (the project-killer) — hard numeric kill line in Spike B, pivot ladder behind `IRecorder`; detect minimized/exclusive-fullscreen and warn instead of recording black frames.
2. **Log format changes on game patches** — permanent (sanitized) fixture corpus + regression tests; tolerant minimal tag set; porting the HearthWatcher discovery pattern (hslog as MIT reference) as the pre-agreed pivot; watchdog banner when the game is running with fresh log writes but no events.
3. **MMR route legal/technical fragility** — HearthMirror has no usable public license (HDT is All Rights Reserved) and memory reading breaks on some game patches (precedent: HSTracker #1419). Mitigation: licensing GO/NO-GO in M1 before any code depends on it; optional-by-architecture provider with health states, kill switch, pinned versions; M4's exit proves recording is indifferent to its absence.
4. **Retention bugs = user data loss** — journaled mover, starred inviolable, VHDX torture suite as a hard exit gate.
5. **Multi-GB seeking in WebView2** — validated week one of M3; HTTP 206 handler is the committed default.
6. **Disk-full mid-recording** — floor check before arming, watchdog finalizes early, staging-dir writes.
7. **Solo-dev scope creep** — the punt list below is contractual for v1; every post-M2 milestone boundary is shippable.

## Out of scope for v1

No cloud/sync/accounts (local-only is a feature). No clip export to standalone files (virtual ranges only; export is v1.1). No auto-highlight detection, non-BG modes, board replay/simulation, in-app trimming, localization, macOS/Linux, HDR or >60 fps. No code-signing cert yet (SmartScreen caveat documented). Duos beyond the mode split (partner display, shared-board markers) is post-v1. **No automatic MMR in v1** (decided at M1 exit): the clean-room memory reader is the post-v1 route, upgraded to HearthMirror if HearthSim grants permission; v1 offers manual rating entry only.
