# Spike B — capture performance

Plan reference: `docs/implementation-plan.md` M1 bullet **B**. Question: can
[ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) (MIT) record the
Hearthstone window at 1080p60 with a hardware H.264 encoder (NVENC), producing crash-safe
fragmented MP4, on this .NET 10 stack — and how much game FPS does it cost?

The FPS-cost half is explicitly a **live play-session** measurement (runbook at the bottom).
Everything else was measured against the running game (at the menu) on 2026-07-14.

## What it does

A single console app that:

1. Enumerates ScreenRecorderLib's recordable windows and picks the **game process's** main
   window. Naive substring matching is not enough here: with Hearthstone Deck Tracker running,
   three windows contain "Hearthstone" (`Hearthstone`, `Hearthstone Deck Tracker`,
   `HearthstoneOverlay`). `WindowResolver` scores candidates and prefers the window that is the
   main window of the process named `Hearthstone` (see the scored candidate log at runtime).
   If nothing matches, it lists every titled window and exits non-zero.
2. Configures an H.264 hardware encode (`IsHardwareEncodingEnabled = true` — Media Foundation
   auto-selects NVENC/AMF/QSV; NVENC on this RTX 3090), CBR at the requested bitrate/fps,
   **audio disabled** (audio is Spike D's job), optional fragmented MP4.
3. Wires `OnStatusChanged` / `OnRecordingComplete` / `OnRecordingFailed` to stdout.
4. Records for `--seconds`, calls `Stop()`, waits for `OnRecordingComplete`, then verifies the
   file exists and is > 1 MB and probes it with `ffprobe` if present. Exits non-zero on any failure.

The `RecordingConfig` record, `WindowResolver`, and `CaptureSession` class are written to be
lifted into the real **CaptureEngine** (`IRecorder`) per the plan.

## How to run

ScreenRecorderLib ships **native C++/CLI (mixed-mode) DLLs per architecture** and injects the
reference via a `.targets` file keyed on `$(Platform)`. `$(Platform)` must be `x64` (the SDK
default `AnyCPU` makes the package emit a hard build error), so the csproj pins `x64` and you
must pass `-p:Platform=x64` on the CLI too:

```powershell
# from spikes/SpikeB.CapturePerf
dotnet build -c Release -p:Platform=x64
dotnet run   -c Release -p:Platform=x64 -- --window Hearthstone --seconds 30 --fps 60 --bitrateMbps 12 --out C:\path\to\out.mp4
```

Or run the built exe directly:
`bin\x64\Release\net10.0-windows\win-x64\SpikeB.CapturePerf.exe --window Hearthstone --seconds 15`

### Arguments

| Arg | Default | Meaning |
|---|---|---|
| `--window <substr>` | `Hearthstone` | window-title substring (case-insensitive) |
| `--seconds <n>` | `30` | capture duration |
| `--fps <n>` | `60` | target framerate |
| `--bitrateMbps <n>` | `12` | H.264 CBR bitrate in Mbps |
| `--out <path>` | `./spikeB-capture.mp4` | output path (or set `SPIKEB_OUT_DIR` for the default dir) |
| `--width` / `--height` | `1920` / `1080` | forced output size (scaled, `Uniform` letterbox) |
| `--native` | off | record the window's native size instead of forcing 1080p |
| `--fragmented` | off | enable fragmented MP4 (crash-safe blocks; M2 requirement) |

Exit codes: `0` success · `2` bad args · `3` window not found · `4` finalize timeout ·
`5` `OnRecordingFailed` · `6` file missing · `7` file ≤ 1 MB.

## What was measured on this machine (2026-07-14)

Hardware: RTX 3090 (NVENC, driver 591.86), Windows 11 Pro build 26200, .NET SDK 10.0.301.
Package: **ScreenRecorderLib 6.6.0 (MIT)**. Target: **net10.0-windows, x64**.

- **net10 + C++/CLI load: OK.** The mixed-mode native DLL referenced at build time and loaded/ran
  under the .NET 10 runtime with no load failure. (Runtimes 8/9/10 are all installed; whichever
  the DLL was built against resolves.) This retires the "net48 TFM / mixed-mode roll-forward" risk
  for the capture library specifically.
- **Capture API:** `WindowsGraphicsCapture` (WGC) for the window source.
- **Encoder engaged:** requested H.264 CBR profile High, `IsHardwareEncodingEnabled = true`.
  ScreenRecorderLib does **not** expose which concrete encoder Media Foundation selected, so NVENC
  was confirmed out-of-band: `nvidia-smi` encoder utilization went **0 % idle → steady ~12–13 %**
  during the 1080p60 capture. That is a live NVENC session — hardware encoding is engaged.
- **15 s plain capture** (`spikeB-capture.mp4`): exit 0, **21,896,630 bytes (20.88 MB)**,
  ffprobe `h264 1920x1080 avg_frame_rate=60/1`, **871 frames**, **duration 14.517 s**
  (~0.5 s startup latency before the first frame). Effective ~12.1 Mbps — matches the 12 Mbps CBR target.
- **5 s fragmented capture** (`spikeB-fragmented.mp4`, `--fragmented`): exit 0,
  **7,019,033 bytes (6.69 MB)**, ffprobe `h264 1920x1080 60/1`, **272 counted frames**,
  **duration 4.533 s**. It probes and plays.
- **Fragmented MP4 support: YES, and verified.** `VideoEncoderOptions.IsFragmentedMp4Enabled`
  exists in 6.6.0. The fragmented file contains **16 `moof` boxes + `mvex`** (true fragmented
  MP4 / "individually playable blocks"); the plain file has **zero** `moof`/`mvex` (single `moov`).
  This is the property M2 needs so a crash mid-match leaves a playable file. Note `IsMp4FastStartEnabled`
  also exists but is left **off** (fast-start rewrites `moov` at finalize, the opposite of crash-safe).

### Still requires a live play session (NOT measured today)

- **The >5 % FPS-loss kill line.** The game was at the menu; a real Battlegrounds match under load
  is required. Runbook below.
- **WGC visual defects** (black frames, exclusive-fullscreen behaviour, dropped frames) over a full match.
- A/V sync, minimized/occluded-window behaviour — out of scope for Spike B.

## PresentMon runbook — the real FPS measurement (do this during actual play)

Install PresentMon (do **not** let this spike install it):

```powershell
winget install --id Intel.PresentMon
```

Intel.PresentMon ships PresentMon **2.x**, whose console tool uses `--` long flags and must run
**elevated** (Run as administrator). (PresentMon 1.x used single-dash flags, e.g. `-process_name`.)

**Protocol (from the plan, ~30 min each, identical conditions):**

1. Fix conditions: same graphics settings, same resolution, **borderless windowed** (WGC-friendly),
   and — critically — **uncap FPS** in Hearthstone's options if possible. Hearthstone is normally
   FPS-capped and the 3090 has huge headroom, so a capped game can read **0 % impact** while still
   dropping frames; uncapping (or measuring 1%-low frametimes) is what exposes real cost.
2. **Baseline** — no recorder running. Play a full BG lobby+match (~30 min):

   ```powershell
   PresentMon.exe --process_name Hearthstone.exe --output_file baseline.csv --timed 1800 --terminate_after_timed
   ```

3. **Recording** — start SpikeB (or the real CaptureEngine) capturing the window, then repeat an
   equivalent ~30 min of play:

   ```powershell
   PresentMon.exe --process_name Hearthstone.exe --output_file recording.csv --timed 1800 --terminate_after_timed
   ```

4. **Compute** from each CSV (column `MsBetweenPresents` in 2.x / `msBetweenPresents` in 1.x):
   - Average FPS = `1000 / mean(MsBetweenPresents)`
   - 1%-low FPS = `1000 / (99th-percentile MsBetweenPresents)`
   - FPS loss % = `(baseline_avg − recording_avg) / baseline_avg × 100`
   - Also eyeball `Dropped` and the presence of any WGC corruption in the VOD.

5. **Kill line / verdict (plan bullet B):**
   - **> 5 % average FPS loss**, a material 1%-low regression, **or** WGC visual defects → **PIVOT**
     down the ladder: ffmpeg child process (`ddagrab` + `h264_nvenc`, GPL binary as separate process),
     then isolated-process libobs, then descope to 30 fps.
   - Otherwise → **GO**: ScreenRecorderLib + WGC + NVENC is the CaptureEngine route.

Record the numbers and the GO/PIVOT verdict in `spikes/DECISIONS.md`.

## ScreenRecorderLib 6.6.0 API notes (for salvage into CaptureEngine)

- `Recorder.GetWindows()` → `List<RecordableWindow>` (`.Title`, `.Handle`, `.IsValidWindow()`,
  `.IsMinmimized()` [sic — the library misspells it], `.RecorderApi`).
- `Recorder.CreateRecorder(RecorderOptions)`; instance `Record(path)` / `Stop()` / `Pause()` /
  `Resume()`; events `OnStatusChanged` (`RecorderStatus` Idle/Recording/Paused/Finishing),
  `OnRecordingComplete` (`.FilePath`), `OnRecordingFailed` (`.Error`).
- Options tree: `RecorderOptions { SourceOptions.RecordingSources, OutputOptions
  (RecorderMode/OutputFrameSize/Stretch), AudioOptions.IsAudioEnabled,
  VideoEncoderOptions (Encoder=H264VideoEncoder{BitrateMode,EncoderProfile}, Bitrate [bits/sec],
  Framerate, IsFixedFramerate, IsHardwareEncodingEnabled, IsFragmentedMp4Enabled,
  IsMp4FastStartEnabled, ...) }`.
- Window source: `new WindowRecordingSource(hwnd)`.
