# RynthAi 0.5.97: buffing never casts heals or dispels

Build 2026.10.20.6 (RynthAi `HandWrittenVersion` 0.5.97-legacy-ui).

## Changed

- Buffing (the built-in list, a buff profile, or a fellowship version of a spell) only casts
  spells that stay on. A self heal for health, stamina or mana (Heal Self, Revitalize, Mana
  Boost, Stamina to Mana and the other transfers) or a dispel (Evaporate / Extinguish / ... All
  Magic) is never cast as a buff, even when a list or profile names it and the character knows it.
  Those are left to the vitals (Heal At, Stamina At, Mana At).
- Buffs that boost are kept, Regeneration, Rejuvenation and Mana Renewal included.
- `BuffManager.IsLastingBuff` decides: the spell catalog's duration must be above 0 and the
  family must not be a dispel; a spell the catalog does not know is judged by name.
- The built-in list never named any of these, and the logs show none was ever cast. The only
  one seen was Mana Boost in a profile, already dropped in 0.5.96. This makes it a hard rule.

## Tests

- Host test "buffing: never a heal for health, stamina or mana, nor a dispel": Heal Self,
  Revitalize, Mana Boost, Stamina to Mana, Incantation of Heal Self and every dispel are refused;
  Strength Self, Blade Bane and the Blood Drinker aura still resolve.
