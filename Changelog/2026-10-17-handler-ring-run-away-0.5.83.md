# RynthAi 0.5.83 — Empyrean Handler lethal ring: run away instead of one back-jump

**Kind:** Hot push (RynthAi). Previous `0.5.82`. DLL stamp `2026.10.18.5`.

## Problem

Silentkelpie died to an Empyrean Handler's lethal ring (2026-10-17, 11:16, log
`RynthCore.45260.log`).

## What the log shows

| Time | Event |
|---|---|
| 11:15:58 | Missile target: the Handler at 57 m. |
| 11:16:03.7 | The Handler walked in: 2.4 m. |
| 11:16:05.299 | The channel line arrived, and the evade did fire: "back-jump away … at 1.7 m (danger 12 m), face 79° then xs". |
| 11:16:05.453 | "Turn done heading=332.1". The Jumper only waits 150 ms and doesn't check the turn took, so it jumped about 107° off. |
| 11:16:06.235 | The jump launched, 0.94 s after the line (150 ms settle + 750 ms charge). |
| 11:16:07.831 | Still 1.5 m from the Handler. |
| 11:16:08.488 | Died, 3.2 s after the line. |

One back-jump moves only a few metres: backwards is AC's slowest motion. It can't clear a 12 m
ring from 1.7 m, even when it points the right way.

## Fix

On the channel line, with a Handler inside the ring:

- Cancel the attack, release a held turn, and end a kite run.
- Face directly away from the Handler and autorun (forward is the fastest motion). Every 250 ms,
  re-aim away from where the Handler is now; refresh autorun every second.
- Stop running once outside the ring plus 4 m (16 m by default), or after 5 s.
- Hold attacks for at least 3.5 s after the line so the ring goes off before re-engaging. If the
  Handler comes back inside the ring meanwhile, run again.
- Keep the target, and re-engage after.
- The old back-jump is used only when the engine has no heading snap or autorun.
- Autorun is never left on: the run ends on death, on macro stop, and when combat stops driving
  it for 1.5 s (the pump-thread orphan watch, beside the kite's).

Log lines: `[CombatEvade] … running away …`, `ring run stopped after N ms: outside the ring (X m
…)`, `Handler back inside the ring … running again`, `ring evade done …`.

## Files

- `Plugins/RynthCore.Plugin.RynthAi/Combat/CombatManager.cs`
- `Plugins/RynthCore.Plugin.RynthAi/Combat/CombatManager.Kite.cs`: the orphan watch.
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiPlugin.Safety.cs`: stop on death.
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiVersionCommand.cs`: 0.5.83.
- `Tools/RynthCore.RynthAiHostTests/Tests/RingEscapeTests.cs` (new, 6 tests) and `Program.cs`.

## Checks

RynthAiHostTests 1064/1066. The 6 new ring-escape tests pass. The 2 known failures are unchanged
(map bake needs the dats; the mini remote help line).
