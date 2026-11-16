# 2026-11-15 — Loot rules: "Slayer damage bonus" reads the weapon's text

Silentkelpie: "is the any slayer rule working for looting".

## What was wrong
- "Any slayer" (Slayer type, int 166, at least 1) worked. ACECustom sends the slayer type, and the item
  lines read it ("Shadow Slayer").
- "Slayer damage bonus at least 1.01" (float 138) never matched:
  - ACECustom never sends SlayerDamageBonus. In ACE.Entity's PropertyFloat it has no [AssessmentProperty],
    so it is left out of every appraisal.
  - AppraiseInfo writes the bonus only into the weapon's text, as "- Shadow Slayer: 1.5x Damage".
  - So float 138 read 0, and every rule with that condition failed on every weapon. In loot profile 0 that
    is every slayer weapon rule: 29-31, 39, 66-68 and the switched-off S3 and [Wpn C] rules.

## Fix
- New RynthCore.Loot.SlayerText.BonusOf: the best "<creature> Slayer: N.Nx" in an item's texts.
- RynthAi's item reader (WorldObject.Values for a float) falls back to it for key 138 when the appraisal
  has no value. It reads the item's description and Use text.
- LootItemFacts.Double(138) falls back to the T11 card's slayer line, for the editor and "/ra loot why".
- A value the server does send still wins. No other key changes.

Tests: LootSdkTests "Slayer" (the text, the fallback, the profile's rule shape); RynthAiHostTests "loot rule
match (11-15)" (the in-game evaluator keeps a Shadow Slayer, reads the bonus from either text, a sent value
wins, a weapon without one is left).
