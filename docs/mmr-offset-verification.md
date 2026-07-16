# MMR memory reader — live offset verification

## STATUS 2026-07-16: ABI verified live; the FIELD PATH is stale and blocks the feature

A read-only probe was run against the live client (Hearthstone build of 2026-07-16). Results:

**Verified against the live `mono-2.0-bdwgc.dll`** — these are now recorded in `MonoOffsets`:

| Offset | Value | How it was confirmed |
|---|---|---|
| `AssemblyImage` | **0x60** | 106 assemblies resolved through it |
| `ImageAssemblyName` | **0x30** | live slots: 0x20=name(path), 0x28=filename(path), **0x30=`"Assembly-CSharp"`**, 0x38=module_name. *The old 0x28 seed read a path — readable ASCII, so calibration passed on the wrong field.* |
| `ImageClassCache` | **0x4D0** | fn-ptr signature; `Assembly-CSharp` → size=6247, num_entries=13911. *The old 0x4A0 seed was wrong — this is why class lookup failed.* |
| `ClassName` | **0x48** | readable identifiers across all bucket heads |
| `ClassFields` | **0x98** | dumped 41 correctly-named fields of `NetCache` |
| `SizeofMonoClass` | **0xF0** | `next_class_cache` at +0x108 walked to **exactly** num_entries (13,911) |
| `ClassDefFieldCount` | **0x100** | plausible counts for every class dumped |
| `domain_assemblies` | **0xA0** | (seed was 0xA8; the runtime calibration scan finds it either way) |

Also confirmed live: root domain via PE-export walk + RIP decode, and the assembly-list walk.

**BLOCKER — the documented field path does not exist in this build.** There is **no `BaconRatingMgr`** class
anywhere in the loaded images (all 13,911 `Assembly-CSharp` classes plus every other image were enumerated).
The rating actually lives here:

```
NetCacheBaconRatingInfo          (Assembly-CSharp)
    <Rating>k__BackingField      off 0x10   (int32, solo)
    <DuosRating>k__BackingField  off 0x14   (int32, duos)
```

reached through `NetCache.m_netCache` (off 0x10 — a type→NetCacheObj map). The unsolved step is the **root**:
neither `NetCache` nor `ServiceLocator` exposes a static instance (`ServiceLocator.m_services` is at off 0x20,
but its only statics are profilers), so there is currently no known static entry point to the singleton.

**Consequence:** the reader as written targets a class that does not exist, so it degrades to `PatchBroken` —
safely, and it stays default-OFF, so nothing ships broken. Making the feature actually work needs a field-path
redesign onto the NetCache route plus a way to reach that singleton (walking a service-locator map, or another
static root). That is a design change, not an offset tweak.

---

The automatic-MMR feature is a **clean-room, read-only, external** Mono memory reader (`src/BgRecorder.Rating/`).
It attaches to the live Hearthstone process with a passive handle (`PROCESS_QUERY_LIMITED_INFORMATION |
PROCESS_VM_READ` — the footprint Spike C proved), walks the Mono heap with `ReadProcessMemory`, and reads
`BaconRatingMgr.s_instance → m_lastRatingResponse → Rating / DuosRating`. It never writes, injects, or calls
Mono in-process, and a failure only ever degrades `IRatingProvider.Health`; recording is unaffected.

Everything except the raw struct **offsets** is verified in CI against a synthetic heap
(`tests/BgRecorder.Rating.Tests`). The offsets themselves cannot be verified without a live game, so this doc is
the runbook for that one step. **The feature ships OFF** (`AppSettings.EnableMemoryRating = false`) until the
offsets below are confirmed on your build.

## Clean-room provenance

Every struct layout in [`MonoOffsets`](../src/BgRecorder.Rating/MonoOffsets.cs) is derived solely from Mono's own
open-source headers (Unity-Technologies/mono, `unity-master`) plus canonical glib/CLR ABI. No HearthMirror, HDT,
HSTracker, Firestone, or other tracker/reader source was consulted. Keep it that way when tuning: read Mono's
headers, not another reader's numbers.

## Enabling it

The flag is intentionally not surfaced in the settings UI yet. Edit `%AppData%\BgRecorder\settings.json` while the
app is closed:

```json
{ "EnableMemoryRating": true }
```

Relaunch with Hearthstone running. The provider attaches lazily and polls every ~2s. When the class scan fails
(the class isn't built yet, or is missing on this build), the scan retries on a slower ~30s cadence instead of
every poll — the scan is the one expensive read, so expect up to ~30s between `PatchBroken` and the flip toward
`Ok` after you enter Battlegrounds. Diagnostics are logged via Serilog with the prefix `Rating reader:`.

## Reading the health signal

`IRatingProvider.Health` (surfaced to the SPA as the `rating.get` health) tells you where you are:

| Health | Meaning | Likely cause |
|---|---|---|
| `AttachFailed` | No readable target | Game not running, handle denied, or not a native-x64 process. Transient — retried with backoff. |
| `PatchBroken` | Attached, but the heap walk did not resolve | **The usual first-run outcome if an offset is wrong.** Also fires if the class isn't initialized yet (before you enter a BG game) or the game moved to IL2CPP. |
| `Ok` (null snapshot) | Resolved, but MMR not populated | Between games — `s_instance`/`m_lastRatingResponse` not set yet. |
| `Ok` (value) | Working | You're done. |

The one distinction to remember: a **null pointer along the path** is `Ok` + null (the game just hasn't set MMR),
while a **structural resolution failure** is `PatchBroken` (an offset is wrong, or the class isn't built yet).

## The FRAGILE offsets to verify

These are marked `FRAGILE` in [`MonoOffsets`](../src/BgRecorder.Rating/MonoOffsets.cs). The seed values are
`unity-master` and move between Unity versions. The `ROCK`/`STABLE`-tagged offsets (object/string headers,
`MonoClassField` stride, hashtable, GSList, `MonoClassRuntimeInfo`, the `MonoClassDef`-relative layout) do not need
verification.

| Offset | What it is | How to confirm |
|---|---|---|
| `SizeofMonoClass` | Size of `MonoClass`; drives `field_count` and `next_class_cache` | If class scanning finds *no* classes at all, this (and the `MonoClass` field offsets) is the first suspect. |
| `ClassName`, `ClassNamespace` | `const char*` in `MonoClass` | With correct values the scan reads readable ASCII class names; garbage → wrong offset. |
| `ClassFields`, `ClassRuntimeInfo`, `ClassParent`, `ClassVtableSize`, `ClassInstanceSize` | `MonoClass` members | `instance_size` should be a small positive; `fields` should point at readable field names. |
| `VtableArrayBase` | Base of `MonoVTable.vtable[]` (0x40 non-interp / 0x48 interp) | Wrong → `s_instance` static read is garbage → `PatchBroken` after the class resolves. Try both values. |
| `ClassicVTableData` / `VtableClassicData` | Pre-refactor static-data mechanism | Only for older builds. If the modern `vtable[vtable_size]` formula fails, set `ClassicVTableData = true`. |
| `AssemblyImage` | `MonoImage*` inside `MonoAssembly` | Wrong → calibration can't confirm assembly names → no class scan. |
| `ImageAssemblyName` | `const char*` in `MonoImage` | Used to confirm the assembly list during calibration. |
| `ImageClassCache` | Embedded `MonoInternalHashTable` in `MonoImage` | The deepest, most build-sensitive offset. Wrong → class scan finds nothing. |

`DomainAssemblies` is **not** in this list: the reader discovers it at runtime by scanning the domain for a clean
GSList of assemblies with a readable image name (the mutex-size fragility makes it unknowable from a table). If
calibration fails, widen `DomainScanStart`/`DomainScanEnd`.

## Verification procedure

1. Enable the flag, launch with the game at the main menu. Expect `AttachFailed` → `PatchBroken` (class not built).
2. Enter a Battlegrounds lobby and start a game. Watch the health flip toward `Ok`. If it stays `PatchBroken`:
   - Confirm the Mono module is present (`AttachFailed` with IL2CPP present would say the field path is gone).
   - Walk the FRAGILE table top-down. The highest-leverage fixes are `SizeofMonoClass` + the `MonoClass` field
     offsets (class scan) and then `VtableArrayBase` (static read).
   - Re-derive suspect offsets from the `unity-master` headers for the exact Mono version in
     `…\Hearthstone\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll`.
3. Once `Health == Ok` with a value, compare it against the in-game post-match MMR screen for both Solo and Duos.
   They must match and must come from **distinct** fields (never merge the two ladders).
4. Note the post-game update latency (how long after a match the value changes) to tune the 2s poll if needed.

## Recording verified offsets

Edit the FRAGILE seed values in [`MonoOffsets`](../src/BgRecorder.Rating/MonoOffsets.cs) to the confirmed numbers,
keep the provenance comments, and pin `SizeofMonoClass` for that build. The synthetic-heap tests use the same
record, so they keep passing as long as the *shape* is unchanged; they do **not** re-verify the live numbers — only
a live game does that. When you're confident, consider surfacing the flag in the settings UI (see the
`GameOnlyAudio`/`MixMicrophone` pattern in `UiBridge`/`types.ts`/`App.tsx`).

## Scope note

Turning `Health` to `Ok` makes the "Automatic MMR unavailable" note disappear in the Rating card. Displaying the
automatic *number* is separate net-new UI work — the SPA currently shows only manual ratings and consumes only the
health from `rating.get`. Threading `rating`/`sampledAt` into the card is a follow-up, not part of this reader.
