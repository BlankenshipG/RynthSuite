# RynthAi 0.5.98: nav goes round steep ground before running into it

Build 2026.10.20.8 (RynthAi `HandWrittenVersion` 0.5.98-legacy-ui).

## Fixed

- Silentkelpie: a nav route out in the water ran straight up to a red slope block (RynthVision's
  "too steep to walk" overlay) and stood there. The route follower walks straight lines between
  waypoints and only asked RynthNav's navmesh for a way round after it was stuck and a jump
  failed. On 2026-10-19 that way round started on the far side of the slope (the navmesh snaps
  the start up to 8 yd away), so its first leg climbed the same slope and stuck again.

## Changed

- Before walking a leg, and again every 10 yd walked toward the waypoint (up to 150 yd ahead),
  nav checks the straight line against the red-slope rule: ACE's walkable-floor limit
  (`TerrainSampler.FloorZ`, about 48°) on the real per-cell triangle split, the same rule the
  RynthVision overlay and the RynthNav navmesh baker use, on land and under water. When the line
  climbs or crosses such a slope, nav walks a RynthNav navmesh way round to the route right
  away, from the same per-waypoint detour budget as the stuck recoveries
  (`NavMaxDetourAttempts`). Running down a steep slope is allowed, as in AC.
- A way round whose own first leg climbs a red slope is refused (logged as "its first leg
  climbs steep ground"); the stuck escapes take over as before.
- Walking in water is unchanged: only steep ground counts.
- Off with Navigation > route recovery (`NavRecoveryEnabled`), like the other detours.

## Tests

- `NavSteepGround`: flat and walkable climbs are clear; climbing or crossing a 59° slope blocks
  where it starts; running down it is clear; beyond the lookahead and unloaded landblocks are
  not checked.
- Nav engine: a way round is planned on the first tick when the leg crosses steep ground (no
  stuck, no jump); the check runs once per waypoint and again after 10 yd; a way round whose
  first leg climbs steep ground is refused.
