# RynthSuite: Fellowship tab, bank item table, ILT settings in RynthILT (RynthAi 0.5.56, RynthILT 0.1.1)

Branch `feat/dynamic-sections`, after c954620 (RynthAi 0.5.55). It pairs with RynthCore engine
2026.10.5.47 on the branch of the same name; build them together. With an older RynthCore, RynthAi
behaves as in 0.5.55 apart from the new Fellowship tab and the bank changes, and RynthILT's ILT
settings view waits for the engine's push.

| Component | Previous | Now |
|---|---|---|
| RynthAi | 0.5.55-legacy-ui | 0.5.56-legacy-ui |
| RynthILT | 0.1.0 | 0.1.1 |

## Fellowship is a universal Mini Remote tab

The fellowship view was only a RynthCore section, so it never showed in RynthAi's own Mini Remote
or its menus.

- `IltSection.Fellowship` is a universal tab, like Quests, Pets and Crafting. It shows on every
  server, after Crafting.
  - Tab id `fellowship`. `/ra hub open fellowship` (or `fellow`) opens it.
  - It is in the Mini Remote's right-click Character menu.
- The tab and RynthCore's Fellowship section draw the same `FellowshipSectionUi`. Its surface,
  `section:fellowship`, is also the tab's surface. It draws without the ILT Hub.
- The ILT server-options list showed unknown features as "off" and features that were off as
  "unknown". It now shows each correctly.

## Banking: one item table, matching the server

`IltBankCommands.Items` is one table of what ACECustom's `/bank` handler allows (upstream
976606695). The deposit, withdraw and transfer lists are built from it.

| Item | Deposit | Withdraw | Transfer |
|---|---|---|---|
| Pyreals, luminance, legendary and mythical keys, enlightened and weakly enlightened coins | yes | yes | yes |
| Trade notes | yes | yes, by denomination | yes, by denomination (new) |
| Prestige coins | yes | yes | no: bound to you |
| Pyreal peas, luminance tokens | yes | no | no |

- **Trade-note transfers (new).** The Transfer form takes a denomination and a note count. It sends
  `/bank t n <denomination> <count> <name>`, which moves the notes' pyreal value. The amount and the
  confirmation show the pyreal total. At most 50,000 notes at a time.
- **Names with a space.** The server reads the recipient as one argument, so a name with a space is
  now sent in quotes. Before, a transfer to such a player failed.
- Transfer replies are matched by `IsTransferReply`. It adds the notes' "(Equivalent to ...)" line,
  the bound-prestige-coin refusal, and bad count or denomination replies.
- Saved form choices keep their item: the old transfer indexes 0 to 5 are unchanged, and trade
  notes are added at the end.

## RynthILT: a copy of the ILT settings

RynthILT gets an **ILT settings** view. It shows:

- RynthCore's Infinite Leaftide verdict and its reason.
- This character's Auto / On / Off override (`/rc server ilt auto|on|off`).
- The ILT world names, with add and remove (`/rc server names ilt add|remove`).
- Which ILT features the server has on, with a Refresh button.

It is in two places:

- RynthCore's Mini Remote, Char section, Infinite Leaftide area: `section:ilt-settings` through the
  new exports `RynthPluginGetSectionsJson` and `RynthPluginRenderEmbed`.
- Its own window: `/ilt settings [show|hide|toggle]`, through the new `RynthPluginRender` export.

Nothing is decided by RynthILT, and the server isn't asked twice:

- RynthCore pushes the verdict, override and world names to RynthILT. This is the same "servermenu"
  text RynthAi gets, sent to the new `RynthPluginApplyRemoteCommand` export.
- RynthAi forwards the feature bits it already reads. New `IltHub/IltFeatureShare.cs` builds the text,
  sent through the engine's plugin-command broker as `iltfeatures`. It is sent when it changes and
  every 10 s.
- Refresh asks RynthAi to check the server again (`hub refresh`).
- Edits are RynthCore commands, run on RynthILT's tick. The next push shows the result.

The project now references ImGui.NET (the engine's `cimgui.dll` is used; no second copy ships) and
links `Shared/SafeImGui.cs`.

## Tests

- `RynthILT.Tests`: new `IltSettingsTests` (6 tests). They compile RynthAi's `IltFeatureShare.cs`, so
  the writer and the reader are checked together. They cover:
  - parsing the engine push, including older pushes without the reason and names;
  - malformed and oversized payloads;
  - the commands the view sends.
- `RynthAiHostTests`:
  - `ServerMenuTests.UniversalTabs` expects four universal tabs and checks that Fellowship's tab and
    section surface are the same.
  - `BankCommandTests` adds the item table against the server, transfer commands (trade notes,
    quoted names, refusals) and transfer replies.

## Files

- RynthAi:
  - `IltHub/IltSections.cs`, `IltHub/IltHubController.cs`, `IltHub/IltHubUi.cs`
  - `RynthAiPlugin.LocalFeatures.cs`, `Huds/MiniRemoteHud.cs`, `RynthAiPlugin.cs` (comment)
  - `IltHub/IltBankCommands.cs`, `IltHub/IltBanking.cs`, `IltHub/IltHubState.cs`
    (`TransferNoteIndex`)
  - `IltHub/IltFeatureShare.cs` (new), `RynthAiPlugin.EngineServers.cs`
  - `RynthAiVersionCommand.cs`
- RynthILT:
  - `IltSettings/IltSettingsModel.cs` (new), `IltSettings/IltSettingsUi.cs` (new)
  - `RynthILTPlugin.cs`, `PluginExports.cs`, `RynthCore.Plugin.RynthILT.csproj`
- Tests:
  - `Tools/RynthILT.Tests/IltSettingsTests.cs` (new), its `.csproj` and `Program.cs`
  - `Tools/RynthCore.RynthAiHostTests/Tests/ServerMenuTests.cs` and `BankCommandTests.cs`
