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
