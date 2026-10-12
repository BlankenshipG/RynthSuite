# RynthAi 0.5.47: environment-hit throttle, per-monster attack power

Base: build-comp-10-11 (0.5.46-legacy-ui). Version 0.5.47-legacy-ui. Pairs with the RynthCore
engine change of the same date (the settings rows and the Power column). An older engine works too:
the new settings keep their saved values, and monster rules keep their power.

## What was asked

When a missile or a spell hits the environment instead of the target, combat should stop and
adjust: either move closer or choose a new target. Missile and magic combat each get their own
choice. Monster rules should be able to choose reckless, variable or high attack power per monster.

## Environment hits (Combat/EnvironmentImpact.cs, CombatManager.EnvironmentImpact.cs)

How a hit on the environment is detected:

- **Missile.** ACE sends "Your missile attack hit the environment." whenever an arrow, bolt or
  dart hits a wall, the ground, or an object that isn't its target
  (`ProjectileCollisionHelper.OnCollideEnvironment`). In Missile mode, each such line counts one
  hit against the monster the server's attack is bound to.
- **Magic.** ACE sends no chat when a war or void projectile hits a wall. It only stops the
  projectile and removes it a few seconds later. So RynthAi follows its own projectiles:
  - Each single-target cast (bolt, streak, arc, blast) is watched.
  - Projectiles named like the shape ("Flame Bolt") that appear next to the player within 4 s of
    the cast are claimed for that cast.
  - Each one is followed until it stops (still for 250 ms after flying) or disappears.
  - The cast counts as one hit only when every projectile stopped more than 4 m from the target,
    at least 2 m nearer to the caster than the target is, and no hit, resist or evade line (or
    health drop) arrived within 1 s.
  - Rings and walls aren't judged, because they don't fly at the target.

The throttle trips once a target has taken **Hits to trip** (default 2) environment hits within
**Window** (default 20 s). Combat then releases the server's attack (a magic cast already in flight
finishes first) and does what was chosen for that kind of combat:

| Choice | What happens |
|---|---|
| Off (default) | Nothing is detected. Behaviour is unchanged. |
| Move closer | Faces the target and autoruns toward it, up to **Move closer by** (6 m), never nearer than **Closest** (3 m). Once already that close, it picks a new target. |
| New target | Skips this monster for **Skip target** (30 s) and fights another. |
| Move closer, then new target | Approaches once; if the shots still hit the environment, it switches. |

Safeguards:

- **The only monster in reach is never skipped.** It would be free to keep hitting the bot while
  combat idles. RynthAi moves closer instead when it can, and otherwise keeps attacking (logged).
- **The approach always ends.** It stops when it reaches its goal, after 8 s, or after 2.5 s
  without gaining 0.5 m (blocked). It also stops when the target changes or drops, when the macro
  stops, and when Think stops driving it for 1.5 s (another activity took over). Autorun is
  turned off each time. It never runs while a cast is in flight.
- **Movement and logging.** Movement goes through MoveAudit as `Combat/Approach`, so the action
  pacer can hold it. The stall watchdog is held during the approach. Every step is logged with an
  `[EnvImpact]` prefix.
- **Skip list.** The skip list is separate from the no-damage blacklist. The blacklist is cleared
  by any damage line, so an arrow still in flight landing would have undone the skip.

## Per-monster attack power (Combat/AttackPowerPlanner.cs)

`MonsterRule.AttackPower` sets the power bar for melee and missile attacks against the monsters a
rule covers:

- **Default:** the DEFAULT line's choice. On the DEFAULT line it means the global Melee/Missile
  Power settings (unchanged).
- **High:** 100% on every attack.
- **Reckless:** 80%, which is inside the band where Recklessness adds damage. The band is
  10-90% on ACE and 20-80% on retail.
- **Variable:** a new random power on each attack, between **Variable Min %** and
  **Variable Max %** (default 50-100).
- **Reckless + Variable:** a random power on each attack, kept inside both the Recklessness band
  (20-80%) and the Variable range. If the two ranges don't overlap, the whole band is used.

When Recklessness is untrained, Reckless falls back to High and Reckless + Variable falls back to
Variable, because a shorter bar would only cost damage. RynthAi sends a new attack request every
cycle, so Variable really does change from attack to attack. The first attack on each monster with
a non-Default choice is logged with an `[AttackPower]` prefix.

## Settings

- **Melee Combat tab:**
  - Variable Min % and Max %.
  - Missile Response.
  - The shared throttle numbers: Hits to trip, Window, Move closer by, Closest, Skip target.
- **Spell Combat tab:** Magic Response, with the same shared numbers.

The new settings are carried everywhere the existing ones are:

- the settings JSON (nullable in the engine payload);
- profile copy;
- meta `SetOpt` / `GetOpt` (ExpressionEngine), clamped;
- the RynthRemote clamp table;
- the VTank profile importer (rule copy).

Rules sent by an engine without the Power column keep each rule's power (matched by name and
expression). Rules saved before this change load as Default.

## Tests (EnvironmentImpactTests, 10 tests, 101 checks)

- The response decision for each choice, including when this is the only monster or the bot is
  already close.
- The hit window and the approach count.
- The "stopped short" geometry.
- Missile lines trip New target: the target is dropped, skipped in the scan and fought again after
  the skip; the next monster is locked.
- Move closer: autorun on, then off at the goal; it goes again on the next trip, gives up when
  blocked, and keeps attacking when close and alone.
- Off, and modes other than Missile, ignore the line.
- Magic: a bolt stopping 9 m out with no hit counts. A bolt reaching the target doesn't, and
  neither does a short bolt followed by a hit line. Two short bolts trip the response.
- An approach that Think stops driving, or a macro stop, turns autorun off.
- Attack power resolution for every mode, the DEFAULT line fallback, untrained Recklessness and
  reversed ranges.
- Power is carried over for an engine without the Power column.

Full host suite: 649 tests, 648 pass. The one failure is `map bake: 0x6346`, which was already
failing before this change and depends on the game data files being present.

## Not tested in game yet

The magic detection relies on how ACE handles projectile collisions (it stops the projectile, then
removes it) and on the projectile names. A lore-named projectile that doesn't end in
Bolt/Streak/Arc/Blast isn't claimed, so it is never counted (it can't cause a false trip).
