# M1 dependency licensing gate

**Verdict authority for the M1 exit requirement** (implementation-plan.md: "a licensing GO/NO-GO per shipped dependency"). This is the primary deliverable of Spike C — the probe is secondary.

The project itself is **MIT** (see repo `LICENSE`, "Copyright (c) 2026 raorbit"). The rule this table enforces: every *shipped* dependency must have a license compatible with distributing an MIT application, and known binary provenance, before it is treated as available. The app's own source stays MIT in every GO case below.

All license text was re-checked on **2026-07-14** at the cited source. Where a source is "well-known / project docs", the license is stable and long-standing and was taken from technical-notes.md rather than re-fetched.

## Verdict legend

- **GO** — permissive/compatible; ship it. App stays MIT; preserve the dependency's own license/copyright notice in the distributed third-party-notices.
- **CONDITIONAL** — usable only under a specific structural constraint (isolated process, redistribution obligation). Ship only if that constraint is honored.
- **NO-GO** — cannot ship as-is. Blocked pending an external change (permission/relicense) or replaced by an alternative.

## Table

| Dependency | Role | Shipped in app? | License | Source checked (2026-07-14) | Verdict |
|---|---|---|---|---|---|
| **ScreenRecorderLib** | Video capture (primary route) | Yes | MIT | [github.com/sskodje/ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) — MIT license shown; wraps Windows.Graphics.Capture + Media Foundation (Windows system components, no GPL) | **GO** |
| **NAudio** | Mic/device audio handling | Yes | MIT | [github.com/naudio/NAudio](https://github.com/naudio/NAudio) — README: "NAudio is licensed under the MIT license". *Note: historically Ms-PL; relicensed to MIT — confirm the pinned package version carries MIT.* | **GO** |
| **Microsoft.Data.Sqlite** | Metadata DB (ADO.NET provider) | Yes | MIT (SQLite engine itself is public domain) | [nuget.org/packages/Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) — "MIT license" | **GO** |
| **Dapper** | Micro-ORM over Sqlite | Yes | Apache-2.0 | [License.txt](https://raw.githubusercontent.com/DapperLib/Dapper/main/License.txt) — "The Dapper library and tools are licenced under Apache 2.0" | **GO** |
| **Serilog** | Rolling-file logging | Yes | Apache-2.0 | [github.com/serilog/serilog](https://github.com/serilog/serilog) — "Provided under the Apache License, Version 2.0" | **GO** |
| **Velopack** | Setup.exe + delta auto-updates | Yes (installer/updater) | MIT | [github.com/velopack/velopack](https://github.com/velopack/velopack) — MIT | **GO** |
| **H.NotifyIcon.Wpf** | Tray icon | Yes | MIT | [github.com/HavenDV/H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) — MIT | **GO** |
| **Microsoft.Web.WebView2** | UI host (Edge/Chromium) | Yes (SDK; runtime is Evergreen) | Microsoft proprietary redistribution license (BSD-style terms; royalty-free redistribution permitted) | [NuGet license page](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4078.44/License) — "Redistribution and use in source and binary forms … are permitted provided that the following conditions are met" | **GO** (see note 1) |
| **hslog** (python-hearthstone) | Log-parsing behavioral reference | **No** (Python; reference only) | MIT | [github.com/HearthSim/python-hearthstone](https://github.com/HearthSim/python-hearthstone) — MIT, Python 100% | **GO** (reference; nothing shipped) |
| **HearthMirror** | MMR memory reader (candidate) | Proposed — **blocked** | **No public license.** HDT repo is "All Rights Reserved"; HearthMirror is not published on NuGet (0 results, 2026-07-14) and has no standalone licensed release | [HDT README](https://github.com/HearthSim/Hearthstone-Deck-Tracker) — "Copyright © HearthSim. All Rights Reserved."; [NuGet search](https://www.nuget.org/packages?q=HearthMirror) — "0 packages returned" | **NO-GO** (see note 2) |
| **libobs** | Capture last-resort | Only if used — never linked | GPL-2.0-or-later | technical-notes.md; well-known project license | **CONDITIONAL** (see note 3) |
| **ffmpeg + libx264** | Capture named pivot (child process) | Only if used — as a bundled binary | GPL (a libx264-enabled ffmpeg build is a GPL binary) | technical-notes.md; well-known | **CONDITIONAL** (see note 4) |

## Notes

**1. WebView2 (GO, with a caveat).** The SDK NuGet is under Microsoft's own redistribution license, not an OSI-approved open-source one, but it grants royalty-free redistribution under BSD-style conditions, so it is compatible with shipping inside an MIT app: WebView2 is a separately-licensed redistributable component, not code merged into our MIT source, so it imposes no relicensing on the app. Obligations: preserve the Microsoft license/copyright notice in third-party notices, and don't imply Microsoft endorsement. The **WebView2 Runtime** is "Evergreen" (installed/updated via the Edge WebView2 bootstrapper), so we distribute the small bootstrapper rather than a bundled browser — confirm the bootstrapper's redistribution terms at packaging time (M6).

**2. HearthMirror (NO-GO — this is the gate).** With no license grant of any kind, default copyright law reserves all rights to HearthSim: we may **not** vendor, copy, fork, link, or redistribute HearthMirror, and reading HDT/HearthMirror source for line-by-line reuse is off the table. The M1 verdict is therefore **NO-GO on shipping HearthMirror as-is**. The MMR feature is *not* blocked, because `IRatingProvider` is optional by architecture. Resolution paths, in preference order:
   - **(a) Ask HearthSim** for permission or a licensed/NuGet release — see `HEARTHSIM-EMAIL-DRAFT.md` (the user sends it). A grant flips this to GO.
   - **(b) Minimal clean reader.** Implement a from-scratch reader for the *single* documented field path (`BaconRatingMgr.s_instance → m_lastRatingResponse → Rating` / `DuosRating`), written from public documentation and our own Mono-heap probing — **no HearthMirror source consulted or copied**. This is a clean-room effort against Mono internals, not a copy of their work; feasibility of the read-only attach it depends on is confirmed PASS by this spike's probe (see `README.md`).
   - **(c) Ship v1 without MMR.** Manual entry / none. M4's exit already proves recording is indifferent to MMR's absence.

**3. libobs (CONDITIONAL — isolated process only).** GPL-2.0-or-later is strong copyleft: linking libobs into our process (in-proc, any binding) would force the *entire* application to GPL, which is incompatible with staying MIT. It is therefore permitted **only** as a fully separate, arms-length process (its own executable, communicating over IPC/CLI), and **never** linked or loaded into the app's process. Even then, distributing that GPL executable carries GPL obligations for *that binary* (see note 4). This is a documented last resort behind `IRecorder`; the MIT primary route (ScreenRecorderLib) means we expect never to ship it.

**4. ffmpeg + libx264 (CONDITIONAL — child process; distribution has obligations).** An ffmpeg build with libx264 enabled is a GPL binary. Two separable questions:
   - **Running it** as a separate child process (spawn `ffmpeg.exe`, pipe frames/args) does *not* taint the MIT app — arms-length process invocation is not linking. GO for use.
   - **Distributing** that `ffmpeg.exe` alongside our installer triggers GPL redistribution obligations **for the ffmpeg binary itself**: ship the GPL license text and a written offer of / link to the corresponding source for that exact build. The app's own source stays MIT. If we don't want that obligation, require the user to supply their own ffmpeg, or stay on the ScreenRecorderLib route.

## Bottom line for M1 exit

- **Ship set is clear: GO** on ScreenRecorderLib, NAudio, Microsoft.Data.Sqlite, Dapper, Serilog, Velopack, H.NotifyIcon.Wpf, WebView2. All permissive/redistributable; app stays MIT; assemble a third-party-notices file at packaging (M6).
- **MMR is gated: NO-GO on HearthMirror as-is.** Do not let any shipped code depend on HearthMirror until a HearthSim grant lands. Proceed on path (b) clean reader or (c) no-MMR; keep the provider optional.
- **Capture fallbacks are fenced:** libobs never in-proc; a bundled GPL ffmpeg only with source-offer compliance. Neither is needed if Spike B's MIT route (ScreenRecorderLib) holds.

Copy these verdicts verbatim into `spikes/DECISIONS.md` when the orchestrator assembles the M1 exit record.
