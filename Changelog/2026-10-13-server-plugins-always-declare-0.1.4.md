# Server plugins 0.1.4 - always declare their server

Previous release: 0.1.3.

Goes with RynthCore 2026.10.5.56
(`Changelog/2026-10-13-server-list-and-forced-server-load-2026.10.5.56.md`).

## Problem

`ServerPackPlugin.BuildSectionsJson` returned null until login completed and again
after logout. Across a logout or character switch the server dropped out of
Options > Diagnostics until the next login finished.

## Change

- `BuildSectionsJson` always returns the plugin's static manifest. The engine
  already collects manifests only once a character is in game.
- Version 0.1.3 to 0.1.4 for all 16 server plugins: AChard, Aelrynth, Coldeve,
  Conquest, Derptide, DreamWeave, Eversong, FrostACE, Frostcull, LeafDawn,
  Leafdawning, Levistras, PortalStorm, Reefcull, Sundering, Thistlecrown.

## Tests

- `Tools/RynthServerPack.Tests`: 14 tests, 819 checks pass.
