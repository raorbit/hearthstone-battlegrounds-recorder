# M1 spike verdicts

Measured on 2026-07-14 against the live game (Windows 11 Pro build 26200, RTX 3090, .NET SDK 10.0.301, Hearthstone running, ~900 MB Power.log containing 17 real solo Battlegrounds matches). Every number below was produced by running the spike, then independently reproduced by a second verification pass. Build everything with `dotnet build Spikes.slnx -c Release` (0 warnings, 0 errors).

## Spike A — log fidelity: **GO**

- Discovery: 6 session folders enumerated, correctly picked the only one holding a live `Power.log` (the other 5 hold only rotated `Power_old.log` — a wrinkle the plan didn't predict; handled).
- Parse accuracy: **17/17 matches** parsed with 0 anomalies — placement histogram {1st×4, 2nd×5, 3rd×2, 4th×1, 5th×3, 7th×1, 8th×1}, all places in 1–8, tavern turns 8–19, hero card ids resolved, one in-progress match correctly flagged truncated. Spot-checked by hand against raw ground truth (matches 3, 14, 17).
- Whole ~900 MB log streams in ~3.5 s; midnight rollover handled (session spans 2026-07-13 → 07-14).
- Live tailer: `FileShare.ReadWrite` polling works against the game-held file; parsed live turn/combat events during an actual match with zero exceptions.
- Fixtures: 17 sanitized, distilled fixtures committed (~830 KB total); full raw slices (~900 MB) stay local in gitignored `fixtures/raw/`. Privacy verified independently: zero BattleTag / account-id / user-path leaks in committed files.
- **Format discovery that corrects the plan:** `STEP value=MAIN_COMBAT` does **not** exist in current BG logs. Combat start = the **even `TURN` transition** on GameEntity, which coincides to the same log line-time with `BOARD_VISUAL_STATE=2`; the parser uses even-TURN as canonical and counts BOARD_VISUAL_STATE=2 as an independent cross-check (they agree on all 17 matches). `docs/technical-notes.md` updated.
- No pivot needed: the HearthWatcher-pattern port stays unused.

## Spike B — capture performance: **GO on feasibility; FPS kill line pending live play**

- ScreenRecorderLib 6.6.0 (MIT, verified in nuspec) on net10.0-windows/x64: the mixed-mode C++/CLI DLL loads and runs under .NET 10 — retires the net48 roll-forward risk.
- Live smoke captures of the Hearthstone window (WGC source): 15 s @ 1080p60 H264 CBR 12 Mbps → 20.9 MB, ffprobe-verified 60 fps / correct duration; NVENC engagement confirmed out-of-band via nvidia-smi encoder utilization (0% idle → ~13% during capture → 0%).
- **Fragmented MP4 confirmed** (`IsFragmentedMp4Enabled`): 16 `moof` boxes + `mvex` vs. zero in the plain file — the M2 crash-safety property is real in this library.
- Window resolution needed care: with Hearthstone Deck Tracker running, three windows match "Hearthstone"; a process-aware resolver binds to the game process's main window (salvage this into CaptureEngine).
- Build note: the package hard-errors on AnyCPU; the csproj pins x64 and `Spikes.slnx` maps the solution platform accordingly.
- **Open:** the >5 % FPS-loss kill line needs a real 30-min play session — PresentMon runbook in `SpikeB.CapturePerf/README.md`. Pivot ladder (ffmpeg child process → isolated libobs → 30 fps descope) stays armed until then.

## Spike C — MMR route + licensing gate: **attach PASS; HearthMirror NO-GO as-is**

- Read-only attach probe against the live game (PID re-resolved by name): 64-bit native process, 114 modules, Unity **Mono** scripting backend (`mono-2.0-bdwgc.dll` found at 0x7FF95B280000, 10.8 MB) — the documented `BaconRatingMgr` Mono-heap field path applies, and attach with `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ` succeeds without elevation. No memory reads performed — that part is gated.
- **Licensing gate (M1 exit requirement): see `SpikeC.MmrRoute/LICENSING.md`** for the full per-dependency table. Bottom line: **GO** to ship ScreenRecorderLib, NAudio, Microsoft.Data.Sqlite, Dapper, Serilog, Velopack, H.NotifyIcon.Wpf, WebView2 (app stays MIT; third-party notices at M6). **NO-GO** on HearthMirror (no public license, not on NuGet as of 2026-07-14). **CONDITIONAL** on libobs (isolated process only, never linked) and ffmpeg+libx264 (child process fine; distributing the GPL binary requires a source offer).
- Resolution paths for MMR, in order: (a) HearthSim permission — draft email ready in `SpikeC.MmrRoute/HEARTHSIM-EMAIL-DRAFT.md`, send to contact@hearthsim.net; (b) minimal clean-room reader for the single documented field path (attach feasibility now proven); (c) ship v1 without MMR. Recording never depends on any of these.

## Spike D — game-only audio: **GO**

- WASAPI process loopback (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, include-process-tree) captured **non-silent** Hearthstone-only audio (menu music) on build 26200: valid 44.1 kHz/16-bit/stereo PCM WAV, peak −30.9 dBFS, reproducible across runs. NAudio 2.2.1 (MIT) handles WAV plumbing; the process-loopback activation itself is hand-ported COM interop (`Interop.cs`) — no NuGet package exposes it.
- Windows 10 fallback proven: full system loopback (NAudio `WasapiLoopbackCapture`) produced a valid float WAV; the <20348 guard prints the honest fallback message.
- v1 default per the plan: game-only audio on Windows 11, automatic system-loopback fallback on Windows 10 with honest labeling. Mic mixing and MP4 muxing/AV-sync are M2 scope.

## Remaining fieldwork (needs real play sessions, tracked for M1 close-out)

1. **Spike B kill line:** one ~30-min BG session with PresentMon, baseline vs. recording (runbook in SpikeB README). This is the only item that can still force a capture pivot.
2. **Corpus gaps:** duos matches (`GT_BATTLEGROUNDS_DUO` — none occurred in the 17), one deliberate concede, and one mid-session client restart *while the tailer is running*.
3. **MMR (gated):** send the HearthSim email; rating read + post-game update latency measurement waits on the licensing verdict (M4 anyway).

None of these block starting M2: the walking skeleton builds on Spike A's parser, Spike B's capture route, and Spike D's audio route, all of which are GO.
