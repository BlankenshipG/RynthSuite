# 2026-11-08 — Dungeon map routes: down levels by jumping, pits, backtrack; quest portals scanned for

From the Abhorrent Eater Jaw run (10:39, dungeon 0x02D4):
- The patrol walked 43 of the dungeon's cells (its top level) for ten minutes. Every way down is a drop, and the
  map's routes didn't take drops.
- At 10:41:50 a chase went down the pit, 17 m. Nav then "arrived" at waypoints overhead, since only north/east
  distance counted. Recovery said "already in the waypoint's room, the map has no way around" or "no walkable
  way", and fell back to the side-step escapes.

## Down a level: walked to the edge and jumped
- The dungeon map's own walks (/ra dunnav, a quest run's dungeon goto) now take drops down of up to 25 m: a pit, a
  shaft or a ledge. Each drop is a Jump point at its edge, facing the way down, then on from where you land. Never
  up a drop. (DungeonPathfinder.Levels.cs BuildDropRoute; NavigateTo.)
- The Dungeon Patrol walks one level: the cells walkable from where it starts. Once round, it goes down the drop
  to the biggest part of the dungeon not patrolled yet, with the same walk to the edge and jump, and patrols there.
  This repeats until no drop leads anywhere new; then it patrols on where it is. The levels patrolled are kept
  until the patrol is started again or the dungeon changes. (DescentPath, RynthAiPlugin.DungeonLevels.cs.)

## A pit, or another level
- A dungeon waypoint more than 8 m above or below you isn't reached by standing under or over it.
- If the patrol finds you on a level it doesn't walk (a fall, a chase down a drop), it is rebuilt on that level,
  from where you are.
- Recovery that finds the waypoint on another level, with no walk to it, hands it back: the patrol is rebuilt
  there, and a dunnav walk is planned again from here with drops (at most every 20 s).

## Backtrack when stuck
- Nav keeps a short trail of where it walked (the last ~40 points, 1.5 m apart). When it is stuck and the map has no
  way round, it walks back along the trail, up to 8 m on the same level, then heads for the waypoint again. This is
  done once per stuck spot, before the side-step escapes.

## Quests: the portal at the step's place
- "dungeon enter" (a step that goes in by a portal) looks for the portal within 60 m. If none is in sight yet, it
  scans again every 1.5 s for 20 s, then uses the portal it finds: ACE walks you to it and in. The scan is quiet
  until it finds one or gives up.
