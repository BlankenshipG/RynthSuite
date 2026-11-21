# 2026-11-21 — RynthOracle: a server's own wiki quests (Conquest's) in the wiki tab

- RynthOracle now asks the active server's plugin for "RynthServer.QuestGuide" (new shared interface,
  Plugins/Shared/RynthServerQuestGuideApi.cs). RynthConquest answers with the Conquest wiki's quests (see
  RynthServers' changelog).
- The guide is read like RynthILT's (Data/ServerGuideClient.cs, QuestGuideClient.Take).
- When a server offers a guide, the wiki tab is "<server> Wiki" ("Conquest Wiki"):
  - The server's quests come first, then the AC Community Wiki's.
  - A server quest replaces the AC Community Wiki's quest of the same name.
  - Kill Tasks, Society and Contracts stay the AC Community Wiki's.
- Every quest there has Run this quest, its route, the loot map and the loot profile.
- The source line above the list shows the server wiki's count and status.
- RynthOracle still names no server: the tab's label comes from the guide's "tag".
- On Infinite Leaftide, RynthILT's guide is used as before.

Tests: RynthOracle.Tests Fandom (the server guide over the AC Wiki's, the tab, its quest with Run this quest, gone
with the server).
