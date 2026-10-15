# Coalesced Aetheria fixes (RynthILT) and the Nav panel's switches on Settings > Navigation (RynthAi)

Date: 2026-10-15

| Plugin | Version | Previous release |
|---|---|---|
| RynthCore.Plugin.RynthILT | 0.2.9 | 0.2.8 |
| RynthCore.Plugin.RynthAi | 0.5.70-legacy-ui | 0.5.69-legacy-ui |

Needs RynthCore engine 2026.10.5.62 for the new Settings rows (see RynthCore's changelog of the same
date). With an older engine, RynthAi works as before and the rows don't show.

## RynthILT: Coalesced Aetheria

Checked against ACECustom (`Entity/Aetheria.cs`, `LootGenerationFactory_Aetheria.cs` and
`PlayerCommands.HandleClap`).

- **The level cap now applies to Coalesced Aetheria.** Coalesced Aetheria gets its level (1 to 10)
  when it drops, and at level 6 or more it can carry a C or CDR rating. RynthILT used to chunk
  every Coalesced piece whatever the "Chunk aetheria with max level below" setting said, so a cap
  of 9 still chunked a level 10 piece. Now Coalesced and revealed aetheria follow the same cap, and
  a piece with no level read yet is never chunked (UtilityBelt's Leaftide does the same).
- **The aetheria IDs were wrong.** `IltInventory` called 42636 and 42635 the red and blue chunks.
  Those are the Red and Blue Coalesced Aetheria. The real IDs are now listed: the chunks (Red
  310147, Blue 310149, Yellow 310150), the powders (Red 42644, Blue 300019), the Yellow Coalesced
  Aetheria (42637), and the Ancient Empyrean (34276) and Ancient Falatacot (34277) Trinkets.
- **Auto-clap sends /clap only when there is a pair to clap.** The server pairs red material (Red
  Coalesced Aetheria at level 1 to 3 and still unrevealed, red chunks, red powder) with Empyrean
  Trinkets, and blue material with Falatacot Trinkets. RynthILT's check used to pass with any
  chunk or any trinket alone. So every 30 minutes it sent a `/clap all` that the server answered
  with "You don't have any aetheria materials to process." It also ignored Coalesced Aetheria and
  powder, which the server does take. The status now says "no aetheria and trinket pair carried
  (red + Empyrean, blue + Falatacot)". "Clap now" still always sends.

## RynthAi: Breadcrumbs and Route overlay on Settings > Navigation

The Nav panel's **Breadcrumbs** and **Route overlay** switches now also appear on Settings >
Navigation, at the top of "Nav Marker Display". They are the same settings
(`NavOverlay.TrackBreadcrumbs` and `NavOverlay.ShowRouteMarkers`), so turning one on or off in
either place shows in the other.

- RynthAi's settings payload carries them as `TrackBreadcrumbs` and `ShowRouteOverlay`.
- A save from an engine that doesn't send them leaves them unchanged.

## Files

RynthILT:

- `Hub/IltInventory.cs`: the Coalesced, chunk, powder and trinket IDs.
- `Hub/IltChunkClap.cs`: `UnderChunkCap` (the cap for every aetheria), `ClapPartOf` and `CanClap`
  (the server's pairing), and the clap status.
- `Hub/IltHubState.cs`: the cap's comment.
- `RynthILTPlugin.cs`: version 0.2.9.

RynthAi:

- `LegacyUi/LegacyUiSettings.cs`: `SettingsBridgePayload.TrackBreadcrumbs` and `ShowRouteOverlay`.
- `LegacyUi/LegacyDashboardRenderer.cs`: sends them and applies them when present.
- `RynthAiVersionCommand.cs`: version 0.5.70-legacy-ui.

## Tests

- `Tools\RynthILT.Tests`: 93 tests, 1529 checks, all passed. 2 are new:
  - the cap covers Coalesced Aetheria, and an aetheria with no level is never chunked;
  - the /clap pairing: every server ID, each colour with its own trinket, Coalesced only when
    unrevealed and at level 1 to 3, the name fallback when no ID has been read yet, and one side
    alone never claps.
- `Tools\RynthCore.RynthAiHostTests`: a new check that Settings and the Nav panel change the same
  two switches, and that an older engine's save leaves them alone.

## Install

Both plugins are published as NativeAOT and installed to `C:\Games\RynthSuite\<Plugin>\`. The files
they replace are first saved to `RynthILT\PreviousReleases\0.2.8` and
`RynthAi\PreviousReleases\0.5.69-legacy-ui`.
