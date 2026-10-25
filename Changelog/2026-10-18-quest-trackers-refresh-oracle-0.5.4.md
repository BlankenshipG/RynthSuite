# Quest trackers refresh like the Quests tab (Oracle 0.5.4)

Oracle 0.5.4 (build 2026.10.18.19)

Every quest tracker tab reads the same quest flags as the Quests tab: Dynamic Stamps, Legendary,
Markers, Society, Augs and Conquest's custom quests. Each of them used to say "not read yet
(Quests tab, Refresh)" and nothing more. Each now has the Quests tab's refresh:

- **Refresh:** a small button that sends the same `/myquests` read.
- **Status:** when the flags were read, "Reading your quest flags...", how long until the server's once-a-minute limit allows another read, or that this server has `/myquests` turned off.

**Dynamic Stamps:** after **Give Parchment**, Oracle reads the flags again about 8 s later, so the
new stamp turns from "need" to "have". If the server's minute isn't up yet, it waits for it.

Unchanged: Oracle still reads the flags by itself 6 s after login, and whenever a tab needs them
before any read.

## Tests

RynthOracle.Tests: 324 passed.
