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

## Later still: jumps from your buffed Jump and burden, and over locked doors (Jump.cs, ObstacleAhead)

Silentkelpie: "the jump on routes should take your buffed jump into account as well as your burden to plan
a route and a jump to get over things to by pass doors and other obstacles".

- How high this character jumps now (JumpModel). AC's height (ACE GetJumpHeight) is:
  burden factor x (Jump / (Jump + 1300) x 22.2 + 0.05) x power.
  - Jump is the buffed Jump skill.
  - Power is how full the bar is: held 0 to 1 s.
  - The burden factor is 1 up to 100% burden and falls to 0 at 200%. Burden is what you carry over
    150 x Strength, plus 30 x Strength for each carrying augmentation.
  - Example: Jump 300 at light burden clears about 3.9 m; at 160% it clears about 1.3 m.
- How high the thing in front is: RynthAi's dungeon path interface v3 adds ObstacleAhead. It casts
  rays forward at rising heights through the static geometry of the dats (what line of sight uses). The
  lowest clear ray is the top. Doors and creatures are live objects and aren't measured.
- At a stuck spot RynthNav measures, then:
  - Jumpable: just enough bar to clear it, then a full jump.
  - Higher than this character's jump, or 200% burden or more: no jump; it steps round at once.
  - Not measured: as before, from a strong jump up.
  - The log says which and why ("1.5 m high; a 450 ms jump clears it (Jump 300, burden 40%)").
- A locked door on the way: it measures the wall the door sits in, 1.6 m to either side. If that is low
  enough for your jump, it jumps over (chat says so); else it plans round the door as before.
- The interface link takes RynthAi's table from version 1 up, checking the newer functions where they are
  used. A RynthAi without ObstacleAhead still walks dungeons; RynthNav just jumps without heights.

Tests: RouteTests JumpFromSkillAndBurden; RynthAiHostTests obstacle ahead (a fence, nothing, a house wall).
