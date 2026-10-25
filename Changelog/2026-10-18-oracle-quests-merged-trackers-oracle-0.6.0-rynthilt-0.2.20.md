# RynthOracle 0.6.0 + RynthILT 0.2.20: trackers merged into the Quests tab (2026-10-18)

ProductVersion 2026.10.18.23.
- RynthOracle previous release: 0.5.4 (2026.10.18.19). This is a major feature, so the minor number goes up.
- RynthILT previous release: 0.2.19 (2026.10.18.22).

## RynthOracle: one Quests list

The **Legendary**, **Markers** and **Dynamic Stamps** tabs and RynthILT's **Temple guardians** are now
rows of the Quests tab's list, next to the quests. A saved tab name of an old tab opens Quests.

- **Rows by server.** Quests, Legendary Key quests (LEG) and exploration markers (MRK) show on every
  server. Dynamic Stamps (DYN) and Temple guardian turn-ins (GRD) show only on Infinite Leaftide.
  The list changes on its own when the server changes.
- **"Trackers only"** narrows the list to Legendary, markers, guardians and Dynamic Stamps, with
  their status above it:
  - John's Legendary counter, and how many are ready to run again;
  - markers found, and the next marker;
  - stamps had, plus those not in the sheet;
  - the guardian controls.
- **Show** picks one kind (or everything). With Dynamic Stamps it adds the area picker.
- **Search, Done / Not done, Repeatable / One-time and Sort** work on every kind:
  - Done means a Legendary quest or stamp you have, a marker found, or a guardian on cooldown.
  - Ready soonest sorts Legendary timers and ready guardians.
  - The quest-only filters (Not in list, the server pack, Kill tasks) leave the trackers out.
- **Paged, 40 rows a page.** All 243 stamps plus every other tracker stay far under the engine's
  1,500-op window limit: 547 ops with every Infinite Leaftide tracker and a guardian opened.

### Clicking a row opens it below the list

- **Quest:** as before: notes, rewards editor, Track, and Go to.
- **Legendary quest:** its area, flags, status, luminance and notes, Print in chat (with the wiki and
  video links), and **Go to** for coordinates in its notes.
- **Marker:** its location, found or not, and directions. **Go to** uses the directions'
  coordinates, or the location's name for RynthNav's Atlas.
- **Dynamic Stamp:** as before. RynthNav plans the route on click, and the box has Go / Arrow /
  Chat and **Give** of a carried Parchment to the stamp's NPC (a /myquests read follows the hand-in).
- **Guardian turn-in:** its NPC, turn-ins, status and next time (exact from /myquests, or estimated
  from /qb), with the guardian controls.

Go to is RynthNav's Go (auto nav) / Route / Arrow. When the notes give several coordinates, a picker
comes first.

### Temple guardians (from RynthILT)

The controls show RynthILT's own state and change it:
- Detect from chat
- **Auto hand-in**
- **Buy if missing** (buys the Temple item from the open or nearest vendor first)
- the last riddle answer with **Hand in / Stop**, and the hand-in status
- Refresh (/myquests and /qb list)

The automation itself stays in RynthILT and runs whether or not Oracle is open. RynthILT's Guardian
window still works.

### Commands

- `/ro legendary`, `/ro markers`, `/ro dynamic` and `/ro guardians` open the Quests tab on that tracker.
- `/ro trackers` opens it on every tracker.
- The About tab lists them.

## RynthILT: "RynthILT.Data" version 3

- `GetGuardianJson` gives the guardian switches, the last answer and hand-in, and the attribute
  turn-in rows (`IltGuardian.BuildJson`).
- `GuardianCommand` takes `handin | stop | refresh | chat on|off | auto on|off | buy on|off`
  (`IltGuardian.RunVerb`).
- Older readers still work: a version-2 table keeps its charms, bank and breeding functions, and the
  new `V2Size` stops a newer reader from mistaking an older RynthILT for one without breeding.

## Tests

- **RynthOracle.Tests** (329 passed):
  - a new `Trackers()` test covers the guardian JSON and verbs, rows per server, Trackers only,
    opening Legendary and marker rows with RynthNav Go to, the guardian controls sending their
    verbs, and the op budget with every tracker;
  - the Legendary / Dynamic Stamps tab checks now check the merged list.
- **RynthILT.Tests:**
  - `DataApiTests` is now registered (it was never run) and covers version 3, with a guardian JSON
    round trip;
  - the breeding interface test is updated for version 3;
  - the "move: ... redirect to RynthOracle" failure was already on the branch before this change.
