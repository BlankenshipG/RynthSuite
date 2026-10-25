# RynthDreamWeave 0.1.5 - DreamWeave server features and player commands

Date: 2026-10-14
Plugin: RynthCore.Plugin.RynthDreamWeave 0.1.5 (previous release 0.1.4)

## What changed

The DreamWeave server card in the Mini Remote now lists what the server adds over stock ACE, and
the player commands for those features, each with a Copy button.

The data comes from the server's public source,
[ACE-DreamWeave](https://github.com/Awful-Waffle-Rofl/ACE-DreamWeave) at commit 4aa7de6
(2026-10-08): its README feature list and the help text of every player-level chat command.

### Features (card facts)

- Class abilities, bought as tokens for class ability points (`/abilities`, `/cavoucher`).
- Equipment and weapon mods (`/mods`).
- Facets: several builds per character, including a PK facet (`/facet`).
- In-game bank for pyreals, luminance, legendary keys, promissory and trade notes (`/bank`).
- Account vault (mule) with its own vendor (`/mule`) and a player market (`/market`).
- Realms and ephemeral instances (`/realm`, `/exitinst`).
- Drift Network recall (`/dn`) next to the retail Town Network (`/tn`).
- Threads dungeons (`/dd`) and treasure-map dig sites (`/digsite`, `/rrtm`).
- PvP arenas (1v1, 2v2, Tugak Brawl) and battlegrounds, with ratings (`/arena`).
- Leaderboards (`/top`), augmentations (`/aug`), experience bonuses (`/bonus`).
- Fast tick opt-in (`/fasttick`) and a web character sheet (`/charsheet`).

### Commands

29 player commands, from `/bank` to `/fi`. Left out:

- Stock ACE commands (`/pop`, `/config`, `/objsend`, ...).
- Grants that only work on the stage-test shard (`/myxp`, `/mylum`, `/myauggem`, ...).
- Admin commands.

### Other

- The Source line credits the ACE-DreamWeave repository and the date it was checked.
- The launcher description now reads "rules, server features, player commands, links, account setup".
- All text is plain ASCII. The profile JSON test checks this.

## Tests

`Tools\RynthServerPack.Tests`: 14 tests, 819 checks, all passed.

## Install

The NativeAOT plugin was published with `dotnet publish -c Release` and installed to
`C:\Games\RynthSuite\RynthDreamWeave`. The 0.1.4 DLL and PDB are backed up in
`PreviousReleases\0.1.4`. The plugin loads only on DreamWeave, so the new card shows on the next
DreamWeave login.
