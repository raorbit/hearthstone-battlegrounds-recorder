# Spike C — MMR route + licensing gate

Plan reference: implementation-plan.md M1 bullet **C**; technical-notes.md "Rating (MMR): process-memory reading".

Two deliverables. The **licensing gate is the primary one**:

1. **`LICENSING.md`** — the M1 GO/NO-GO/CONDITIONAL verdict per shipped dependency (the M1 exit requirement). Start here.
2. **`HEARTHSIM-EMAIL-DRAFT.md`** — a ready-to-send draft asking HearthSim about HearthMirror licensing. **The user sends it, not the tooling.**
3. **The probe** (this console app) — proves that the *read-only attach* the MMR route depends on is feasible against the live game, without any HearthMirror code, memory reads, or injection.

## What the probe does

A single console app (`Program.cs`) that, against the running `Hearthstone` process:

1. Finds the process **by name** (re-resolved every run — the PID changes across launches).
2. Opens a handle with **`PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ` only** — read-only, the same passive footprint deck trackers use. It never requests write/inject rights, never calls `ReadProcessMemory`, never walks the heap, never injects.
3. Confirms **bitness** with `IsWow64Process2` (reports both the process machine and the native machine).
4. Enumerates **all modules** with `EnumProcessModulesEx(LIST_MODULES_ALL)`.
5. Reports whether a **Mono runtime module** (`mono-2.0-bdwgc.dll` or similar) is present, with base address and size. If Mono is absent it looks for `GameAssembly.dll` (IL2CPP) and flags that the field path would need re-evaluation.
6. Prints a clear **PASS/FAIL** for "read-only attach feasible" and sets an exit code.

It deliberately does **nothing** to the game: no writes, no input, no injection. It treats the process strictly read-only.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | PASS — read-only handle opened and modules enumerated |
| 1 | Process `Hearthstone` not found (start the game) |
| 2 | `OpenProcess` denied (access denied / anti-cheat blocking read handles) |
| 3 | `EnumProcessModulesEx` failed |

## How to run

```powershell
cd spikes/SpikeC.MmrRoute
dotnet run -c Release
```

The game must be running. No elevation is needed (and none should be — if a normal-user read handle fails, that is itself the finding). The project pins `PlatformTarget=x64` so it can enumerate the 64-bit target's modules.

## Measured on this machine (2026-07-14, PID 22620, live game)

Real captured output:

```
=== Spike C: read-only attach feasibility probe ===
Target process name : Hearthstone
Requested access    : PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ (read-only)
Footprint           : passive handle only — no ReadProcessMemory, no injection, no writes

Found process       : PID 22620
Executable          : C:\Program Files (x86)\Hearthstone\Hearthstone.exe

Process bitness     : 64-bit native (not under WOW64; OS/native arch = x64 (AMD64))

Module count        : 114

Mono runtime module : FOUND
  name              : mono-2.0-bdwgc.dll
  base address      : 0x7FF95B280000
  size              : 10,825,728 bytes (10.3 MiB)
  path              : C:\Program Files (x86)\Hearthstone\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll

Sample modules (first 8):
  - Hearthstone.exe
  - ntdll.dll
  - KERNEL32.DLL
  - KERNELBASE.dll
  - UnityPlayer.dll
  - USER32.dll
  - win32u.dll
  - GDI32.dll

RESULT: PASS — read-only attach is feasible.
```

Exit code: **0**.

| Measurement | Value |
|---|---|
| Process bitness | 64-bit native (x64 / AMD64), not under WOW64 |
| Module count | 114 |
| Mono module found | **Yes** — `mono-2.0-bdwgc.dll` |
| Mono base / size | `0x7FF95B280000` / 10,825,728 bytes (10.3 MiB) |
| Mono path | `…\Hearthstone\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll` |
| Read-only attach | **PASS** (no elevation, no injection) |

**Interpretation:** the game runs a standard Unity **Mono** backend (not IL2CPP), so the documented Mono-heap field path is applicable, and a read-only handle opens without elevation — the two structural preconditions for any memory-read MMR route both hold. The probe stops exactly there.

## Next step for a clean reader (the part that is GATED)

This spike proves *attach feasibility only*. It does **not** read rating, because doing so is the licensing-gated part. The documented path a reader would follow (from technical-notes.md, HDT/HearthMirror behavior as public reference — **not** copied code):

```
mono-2.0-bdwgc.dll  (base found above)
   → resolve the Mono root domain / loaded image for the game assembly
   → BaconRatingMgr  (static class)
        → s_instance            (static field → the singleton)
             → m_lastRatingResponse   (last rating response object)
                  → Rating            (solo BG rating)
                  → DuosRating         (duos BG rating — SEPARATE ladder)
```

Rules the real reader must honor (already in the plan):

- **Solo `Rating` and duos `DuosRating` are separate fields** — never merge the two ladders into one series.
- **Per-match delta:** sample at match start, poll after the post-game rating update, store the difference. Post-game update latency is still **unmeasured** — it requires a live play session (see below).
- **Optional by architecture:** `IRatingProvider` with health states (OK / AttachFailed / PatchBroken) and a kill switch. If it breaks on a game patch, the app degrades to "recordings without MMR"; recording never depends on it.

**This entire reader is gated on the `LICENSING.md` verdict.** HearthMirror is currently **NO-GO** (All Rights Reserved, not on NuGet). Build the reader only after either (a) HearthSim grants permission, or (b) a decision to write a minimal clean-room reader for just the field path above (no HearthMirror source consulted), or (c) a decision to ship v1 without MMR.

## Still requires a live play session to measure (not done here)

The probe is a static attach check; it does not read rating and cannot, on its own, validate the dynamic behavior. Left for M4 (or a follow-up spike once the license question is resolved):

- Actual read of `Rating` / `DuosRating` values (needs the gated reader).
- **Post-game rating-update latency** — how long after match end the memory value changes (tunes the provider's poll timeout).
- Solo/duos separation confirmed against real values from both modes.
- Correctness of MMR± vs the in-game post-match screen (M4 exit criterion).
- Behavior across a game patch (the fragility risk — can only be observed when a patch lands).

## Salvage into the real app

`Program.cs`'s process-discovery + read-only-open + bitness + module-enumeration helpers are written to be lifted into the `RatingProvider`'s attach/health layer: the `FindGameProcess`, `OpenProcess`, `IsWow64Process2`, and `EnumProcessModulesEx` plumbing is exactly what the provider needs to (a) locate the game, (b) confirm it's an attachable Mono x64 target, and (c) surface an `AttachFailed` health state. No HearthMirror or HDT code is present or referenced anywhere in this spike.
