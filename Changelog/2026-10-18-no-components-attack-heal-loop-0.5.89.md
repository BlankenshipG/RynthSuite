# RynthAi 0.5.89: no more casting spells the character has no components for (2026-10-18)

ProductVersion 2026.10.18.26. Previous release: 0.5.88-legacy-ui (2026.10.18.12).

## What happened (Sk, 2026-10-18 20:23 onward)

Sk "wasn't attacking in combat mode". He locked targets, then spent his time on spells the server
refused with "You don't have all the components for this spell.":

- **Attack spell:** Flame Arc II was refused **245** times. Each refusal logged "Marked unresolvable
  -> tiering down to an alternative", and Flame Arc II was picked again anyway.
- **Heal:** with health under Heal At (55 to 60%), Heal Self V was tried every 0.3 to 0.4 s and
  refused every time (96 times).
- **Effect on attacks:** those refusals built "too busy" streaks (22 back-offs of 8 s), which held
  every cast, attacks included. Only 2 attack spells landed in that time.

An earlier 4-minute stall (20:26 to 20:30) was not this. Sk was in peace mode and the monsters
around him couldn't be attacked (the engine counted 1 attackable object out of about 190).

## Why

1. **Combat's "unresolvable" mark doesn't hold a known spell.** It was made for spells a character
   doesn't know: every known-spell refresh purges the known ones from it, and the combat picker
   doesn't read it at all. Combat's prediction checks only the scarab, not the tapers, so Flame
   Arc II always looked castable.
2. **Refusals were blamed on the wrong spell.** AC's refusal doesn't name the spell. A Flame Arc II
   refusal that arrived while Heal Self I was pending parked **Heal Self I**, the one heal Sk could
   cast (it healed him at 20:33).
3. **The above-tier heal ignored the no-components list.** With Heal Self I parked, the fallback
   ("no Heal Self at the tiers Life Magic allows now; trying Heal Self V (may fizzle)") picked
   Heal Self V even though Heal Self V was on that list.

## Fixed

- **One shared no-components list** (SpellManager `MarkNoComponents` / `LacksComponents`, 10 min):
  - A spell the server refuses for components is passed over everywhere: the attack picker
    (`TrySpellByName`, so the tier loop drops to a level or another spell he can pay for), the vital
    and buff pickers, and the above-tier heal fallback.
  - It is lifted early when the pack gains scarabs or tapers (bought or looted).
  - For attack spells, chat says it once: "No components for Flame Arc II: attacking with a lower
    level or another spell for 10 min."
- **A refusal is blamed on the last cast sent** (`ActionPacer.LastCastSpellId`, set for every cast).
  If a heal was sent after an attack, the heal is blamed, and the reverse; the other cast stays
  pending.

## Tests

RynthCore.RynthAiHostTests has three new tests:
- an above-tier heal refused for components is not cast again;
- a refusal that answers a later attack cast leaves the pending heal alone;
- the no-components list is lifted by new scarabs or tapers.

The combat hook misattribution test now reads the shared list. The suite passes except the "map bake"
and "mini remote" failures, which were already on the branch. RynthCore.RynthAiTests: 44 of 44 pass.
