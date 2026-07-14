# Spike A — Log fidelity

Proves we can **discover, tail, parse, and fixture** Hearthstone Battlegrounds `Power.log` data — the
foundation the real `LogWatcher` / `LogParser` are salvaged from (M1 bullet A of
[`docs/implementation-plan.md`](../../docs/implementation-plan.md)).

Verdict: **GO.** Every acceptance check passed against the live game on this machine. No pivot to the
HearthWatcher discovery pattern is needed for solo Battlegrounds. Details and numbers below.

## Running it

```
cd spikes/SpikeA.LogFidelity
dotnet build -c Release

# 1. find the newest session's Power.log
dotnet run -c Release -- discover ["C:\Program Files (x86)\Hearthstone"]

# 2. split it into per-match fixtures (raw = local/gitignored, sanitized = committed)
dotnet run -c Release -- extract "<...>\Hearthstone_YYYY_..._SS\Power.log"

# 3. parse a file or a whole fixture directory → table + JSON report
dotnet run -c Release -- parse fixtures/sanitized

# 4. live-tail the running game for ~20s (heartbeats + parsed events)
dotnet run -c Release -- tail "C:\Program Files (x86)\Hearthstone"
```

`net10.0`, nullable + implicit usings, no third-party packages. `HearthMirror`/HDT code is **never**
referenced — heuristics were grounded by reading the real log; MIT
[python-hearthstone/hslog](https://github.com/HearthSim/python-hearthstone) is the behavioral reference.

## Subcommands

- **`discover [installDir]`** — enumerates `<install>\Logs\Hearthstone_YYYY_MM_DD_HH_MM_SS\`, picks the
  newest by the folder-name timestamp (falls back to directory creation time when the name doesn't parse),
  and prints the chosen `Power.log`. Handles a missing `Logs` dir and folders without a `Power.log`.
- **`extract <powerLogPath> [--out <projectDir>] [--full]`** — splits into per-match segments (a match runs
  from a GameState `CREATE_GAME` to the line before the next, or EOF). Writes `raw/match-NN.log` (full
  fidelity, gitignored) and `sanitized/match-NN.txt` (redacted + distilled, committed). A `git check-ignore`
  gate refuses to write raw fixtures unless they are ignored. Ends with a privacy leak scan. `--full` writes
  full (undistilled) sanitized copies — huge; only for reproducing exact raw↔sanitized equivalence.
- **`parse <fileOrDir> [--seed-date YYYY-MM-DD] [--report <path>]`** — parses one file (single- or
  multi-match, e.g. a whole `Power.log`) or every `match-*.txt` in a directory. Emits a human table, one
  JSON object per match, and a full JSON report (default: this spike's scratchpad `spikeA-parse-report.json`).
- **`tail <installDir> [--seconds N]`** — discovers the newest log, opens it `FileShare.ReadWrite`, seeks to
  end, polls ~250 ms, prints parsed events (match start/end, turn, combat) and a bytes-watched heartbeat,
  and re-runs discovery every few seconds to follow a game restart into a new session folder.

## What the real log looks like (grounded, not assumed)

Line format: `D HH:mm:ss.fffffff <Source>() - <payload>`. Every event is printed **twice** — by
`GameState.DebugPrintPower()` (authoritative, real-time, monotonic) and by `PowerTaskList.DebugPrintPower()`
(animation-delayed replay, non-monotonic). **We parse `GameState` lines only.** Timestamps carry **no date**,
so a cursor rolls the date forward whenever the time-of-day decreases (see `DateCursor`), seeded from the
folder name (`Hearthstone_2026_07_13_21_56_22` → `2026-07-13 21:56:22`).

Heuristics, each verified against the real `Power.log`:

| Field | How | Grounding |
|---|---|---|
| Match boundary | GameState `CREATE_GAME` → next `CREATE_GAME`/EOF | 17 boundaries in the session log |
| Game type | `DebugPrintGame() - GameType=GT_…` | all `GT_BATTLEGROUNDS`; duos would be `GT_BATTLEGROUNDS_DUO` |
| **Local player** | the `DebugPrintGame` `PlayerName=` that contains **`#`** (a BattleTag) | the only `#`-tagged name in the log is the human (`Player1`, 99 213 hits); the other slot is an AI/placeholder name with no `#`. **`GameAccountId` is not used** — the sanitizer zeroes it, so raw and sanitized slices parse identically |
| Hero | last `TAG_CHANGE Entity=<localName> tag=HERO_ENTITY value=<id>` at/before end of tavern turn 1 (the TURN→2 transition), then resolve id→cardId from inline `[… id=… cardId=… ]` descriptors | absorbs mulligan hero swaps; cardId carried on the hero's `PLAYER_LEADERBOARD_PLACE` descriptor |
| Tavern turns | `(max GameEntity TURN + 1) / 2` | matches HDT `GetTurnNumber()`; raw TURN counts both recruit+combat halves |
| **Combat starts** | each **even** `GameEntity TURN` transition, timestamped | this client emits **no `STEP=MAIN_COMBAT`**; even-TURN coincides exactly (same timestamp) with `TAG_CHANGE Entity=GameEntity tag=BOARD_VISUAL_STATE value=2` ("board switched to combat view"), which the parser counts independently as a cross-check — the two agree on every non-truncated match |
| Placement | last `PLAYER_LEADERBOARD_PLACE` for an entity whose `id=` is one of the local player's hero entities (or whose descriptor `player=` equals the local PlayerID) | entity ids are **reused across matches**, so all per-entity state resets at each `CREATE_GAME` |
| End / truncated | local player `PLAYSTATE` `WON`/`LOST`/`CONCEDED`; `truncated=true` when no `GameEntity STATE=COMPLETE` | 17 matches, 16 `COMPLETE` → 1 truncated (the last, still-in-progress match) |

> Deviation from the task's tag list, documented deliberately: the plan named `STEP=MAIN_COMBAT` for combat
> boundaries. That value does not exist in this client's BG logs (only `MAIN_ACTION/START/READY/…`). The
> semantically correct signal is `BOARD_VISUAL_STATE=2`, which is identical in timing to the even-TURN
> transition; the parser uses even-TURN as canonical and reports the `BOARD_VISUAL_STATE=2` count as an
> independent check.

## Privacy boundary

`raw/` (full slices) is gitignored twice over (`fixtures/raw/` **and** `*.log`) and never leaves the machine.
Only `sanitized/*.txt` is committed. The sanitizer (per-file, stable mapping):

- every BattleTag (`name#digits`) → `PlayerN#00000` (only `Player1` occurs → `Player1#00000`),
- `GameAccountId=[hi=… lo=…]` → `[hi=0 lo=0]`,
- Windows user paths → `<redacted-path>` (defensive; none occur in `Power.log`).

`extract` proves no leaks by scanning every committed file for un-mapped BattleTags, non-zero
`GameAccountId`, the known originals (`raorbit`, the real account hi/lo), and user paths — **0 hits**.

## Committed corpus is **distilled**, not a full copy

A single BG match is ~50 MB of `Power.log` (every shop roll/minion stat, printed twice); the full 17-match
sanitized corpus is **~895 MB** — far too large for Git. So the committed `sanitized/*.txt` keeps only the
GameState lines the parser consumes (`CREATE_GAME`, `DebugPrintGame`, and `TURN`/`BOARD_VISUAL_STATE`/
`HERO_ENTITY`/`PLAYER_LEADERBOARD_PLACE`/`PLAYSTATE`/`STATE` tag changes). Result: **~830 KB total**,
~200–380 lines/match, still redacted and still parsing to identical structural results. Full-fidelity slices
remain locally in the gitignored `raw/`. Reproduce a full (undistilled) sanitized copy with `extract --full`.

Equivalence was verified both ways: `--full` sanitized parses **byte-for-byte identically** to the raw slice
(incl. `endTimestamp`); the distilled sanitized matches the raw slice on every structural field and every
combat timestamp — only `endTimestamp` differs, because the distilled file ends at the true match-end event
rather than the trailing post-game cleanup line (arguably more correct).

## Measured on this machine (2026-07-14, live game, build 246003)

- **discover:** 6 session folders, **1** with `Power.log` (5 hold only rotated `Power_old.log`); correctly
  chose `Hearthstone_2026_07_13_21_56_22`.
- **extract:** **17 raw + 17 sanitized** fixtures, **0 privacy leaks**. Raw ≈ 895 MB; committed sanitized
  ≈ 830 KB.
- **parse (17 sanitized):** all `GT_BATTLEGROUNDS`; placements all in 1..8 (histogram 1×4, 2×5, 3×2, 4×1,
  5×3, 7×1, 8×1 — no 6th this session); tavern turns min 8 / max 19; combat count within 1 of
  `tavernTurns-1` on every match and equal to the `BOARD_VISUAL_STATE=2` count on every non-truncated match;
  **1 truncated** (match 17, the still-open last game); **0 parse anomalies**.
- **parse (full live `Power.log`, ~900 MB):** streamed in ~3.5 s; midnight rollover handled (matches span
  `2026-07-13`→`2026-07-14`).
- **tail:** ran 20 s with no exception on the game-held file; watched ~765 KB of appended bytes; 0 BG events
  (game between matches at the time), proving clean open/poll under the game's write lock.
- **live-file robustness (observed):** the log grew during the run and gained an 18th match mid-session —
  extract/parse/tail all handled the concurrently-written file cleanly. The committed corpus is a 17-match
  point-in-time snapshot.

## Still requires a live play session to measure

- **Duos** (`GT_BATTLEGROUNDS_DUO`): none in this session. The type is recognized and the shared-board /
  partner leaderboard semantics are untested — needs a real duos game.
- **Mid-session client restart producing a new folder:** the tailer's re-discovery path is coded and
  unit-exercised by discovery, but a real restart-while-tailing was not captured (the truncated match here
  came from an in-progress game, not a restart).
- **Combat-marker accuracy vs. video** (±2 s target, M3): combat timestamps look right relative to turns but
  were not yet correlated against a recording.
- **Hero swaps mid-game** (transforming heroes) and **anomaly/tie edge cases** beyond those in these 17
  matches.

## Files

`Program.cs` (CLI + leak scan) · `Discovery.cs` · `LogLine.cs` (line + `DateCursor`) · `Sanitizer.cs` ·
`LineFilter.cs` (distillation) · `Extractor.cs` · `BgMatchParser.cs` · `Tailer.cs`.
