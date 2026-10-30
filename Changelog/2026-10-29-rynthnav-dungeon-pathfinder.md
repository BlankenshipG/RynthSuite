# RynthNav walks to the dungeon portal a route needs with RynthAi's dungeon pathfinder

Routes through dungeons used only the walks RynthNav had recorded: a dungeon portal you'd taken
from somewhere else, or a landing (a gem, a recall, a portal) away from every recorded walk, left
the route stuck. RynthAi already knows how to get around any dungeon from the client's dats (the
Dungeon Patrol and `/ra dunnav`), so RynthNav now asks it for exactly the walk it needs.

- **RynthAi**: new plugin interface "RynthAi.DungeonPath" v1 (`Plugins/Shared/RynthAiDungeonPathApi.cs`,
  `DungeonPathInterface.cs`, `RynthAiPlugin.DungeonPath.cs`): a walk between two points of a dungeon
  landblock through the doorways its cells share (DungeonPathfinder.FindPath + BuildNavRoute, the
  /ra dunnav path), as world points.
- **Planned walks**: with RynthAi loaded, the route hub (TownNet.Merge) gets a planned walk from
  every learned arrival to every portal of that dungeon whose landing is known and that nobody
  walked to from there; map portals landing where nobody walked from yet become entries; a dungeon
  portal into another dungeon lands at an arrival there, planned on from it. A planned walk counts
  15% longer than its length (a walked one is trusted more). Walking a planned walk and taking the
  portal records it as a walked one. The log line says how many were planned.
- **Landing away from every walk**: the hub walker asks the pathfinder for the way from where you
  stand to the exit portal instead of stopping.
- **Learn portals in dungeons** walks to untried portals by the pathfinder (any distance, any floor;
  portals seen there earlier included), and only falls back to a straight walk without RynthAi.
- **Saved nav routes** through a dungeon entered by a gem or recall now carry the walk inside
  (it stopped there before).
- Tests: `Tools/RynthNav.RouteTests/DungeonPathTests.cs`.
