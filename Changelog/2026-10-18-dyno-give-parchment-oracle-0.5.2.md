# Dynamic Stamps: give the Parchment to the NPC (Oracle 0.5.2)

Oracle 0.5.2 (build 2026.10.18.17)

The destination box in Oracle's Dynamic Stamps tab (opened by clicking a quest) has a new action:

- **Give Parchment:** when you carry a Parchment, the box shows **Give <its name>**. It hands one to that stamp's NPC through RynthAi's `/ub give 1 <item> to <npc>`.
  - RynthAi finds the NPC by name and uses AC's give action, which also walks you to the NPC.
  - If the NPC isn't in sight, RynthAi says so in chat.
- **No Parchment:** the box says there is none in your packs to give.

Your packs are the main pack and its side packs. Any item with "Parchment" in its name counts. They're read at most every 1.5 s while the box is open, and again right after a give.

The usual order: click a quest, **Go (auto nav)**, then **Give Parchment** once you're there.

## Tests

RynthOracle.Tests: 319 passed.
