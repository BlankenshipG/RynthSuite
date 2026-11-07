# Quest runs carry on inside the dungeon (2026-11-07)

Silentkelpie: "when creating a route for a quest and enters a portal to a dungeon, look if a map is loaded if so
use that map to then create a route to continue the quest inside the dungeon, to fight, pickup items, unlock
doors, open doors etc."

## Run this quest (RynthOracle QuestRunner)

- The run hears when the character goes into a dungeon and comes out (the plugin's tick reads the landcell).
- Outdoors, at the place of a step that goes in ("Enter the Hidden Keep at ...", "the portal to ..."), RynthAi
  takes the portal there: the nearest within 25 m.
- Going in, a step whose only job is going in is done ("Step 1 done: you're in"). The step after it is walked
  with the dungeon's map: the cells from the client's dats, as the Dungeon Patrol and /ra dunnav use. That is
  RynthAi, not RynthNav.
  - A step with coordinates: RynthAi walks the map there (`/ra dunnav`).
  - A step without: RynthAi patrols the whole dungeon.
  - Either way, with the macro on: fighting, looting the quest's items (its loot profile is in use), and
    opening doors. A locked door gets the quest's keys first, then any carried key, then lockpicks.
- The quest's stamp or Next moves on; the next step in the dungeon is walked the same way.
- Coming out, or Stop, ends the walk. The macro and Open Doors go back as they were.
- No map (the dats not loaded, no dungeon cells there): RynthAi says so and the run waits for you.

## RynthAi

- The "questloot" broker action takes `dungeon patrol | key...`, `dungeon goto <NS> <EW> | key...`, `dungeon stop`
  and `dungeon enter` (RynthAiPlugin.QuestDungeon.cs).
- Doors (DoorInteractionController): a door the server says is locked gets a carried key first. That is
  during a quest dungeon walk, or with Auto Unlock Doors on. The quest's keys by name go first, then anything
  called key, keys or keyring (not a Keystone or a Monkey Paw), each once per door, then lockpicks as before.

Tests: RynthOracle.Tests QuestDungeonTests (in, the way-in step done, the patrol, a goto with coordinates, out,
Stop, the keys); RynthAiHostTests door keys before lockpicks, the dungeon verbs outdoors.
