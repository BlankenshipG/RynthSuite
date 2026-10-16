# RynthAi 0.5.48: upstream's magic position re-sync, working with the environment-hit throttle

Base: build-comp-10-11 at 0.5.47-legacy-ui. Version 0.5.48-legacy-ui. RynthAi only; no engine
change is needed.

## What was asked

Bring upstream's magic position re-sync (rynth, 2026.10.12.1) into build-comp-10-11, and reset its
miss counter whenever the environment-hit throttle acts, so the two features don't both answer the
same misses.

## The re-sync, as upstream wrote it (CombatManager.cs)

The code is the same as upstream's, in the same places:

- After 3 magic casts in a row with no damage, on any target (swapping between monsters doesn't
  reset the count), RynthAi takes one short real sidestep. It holds a sidestep key for 300 ms,
  releases it, and waits 400 ms. The side alternates each time. Nothing is cast while it runs.
- The cause it addresses: ACE launches projectiles from the server's copy of the player's position,
  and that copy only updates when the client reports a position that has changed. A real step
  forces an update.
- It starts only when the server has answered the last action (action pacer idle). If the server
  never goes idle, it gives up after 10 s. It runs at most once every 30 s.
- Any damage from our casts resets the count.
- Movement goes through MoveAudit as `Combat/Resync`. Log lines use the `[CombatCast]` prefix.

## Working with the environment-hit throttle (CombatManager.EnvironmentImpact.cs)

- **The throttle acting restarts the count.** When the throttle switches target or moves closer,
  the misses it acted on are explained by a wall, so the re-sync's count goes back to 0. A sidestep
  that was wanted but hadn't started yet is dropped, and the drop is logged with an `[EnvImpact]`
  prefix. Moving closer is real movement, so it re-syncs the server's position anyway. A wall
  that keeps blocking the new target still adds up to a sidestep after 3 more misses.
- **One movement at a time.** If the sidestep key is held when the throttle trips, the throttle
  waits for the key to come up (at most 300 ms) before it runs or turns.
- **The sidestep key is always released.** Upstream releases it only from Think, in magic mode.
  Now the heartbeat also releases it if Think hasn't for 1.8 s (300 ms step plus the approach's
  1.5 s orphan time), and a macro stop releases it too. This is the same safety net the throttle's
  approach already had for autorun.

## Tests (5 new in EnvironmentImpactTests)

- Upstream's `ResyncSidestep` test, ported onto this file's rig: two misses do nothing, a hit
  resets, three misses step right, the key comes up at 300 ms, it's done at 700 ms, there's no
  second step within 30 s, and the next step goes left.
- The throttle switching target restarts the count, and three fresh misses still ask for a step.
- Moving closer drops a wanted sidestep, and no sidestep key is sent.
- A held sidestep key comes up before the throttle's approach starts running.
- The heartbeat releases a sidestep key that Think stopped driving, and a macro stop releases it
  too.

The test fake host gained an opt-in `SetMotion` hook (`SetMotionCalls` / `MotionCalls`), wired the
way upstream's is.

Full host suite: 654 tests, 653 pass. The one failure is `map bake: 0x6346`, which skips itself
when the game data files aren't present, as before.

## Also fixed

The section-divider comments in `CombatManager.EnvironmentImpact.cs` had been saved with
double-encoded box-drawing characters. They're plain `─` again. This is a comments-only change.

## Publishing note

Upstream has the re-sync but not the throttle. When this work goes onto `sk`, the re-sync parts of
`CombatManager.cs` match upstream line for line. Only the throttle hooks are new there.