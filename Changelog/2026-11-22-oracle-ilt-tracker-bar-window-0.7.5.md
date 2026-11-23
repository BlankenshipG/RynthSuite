# RynthOracle 0.7.5 — ILT Tracker is its own bar icon (2026.11.22)

Previous: RynthOracle 0.7.4.

## Silentkelpie: ILT Tracker off the Oracle tab strip

The Infinite Leaftide **ILT Tracker** (quest bonus / QB Deeds, bounty slots, charms,
school gems, lum tokens, T11 gear, wiki login how-to) is no longer a tab inside the
Oracle window. It is its own RynthCore bar button, same pattern as other plugin windows
(Oracle, Conquest).

- Bar button title: **ILT Tracker** (only while Infinite Leaftide is the active server).
- Open with the bar button or `/ro tracker` (also `/ro t11`, `/ro ilttracker`).
- Hub pages (Banking, Gear, Charms, Games, …) are on RynthILT's **ILT Features** bar
  window (see RynthILT 0.3.5).

## Tests

- RynthOracle.Tests: every server shows the same twelve Oracle tabs; TabT11 is no longer
  in the tab strip.
