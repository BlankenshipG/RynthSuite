# RynthJuice 0.1.2: mob polling reaches every tracked object

**Date:** 2026-10-04
**Previous release:** 0.1.1

## Problem
Every other tick, `PollMobHealth` walked the tracked objects with a budget of 80. The list was a
`HashSet` holding every created dynamic object, items included (~700 in busy areas). Its
enumeration order is stable, so each poll re-checked the **same first 80 objects** and never
reached the rest. Juice saw 0–1 attackable mobs while the engine saw 1–11. Tracking was also
capped at 800, and runs reached about 750, so new spawns stopped being tracked in busy areas.

## Fixed
- **Two-tier tracking** in `RynthJuicePlugin.cs`:
  - **Candidates** (not yet known to be attackable) are checked in **rotation**: dequeue,
    decide, re-enqueue at the back. The budget stays at 80 per poll, so ~700 candidates are
    all covered within about 9 polls (~18 ticks) instead of never.
  - **Mobs** (confirmed attackable) are polled every cycle, so health bars stay continuous.
    The budget is 64; above that the mob queue rotates fairly too.
  - A candidate promoted this poll is polled in the same cycle.
  - A mob that stops being attackable goes back to the candidates.
- **Known non-creatures are dropped:** objects whose ItemType is known and lacks
  `TYPE_CREATURE` are dropped. This is checked when the object is created, and again in
  rotation in case the type arrives late. Type 0 or unreadable counts as unknown and stays a
  candidate. The engine now reads ItemType when an object is created (installer
  2026.10.4.10), so items are usually dropped immediately and never tracked.
- **Safety cap raised:** 800 → 4096. With items dropped it should rarely be reached, and the
  first time it is, a log line is written.
- Queue duplicates after a delete and recreate are prevented by tracking which ids are
  queued. Deleted ids are discarded lazily.
- **Diagnostics:** the `pollcycle` log line now reports
  `candidates=`, `mobs=`, `promoted=`, `droppedNonCreatures=`, `attackable=`, `vitals=` and
  `bars=`.

## Files
- `RynthJuicePlugin.cs`: `CheckCandidates`, `IsKnownNonCreature`, `EnqueueCandidate`,
  `EnqueueMob`, `ClearTracking`, plus the rewritten `PollMobHealth` and
  `OnCreateObject` / `OnDeleteObject`.
- Version 0.1.1 → **0.1.2** (`RynthCore.Plugin.RynthJuice.csproj`, `VersionPointer`).

## Build
- Release build and NativeAOT publish succeed. The publish lands in `C:\Games\RynthSuite\RynthJuice\`.
- Not yet verified in game.
