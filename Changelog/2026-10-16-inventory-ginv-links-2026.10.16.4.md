# 2026.10.16.4 - Inventory / Global Inventory link cleanup

Previous: RynthAi ProductVersion 2026.10.16.2, RynthInventory 0.1.0 / 2026.10.15.7,
RynthILT 0.2.12.

## Change

Wire every Inventory / Item Count / Global Inventory / Banking entry to the right
surface, and stop treating `/ginv bank` as a search for the word "bank".

### RynthInventory 0.1.1

- `/ginv bank` and `/ginv banking` open Global Inventory (same as the old Mini Remote
  Banking tab). They no longer fill the search box with "bank".
- Help text lists the bank alias.

### RynthAi (HandWrittenVersion stays `0.5.0-legacy-ui`)

- Character / Mini Remote menu: Global Inventory (`/ginv`).
- Banking (ILT) still uses `/ginv bank`; tooltips say Global Inventory, not a
  fictional Banking tab.
- Inventory / Item Count / HUD setup tooltips distinguish the Item Count panel from
  the floating pack item count HUD.
- `MovedTabs.GlobalInventory = "/ginv"`; HomeOf maps both bank aliases to Global Inventory.
- Host test: banking redirects to `/ginv bank`.

### RynthILT 0.2.13

- Banks / Banking tooltips and feature catalog point at Global Inventory (`/ginv bank`).

## Files

- Plugins/RynthCore.Plugin.RynthInventory/RynthInventoryPlugin.cs
- Plugins/RynthCore.Plugin.RynthAi/Huds/MiniRemoteHud.cs
- Plugins/RynthCore.Plugin.RynthAi/Huds/MovedTabs.cs
- Plugins/RynthCore.Plugin.RynthAi/Huds/SectionsManifest.cs
- Plugins/RynthCore.Plugin.RynthILT/Hub/IltHubUi.cs
- Plugins/RynthCore.Plugin.RynthILT/Hub/IltSections.cs
- Plugins/RynthCore.Plugin.RynthILT/Hub/IltFeatureCatalog.cs
- Plugins/RynthCore.Plugin.RynthILT/RynthILTPlugin.cs
- Tools/RynthCore.RynthAiHostTests/Tests/SectionsTests.cs
