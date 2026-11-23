# 2026-11-07 — Quest runs: portals, coordinates, and starting at step 1

Silentkelpie: "rynthnav on a quest while it routes to the portal it stops short of going in the
portal to do the next step, it also if it can't find the quest location by name, and it has coords
it fails to use the coords to goto the location" / "when clicking run this quest it should start
with the 1st part of the quest after building all the parts needed".

## What the logs showed (17:10 and 17:22)
- On RynthNav's way through the Town Network (dungeon 0x0007) the run took it for the quest's
  dungeon and had RynthAi patrol it, fighting RynthNav for the character.
- At a portal step's place ("Take the portal to ...", with a stamp) nothing went in: only steps
  with no stamp and no item used the portal.

## Now
- A dungeon on RynthNav's way is passed through: the run waits for RynthNav's arrival, and the
  Town Network never counts as a quest dungeon. Arriving inside a dungeon walks the step there.
- Any step that says portal / enter / dungeon uses the portal at its place on arrival, the one
  whose name has the step's words first, within 40 m (was 25).
- Coordinates in a step's own text, notes or names ("A Renegade Stronghold at 71.4N, 28.6W") are
  gone to directly. When RynthNav knows none of a step's names, the run goes to the coordinates
  the quest gave last (the step before, or where it starts) instead of stopping.
- Run this quest adds the loot and switches to the quest's loot profile, then starts at step 1:
  stamps held from an earlier run don't count; the ones this run gets move it on.

## Later: NPCs, doors and the stop inside Abayar's Laboratory (17:29-17:33)
- The run walked the laboratory to its entrance's outdoor coordinates (92.1N, 46.3W): RynthAi
  walked 52 waypoints inside, then "this route is in a different area (the landscape)" and
  stood still, never near the doors. The step's own outdoor place, inside, is now a patrol.
- A portal from one dungeon into another walks the step again in the new one (the walk had
  carried on on the old dungeon's route).
- A talk / hand-in whose NPC isn't in sight yet keeps looking for 20 s (every 2 s) and uses
  the NPC (the double-click: the client walks over and talks) as soon as it shows; the 17:09
  "no Ollar near you" was a talk sent before the run had gone anywhere.
- The Leaftide check in RynthAi is listed with the other Leaftide-gated files
  (BoundaryTests), and the loot SDK's helper is IsCustomServerKey (the shared code names no
  server).

## Later: the Female Tusker Tusk run (21:29)
- The clients were on 2026.11.7.64: build .70 (everything above) was still waiting at its install
  pause, so the run had none of it.
- No loot profile: the wiki lists no loot for the quest, and the run made a profile only with loot.
  Every run now switches to the quest's profile (base-quest-dynamic alone when there are no items),
  and items the rewards hand in ("For Hand in Female Tusker Tusk:") are looted.
- "Head to the Tusker Burrow at 2.0N, 97.9E" didn't say portal / enter: a step goes in when its
  place is a dungeon by name (Burrow, Cave, Crypt, Mine, Catacombs, Lair, ...) or the next step
  happens in the dungeon ("at the end of the dungeon").
- The wrong dungeon: a building's rooms are indoor cells too, and the run took one for the
  dungeon six seconds before the burrow's portal. A dungeon is now an indoor cell in another
  landblock than the one last outdoors (a portal moves you; walking into a building doesn't), and
  RynthAi checks the dats before patrolling ("in a building, not a dungeon").
- "Talk to Female Tuskers": "dropped by", "kill", "slay", "loot" name monsters, nobody to talk to;
  a note in brackets ("(see map)") no longer reads as "see" someone.
