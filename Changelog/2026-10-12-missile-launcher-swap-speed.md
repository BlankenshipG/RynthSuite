# RynthAi 0.5.50: Mixed-launcher swaps without the stance flicker, in about 1.5-2 s

Base: build-comp-10-11 at 0.5.49-legacy-ui. Version 0.5.50-legacy-ui. RynthAi plugin only.

## What was asked

Check upstream for conflicts first. Then fix two things in missile combat's swap between
launchers that shoot different ammo (bow, crossbow, atlatl):

- Send no stance request until the new launcher and its ammo are on. This ends the flicker.
- Wait less after the old ammo comes off, cutting the swap from about 5 s to about 1.5-2 s.

## Upstream check

Upstream `aelrynth/main` (snapshot 2026.10.12.2) has not touched `CombatManager.Ammo.cs`,
`WeaponSwapGate.cs` or `AmmoTracker.cs` since our merge base. In `EquipWeaponAndSetStance`
its only change is one line for the mana-reserve feature (`_chosenWeaponMode = desiredMode;`),
which doesn't overlap these changes. No conflict.

## What the log showed (RynthCore.30724.log, 2026-10-11 14:45:11-17)

A crossbow with quarrels was swapped to the Electric Bow with arrows. The swap took 6.4 s:

| Time | What happened |
|---|---|
| 11.35 | The quarrels are sent to the pack. |
| 11.43 | The server drops the character to peace mode. |
| 11.43, 12.57, 14.64 | RynthAi asks for Missile mode with the bow not on yet. The server bounces each request, and the stance flips Missile, peace, Missile, peace, Melee, Missile. |
| 14.37 | The bow is wielded, 3.0 s after the quarrels came off. |
| 17.36 | The arrows are wielded, 3.0 s after the bow. |
| 17.80 | The arrows are confirmed in. |

Each 3.0 s gap is the weapon-swap gate's full interval. The gate releases early only when no
action RynthAi sent is still waiting for the server. A bounced stance request waits up to its
2 s limit, so the gate never saw the server idle. The ammo move was also recorded as a pending
item move, which always forces the full 3 s. The 2 s equip pacing was stamped at the dequip too.

## What changed

- **No stance request mid-swap** (`CombatManager.cs`, the not-yet-wielded branch, and
  `LauncherSwapHoldsStance` in `CombatManager.Ammo.cs`):
  - The Missile request is held while the old ammo is going into the pack, while the launcher's
    wield is in flight, while its ammo follow-up is armed, or while the wrong ammo is still on.
  - The hold is logged with the equip diagnostics as `[EquipDiag] stance held: ...`.
  - Once the launcher reads in hand, the wielded branch sends the stance after the ammo is on,
    as it already did.
  - Thrown weapons, and launchers whose ammo type isn't known, are unchanged.
- **Shorter wait once the ammo is off** (`PrepareAmmoFor`):
  - When our own dequip has read off for 400 ms (`AmmoOffSettleMs`), the swap gate is told the move
    is done (the new `WeaponSwapGate.MoveDone`). The launcher then goes on the gate's 400 ms
    floor once the server is idle, not on its full 3 s.
  - The dequip no longer stamps the 2 s equip pacing.
  - The server's own drop to peace mode is still waited out by the combat tick's animation gate.
  - Weapon and shield pack moves keep the full 3 s. They have an unwield motion that nothing
    reports the end of.
- Without the bounced stance requests, the server reads idle soon after the launcher lands, so the
  ammo follows on the gate's floor too.

Expected swap: the ammo off at about 0.1 s, the launcher at about 0.5-1 s (after the stance-out
animation), the ammo at about 1-1.5 s, then the stance. That's about 1.5-2 s in all, down from
5-6 s.

## Tests

- New `LauncherSwapTests` (4 tests), built on the logged swap:
  - No Missile request until the launcher and its ammo are on, from Missile mode and from peace
    mode.
  - The launcher at 0.6 s and the ammo at 1.1 s on the swap gate's clock (they were 3.0 s and
    6.0 s).
  - `MoveDone` releases only the noted item.
- `AmmoTests`: the rig turns the settle off (its ticks don't use a clock), and bow-to-crossbow now
  checks that the settle holds the first tick after the arrows read off.

Full host suite: 660 tests, 659 pass. The one failure is `map bake: 0x6346`, which needs the game
data files on this machine, as before.