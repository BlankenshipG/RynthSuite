# RynthAi: a heal the engine refuses is no longer waited on (2026-10-20)

RynthAi 0.5.95-legacy-ui, ProductVersion 2026.10.20.3. Previous release: RynthAi 0.5.94
(2026.10.19.4).

## Why Silentkelpie didn't heal with his kit (2026-10-19, two deaths)

**18:20:07, the kit was used but too late.** He ported into a room of Banshees (18:19:58) and was
swapping to sword and shield when health dropped from over 80% to 65% in one tick. The Eternal
Health Kit went out 2 ms later. He died 1.26 s after that. A kit takes about 2.45 s on the server
(the heal animation; every kit that landed that session took 2.41-2.52 s), so this one could not
land in time. Nothing was refused; the damage outran the kit.

**18:21:12, the kit never went out, and nothing else was tried.** He respawned into the same room
(portal exit 18:21:05.9). The engine holds and refuses new game actions for 5 s after a portal, a
teleport or a respawn (`WorldTransitionState`, which protects the client while it loads the new
area). The kit at 18:21:09.4, at 72% health, fell inside that window. The engine refused it: the
heartbeat's `qrej` went from 307 to 308, and no "has no answer yet" pacing line followed. RynthAi
ignored the refusal. It marked the kit as in flight, and the vitals then wait up to 8 s for a kit's
result before trying anything else. No result could come, so nothing else was tried in the 3.4 s
before he died.

### Changed

- **Healing kits, stamina kits and potions** check whether the engine sent the use. A refused use
  is not marked as used: no kit wait, no potion cooldown, no heal counted. The vitals try again on
  the next tick, and the other heals keep their turn.
- A refused use is logged at most every 5 s: `Vitals: the engine didn't send <item> (it holds game
  actions for 5 s after a portal, teleport or respawn): trying again next tick.` It is counted in
  `VitalUsesRefused`.

### Not changed

- The engine's 5 s hold after a portal is unchanged. Inside it no kit, potion or spell can reach the
  server, so a character who arrives in a fight can still take damage for those seconds. With this
  fix, the first heal goes as soon as the hold ends instead of waiting out a kit that never went out.

## Tests

- **RynthAi host tests** 1163/1165. New "vitals: a kit the engine refuses (after a portal) isn't
  waited on": the refused kit isn't pending, the potion goes instead, and once the engine takes uses
  again the kit goes and is waited on. `FakeHost.UseObjectOnAccepts` can now refuse a use. "map
  bake" and "mini remote" were already failing.
