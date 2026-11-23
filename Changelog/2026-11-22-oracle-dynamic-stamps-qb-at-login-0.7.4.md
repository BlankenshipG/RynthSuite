# RynthOracle 0.7.4 — Dynamic stamps ask /qb at login (2026.11.22.41)

Previous: RynthOracle 0.7.3 (2026.11.22.40).

## Silentkelpie: stamps showed 0 of 587 done

RynthCore log for Silentkelpie (pid 28216, 2026-11-22):

`Dynamic stamps: 0 Dynamic flag(s) in /myquests, 0 in the /qb list (0 completed flags read); 0 of 587 listed stamps done.`

Right after that it held 2,763 /myquests flags. The server's own flag dump still lists dozens of
`Dynamic_1_…` stamps (33 of the ones in the dump match the wiki Dynamic Stamps page exactly).

### Why

- ACECustom leaves Dynamic stamp flags out of `/myquests` when it has no quest text for them, so
  `/myquests` correctly has **0** `Dynamic_` keys.
- Done stamps are supposed to come from RynthILT's `/qb list`. That ask only ran when the Quests
  tab's stamps bar was drawn (`DrawStampQbSource`). With Oracle closed (`mainWindowOpen: false`)
  the list never loaded, so every listed stamp stayed "not done".

### Fix

- On Infinite Leaftide login, RynthOracle asks `/ilt qb` once ~8 s in (after the login `/myquests`),
  whether or not the window is open.
- The Quests tab "Read /qb" button and ILT Tracker still work the same way.
- Log line when the ask goes out: `Reading /qb list for Dynamic stamps (/myquests leaves those flags out).`

## Tests

- RynthOracle.Tests (Dynamic stamps / qb source behaviour unchanged aside from the login ask).
- In game on ILT with Oracle closed at login: log should show the new `/qb` line, then a later
  `Dynamic stamps:` line with `InQb` and `Matched` above zero when the character has stamps.
