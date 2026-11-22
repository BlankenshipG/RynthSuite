# RynthOracle: the ILT Tracker's QB levels are ACECustom's QB Deeds

Silentkelpie, 2026-11-21: "look at acecustom ... should be called deeds based on total qb".

The first ILT Tracker had made-up "bonus levels": one every 50 QB, x0.25 quest XP each. Those levels are gone. The
section is now **QB Deeds**, taken from ACECustom (origin/master 8d2460ff2, `DeedsTiers.cs` and `Player_Deeds.cs`).

## How Deeds work in ACECustom

- There are 15 tiers, one per 1,000 account QB. Each tier adds to the ones before it, and all of them apply while Deeds
  are on (`/deeds on` / `/deeds off`).
- Deeds read the account QB: 1 per quest started on the account, 1 more once it is finished.
- The server can cap the tiers (`deeds_max_tier`). Some lines are live server settings: the damage step
  (`deeds_damage_step`, +2% at tiers 3, 6, 9, 12 and 15 by default), the Breath of Renewal chance, and Cheat Death.

## The tab

- **Header:** your QB and your tier, for example "tier 12 of 15", with OFF when Deeds are off. A progress bar and the
  QB still needed to reach the next tier.
- **The 15 tiers:** each tier's QB and what it gives. Unlocked tiers are green, the next one is gold, and the rest are
  grey. Each tier shows:
  - the server's own text, from RynthILT's quiet `/deeds list` read, when RynthILT has one;
  - otherwise ACECustom's table at the default settings, marked as such.
- **Buttons:**
  - Read /deeds (`/ilt deeds refresh`; it also runs once per session by itself).
  - Deeds on / off.
  - /rebuff and Writ, from tier 10.
  - Deeds window (`/ilt deeds show`).
- **XP multipliers:** the `/bonus` multipliers stay, with `/bonus` and `/top qb` buttons.
- **Bonus tracker:** the "finish the started ones" line now names the Deeds tier you would reach. The QB rule text says
  1 per quest started and 2 once finished.

The QB shown comes from the first of these that is available:

1. RynthILT's `/deeds` read.
2. RynthILT's `/qb` read.
3. A `/qb` count in chat.
4. The `/bonus` Quests multiplier.

## Data

- `Data/QbDeeds.cs`: the tier table, TierFor, Progress, and the damage step.
- `IltServerProfile`: parses the profile's new `deeds` object (RynthILT from 2026-11-21) into `IltDeedsInfo`. An older
  RynthILT has no `deeds` object, and the tab then falls back to the table.

## Tests

`QuestBonusTests`, now 18 checks, covers:

- the tiers;
- 12,345 QB is tier 12, 655 QB from tier 13;
- the damage steps;
- the profile's deeds;
- which QB comes first.
