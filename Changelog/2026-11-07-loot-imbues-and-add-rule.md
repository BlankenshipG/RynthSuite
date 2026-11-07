# 2026-11-07 — Loot: imbue key fix, "has any" imbues, Add rule uses the checklist

## Key 353 is weapon type; imbues are 179
- The loot editor named key 353 "ImbuedEffect" and offered imbue names for it. 353 is the
  weapon type (mastery: 9 = Crossbow, as VTank profiles such as T10locky use it). It now reads
  "WeaponType (mastery)" with Unarmed ... Magic.
- Imbues are key 179 ("ImbuedEffect (imbue)"), with the imbue names. The checklist's Imbue row
  is 179; a new "Weapon type" row (353) is under Weapon / Armor.
- RynthAi's loot check (and Global Inventory's matcher) reads 179 as every imbue the item has:
  179 ORed with ImbuedEffect2-5 (303-306), as ACE does.

## "has any" for flags (imbues, damage types)
- A new comparison, "has any" (VTank's Long Key Has Flag: (value AND picked) != 0), so one
  rule can take any of several imbues. The Imbue row starts at "has any".
- Picks of several imbues: Any elemental rend (slash, pierce, bludgeon, acid, cold, electric,
  fire, nether), Any physical rend, Any magical-element rend, Any rend (with Armor Rending),
  Critical Strike or Crippling Blow, Any defense imbue. One element: pick that rend.
- A caster's spell armor rend is the Armor Rending imbue (4) on a wand, staff or orb.

## Add rule = the Filter inventory checklist
- The Loot Editor's Add rule (and the Classic Loot tab's Add) opens the same attribute
  checklist as Filter inventory, with no item: tick what the items must have, add spells and
  cantrips from the lists, pick the action (Keep, Keep #, Salvage, Sell, Read) and a name.
  The rule goes in at the end; the default name is the action and its first conditions.
- "Start empty" in the popup is the old empty rule to write by hand (also used when the
  profile has unsaved edits). An older RynthAi says so and offers Start empty.
