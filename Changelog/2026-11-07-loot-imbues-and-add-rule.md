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

## Later: ACECustom properties, any slayer, multi imbues, edit with the checklist
- From ACECustom (BlankenshipG/ACECustom): new checklist rows.
  - Item: Times tinkered, Rare, Attuned, Bonded, Max item level.
  - Weapon / Armor: Elemental damage bonus, Absorb magic damage.
  - A new "Weapon cards" group: Biting Strike, Crushing Blow, Armor Cleaving, Resistance Cleaving
    (+ its element), Cleaving targets, Split arrows.
  - Ratings: Life resist, Overpower, Overpower resist.
  - T11: T11 rare (Pristine / Ascendant), Weapon quality.
- Cards the client only sees as appraisal text ("Property Details:") are RynthAi virtual keys:
  Spell Armor Rend % (casters; the float is server-only, no imbue bit), All Creatures slayer,
  Slayer damage %, Rend power %, Phantom.
- "any" on every named row: any slayer (Slayer Type >= 1), any imbue, any weapon type.
- Multi imbues: "has all" (one Has Flag per imbue: the item needs every one) and "has any" (one
  of them); the popup ticks several in the list. The Imbue row lists only what the ticked class can
  carry (ACECustom's loot and recipes): melee / missile Crit, Cripple, Armor Rending and the
  rends; casters the rends and Nether, no Armor Rending; armor, clothing and jewelry the defense
  imbues, one at a time.
- Edit with checklist: the Loot Editor's rule view (and its Add condition) opens the same
  checklist on the rule; the rule's conditions come back as ticked rows (two Has Flag on a key as
  has all, >= 1 on a named key as any), what no row can say is kept as written, Save rule
  replaces it in place (old file kept as .bak). Raw condition is the old by-hand picker.
