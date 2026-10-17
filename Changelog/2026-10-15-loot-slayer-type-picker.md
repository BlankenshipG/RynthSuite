# Loot editor: pick Slayer Type by name

Date: 2026-10-15
Area: Shared/RynthCore.LootSdk + Loot Editor; in-game loot face (engine Picker filter)
Plugin: RynthCore.Plugin.RynthAi (HandWrittenVersion stays 0.5.0-legacy-ui on sk)

## Change

Make slayer loot rules easier to write without memorizing ACE ids:

- Long key **166** shows as **Slayer Type** (was SlayerCreatureType).
- Its value picker lists creature types **A–Z** (Olthoi, Virindi, …); the .utl still stores the ACE id.
- Pet essence keys use spaced names: **Pet Bond Level** (9053), **Pet Potency Stored** (9056).
- Float **138** shows as **Slayer Damage Bonus** (still often unset on ACE — prefer 166).
- Desktop Loot Editor: type-to-jump on the value combo; tip for “any slayer” (`>= 1`).
- Engine loot face: long pickers (12+ items) get a **Type to filter…** box so Slayer Type is searchable in-game.

## How to use

1. Add condition **Long Value Key =** (or `>=`).
2. Key → **Slayer Type**.
3. Value → pick the creature (e.g. Olthoi), or use `>= 1` for any slayer.

## Local install

- RynthAi + LootEditor redeployed with the renamed vocab.
- Engine hot-patch for the filterable picker (version bump in RynthCore changelog).
