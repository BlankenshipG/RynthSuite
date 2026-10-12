# RynthAi 0.5.49: Pets roster colored by damage type, as in UB-ILT

Base: build-comp-10-11 at 0.5.48-legacy-ui. Version 0.5.49-legacy-ui. It pairs with the RynthCore
engine change of the same date, which colors the Items panel's weapons the same way.

## What was asked

Color the Items list and the Pets list by damage type, following how UB-ILT (`UB\ub-IT`) does it.

## Why every pet was white

The roster already colored the first line of each pet by damage type. But it read the school only
from the captured damage type (9054) and DamageType (45) properties, and most ILT essences carry
neither. The school stayed empty, so the line was white. UB-ILT had the same problem and fixed it in
v2.0.1119 by reading the school from the essence name.

## What changed (IltHub\IltDamageTypeTint.cs, a port of UB-ILT's two classes)

- **School picking** (`PetDamageTypeTintPolicy.PickFlag`):
  - The captured damage type (9054) is tried first, then the DamageType mask (45), then the
    essence name.
  - Names are matched on whole words, including ILT synonyms: Incendiary, Blizzard, Shocked,
    Caustic, and so on. "ice" inside "Novice" doesn't count.
  - An elemental name beats a physical-only 9054. ILT reuses that property id for the
    min-summon aug, where the values 1, 2 and 4 read as Slash, Pierce and Bludgeon.
  - A combined mask is read elements first: Nether, Lightning, Acid, Fire, Cold, then physical.
- **Palette** (`LeaftideDamageTypeVtColors`), shared by UB-ILT's Items, Spell and Pets tabs:

| School | RGB |
|---|---|
| Slash | 255, 180, 120 |
| Pierce | 240, 200, 140 |
| Bludgeon | 220, 190, 140 |
| Slash/Pierce | 255, 160, 100 |
| Cold | 160, 210, 255 |
| Fire | 255, 130, 70 |
| Acid | 160, 255, 140 |
| Lightning | 255, 240, 120 |
| Nether | 200, 160, 255 |
| Unknown | white |

Lightning is yellow now, where the old roster colors made it purple. The school found from the name
is also shown at the end of the second line, as before for property-read schools: for example,
"F, Mut 0, Potency 100 (100 act +200), Fire".

## Tests (PetDamageTintTests, 2 tests)

- School picking: from the name only (with synonyms and whole-word matching), an elemental 9054
  over the name, the name over a physical-only 9054, the mask, a combined mask, and Slash/Pierce.
- The palette matches UB-ILT's values.

Full host suite: 656 tests, 655 pass. The one failure is `map bake: 0x6346`, which needs the game
data files on this machine, as before.