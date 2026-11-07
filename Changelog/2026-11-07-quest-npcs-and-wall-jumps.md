# Quest runs go to the step's NPC, talk and hand in; RynthNav jumps walls (2026-11-07)

Silentkelpie: "it should read the quest for names to build a route to if coords are not present, and
talk to npc in question. it should also hand quest items back to the npc that were looted for each
quest. routes are still getting stuck at walls that could be jumped."

## Run this quest (RynthOracle Data/QuestNames.cs, QuestRunner.cs)

- 5,739 of the guide's 7,091 steps have no coordinates. They name someone or somewhere instead. A step
  without coordinates now goes to those names: "Talk to Xsao Lann in Fort Tethana" sends
  `/rnav go Xsao Lann || Fort Tethana`, and RynthNav takes the first one on its Atlas.
  - The quest's own people ("starts with") come first.
  - A place the step comes "from" comes last.
  - Item names, the quest's title and sentence words ("Using", "Jump") are not names.
  - About three in four of those steps now give a name. When RynthNav knows none of them, the run says so
    and waits.
- At the step's place, RynthAi talks to the person a talking step names ("Talk to", "find", one of the
  quest's people).
- On a hand-in step ("Return the collar to Xsao", "Take the note to Ahyara") it gives them the quest's
  items got by then: the items of the steps before, but not what this step gives back. It hands over
  those the step's words name ("the collar") if any, else all of them.
- A step naming nobody ("Hand her each note") uses the step before's person.

## RynthAi (RynthAiPlugin.QuestNpc.cs)

- The "questloot" broker action takes `talk <name> || <name>` and `handin <name> || <name> | <item> | <item>`.
- It picks the NPC that best answers the names: the same name, then one starting with it ("Xsao" is
  Xsao Lann), then a whole word of it. Only an NPC, a vendor or a creature that can't be attacked counts,
  within 60 m.
- Item names match as the wiki writes them, with or without a note in brackets ("Darling's Collar
  (Quest Item)").
- Items go through the give queue. RynthAi's own items, attuned items and equipped gear are never given.
- With nothing of the quest carried, it talks to the NPC instead.

## RynthNav: stuck at walls (WallStuck.cs, RynthNavPlugin.Blocked.cs)

- The jump was a tap of the key, the smallest hop AC has. In every log the character had moved 0.0 u
  1.2 s after it. It now charges like a held spacebar: 0.6 s, then a full 1 s at the same spot. It is let
  go with run and forward held (the engine's LaunchJumpWithMotion, as /mt jump does). Only after both
  jumps does the walk step round the spot.
- The stuck watch only counted ticks that ran, and a turn in place started it again. At Yaraq on 11-07
  the walk swung between headings against a wall and was never seen as stuck. Now turning counts too:
  1.2 s with running in it, or 3 s of turning only.
- Where the local plan ran out on a trip's path, the walk stood still until the 30 s watchdog stopped
  the goto (25 s against a wall at Yaraq). It now keeps pushing along the trip's path or the route's next
  crossing, so the jumps and stepping round get their chance.

Tests: RynthOracle.Tests QuestNamesTests (Lost Pet, Note of Congratulation) and the runner. RynthNav.RouteTests
WallStuck (the stuck watch, the charged jumps). RynthAiHostTests quest talk/handin.

## Later the same day: more jump power (Graph.cs, WallStuck.cs)

Silentkelpie: "when it needs to build a nav route it should use more power to jump if needed". At 10:05 a
goto to 89.3S, 93.5W stopped three times before moving: "the route graph has no walking way to that spot".
The target was on the same land, but the graph had no link across (a wall or a ledge the navmesh's 2 m agent
doesn't cross).

- On the same land, that no longer stops the goto. RynthNav heads for the spot along the trip's line,
  jumping hard: three full jumps (1 s charge) at each place it is stuck, then it steps round. The stuck
  tries and the 30 s progress watchdog still stop it where nothing gets it over. Another landmass still
  plans with portals and recalls.
- A walking way found later turns hard jumping off.
- Ordinary walking jumps start stronger: 0.7 s (was 0.6 s), then a full 1 s.
