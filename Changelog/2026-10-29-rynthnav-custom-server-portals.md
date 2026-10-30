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
