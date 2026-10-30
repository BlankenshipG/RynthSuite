# RynthNav: dungeon walks open doors, unlock them, jump, drop down ledges and keep to their level (2026-10-30)

RynthNav's dungeon walks used to have four problems:

- A closed door made them stop after 8 s. They never jumped.
- RynthAi's dungeon pathfinder ruled out every steep edge in both directions, so a route never
  went down a ledge or a shaft.
- The walker worked in 2D only. On a ramp that turns back over itself, a point directly above or
  below counted as reached.

The dungeon walks are a route's way through a learned dungeon and the learn-portals walk.

## Doors and locks (RynthNavPlugin.Doors.cs, WalkDoors.cs)
- Each tick, the walk looks for a closed door across its next 10 m: within 1.8 m of the walk,
  on the walk's level. A door within 6 m is used. The server walks you to it and opens it.
  The walk and its stuck clock wait while it opens.
- A door counts as locked when it is still closed after the server answers: the server says
  "The door is locked!", or two uses didn't open it. Then the pack's keys and keyrings are tried
  once each, then each lockpick up to three times, with a use of the door after each.
- If nothing opens it, the door is remembered for the session. The walk is planned again around
  it: RynthAi's pathfinder leaves out the door's cell. If there is no other way, the walk stops
  and says which door.
- `/rnav doors [on|off] | unlock on|off | drops <m>|off | forget`. Saved in rynthnav.json as
  doors, unlockDoors and dropMeters.

## Jumps
- Dungeon walks: with no progress for 2 s, the walker jumps while running, twice per spot, before
  it gives up at 8 s (HubWalker.Act.Jump).
- Outdoor gotos: the first block at a spot is a running jump on the same heading. Only then does
  the walk turn away and plan again.

## Levels and drops
- If the points of a walk span more than 2.5 m in height, a point is reached only within 3 m of
  its own height. Past a drop's edge, being below it also counts.
- RynthAi.DungeonPath interface v2 adds FindPath2, with a maximum drop, points to avoid, and the
  kind of each point. A drop down of up to `/rnav doors drops` metres (8 by default) is a one-way
  step and never goes up. It costs about 10 m extra, so stairs or a ramp of similar length win.
  The walk is split at the drop: it goes to the opening's edge, then lands 1.5 m on, lower down.
- With an older RynthAi, RynthNav uses v1 as before.

## Tests
- RouteTests WalkDoorsTests: the door across the walk, keys before lockpicks, the locked text,
  jumps before giving up, levels, the drop edge, DungeonWay.
- RynthAiHostTests DungeonDropTests: a drop only down and only when allowed; an avoid point sends
  the walk the other way around.
