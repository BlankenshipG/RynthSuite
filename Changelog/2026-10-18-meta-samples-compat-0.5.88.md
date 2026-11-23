# Meta samples: what 12 real metas needed (RynthAi 0.5.88)

RynthAi 0.5.88-legacy-ui (build 2026.10.18.12)

I audited 12 metas in `meta samples`:
- PistolPete 2.2, in its CH, EW and RB variants;
- S_BulbOfMornings, S_Caul, S_Colo, S_CountPhainor, S_Cows, and S_Deewain (two copies);
- Willow.

Each was loaded with RynthAi's own .met parser. Every chat command went through the real `/vt`,
`/mt` and `/ub` dispatchers, every expression function through the engine, and every VTank option
against the option map. All 69 expression functions they use, and every condition and action kind,
were already supported. These are what was missing.

## Fixed

- **`vtsetsetting[]` dropped VTank distances.**
  - **Where:** S_BulbOfMornings, S_CountPhainor and S_Deewain set their ranges with `vtsetsetting[AttackDistance,`0.025`]`.
  - **The bug:** the expression went straight to the settings, without the landblock-to-yards conversion that `/vt opt set` does. The attack range stayed at the 50-yard default instead of 6 yards. The same went for 0.2166 (52 yd) and the approach distance 0.0208 (5 yd).
  - **Now:** `vtsetsetting` and `vtgetsetting` work exactly like `/vt opt set|get`: VTank names, VTank units, and the special options.
  - **Reading back:** distances read back in VTank's units (6 yd reads 0.025), the way the meta wrote them.
  - **Unchanged:** `rasetsetting`/`ragetsetting` keep RynthAi's own names and units.
- **`/ub close corpse|chest`** (S_Caul, S_CountPhainor, S_Cows, S_Deewain): ub-IT's command that closes the open corpse or chest. It used to answer "Unrecognized /ub command" and the corpse stayed open.
- **Options that were stored but nothing read:**
  - `PetCustomRange` sets `CustomPetRange`. Landblock units become whole yards: 0.0625 is 15 yd.
  - `BlacklistMonsterTimeoutSeconds` sets `BlacklistTimeoutSec`.
  - `RebuffTimeRemainingSeconds` (VTank's spelling) sets `RebuffSecondsRemaining`.
  - `AutoAttackPower` on means automatic attack power. Off means the last `/vt setattackbar` value. It reads back as true/false.
- **`/ub <expression>`** (S_BulbOfMornings broadcasts `/ub bc /ub vtsetmetastate[State]`): a `/ub` line whose verb is an expression call is evaluated. ub-IT has no such command either.
- **`/vt deletemonster`** (S_Colo, leaving the arena) is accepted and logged once, instead of going to AC's chat parser as an unknown command. No RynthAi equivalent is known.
- **`/mt face` and `/ub face`** now behave as in ub-IT:
  - the heading may have decimals (`getheadingto[]` gives them), read the same on every PC;
  - it wraps (450 is 90);
  - navigation is held while turning, the turn is sent again every 100 ms until within 1°, and it gives up after 5 s;
  - a jump started meanwhile takes over.

  New: a coordinate (`/mt face 42.1N, 33.6E`, comma optional) faces that spot, using the same heading as `getheadingto[]`.

## Left as they are

- **`PetRangeMode`** (0/1/2) and **`TargetSelectMethod`** (2/3) are still stored without effect, until VTank's meaning of their values is confirmed. RynthAi has `TargetSelection` 0-3 (lowest health, closest, farthest, least turning).
- **`/vt opt setidlepeacemode false`** (S_Colo, twice) is a typo in the meta (no space); VTank rejects it too.
- **S_Colo's "Cancel Recall" button** is a VTank view, which RynthAi doesn't support. The recall goes ahead.
- **Files the metas need:**
  - PistolPete loads `LostRelics3.3`, which must be in MetaFiles.
  - The `.utl` loot profiles must be in LootProfiles.
  - S_Caul and S_Cows load nav routes they don't embed, so those must be in NavProfiles.

## Tools

- `RynthAiHostTests`: `MetaAuditTests` is an opt-in audit. Set `RYNTH_META_AUDIT=<folder of .met files>` and run with the filter `audit`. It writes `%TEMP%\meta_audit.txt`.
- `MetaSampleCompatTests`: 6 regression tests for the fixes above. The full suite has 1080 passed, and the 2 known failures are unchanged.
