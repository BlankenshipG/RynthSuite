# Loot editor: SlayerCreatureType and pet essence long keys

Date: 2026-10-15
Area: Shared/RynthCore.LootSdk (Loot Editor + in-game loot rule pickers)
Plugin: RynthCore.Plugin.RynthAi (HandWrittenVersion stays 0.5.0-legacy-ui on sk)

## Change

Named long keys for ACECustom / ACE appraisal properties used when looting
slayer weapons and combat pet essences:

| Key | Name | Notes |
|---|---|---|
| 166 | SlayerCreatureType | Value picker lists creature types (Olthoi, Drudge, …) |
| 9053 | PetBondLevel | Bond on combat essences after ID |
| 9056 | PetPotencyStored | Stored potency on combat essences after ID |
| float 138 | SlayerDamageBonus | Listed; often unset on ACE appraisal — prefer 166 / LongDesc |

Rules that already used raw key 166 continue to work; the editor now shows
"Olthoi (1)" instead of a bare number.

## Example rules

- Any slayer weapon: class Melee/Missile/Wand + `LongValKeyGE(166, 1)`
- Olthoi slayer: same + `LongValKeyE(166, 1)`
- Strong essence: name/WCID match + `LongValKeyGE(9053, N)` and/or `LongValKeyGE(9056, M)`

## Local install (2026-10-15)

- NativeAOT publish of sk RynthAi → `C:\Games\RynthSuite\RynthAi\`
  (previous live DLL saved under `PreviousReleases\0.5.72-legacy-ui`)
- LootEditor publish → `C:\Games\Rynthcore\Tools\LootEditor\`
  (previous files under `PreviousReleases\pre-slayer-keys-2026-10-15`)
- Reload with overlay **RL**, or restart the Loot Editor.
