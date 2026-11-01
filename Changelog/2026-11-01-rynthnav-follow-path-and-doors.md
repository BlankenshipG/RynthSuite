# RynthNav: walks keep to the trip's path, and open doors on the way (2026-11-01)

Silentkelpie: "when following a route it wanders off the path a lot, and doesn't try to open a door
if in the way."

- **Keeping to the path.** The trip's walk is worked out once for each step (TripPath, the path
  the markers draw). The walker ignored it: every 0.7 s it planned its own way to the route
  graph's next border crossing, or straight at the goal when the graph had no route ("route: none
  from region ..."). So it crossed fields and went round the other side of buildings, away from
  the markers.
  - Now each plan aims 28 yd along the step's walk past where you are on it (TripFollow.cs). The
    navmesh still plans those few yards, so walls and portals are still kept out of.
  - More than 14 yd off the path, it heads back to the path first, and logs "off the trip's path".
  - Progress along the path only goes forward, so a path that doubles back is never cut across.
  - Steps whose walk is only a straight line (the path finder had nothing there) and dungeon walks
    plan as before.
  - `/rnav follow on|off` (on by default).
- **Doors on every walk.** Only the dungeon walks opened doors (2026-10-30). Now the goto's own
  walk over land and through towns and buildings does too:
  - A closed door across the path in the next 6 m is used. The walk holds while it opens; its
    progress and stuck clocks wait too.
  - Running into something with a closed door within 4.5 m in front (65 degrees either side),
    the door is opened before the jump and the step round it. The mesh may have gone round a door
    it was baked without.
  - A locked one gets the pack's keys, then lockpicks, as in dungeons. A door that won't open is
    planned round.
  - A door just over a landblock line counts too.
  - `/rnav doors` covers both.
- Tests (RouteTests): DoorInFrontWhenBlocked, FollowKeepsToTheTripPath, FollowRejoinsAndNeverCutsBack.
