# RynthNav: custom-server portals for routes

Custom servers (Infinite Leaftide and others) add portals the world data (`portals.tsv`) doesn't
have, so routes never used them. RynthNav now learns them, per server.

- `/rnav portals scan [yards]`: writes down every portal object the client knows around you, for
  the server you're logged into (its world name). Portals `portals.tsv` already routes are skipped;
  portals inside dungeons aren't mapped.
- Taking a portal learns where it lands. A scanned portal becomes a route link the first time you
  use it; a portal you take without scanning is added then. A landing in a dungeon is kept and
  listed, but not routed (there's no map to walk on from it).
- Routes (`/rnav go`, `/rnav route`, the arrow, the Town Network leg) use `portals.tsv` plus this
  server's custom portals with both ends on the map, with `/rnav portals on`. The plan line in the
  log says how many custom portals it had.
- `/rnav portals list` | `forget <name>` | `clear confirm` manage this server's list.
- Saved in `NavData\custom-portals.txt` (one line per portal, the server first), next to
  `recalls.txt`.
- Tests: `Tools/RynthNav.RouteTests/CustomPortalTests.cs`.

## Learn portals (the same day)

RynthNav can map a server's portals by itself:

- **Learn portals** (RynthNav panel, Go tab, "Custom portals (this server)"; or `/rnav portals learn`):
  from where you stand, it walks into the nearest portal within the search range it hasn't mapped,
  learns where it lands, routes back to the start (portals and recalls are used for the way back
  even when the switch is off), and goes on with the next, until every portal in range has been
  tried or you press Stop (or any movement key, or `/rnav portals learn stop`). Portals the world
  data already has, and wrecked ones, are skipped; a portal that won't take you is skipped after
  RynthNav's usual tries. Two failed ways back (a dungeon with no recall out) end it. One run tries
  at most 150 portals.
- **Search range**: the panel's - / + (20-2000 yd), or `/rnav portals learn range <yd>`; kept in
  rynthnav.json (`learnRange`, 200 by default). **Scan here** writes down the portals in that range.
- The panel shows this server's saved and routable counts and what learning is doing.
- Engine: `RynthNavFace` draws the section (shown only with a RynthNav that sends `learn`).

## Inside dungeons (the same day, later)

Dungeons have no navmesh, so RynthNav now keeps the way walked inside them and routes through them.

- **Walks inside dungeons**: from each landing in a dungeon RynthNav keeps the way you walk (loops
  cut out). Taking a portal there keeps that walk with the portal and where it lands, whoever did
  the walking (you, a goto, or learning). A shorter walk between the same landing and portal
  replaces a longer one. Saved per server in `NavData\custom-dungeon-walks.txt`; dungeon portals
  (where they stand inside) and dungeon landings (where you were set down) go in
  `custom-portals.txt`.
- **Routes through dungeons**: a learned dungeon is used like the Town Network. A map portal into
  it, the walk inside to an exit portal, then out onto the map, or into another learned dungeon and
  on through that one. The walker follows the kept walk inside (stuck or off the walk stops it, the
  same as in the Town Network). A goto started inside a learned dungeon takes its walk out when that
  costs less than a recall.
- **Learn portals goes into dungeons**: landing in a dungeon, it writes down the portals there and
  walks straight to the nearest one it hasn't tried (within 60 m, on about the same floor); a
  portal round a corner it can't reach that way is given up when the walk sticks. Up to three
  dungeon portals in a row, then back to the start. From the start it goes back into a dungeon
  through its entry portal while portals inside are still untried.
- `/rnav portals scan` now writes down portals inside the dungeon you're in, and `/rnav portals
  list` shows them and the number of walks learned. The panel's routable count includes learned
  dungeon entries and exits.
- Portal learning now also works when the client moves you a moment before portal space (seen on
  portals): the portal you stood at is the one taken, and the portal space after the jump isn't a
  second teleport.
- Tests: `Tools/RynthNav.RouteTests/DungeonWalkTests.cs`.
