# Spike D — game-only audio via WASAPI process loopback

Milestone M1, plan bullet **D**. Verdict target: can we capture audio from **only**
the Hearthstone process (not Discord/system sounds) so recordings carry clean game
audio? Answer on this machine: **yes — GO.**

## What it does

1. **Process loopback (primary path).** Activates a WASAPI capture client bound to a
   single process via `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` /
   `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`, targeting the Hearthstone PID,
   captures N seconds, writes a WAV, and reports peak + RMS levels.
2. **System loopback (fallback path).** Records 3 s of the full default render device
   with NAudio's `WasapiLoopbackCapture` — the exact Windows-10 fallback the app uses
   when process loopback is unavailable — and reports its levels too.

The point of the spike is proving the process-loopback path exists and captures a
non-silent stream; the system-loopback clip is the honest fallback proof.

## How to run

```powershell
cd spikes/SpikeD.ProcessAudio
dotnet run -c Release -- --process Hearthstone --seconds 10 --out C:\path\spikeD-hearthstone.wav
```

Arguments (all optional):

| Arg | Default | Meaning |
|---|---|---|
| `--process <name>` | `Hearthstone` | target process (`.exe` suffix stripped); newest instance by start time wins |
| `--seconds <n>` | `10` | length of the process-loopback capture |
| `--out <wav>` | `%TEMP%\spikeD-hearthstone.wav` | output WAV path |
| `--include-tree <bool>` | `true` | include the target's child process tree (`--no-include-tree` to exclude) |

The system-loopback clip is always written next to `--out` as `spikeD-system.wav`.
Exit code: `0` = process-loopback WAV produced (silence still counts — pipeline OK),
`1` = skipped because the OS is below build 20348, `2` = process loopback hard-failed.

## The build-20348 constraint

`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` requires **Windows build 20348 or later**
(effectively Windows 11; consumer Windows 10 tops out at 19045). The app checks
`Environment.OSVersion.Version.Build` at startup:

- **>= 20348:** run the process-loopback capture (game-only audio).
- **< 20348:** print the honest fallback message and skip straight to full system-output
  loopback, which captures *all* desktop sound, not just the game. The audio option in
  the shipping app is relabelled accordingly on those machines.

This machine is build **26200**, so the primary path runs.

## Fallback semantics

Two capture paths, one honest downgrade:

| OS | Path | What it captures |
|---|---|---|
| Windows 11 (20348+) | process loopback | **only** Hearthstone's audio streams |
| Windows 10 (< 20348) | system loopback | the entire default render device mix (game + Discord + notifications + everything) |

Both paths produce a standard WAV; only the *scope* of what's captured differs. The
system-loopback path is exercised here on every run regardless of OS so the fallback
code stays alive and measured.

## How it works (interop)

NAudio 2.2.1 (MIT) does **not** expose process loopback, so the one missing API is
hand-ported:

- `Interop.cs` declares the minimal COM surface: `ActivateAudioInterfaceAsync`
  (`Mmdevapi.dll`), `IActivateAudioInterfaceCompletionHandler`,
  `IActivateAudioInterfaceAsyncOperation`, `IAudioClient`, `IAudioCaptureClient`, plus
  the `AUDIOCLIENT_ACTIVATION_PARAMS` / `PROPVARIANT(VT_BLOB)` / `WAVEFORMATEX` structs.
- The activation is asynchronous: we pass a managed completion handler (the CLR builds a
  COM-callable wrapper) and block on a `ManualResetEventSlim` until `ActivateCompleted`
  fires on an MTA worker thread, then pull the `IAudioClient` out of the async operation.
- The client is initialized `AUDCLNT_SHAREMODE_SHARED` with
  `AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK`, a fixed 44.1 kHz /
  16-bit / stereo PCM format (the process-loopback virtual device resamples the game's
  native mix to this), and a 20 ms buffer. Capture drains packets on each event via
  `IAudioCaptureClient` and writes them with NAudio's `WaveFileWriter`.
- Level metering (peak + RMS in dBFS) runs inline over the captured samples.

Behaviour pattern is from Microsoft's public **ApplicationLoopback** C++ sample and the
`audioclientactivationparams` headers — those are OS APIs; the C# here is original. No
HDT / HearthMirror code is referenced (this path doesn't touch either).

**Licensing:** the only NuGet dependency is **NAudio 2.2.1 — MIT** (used for WAV writing
and the `WasapiLoopbackCapture` fallback). Compatible with this MIT project. GO.

## Measured on this machine (build 26200, RTX 3090, live game at menu)

| Path | Format | Duration | Peak | RMS | Silent? |
|---|---|---|---|---|---|
| Process loopback (game only) | 44100 Hz / 16-bit / 2 ch (PCM) | 9.99 s | -30.9 dBFS | -53.0 dBFS | no |
| System loopback (full desktop) | 48000 Hz / 32-bit float / 2 ch | 2.98 s | -65.7 dBFS | -83.4 dBFS | no |

A second run (5 s) reproduced the activation and produced an exact-duration WAV, so the
async COM activation is reliable, not a one-off. Both WAV headers validate as RIFF/WAVE
with the expected format tags (PCM=1, IEEE-float=3). The captured game menu music is
clearly non-silent, which is the ideal proof: the process-loopback stream carries the
game's own audio. (Note the process-loopback level tracks the app's *pre-endpoint* stream
while system loopback reflects post-master-volume mix, so the two levels need not match.)

## Out of scope for this spike

- **Mic mixing** — the plan's game-audio-**plus-mic** mixing is **M2** scope (audio
  productionized and muxed into recordings, per-settings mic mix, A/V sync ±100 ms). This
  spike proves only the game-only capture source; it does not open a mic or mix streams.
- **Muxing into MP4 / A/V sync** — also M2. Here we only write standalone WAVs.
- **Device-loss handling mid-recording** — M2 acceptance criterion; not exercised here.

## Still requires a live play session to measure

- Level stability **across an in-match session** (combat SFX bursts, not just menu music),
  and that process loopback keeps following the game's audio through match start/stop.
- Behaviour when the game **changes audio device** or is muted mid-session.
- Long-run stability (30-min) and any buffer glitching — deferred to M2's audio
  acceptance work, where sync and device-loss are the actual gates.
