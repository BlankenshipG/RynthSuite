# RynthAi: buffing drops a level for components, fellowship buffs (2026-10-20)

RynthAi 0.5.96-legacy-ui, ProductVersion 2026.10.20.5. Previous release: RynthAi 0.5.95
(2026.10.20.3).

## How buffing picks a spell (built-in list and buff profiles alike)

For each buff, RynthAi casts the highest level the casting skill allows, using the Buffing
section's tier lines (Buff Min Skill Level per tier). It drops to the best level the character
knows: a spell not in the spellbook is never cast. A mastery for an untrained skill is left out
of the built-in list. As skills and the spellbook grow, so does the level cast. A buff profile's
lines go through the same picker; a line with no tier ladder uses the best known spell of its
family, capped the same way. This was already so. What changed:

### Changed

- **Missing components drop a level, not the buff.** When the server refuses a buff with "You
  don't have all the components for this spell.", that level is passed over for 10 minutes (or
  until more scarabs or tapers are picked up), and the next lower known level is cast. With no
  castable level left, the buff is left out. Before, the whole buff was parked for the cooldown
  although a lower level was castable. The chat line now says "buffing with a lower level". Other
  refusals still park the buff as before.
  - This goes on the server's answer, not a check of the pack. portal.dat's scarab names don't
    match ACE's inventory names, and the same check in combat rejected every level of spells the
    character had the scarab for (it is switched off there for that reason).
- **New Buffing setting: "Use fellowship buffs in a fellowship"** (off by default). While in a
  fellowship, a buff with a fellowship version casts that version instead. These include
  Empowering / Vivify / Alacrity / Speed / Acumen / Volition the Conclave for the attributes, the
  Clutch spells for the defenses, the Chants for the magic masteries, the Wards for the protections,
  and the Winds and the Endless Well. It is the same family, so it replaces your own buff and
  buffs every member too.
  - The same rules apply: the highest level the skill allows that the character knows and that
    wasn't refused for components.
  - It is never a lower level than the self buff it replaces, which would weaken your own.
  - Solo, it never applies. It works for the built-in list and for buff profiles.
- **Buff profiles leave out spells with no duration.** Mana Boost restores mana at once
  (duration -1); as a profile "buff" it never showed as active and would be cast on every pass.
  It is skipped and logged once. Silentkelpie's `default.json` has Incantation of Mana Boost Self.

### Checked

- Silentkelpie: buffing off, profile `default.json` (68 Incantation spells, 3 unticked).
  Silentstorm: buffing on, the built-in list. The two cover the same buffs, except that the
  profile has Mana Boost (not a buff, above) and lacks Defender and Impenetrability, which the
  built-in list casts.

## Tests

- **RynthAi host tests** 1166/1168, 3 of them new:
  - a level refused for components steps down a level at a time (built-in ladder and a profile's
    family fallback), and more scarabs reopen every level;
  - fellowship versions apply only with the setting on and in a fellowship, are capped by the skill
    tier, are never lower than the self level, and a refused fellowship level falls back to the
    self buff;
  - a profile leaves out Mana Boost, and the built-in list has none.

  "map bake" and "mini remote" were already failing.
