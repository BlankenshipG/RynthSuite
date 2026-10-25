# Upstream snapshot comparison: rynth/RynthSuite 2026.10.6.x vs GitHub main

Date: 2026-10-06
Report only. Nothing has been merged or pushed.

| Side | Ref | Commit |
|---|---|---|
| Upstream (aelrynth `rynth/RynthSuite`) | `aelrynth/main` | `dafd608` Snapshot for release 2026.10.6.2 (internal e3a94cf) |
| GitHub (`BlankenshipG/RynthSuite`) | `origin/main` | `c1ccf5e` Merge PR #8 (T11 loot support) |
| Common base | merge-base | `6847868` release 2026.10.5.20 |

Upstream snapshots not on GitHub:
- `5416e80` 2026.10.6.1 (internal bd2f068): 97 files, +8926 / -244.
- `dafd608` 2026.10.6.2 (internal e3a94cf): "Faster weapon switches; T11 loot support (by SilentKelpie, rynth/RynthSuite PR #3)". 24 files, +1645 / -34.

Upstream PRs from the SK fork: all open, none merged.
- `#1`: superseded.
- `#2`: ILT Hub, HUDs, item info, translation, ground loot, nav editing, UB damage seed.
- `#3`: T11 loot support and item info.

## Policy for the next merge

- **Keep everything we sent up.** That is the PR #2 work and all of PR #3, including the
  Mag-style item info (`MagItemDescriber`, `RynthAiItemInfo.cs`, settings window, click trigger,
  0.5.17 formatting, 0.5.18 select/click fix).
- **Bring down the new upstream items flagged below**, merge them in and test them.

## Already identical on both sides (12 files, nothing to do)

These are the T11 loot engine from PR #3, taken upstream unchanged:
- `Plugins/RynthCore.Plugin.RynthAi/Combat/AcStubs.cs`
- `Plugins/RynthCore.Plugin.RynthAi/Loot/T11ItemSupport.cs`
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiT11Command.cs`
- `Shared/RynthCore.LootSdk/Editing/LootRuleText.cs`
- `Shared/RynthCore.LootSdk/T11/` (`T11Catalog`, `T11ItemInfo`, `T11Keys`, `T11PlayerAugs`)
- `Tools/RynthCore.LootSdkTests/` (`LootT11Tests`, `LootValueNameTests`, `Program`)
- `Tools/RynthCore.RynthAiHostTests/Tests/T11LootTests.cs`

Upstream did **not** take these parts of PR #3. They stay ours and should be re-offered:
- `MagItemDescriber` formatting (0.5.17);
- `RynthAiItemInfo` select/click fix (0.5.18);
- `MagItemDescriberTests`;
- the three changelogs.

## FLAG: new upstream items to bring down, merge and test

### New files (31): missing on GitHub
- **Action pacing:**
  - `Plugins/Shared/ActionPacer.cs`
  - `RynthAiPlugin.Pacing.cs`
  - `Loot/UseRefusalWatch.cs`
  - `Combat/KillClock.cs`
  - `FlightRecorderSignal.cs` (pairs with the RynthCore flight recorder)
  - tests: `ActionPacingTests`, `UseRefusalTests`, `NoKillTests`, `PetRefillTests`
- **RynthNav trips and routes:**
  - `TripPlan.cs`, `TripPath.cs`, `TilePathFinder.cs`
  - `NavRouteExport.cs`, `PathMarkers.cs`
  - `RynthNavPlugin.Markers.cs`, `RynthNavPlugin.SaveRoute.cs`
  - tests: `TripExportTests`, `NavReachTests`
- **Nav overlay:**
  - `RynthAi/NavOverlay.cs`, `Plugins/Shared/Nav3DText.cs`
  - test: `NavOverlayTests`
- **Meta:** `Meta/MetaRuleEdits.cs`.
- **Monster Editor:**
  - `Tools/RynthCore.MonsterEditor/MonsterRulesFile.cs`
  - test: `MonsterEditorTests`
- **World cache:** test `WorldCacheTypedTests`.
- **Upstream item info:** `ItemInfo/ItemInfoText.cs`, `ItemInfo/SelfSelect.cs`,
  `RynthAiPlugin.ItemInfo.cs`, test `ItemInfoTests`. See the overlaps below.
- **Install paths:** `Plugins/Shared/RynthInstallPaths.cs`, test `InstallPathsTests`. See the
  overlaps below.

### Upstream edits to files GitHub never changed (51): merge cleanly
- **Combat:**
  - `BuffManager`, `CastTracker`, `CombatManager.Ammo`, `CombatManager.Offhand`
  - `MissileCraftingManager`, `SpellManager`
  - `WeaponSwapGate` (faster weapon switches)
- **Meta:** `AfFileParser`, `AfFileWriter`, `ExpressionEngine`, `ExpressionReference.txt`,
  `MetaSchema`.
- **Navigation:** `NavigationEngine`, plus RynthNav `Avoid`, `Panel`, `Travel` and the plugin
  file.
- **Other RynthAi:** `CreatureProfileStore`, `LegacyAdvancedSettingsUi`, `LootEditorBridge`,
  `MagToolsCommands`, `ManaStoneManager`, `DungeonMapBake`, `AutoTradeManager`,
  `UtilityBeltExtraCommands`, `AutoVendorManager`, `MoveAudit`, `UseAudit`.
- **Other plugins and tools:**
  - RynthLua paths, RynthNet settings
  - Loot Editor `MainViewModel`, Monster Editor window
- **Tests:** RynthAi host-test fakes and tests; test project files (RynthLua, RynthNav,
  RynthAiTests).

### Edited on both sides (19): needs a hand merge (11 conflict in a trial merge)

Conflicting:
- `Combat/PetManager.cs`, `Combat/WorldObjectCache.cs`
- `DungeonHazardStore.cs`, `DungeonHazardSurfaces.cs`
- `LegacyUi/LegacyMetaUi.cs`, `Meta/MetaManager.cs`
- `RadarWallRenderer.cs`
- `RynthAiCommands.cs`, `RynthAiPlugin.cs`
- `Tools/RynthCore.MonsterEditor/JsonContext.cs`
- `Tools/RynthCore.RynthAiHostTests/Program.cs`

Auto-merged, but changed on both sides, so review them:
- `CombatManager.cs`, `CorpseOpenController.cs`, `CreatureWeakness.cs`
- `LegacyDashboardRenderer.cs`, `LegacyUiSettings.cs`
- `NavMarkerRenderer.cs`
- `RynthCore.Plugin.RynthAi.csproj`
- Monster Editor `Models.cs`

## Overlaps to resolve when merging

1. **Item info: both sides define `HandleItemInfoCommand`, `TickItemInfo` and
   `_itemInfoPendingId`** in the `RynthAiPlugin` partial class, so a straight merge does not
   compile.
   - Keep ours.
   - Adopt upstream's `SelfSelect`, which marks selections RynthAi makes itself (looter, meta
     `selectitem`) so on-select item info only prints for the player's clicks.
   - Map upstream's `ItemInfoOnSelect` setting (`LegacyUiSettings`, expression variable,
     advanced settings checkbox) onto `MagItemInfoSettings.OnSelect`, or drop it.
2. **Install paths: two copies of `RynthInstallPaths`.** GitHub has `Shared/RynthInstallPaths.cs`;
   upstream has `Plugins/Shared/RynthInstallPaths.cs`. The two differ by about 190 lines. Keep
   one, and update the links in every project file (RynthAi, RynthLua, RynthNet, Loot Editor,
   test projects).

## Suggested test pass after merging

- `Tools/RynthCore.RynthAiHostTests`: all suites. The new upstream suites listed above must
  pass alongside `MagItemDescriberTests` and `T11LootTests`.
- `Tools/RynthCore.LootSdkTests`, `RynthNav.RouteTests`, `RynthNav.GraphTests`,
  `RynthLua.CompatTests`.
- In game:
  - item info on select and on click (no double print, no print for looter selections);
  - weapon swap speed;
  - trip planning and route export;
  - nav overlay.
