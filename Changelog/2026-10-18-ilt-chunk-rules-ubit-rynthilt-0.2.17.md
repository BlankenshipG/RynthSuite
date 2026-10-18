# RynthILT 0.2.17: auto-chunk rules match ub-IT (2026-10-18)

ProductVersion 2026.10.18.20. Previous release: 0.2.16 (2026.10.18.13).

Auto-chunk was leaving aetheria and corrupted weapons alone even though they met the rules.
The Lugian Aetheria Hammer rules in `Hub/IltChunkClap.cs` are now checked against ub-IT's
(`AetheriaChunkGuard`, `Leaftide.IsValidCorruptedWep`, `CorruptedChunkGuard`,
`AutoChunkCombatBusyPolicy`) and match them.

## Fixed

- **Unlevelled Coalesced Aetheria.** RynthILT kept every Coalesced Aetheria that had no level
  (int 319 = 0) after appraisal. ub-IT treats that as normal for Coalesced pieces and chunks them,
  and RynthILT now does the same. A Coalesced piece that does have a level still gets the cap,
  and any other aetheria with no level is still never chunked.
- **Two-handed corrupted weapons.** RynthILT capped cleave at 1 for every weapon, so no
  two-handed corrupted weapon (cleave 2) was ever chunked. As in ub-IT, the limit is now 2 for a
  two-handed weapon (WieldSkillType 41 or WeaponType 11) that is not a thrust weapon, and 1 for
  everything else.
- **Chunk in combat.** With "Allow while in combat mode" on, the hammer still waited for the client
  to be idle, and a bot that is swinging never is. As in ub-IT v2.0.1059, being busy in combat mode
  no longer blocks the hammer when that option is on. Peace mode still waits for idle.

## Added (ub-IT parity)

- Aetheria: tinkered or imbued pieces are kept (ints 171 / 179).
- Corrupted weapons: a weapon with no loot-gen workmanship (int 105) is kept, as in ub-IT.
- Corrupted weapon keep rules, under the new "Corrupted weapon keep rules" node on the Gear tab.
  All are off by default, as in ub-IT:
  - Crushing Blow, Biting Strike, Armor Cleaving
  - any slayer, multi-strike, elemental damage bonus
  - any spell, max damage at least N, and a list of spell ids
- Aetheria chunks and powder are never hammer targets (they are clap material).
- Diagnostics: each item's skip reason is logged once (`chunk: keep 0x... 'name': reason`), and
  the status line tallies the reasons, for example "nothing eligible; not chunked: 3 kept for its
  C / CD / CDR rating".

## Unchanged

- RynthILT's C / CD / CDR keep switches for aetheria. They are RynthILT's own and stay on by
  default, so a Coalesced Aetheria that shows `[C 2]` is kept until the C switch is turned off.
- Not ported: ub-IT's "use loot profile" override, which matches VTank loot rule names
  ("Chunk Aetheria", "Chunk Corrupted"). RynthILT does not see the loot rule that kept an item.

## Tests

`RynthILT.Tests/AetheriaChunkTests.cs` has five new tests: the aetheria rules, item kinds, the
corrupted hard rules, the corrupted keep rules, and the busy gate. The Coalesced cap test is
updated. The stale `IltSettingsTests` / `ProfileTests` compile errors and the "move: ... redirect
to RynthOracle" failure were already on the branch before this change.
