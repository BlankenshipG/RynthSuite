# Quests: go to a quest's coordinates with RynthNav (Oracle 0.5.3)

Oracle 0.5.3 (build 2026.10.18.18)

Opening a quest in Oracle's Quests tab now adds a **Go to** section when the quest's text gives
coordinates. That is about 2,900 of the 3,565 retail quests; 2,200 of them give more than one place.

- **One place:** its step and coordinates, for example "Speak with Hoshar ibn Jalaq - 59.6S, 88.0W", with **Go (auto nav)**, **Route** and **Arrow**.
- **Several places:** a picker lists them in walkthrough order, each with its step. Picking one plans RynthNav's route to it, as clicking a Dynamic Stamp does. The same three buttons then act on the picked place.
- **Go:** RynthNav plans the route (walking, portals, recalls, a dungeon's entrance) and walks it. **Route** only prints the plan; **Arrow** only points the arrow.

## How the places are found

Coordinates are read however the list writes them: "59.6S 88.0W", "8.8S, 53.1E", "42.6n 62.4e".
- **Labels:** each one is labelled with the words just before it in its step (walkthroughs separate steps with " - "). Little words like "at" and "near" are dropped.
- **Repeats:** coordinates that repeat are listed once.
- **What RynthNav gets:** the coordinates followed by the label. When the label is an Atlas place there, RynthNav uses it (a dungeon's entrance, for example).

The Dynamic Stamps tab now uses the same coordinate reader (`Data/NavTargets.cs`) and the same
RynthNav sender.

## Tests

RynthOracle.Tests: 324 passed (5 new: the stops of two real walkthroughs, Splitting Grael and
Shadowfire Stone, and the edge cases).
