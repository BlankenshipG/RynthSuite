# RynthAi 0.6.16: typed objects no longer stuck as "Unknown"

**Date:** 2026-10-04
**Previous release:** 0.6.15 (ILT Hub overlay window)
**Pairs with:** RynthCore engine create-descriptor identity seeds (installer 2026.10.4.10)

## Problem
In `WorldObjectCache.TryClassify`, a positioned non-creature was only given a class if it was a
weapon, armor, caster or container. Keys, gems, spell components, food, misc items and portals
(Valley Golem Keyring, Stipend, Powdered Turquoise…) fell through to **Unknown** landscape.
`ReclassifyUnknownDynamics` re-probed all of them every 2 s and never promoted any (193 objects
in one run). Portals were never classed `Portal`, so `closestportal` couldn't find positioned
portals.

## Fixed
- **Typed objects are placed by ItemType** (`PlaceTypedObject`). Any non-creature whose ItemType
  maps to a real class is classified:
  - **Portals / lifestones**: landscape, classed `Portal` / `Lifestone`.
  - **Dynamic objects owned by you** (wielded, main pack, or a side pack whose container is
    you): inventory.
  - **Other dynamic objects** (ground drops, corpse or chest contents, gear worn by others):
    landscape with their real item class. Previously ground and NPC-worn weapons and armor were
    put in *your* inventory.
  - **Ownership unreadable**: the old rule (gear-like types go to inventory, the rest to
    landscape).
  - **Static scenery** (doors, signs): stays Unknown as before. Static containers (chests) now go
    to landscape instead of inventory.
- **The recheck pass now promotes items too:** an Unknown whose ItemType resolves later is given
  its item class instead of being re-probed forever. Logged as
  `ReclassifyUnknownDynamics: typed N Unknown object(s) as items`.
- **Login log flood:** `CLASSIFY-GIVEUP` is logged only after an object also fails a full
  slow-retry pass (≥ 2 s). The first fast burst is about 10 ms, shorter than the engine's 500 ms
  identity snapshot, so a first give-up is expected. With the engine's new create-time
  name/type seeds, login objects should classify on the first attempt anyway.

## Files
- `Combat/WorldObjectCache.cs`: `PlaceTypedObject`, `IsOwnedByPlayer`, typed promotion in
  `ReclassifyUnknownDynamics`, `_giveupOnce`.
- Version 0.6.15 → **0.6.16** (`RynthCore.Plugin.RynthAi.csproj`, `VersionPointer`).

## Build
- NativeAOT publish succeeds. Not yet verified in game.
