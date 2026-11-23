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

## Later: readable names everywhere in the loot editors
- Property names are words, not the server's variable names: "Imbue" (not ImbuedEffect),
  "Weapon type", "Material", "Workmanship", "Wield skill", "Current mana", "Times tinkered",
  "Slayer type", "Description" / "Use text" (LongDesc / Use), "T11 zone tier" ...
- Condition types read as sentences ("Property is at least", "Text matches", "Your level is at
  least") instead of "Long Key >=", and rule summaries say "Workmanship at least 6",
  "Material is not Steel", "at least 2 spells matching ...".
- Item classes are split into words ("Melee Weapon", "Wand / Staff / Orb", "Spell Component").
- Pickers show names only (no "(45)" id after each), and the checklist's comparisons are words:
  at least / is / at most / is not / has any / has all / any.
- Display only: the files keep VTank's keys and numbers.

## Later: clicking a rule opens the checklist; has-flag values by name
- Clicking a rule in the Loot Editor's list opens it in the checklist (the same popup as Add rule
  and Filter inventory); its "Raw editor" button opens the rule view (conditions one by one).
- "Property has Imbue 16384" reads "Nether Rending": has-flag values are named in the rule view,
  its pickers and the summaries.
- Fixed: a rule with three or more imbue flags (Nether + Armor Rending + Crit + Cripple) kept the
  third and later "as written"; they all read back as one "has all" now.
- Casters list Armor Rending (ACECustom casters carry it).
- The action "Keep #" showed as a second "Keep" in the popup; it reads "Keep up to".

## Later: T11 only on Infinite Leaftide; one checklist look everywhere
- T11 is Infinite Leaftide's (RynthCore's active server, as the Banking page and the quest
  guide's Leaftide tabs already go by). Elsewhere:
  - RynthAi reads no item as T11 (T11ItemSupport.Enabled): no T11 info lines, T11 keys read 0.
  - RynthInventory's scan reads no T11 attributes.
  - The checklists (Loot Editor, Filter inventory, Global Inventory) have no T11, pet essence or
    text-only card rows, and the raw editor's pickers offer no T11 / pet / Zc keys. A Leaftide
    rule opened elsewhere keeps those tests as written. ACE's own properties (imbues, Biting
    Strike, Crushing Blow, cleaving) stay everywhere.
  - The Loot Editor asks for its pickers again when the server changes.
- One look for every checklist: groups fold (one open at a time, the others show their ticked
  rows) with the ticked count and all / none, in the Loot Editor popup and the Global Inventory
  window alike; the comparison is a list in both.
