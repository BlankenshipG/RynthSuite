# RynthOracle 0.4.0 — Legendary tab (John's Legendary Key quests)

**Date:** 2026-10-17  
**Kind:** Feature (HandWritten `0.3.4` → `0.4.0`).

## What

New **Legendary** tab in RynthOracle (also `/ro legendary`, `/ro john`): the Legendary Key
quests that John in Arwic (33.6N 56.7E) counts — 30 different ones earn 500,000 luminance and
a 25-use Legendary Key.

- **Progress bar:** John's own counter (`legendaryquestcounter_0913`) when /myquests has it,
  otherwise the quests on the list you've done.
- **Per quest**, by area (Graveyard, Rynthid, Hoshino, Frozen Valley, Neftet, Random Locations):
  have / need, luminance, and the **time left on the repeat timer** (or "ready").
- Summary line: how many done, how many ready to run again, how many missing and the luminance
  still unearned.
- Filters: **Missing only**, **Ready to run**, **On timer**.
- Hover a quest for the route notes; click it to print its flags, notes, wiki link and video.

## Data

`Resources/legendary.csv` (40 quests): list, short names, luminance, notes and videos from the
"Arwic John Route" sheet; each quest mapped to its /myquests completion flag from the bundled quest
catalog (extra timer flags where a key pickup carries the real wait, e.g. Empyrean Rescue,
Deewain's Dark Cavern). The Oubliette (boss token pickup) and Slave Master (Free Hatsheput wait)
use the closest stamp the catalog has.

## Notes

- Needs the quest flags read (Quests tab, Refresh) — the tab says so when they aren't.
- The John counter counts every key quest, so it can be above the number done on this list.

## Checks

RynthOracle.Tests 298/298 (list loads 40 quests in 6 groups, every flag is a catalog flag,
have / missing / timer rules, John counter, tab on every server).
