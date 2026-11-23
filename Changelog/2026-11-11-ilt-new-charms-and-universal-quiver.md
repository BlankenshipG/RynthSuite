# 2026-11-11 — ILT's new items: the levelled charms and the Universal Quiver

Silentkelpie: "look at acecustom add in the new items for ilt that where just added in".

Read from ACECustom upstream (rkroska/ACECustom master 8d2460ff2, 66 commits past our last merge a8f38c885).

## RynthILT (RynthServers): Mini Remote > Charms
Seven charms the registry didn't have, with the server's WCIDs and player flags (CharmAbilityRegistry,
PropertyBool):

| Charm | WCID | Flag | What it does |
|---|---|---|---|
| Split Arrow | 777700035 | 51201 | Levelled: a launcher fires extra arrows |
| Chain | 777700036 | 51202 | Levelled: hand-cast war arcs, bolts and streaks jump between monsters |
| Pandemic | 777700037 | 51203 | Levelled: a ticking void DoT spreads to nearby monsters |
| Improved Cleave | 777700038 | 51204 | Levelled: melee cleave reaches further and hits more targets; two-handers hit harder |
| Armor Breaker | 777700039 | 51207 | Levelled: a dual wielder's hits ignore a share of the armor left |
| Shield Arc | 777700040 | 51208 | Levelled: the shield guards a wider arc and shares cleave damage |
| Universal Quiver | 78780032 | 50061 | An empty ammo slot fires Infinite Deadly Prismatic ammo for any bow, crossbow or atlatl |

- Levelled charms go from level 1 to 100 and work in T11+ only, up to the zone's charm level cap. Their
  level belongs to the owner (the Prestige Coins put in). The item itself keeps CharmLevel 1, and only the
  appraisal says the level: "Charm [Level 37 (12% to 38)]", "Charm [Level 100 (max)]".
  - The Tier column now reads that text, out of 100.
  - A charm whose text starts "Charm [Level" counts as a charm even without a known WCID.
- They show in the Charms tab (carried / stored, ON / OFF from the appraisal or the player flag, Toggle) and
  in the server features list under Charms.
- Banking: "/b lum" and deposit-all also take the Bounty Reward Tokens and the Lum or Die Event Lum Tokens
  (1B / 3B / 5B) now. That's server-side; the note is in the bank commands' contract.

## RynthAi: the Universal Quiver in combat
When the charm is on (the player's HasUniversalQuiverCharm), the server fires a bow, crossbow or atlatl with
an empty ammo slot. Before, a launcher with no fitting ammo was never swapped to, and one in hand held its
attacks with "No quarrels for ...". Now, while the charm is on:
- A launcher with no fitting ammo is a candidate. Its element is prismatic (the target's best), unless the
  Items panel sets one.
- With the launcher in hand and the slot empty, it shoots (logged once). No pack ammo is wielded, since
  the server would use that up first and the quiver's ammo is free and top tier.
- Ammo of another launcher's type still goes into the pack first, so the slot is empty.
- Ammo that fits and is already on stays on (the server uses it first).
- Missile crafting holds off: there's no ammo to make or wield.

Not added: the Charm Infusion Gems, School Gems, the Sigil of Fury X deed, item bounties, Size Stones,
the Forge's salvage bags and the Paragon Collector NPC. Nothing in RynthILT or RynthAi lists or handles
those kinds of items yet.
