# RynthSuite: merge feat/dynamic-sections into build-comp-10-11 (RynthCore 2026.10.5.50)

This merge brings the `feat/dynamic-sections` branch onto the local build line. It pairs with the
RynthCore 2026.10.5.50 merge.

| Plugin | Previous | Now |
|---|---|---|
| RynthAi | 0.5.57-legacy-ui | 0.5.58-legacy-ui |
| RynthILT | 0.2.3 | 0.2.4 |
| RynthOracle | 0.2.2 | 0.2.3 |
| 16 server plugins | not on this line | added |

The branch's own notes (`2026-10-13-mini-remote-sections.md`, `...-server-plugins.md`,
`...-fellowship-view-ilt-fship.md` and `...-fellowship-tab-bank-table-ilt-settings.md`) are kept.
Where they disagree with this note, this note describes what ships.

## Server plugins

- The 16 per-server plugins (`Plugins/Shared/ServerPack` plus one project each) and
  `Tools/RynthServerPack.Tests` arrive unchanged.
- RynthILT is now Infinite Leaftide's server plugin. Its csproj declares server id `ilt`, host
  `infiniteleaftide.online` and world names `InfiniteLeaftide|*Leaftide*`.

## RynthILT 0.2.4

- **ILT settings page.** The branch's settings model and UI now live in `IltSettingsView/`; the
  folder was renamed so it doesn't clash with the Hub's `IltSettings` class. Open the page with
  `/ilt settings` or the Mini Remote "ilt-settings" section. It shows the engine's verdict and the
  Auto/On/Off radio buttons, and the server features come from RynthILT's own Hub.
- **Engine verdict.** `IltEngineVerdict` reads the verdict from the "servermenu" push. When the
  engine decides, `IsIltLikeWorld` follows it. The status export still reports only the local
  verdict (`IsIltLikeWorldLocal`), so it can't echo the engine's verdict back.
- **`/fship` commands.** `IltFellowshipCommands` lists Create, Invite, Dismiss, Invite landblock,
  List nearby, Leave, Disband and Help. RynthILT shows them in its own window (`/ilt fellow`) and
  shares them with RynthOracle.
- **Server bank item table.** Note denominations, note values and the transfer-note index come
  from the branch. The Banking page previews a note's value.
- Shutdown frees every export buffer, including sections, and each step is guarded.

## RynthOracle 0.2.3

- The Fellowship tab gains an "ILT /fship" block: a Name field and the command buttons, four per
  row. Disband and Dismiss need a second click within 4 seconds, and `/fship list` has a
  10-second cooldown. Each button's tooltip shows the command it will send.
- New command: `/ro fellowship` (also `fellow` and `fship`) opens the Fellowship tab. The
  engine's Char menu uses it.

## RynthAi 0.5.58-legacy-ui

- `SectionsManifest` was rewritten for the engine's section catalog. RynthAi owns Pets, Crafting,
  Spells and Fellowship. RynthILT's tabs (Character, Charms, Banking, Gear and Games) are placed
  from `IltClient.Tabs()` and drawn by RynthILT through the "RynthILT.Hub" interface.
- The Mini Remote's Character menu has a Fellowship item that runs `/ro fellowship`.
- "Force ILT" sets the local flag only when the engine isn't deciding.
- Dropped from the branch: copying the ILT settings from RynthAi to the engine, and the
  "iltfeatures" sharing. RynthILT already owns those settings on this line, and
  `IltHub/IltHubUi.cs` stays deleted.

## Tests

- `RynthILT.Tests`: 70/70 (781 checks), including the new engine-verdict test.
- `RynthServerPack.Tests`: 12/12.
- `RynthOracle.Tests`: 191 passed.
- `RynthCore.RynthAiHostTests`: 960/961. The one failure is the known map-bake check, which
  needs the real dat files. All sections and Mini Remote tab tests pass.
- All 16 server plugins build in Release.
