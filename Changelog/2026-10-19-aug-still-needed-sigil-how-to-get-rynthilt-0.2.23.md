# RynthILT 0.2.23: luminance still needed for augs, and how to get each sigil chain item (2026-10-19)

ProductVersion 2026.10.18.31. Previous release: 0.2.22 (2026.10.18.29). The Skills panel changes
are in RynthCore engine 2026.10.18.31; an older engine simply doesn't show the new parts.

## Augmentations

- **New STILL NEEDED row** at the bottom of the table:
  - Luminance: the plan's total luminance minus banked luminance (`/bank`) and character luminance.
  - Coins: the e-coins still short.
- **New line under the table**, which spells it out, for example:
  `Luminance still needed: ~11.68T   (plan 18.45T - banked 6.77T - character 0)`.
  When Lum per e-coin is set, it adds what buying the short e-coins costs on top
  (`with the short e-coins: ~...`).
- The luminance line now reads `Character luminance: X   banked (/bank): Y`. It used to say the
  banked luminance was "not used here".

## Enlightenment materials and the sigil craft chain

- **New "How to get" column** in both tables:
  - **Crafted items** list their recipe, with the crafts still to do:
    - Ascension Coin = E-coin + Ancient Yalani Trinket
    - Lifeblood Elixir = Lifebloom Resin + Necrolytic Extract
    - Lifebloom Resin = Life Splitter + Plant
    - Sigils = Lifeblood Elixir + Ascension Coin
  - **Items a vendor sells** list the trade (rates from the owner, 2026-10-19):
    - Enlightenment Tokens: 20 e-coins each (the existing Coins per token), and the total for the rest.
    - Ascension Coin: 100 Necrolytic Extracts each, and the total for all still to craft.
    - Necrolytic Extracts: the vendor trades 50 for 100 Trophies, with the Trophies on hand
      (counted from the pack as "Trophy" or "Trophies").
    - E-coins: their luminance price from Lum per e-coin, and the total for the short.
  - Raw items with no known vendor say nothing, rather than guess where they come from.
- **Editable rates** under the chain: Extracts per Ascension Coin, and the Trophy trade (extracts
  and Trophies). They are saved per character; 0 hides a trade.

## Tests

- RynthILT.Tests `SigilChainTests` "how to get each item":
  - the recipes with the crafts left;
  - the Ascension Coin price in extracts;
  - the e-coin luminance price;
  - the Trophy trade with the Trophies on hand;
  - 0 hides a trade;
  - "Trophies" counts as Trophy.
- The "move: ... redirect to RynthOracle" failure was already on the branch.
