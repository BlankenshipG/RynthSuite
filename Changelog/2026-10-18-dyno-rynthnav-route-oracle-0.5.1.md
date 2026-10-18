# Dynamic Stamps: click a quest for RynthNav's route (Oracle 0.5.1, RynthNav 0.6.8)

Oracle 0.5.1 and RynthNav 0.6.8 (build 2026.10.18.16)

In Oracle's Dynamic Stamps tab (Infinite Leaftide), clicking a quest:
- **Plans the route:** RynthNav works out the way there, with walking, portals, recalls and the dungeon's entrance, and prints it in chat.
- **Opens a destination box** at the top of the tab, showing the NPC and where RynthNav will go. Its buttons:
  - **Go (auto nav):** RynthNav walks the route.
  - **Arrow:** points RynthNav's arrow at it only.
  - **Chat:** prints the stamp, as clicking a row did before.
  - **Close.**

## Where it goes

The sheet doesn't always give coordinates, so Oracle sends RynthNav a short list of targets, best
first, and RynthNav takes the first one that is on its map:

1. Coordinates in the note or the place (`8.8S, 53.1E`, `42.6n 62.4e`, "Gerent's Compound at 87.2S, 17.5W").
2. The place, then its parts: "Mhoire Castle - Great Hall" also tries "Mhoire Castle"; "Lost Wish Cottages (Arwic)" also tries "Arwic".
3. The NPC.
4. The area. Sheet groupings such as "Looking Around" and "Odd Stamps" are left out.

Of the 243 stamps, all but 13 have something to try.

## RynthNav

- **`/rnav go`, `route` and `arrow` take `a || b || c`:** the first candidate that is coordinates, or that names a place on the map as a whole word (`Atlas.FirstOnMap`).
- **No loose matches:** "Bur" inside "Burun" or "olt" inside "Holtburg" don't count, so a loose candidate never sends the character somewhere unrelated.
- **When none resolves,** it says so.

## Tests

- RynthOracle.Tests: 319 passed (5 new: the targets of real sheet rows).
- RynthNav.RouteTests: 446 assertions (5 new: `FirstOnMap`).
