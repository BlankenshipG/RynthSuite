# Inventory right-click: Add to Damage panel weapons (2026.10.20.19)

Right-click a weapon, wand or shield in the Inventory panel and choose
**Add to Damage panel weapons**. It joins RynthAi's weapons list (Items), which is
what the Damage panel offers per monster and what combat may use. A shield goes in
as an off-hand item. RynthAi says in chat whether it was added or is already
listed (and refuses anything that is not a weapon, wand or shield).

- RynthAi: `RynthPluginAddWeaponById(uint id)`; `LegacyWeaponsUi.AddWeapon(id)` is
  the old Add Selected logic without needing the item selected first (Add Selected
  now calls it). Saves the settings.
- Engine: `ItemsCommands.AddWeapon(id)` (refreshes the Items and Damage views);
  the menu entry shows for items whose type is melee, missile or caster, or that
  go in the shield slot. With a RynthAi that lacks the export, the click is logged.
