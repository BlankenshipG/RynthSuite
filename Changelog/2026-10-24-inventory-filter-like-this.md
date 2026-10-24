# Filter like this (Inventory panel) and /ginv like (Global Inventory)

Pairs with RynthCore's Inventory right-click "Filter inventory like this..." and "Find like this
in Global Inventory...", which reuse the Add to loot profile checklist to build a filter.

## LootSdk

- `LootEditWire`: request `Find`, `Scope` (`LootFindScopes`), `Candidates`; draft `Find`;
  state `ItemFind` (`LootEditItemFind`: matches, unknown, conditions, spec).
- `Editing/LootFindSpec.cs` (new): the rule's conditions as one base64url word for `/ginv like`.
- `Editing/LootFactsMatcher.cs` (new): judges conditions on saved `LootItemFacts` (class, name,
  long/double keys incl. T11 virtual keys, rating total, spells), with Unknown for unidentified
  items when a condition needs an identify.
- `AcItemClassifier.cs` (new): RynthAi's ItemType classification (scroll and weapon-slot
  corrections) for plugins without an object cache.

## RynthAi

- `LootEditorBridge`: item_preview with Find builds the checklist draft without a loot profile;
  item_find judges the panel's items (scope inventory) or returns the spec (scope global);
  find_refresh re-judges with the same rule and only bumps the state when the result changed;
  find_close drops it.
- `LootItemHooks.Judge` (RynthAiCommands): `VTankLootEvaluator.Match` per item; not-yet-identified
  misses count as unknown.

## RynthInventory 0.1.5

- Items are saved with their class, the checklist's int/float properties and T11 virtual keys
  (`class`, `ints`, `floats` in the file; older files still read).
- `/ginv like <spec>` opens the search page filtered by the rule (bar with the conditions and
  Clear filter; a count of items it can't judge). `/ginv like clear` removes it.
- Items saved by 0.1.4 or older, or not identified when saved, can only be judged on public data
  until their character is scanned again.

## Tests

- RynthInventory.Tests: "like filter: spec, facts, matcher, query".
