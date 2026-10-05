# RynthAi 0.6.11 / Monster Editor 0.1.6 — UB damage-insights seed for "Auto" damage type

**Date:** 2026-10-04
**Branch:** `SK-local`
**Previous releases:** RynthAi 0.6.10, Monster Editor 0.1.5

## Added
- **Monster Editor: "Import UB damage insights"** toolbar button (`UbMobSeedImport.cs`).
  - Reads UtilityBelt's `mob_damage_insights.ldb` (LiteDB 5.0.21) from
    `Documents\Decal Plugins\UtilityBelt\InfiniteLeaftide\` — the shared server database plus every
    per-character database one folder below it. Asks for the folder when that path has no database.
  - Each database is snapshotted (with its `-log.ldb`, if present) to a temp folder before opening,
    so a running UB session is never touched.
  - UB pushes character hits into the shared database every 12 hours, so the sources overlap: for
    each monster the single row with the most hits is used (no summing / double counting).
  - Damage types need 20+ hits to be ranked, and a monster needs two or more ranked types to be
    written at all (one type says nothing about weakness). Normal damage and wcid 1 (the player
    weenie) are skipped.
  - Score per type = average damage vs the best average, blended 50/50 with UB's own effectiveness
    measure (peak hit vs the best peak). Mag-Tools rows have no peaks and use averages only.
  - Mag-Tools name-only rows are included unless a wcid row already covers that name.
  - Writes `<SuiteDir>\RynthAi\CreatureData\ub-mob-seed.json` atomically. Monster rules are not changed.
- **Shared seed schema** `Shared\UbMobSeed.cs` (`UbMobSeedFile` / `UbMobSeedEntry` / `UbMobSeedElement`),
  compiled into both the editor and the plugin.
- **RynthAi: `CreatureData\UbMobSeedStore`** — read-only, source-generated JSON (NativeAOT-safe),
  wcid lookup with name fallback, reloads when the editor rewrites the file (checked at most every 15 s).

## Changed
- **"Auto" damage type** (`CombatManager.GetPreferredElement`) now resolves in this order:
  1. the monster rule's explicit damage type;
  2. the weakest appraised resist from `creatures.json` (unchanged);
  3. **new:** the best-ranked UB seed element that the character can cast with the rule's spell
     shapes (Nether only when Void spells resolve, etc.);
  4. Fire.
  Seed picks are logged once per decision (first 20) as
  `[CombatCast] auto-element '<name>': UB seed <element> (score, hits, wcid)`.

## Fixed
- Learned "electric" weakness never matched a spell: it was capitalized to `Electric`, but the war
  spell tables key that element as `Lightning`, so Auto silently fell back to Fire. It now maps to
  `Lightning`.

## Not seeded (by design)
- Per-character weapon/element/tier damage rows, crit averages, casts-to-kill and HP-to-kill in
  `monster_damage.txt`: UB has no weapon, tier or kill data and mixes many characters of very
  different strength, so seeding those would corrupt kill prediction and weapon choice.
- Weapon selection for "Auto" is unchanged (spell element only).

## Data notes
- First run against the local InfiniteLeaftide data: 3 databases → 31 monsters by wcid +
  224 by name (Mag-Tools).
- The ranking reflects which damage types players used as much as true weakness (e.g. Banshee is
  ~99.99% Nether hits). Appraised resists always take priority over the seed.

## Build
- RynthAi `0.6.11` (`VersionPointer` `0.6.11-legacy-ui`): `dotnet build` and NativeAOT
  `dotnet publish -c Release` clean (no IL trim warnings).
- Monster Editor `0.1.6`: builds clean apart from the existing `SQLitePCLRaw.lib.e_sqlite3`
  NU1903 advisory (pre-existing, via `Microsoft.Data.Sqlite` 9.0.0).
