# RynthAi 0.5.71 - missile crafting: no ammo wields at a launcher that isn't in hand

Date: 2026-10-15
Plugin: RynthCore.Plugin.RynthAi 0.5.71-legacy-ui (previous release 0.5.70-legacy-ui)

Silentkelpie stopped on the nav route about every 5 seconds today, about 600 stops an hour
against about 55 an hour on 10-14. Each stop was RynthAi/Arbiter StopCompletely() from missile
crafting. Missile Crafting was off in Kelpie's profile on 10-14 (14:39 backup) and on today.

## What went wrong

- Combat swaps launchers by element, for example Electric Bow with arrows, then Fire Crossbow
  with quarrels (13:25:12 and 13:25:37).
- The client keeps an item's old wielder and slot in its object description after the item goes
  back to a pack. A stowed bow still read wielded in the missile slot, and the crossbow in hand
  read slot 0, so the ammo check missed it.
- So crafting decided a bow was in hand with no arrows on. Every 5 seconds it used
  'Infinite Deadly Prismatic Arrow' (784 times in one session). The server ignored each wield.
- While crafting is busy, the plugin stops nav, and the use held autorun until it timed out
  (about 3.5 s of every 5).

## The fix (`Combat/MissileCraftingManager.cs`)

- An item the inventory scan found inside a pack is never counted as wielded. AC's container
  lists don't hold worn gear, so this beats a stale slot.
- Gear in hand whose slot reads 0 is recognised by its wielder. The check also looks at the
  cache-known items the scan leaves out; the scan merges worn gear only when the slot reads
  above 0.
- Ammo (filed as a missile weapon by the server) is never taken for the launcher. Wielded
  quarrels with no "crossbow" in the name used to read as a bow.
- An ammo wield the server ignores backs off. After the same item is sent twice inside 30 s,
  crafting leaves the ammo alone for 2 minutes and says so in chat and the log.
- When the check finds no ammo on, the engine log names the launcher it saw:
  `[RynthAi] MissileCraft: no <Arrow|Quarrel|Dart> wielded with launcher 0x... '<name>' (slot n); evaluating`.

## Tests

`Tools/RynthCore.RynthAiHostTests/Tests/MissileCraftingTests.cs` (new, 4 tests):

- a stowed bow that still reads wielded is not the launcher (Kelpie's state at 13:36);
- wielded quarrels are not taken for a bow launcher;
- an ignored ammo wield backs off after 2 tries;
- a bow in hand with its arrows in the pack still gets them wielded.

The first three fail on 0.5.70. The fourth fails there only on the new launcher log line.
Host tests: 1014 of 1015 pass. The one failure is the map bake check, which makes no checks
without the DAT files on this machine. Unit tests: 40 of 40.

## Notes

- Kelpie uses infinite arrows and quarrels, so Missile Crafting has nothing to make for Kelpie.
  Turning it off there is also fine.
- Version: `RynthAiVersionCommand.cs` HandWrittenVersion 0.5.70-legacy-ui -> 0.5.71-legacy-ui.