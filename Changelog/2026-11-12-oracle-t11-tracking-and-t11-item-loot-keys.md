# 2026-11-12 — Oracle: T11 Tracking tab; loot rules for ILT's new T11 items

Silentkelpie: "progress skills. attributes, and progress should still show for all, but the progress top
section should be hidden unless on leaftide. add a new tab for itl in oracle called T11 tracking move all
tracking of t11 items to that tab include bounties, infusion tracking, charms gems, and school gems etc. add in
loot rules for the new t11 loot items as well."

## Skills / Attributes / Progress
- All three tabs still show on every server.
- Off Infinite Leaftide, Progress shows only the retail attribute XP planner. The ILT parts are gone there:
  the infinite-attributes raiser, the augmentation and enlightenment planners, and the "need RynthILT" note.
  This change is in the RynthCore engine (SkillsFace.Progression), so it needs the full install build.

## RynthOracle: T11 Tracking (Infinite Leaftide only)
A new tab after Void. Refresh re-reads the packs, asks for the bounty slots (/bounty sync) and checks the gear.
- **Item bounties.**
  - The R1 / R2 / B1 / B2 slots come from the ItemBountyDisplay the server keeps on the character. Each
    shows what to kill and where, progress (kills / count, a bar), the reward and the kills to go.
  - Bounty items carried but not yet started are listed.
  - Buttons: Sync, /bounty, and Kill bounties (RynthCore's Bounties panel).
- **Levelled charms and infusion.**
  - Each levelled charm carried shows ON / OFF, its level and % to the next, the Prestige Coins put in and
    the coins to the next level, read from the charm's appraisal. Toggle uses RynthILT.
  - The Charm Infusion Gems carried are shown by denomination with their total coins. A charm's tooltip
    says whether that total is enough for its next level.
- **School gems.** Battlemage's Wrath, Nether Veil, Crashing Steel and True Shot: how many are carried, by the
  tier each was earned at.
- **Lum tokens.** Bounty Reward Tokens and Event Lum Tokens carried, with a "Bank them" button (/b lum).
- **T11 gear.** Moved from the Gear tab: the worn T11 totals and each T11 item's modifier lines, with its own
  Check gear button. On Infinite Leaftide the Gear tab now points here; on other servers it keeps the lines.
- Items the tab needs read (charms, school gems, bounty items) are appraised a few at a time, and each
  one is not asked about again for a minute.

## Loot rules for the new T11 items (LootSdk + RynthAi)
Three new RynthAi loot keys (VTank reads them as 0, like the other T11 keys):
- **T11 Item: Kind.** One of: Item bounty, School gem, Charm Infusion Gem, Levelled charm, Bounty Reward Token,
  Event Lum Token, Universal Quiver Charm.
  - Known by WCID. An item bounty is known by its description ("Use it on a ring or bracelet"), so it needs
    its ID.
- **T11 Item: Tier.** An item bounty's tier ("in Tier 15"; 0 for a Valley of Death / Thaelaryn Island one) or
  the tier a school gem was earned at.
- **T11 Item: Infusion Coins.** The coins one Charm Infusion Gem puts in (1 ... 5,000).

How to use them:
- "Add to loot profile" on one of these items ticks "T11 item is <kind>" and offers its tier or coins.
- The Loot Editor's Add rule checklist has the three rows under T11. For example, "T11 item is Item bounty"
  with "T11 item tier at least 15", or "T11 item is School gem".
- They are only answered on Infinite Leaftide, like every T11 key.
