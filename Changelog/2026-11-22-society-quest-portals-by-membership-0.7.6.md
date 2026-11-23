# RynthOracle 0.7.6 / RynthAi 0.5.102 — Society quest runs use your society’s portals

Previous: Oracle 0.7.5, Ai 0.5.101-legacy-ui.

## Silentkelpie: stuck loop on society quests

Wiki society quests list all three factions (CH / EW / RB NPCs, Palm Fort coords, Town Network
“Stronghold” portals). **Run this quest** took the first / nearest match and retried the wrong
portal forever.

### Fix

- **Oracle:** when starting a run, read PropertyInt 287–289 (Celestial Hand / Eldrytch Web /
  Radiant Blood) and filter the quest to that society: tagged `(CH)/(EW)/(RB)` NPCs and coords,
  “Faction Stronghold” → that society’s stronghold name, step coords matched from tagged StartsAt.
- **Oracle:** portal enter hints for faction/society stronghold steps prefer that society’s name
  words (`Celestial || Hand || Stronghold`).
- **RynthAi:** `dungeon enter` scores portals by how many hint words match the name (not just
  nearest named), so the three Town Network Strongholds no longer tie on “Stronghold” alone.

Deploy via Desktop `Rynth-hot-build.cmd` (full regression first) — not an ad-hoc Games copy.
