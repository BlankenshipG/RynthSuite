# RynthAi: reload the launcher in hand after an abandoned swap (2026-10-19)

RynthAi 0.5.94-legacy-ui, ProductVersion 2026.10.19.4. Previous release: RynthAi 0.5.93
(2026.10.19.3).

## Fixed: standing in combat with a target and no attack (Silentkelpie)

Kelpie fought with several launchers and swapped between them by the target's weakness. At
15:24:33 combat started a swap from the Electric Atlatl to the Fire Crossbow and put the darts into
the pack. The target died before the crossbow was wielded. The next target wanted the atlatl, which
was still in hand, so no swap was needed. But the atlatl now had nothing in the ammunition slot.

From there until a recall at 16:55, combat locked target after target in peace mode. Nothing was
logged and nothing was sent; the log shows `mode=1 attack=cold` with `msSinceAttack` past 5 million.
The "already wielded" path saw no ammo and returned. The ammo wield after a swap only runs for the
launcher the swap was for (here the crossbow). The fallback the code relied on, an ammo crafting
pass, is no longer in RynthAi. So nothing ever put the darts back on.

### Changed

- **RynthAi, missile weapons.** When the chosen launcher is in hand with an empty ammunition slot and
  a stack that fits it is in the pack, combat wields that stack. This goes through the same tracked
  step as after a launcher swap: one wield in flight, confirmed in the slot, 3 tries, then a rest.
  It logs `[EquipDiag] reload: ... is in hand with no darts on — wielding a stack from the pack`.
- After a give-up or a held ammo wield, the reload waits 10 seconds before trying again, so a resting
  stack doesn't retry or log on every tick.
- An old-ammo dequip that was still recorded for the abandoned swap is cleared at the reload. Left
  set, it would also hold back the Missile stance request.

## Tests

- **RynthAi host tests** 1162/1164. New "ammo: a swap abandoned after the ammo came off reloads the
  launcher in hand": the arrows go into the pack for a crossbow, the plan goes back to the bow in
  hand, and the arrows are wielded again (once, the crossbow never). "map bake" and "mini remote"
  were already failing.
