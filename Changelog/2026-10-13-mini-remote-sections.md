# RynthSuite: RynthAi in the Mini Remote's sections (RynthAi 0.5.55)

Branch `feat/dynamic-sections`, from 20d50ef (RynthAi 0.5.54). It pairs with the RynthCore branch of
the same name (engine 2026.10.5.46); build the two together. With an older RynthCore, RynthAi behaves
as in 0.5.54.

| Component | Previous | Now |
|---|---|---|
| RynthAi | 0.5.54-legacy-ui | 0.5.55-legacy-ui |

## What RynthAi puts in the sections

RynthCore's Mini Remote is now a rail of sections. RynthAi publishes its views through the new
`RynthPluginGetSectionsJson` export (`Huds/SectionsManifest.cs`). Each view is a surface
`section:<name>` the engine draws through `RynthPluginRenderEmbed`.

| Section | Every world | Infinite Leaftide only |
|---|---|---|
| Char | | ILT Character, Charms; the ILT server features (server area) |
| Inventory | | Banking, Gear |
| Spells | Spells (the catalog and buff profiles) | |
| Fellowship | Fellowship | |
| Pets | Pets | |
| Crafting | Crafting | |
| Quests (RynthAi's own) | Quests | |
| Games (RynthAi's own) | | Games |

- An ILT view whose feature the server hasn't reported is listed but greyed, with a note.
- New `Huds/FellowshipSectionUi.cs`: the fellowship you're in (name, open or closed, locked, XP
  sharing, members with the leader and you marked), read from `FellowshipTracker` once a second.
- New `section:ilt-options`: the server's ILT feature bits and the Force-ILT override.
- `LegacySpellsUi` can draw its body inside another window (`RenderEmbedded`).
- New `RenderEmbed` surfaces:
  - `miniremote-remote` is the remote without its tab bar; the rail replaces it.
  - `miniremote-host` draws nothing. The engine sends it while its Mini Remote shows another section,
    so RynthAi keeps its floating copy hidden.
- While RynthCore hosts the sections, `/ra` tab commands open the matching section in the engine's
  window.

## RynthCore decides the ILT world

RynthCore now sends its verdict for each server, with a per-character Auto / On / Off override. When
it does:

- The ILT Hub, the Mini Remote and `ServerFeatureGate` (Progression planners, attribute raiser) follow
  RynthCore's verdict.
- The Mini Remote's "Treat this world as ILT" and `/ra hub force` set RynthCore's override
  (`/rc server ilt on|auto`).
- Settings > Misc "Server Features" shows RynthCore's verdict and how to change it, instead of the
  local list.
- The login check keeps waiting (up to 60 s) for RynthCore's verdict before concluding a world isn't
  ILT.

### One-time copy of RynthAi's settings (`RynthAiPlugin.EngineServers.cs`)

- Server Features patterns that aren't the defaults go to RynthCore with
  `/rc server names ilt add <pattern>`, once per pattern. Only patterns of one line, without control
  characters and up to 64 characters are copied.
- If the Settings > Misc override or the Hub's per-character "Treat this world as ILT" was on while
  RynthCore is on Auto, RynthAi runs `/rc server ilt on`, once per character.
- What was copied is recorded in `%LOCALAPPDATA%\RynthCore\rynthai-engine-servers.txt`, so a later
  "auto" set in RynthCore isn't undone.
- The check and the commands run on the plugin tick.

## Tests

New `Tools/RynthCore.RynthAiHostTests/Tests/SectionsTests.cs`:

- The V and X payload lines.
- The engine verdict driving the ILT gate, and the fallback with an older payload.
- RynthAi's manifest read by the engine's own parser and placed by its catalog (both linked from
  RynthCore).
- Surface names round trip.
- The pattern filter for the copy.

## Files

- New: `Huds/SectionsManifest.cs`, `Huds/FellowshipSectionUi.cs`, `RynthAiPlugin.EngineServers.cs`,
  `Tools/RynthCore.RynthAiHostTests/Tests/SectionsTests.cs`.
- Changed: `PluginExports.cs`, `RynthAiPlugin.cs`, `RynthAiPlugin.LocalFeatures.cs`,
  `RynthAiVersionCommand.cs`, `Huds/HudController.cs`, `Huds/MiniRemoteHud.cs`,
  `Huds/ServerMenuModel.cs`, `IltHub/IltHubContext.cs`, `IltHub/IltHubController.cs`,
  `IltHub/IltHubUi.cs`, `IltHub/IltServerOptions.cs`, `IltHub/ServerFeatureGate.cs`,
  `LegacyUi/LegacyAdvancedSettingsUi.cs`, `LegacyUi/LegacySpellsUi.cs`,
  `Tools/RynthCore.RynthAiHostTests` (project and runner).