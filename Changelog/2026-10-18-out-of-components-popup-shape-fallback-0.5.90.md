# RynthAi 0.5.90: out-of-components pop-up, and attack spells fall back to what the character can cast (2026-10-18)

ProductVersion 2026.10.18.27. Previous release: 0.5.89-legacy-ui (2026.10.18.26).

## Why

Sk was still stuck after 0.5.89 was deployed, because the client had not loaded it. RynthAi loaded
once at 20:11 and was never reloaded, so the old code was still running:
- Lightning Arc V was refused for components 101 times in 133 casts, and never dropped a level.
- Heals and attacks were sent into each other's gestures, giving "too busy" streaks of 25 to 35,
  `StopCompletely` resets and 8 s pauses.
- No warning appeared.

0.5.89 fixes the loop: it passes over a spell refused for components, and it blames each refusal
on the cast it answers. This release adds what was asked for next.

## Added

- **"Out of components" pop-up**, a small window at the top of the screen:
  - it names the spell and what RynthAi does instead (a lower level or another spell it can pay
    for; for a heal, a lower level, a kit or a potion);
  - it says to restock scarabs and tapers;
  - OK closes it, and it closes by itself after 30 s;
  - the same spell pops up at most once every 2 min.
  - The chat lines stay as well.
- **Attack spells fall back to what the character can cast.** The shape set on the Monsters tab
  still comes first, at the highest level the character **knows and has components for**. When no
  level of that shape can be cast (none known, or all refused for components), another shape of
  the same element stands in, in this order:
  - Bolt, Arc, Streak, Blast;
  - then the element's **Volley** (Flame / Frost / Lightning / Acid / Blade / Force / Bludgeoning
    Volley, levels I to VI where they exist).

  Chat says once which spell stands in.
- **What the fallback never picks:**
  - an unknown spell;
  - another element's spell;
  - a blocked arc (the arc that gave way to an obstacle is never used as its own stand-in).
- **Rings** are unchanged: they are still the "pack in range" override and are never a stand-in.
  A ring the character doesn't know already falls back to the configured shape.

## Tests

RynthCore.RynthAiHostTests:
- a new test covers Arc V out of components going to Arc IV, every Arc out of components going to
  Bolt, only a volley known, nothing castable, and another element never standing in;
- "Arc with no arc known" now expects the element's Bolt instead of no attack.

The "map bake" and "mini remote" failures were already on the branch. RynthCore.RynthAiTests: 44 of 44.

## Note

The client must load the new RynthAi (relog, or restart the client) before any of this, or
0.5.89, takes effect.
