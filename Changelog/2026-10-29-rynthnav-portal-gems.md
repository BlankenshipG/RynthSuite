# RynthNav: portal gems in your pack as route steps

RynthNav now looks through your pack (main pack and side packs, every couple of seconds) for
teleport items: portal gems, recall gems and stones, a custom server's town gems. Each kind it knows
the landing of is a recall the route planner can take (`/rnav go`, `/rnav route`, the arrow).

- **Where a gem lands**, first found wins:
  1. learned in game: use one (by hand or in a route) and the teleport that follows within 20 s
     teaches its landing, kept per server and weenie class in `NavData\gems.txt`, so every
     character on that server knows it;
  2. a gem that casts Lifestone Recall, Portal Recall or a portal tie recall lands where that
     character's own recall does;
  3. a gem that casts a fixed recall the Atlas knows (Aerlinthe Recall...) lands where the recall does.
  A gem landing in a dungeon, or one whose landing isn't known yet, is left out with a note.
- **Using one**: the route's step uses the gem from your pack (no magic stance, no wand) and waits
  for the teleport like a recall; with none left the goto stops and says so.
- **Cost**: a gem counts as its use plus 240 yd of walking (`/rnav gems cost <yd>`, rynthnav.json
  `gemCostYards`), so the same recall cast as a spell comes first and a gem is only spent when it
  saves real walking.
- `/rnav gems` lists the pack's gems, their counts and landings; `on|off` (rynthnav.json `gems`,
  on by default) and `forget <name>`. `/rnav recalls` and the panel's recall list include them.
- Save as nav route: a gem step is written as `/mt usei <gem name>` (RynthAi's MagTools use) and a pause.
- A gem's teleport is never taken for a portal (custom portals) or a hand-cast recall.
- Tests: `Tools/RynthNav.RouteTests/GemTests.cs`.

## Gems and recalls into learned dungeons (the same evening)

From the logs: the Abandoned Mines Portal Sending Gem was left out ("where it lands isn't known
yet"), and even known, a gem or recall landing in a dungeon was never routed, so the Abandoned
Mine's learned portals (Dark Isle, AC Storage → Snowy Valley, ...) went unused.

- A gem or recall that lands in a dungeon RynthNav has learned walks in now lands the plan at that
  dungeon's arrival, and the route goes on through its portals (and on through further learned
  dungeons). This covers Portal Recall, portal ties and fixed recall spells too.
- A gem's landing is guessed from its name when nothing better is known: "Abandoned Mines Portal
  Sending Gem" is the Atlas's dungeon "Abandoned Mine" (landing where the world data's portal into
  it does), "Timaru Portal Sending Gem" the town. Using it learns the real landing, which wins.
- Starting a goto inside a learned dungeon, the plan weighs walking to each of its portals (through
  further learned dungeons too) against your recalls, instead of only recalls.
- The world data's map portals into a learned dungeon (the Abandoned Mine portal) are entries too,
  and learned dungeons take the Atlas's name.
- Landings in dungeons are now kept with their cell and spot (recalls.txt, gems.txt).
- Tests: `Tools/RynthNav.RouteTests/GemDungeonTests.cs`.
