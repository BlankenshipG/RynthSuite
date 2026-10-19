# RynthOracle: T11 ratings and a Mag-style Ratings view (2026-10-20)

RynthOracle 0.7.1, ProductVersion 2026.10.20.2. Previous release: RynthOracle 0.7.0 (2026.10.20.1).

## T11 ratings in Gear > Ratings

T11+ gear (ACECustom's zone loot) carries **modifier lines**, which are flat bonuses stamped on
the item. The appraisal shows them in a "Modifiers:" section of `- Name value [min-max]` lines: in
the Use text on armour, clothing and jewelry, and in the long description on weapons. A line that
is a gear rating is taken *out* of the item's appraised ratings so that it doesn't show twice. So
0.7.0's Ratings view missed exactly the ratings T11 gear gives.

- The Gear Check reads each item's "Modifiers:" section. The roll range is dropped, and the
  "Zone Locked (Power Reduced)" line is noted rather than listed.
- The nine rating lines are added to the item's ratings and to **Worn totals**: Damage, Damage
  Resist, Crit Chance (Crit), Crit Resist, Crit Damage, Crit Damage Resist, Healing Boost, Nether
  Resist and Max Health. A line is added only where the appraisal didn't send that rating, so older
  pieces that send both aren't counted twice.
- Every line shows under its item ("T11: Damage Rating +52, All Attributes +40, ..."), and a zone
  lock is noted.
- **T11 worn** totals the lines that aren't ratings:
  - summed across pieces: All Attributes, Max Health Pct, Life on Hit;
  - the highest single one worn (the slot specials): Fortify Vitals, Regeneration, Pct HP Damage
    (shown as its percent);
  - "yes" for the flags: Battle Mending, Cheat Death.

  Armor Level and Reinforced change only their own piece, so they have no total.
- Servers without these lines show none.

## Mag-style format

**Ratings** now opens in **Mag-style**: each item as one Mag-Tools ItemInfo line, the text Mag-Tools
prints when you identify an item (Mag-Plugins `Mag-Tools/ItemInfo/ItemInfo.cs`). It uses the same
order and abbreviations:

> Gold Heavy Crossbow (Crossbow), *set*, AL, CS FireRend, Tinks 3, 35.25-47, +12, +20%, +5%a,
> 10%md, *spells*, Wield Lvl 180, Diff 300, Craft 9, Value 12,345, BU 50, [D 3, CD 2]

- It uses the same spell filter: on loot, the cantrips and level 7+ weapon buffs, without the level
  1-6 noise. Banes and Impenetrability show only on unenchantable gear, whose fixed protections show
  as `[1.4/1.4/1.2/...]`. Items that aren't loot show every spell.
- Added to Mag's line: the item level after the set (`Lvl 12/50`), T11 ratings in the rating
  cluster, and `T11 [...]` with the modifier lines (and `(zone locked)`) at the end. Mag-Tools'
  "buffed values" (its own weapon damage maths) are left out.
- The rating cluster uses Mag's letters: D, DR, C, CD, CR, CDR, HB (healing boost), V (vitality / max
  health), plus NR, LR, PKD, PKDR, OP, OPR. Worn totals use the same letters.
- The Mag-style view lists every worn piece (and the pack gear with Include pack items), not only
  rated ones. Weapons are tinted by damage type and paragon pieces in tan. **Print to chat** writes
  the lines to your chat window.
- **Columns** switches back to 0.7.0's columns, which list rated items only. The choice is saved.
- Names come from ACE's enums: materials, weapon masteries and skills (for "Melee Defense 300 to
  Activate"). `Make-GearNames.ps1` now writes `materials.csv`, `masteries.csv` and `skills.csv`,
  and numbers implicit enum members the way C# does.

## Tests

- **RynthOracle.Tests** 564/564, 25 of them new:
  - T11 lines from the Use text and a weapon's long description; the zone lock; an unknown line;
    Max Health Pct not read as Max Health; no section, no lines;
  - modifier ratings added once; worn totals; the T11 totals (summed, best, yes);
  - Mag lines: Mag's order and numbers, the spell filter (loot and not), unenchantable armour's
    banes and protections, T11 at the end;
  - both Ratings formats through the engine's display-list parser.
