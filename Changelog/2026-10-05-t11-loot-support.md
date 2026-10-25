# T11 loot support (ACECustom tier 11+ items)

Date: 2026-10-05
Branch: `feat/t11-loot-support`
RynthAi plugin version: 0.5.16-legacy-ui (the deployed build was 0.5.15-legacy-ui from
`fix/restore-sk-features`; the branch's interim 0.5.1 / 0.5.2 numbers would have collided with
existing `PreviousReleases` folders)

## Build 0.5.16-legacy-ui

- NativeAOT publish (win-x86): `-p:Version=0.5.16-legacy-ui`, staged at
  `D:\rynth-merge\stage-suite\RynthAi-0.5.16\` (17,372,672 bytes, file version 0.5.16.0).
- Deploy: the live 0.5.15 DLL is saved to
  `C:\Games\RynthSuite\RynthAi\PreviousReleases\0.5.15\`, then replaced with 0.5.16.
- Server tiers are capped at 25 for now, so the tier estimate is capped at 25
  (`T11Catalog.MaxTier`).

## Why

ACECustom's tier 11+ drops don't look like retail items to a loot engine:

- Every drop is renamed `T11 - <name>` and has no material.
- The appraisal strips the item-augmentation wield requirement (PropertyInt 158/159/160,
  `WieldRequirement.Int64Stat = 13`) and prints it as text instead
  (`Wield requires: 2,000 Item Augmentations`, plus Triune Weave and charm gates).
- Weapon grade (`- Weapon Grade: B+ (86% of max damage)`), Cast on Strike and the rolled
  zone modifiers (`Modifiers:` block, `- Damage Rating +41 [14-69]`) exist only as text.
  Weapons carry it in LongDesc, armor, clothing and jewelry in the Use string.
- A Gear* rating that comes from a modifier is removed from the appraisal's ratings, so
  `TotalRatings` and `GearDamage >= N` rules read 0 on T11 gear.
- The player's item-augmentation and charm counts aren't sent at login.

The change is client-only. Nothing changes on the server.

## What changed

### LootSdk (`Shared/RynthCore.LootSdk/T11/`)

- `T11ItemInfo.Parse(name, longDesc, use)` reads the server's exact text: grade, damage %,
  wield gates (any thousands separator), Cast on Strike, zone lock and the modifier block.
  Modifier lines get their value, roll band and roll %, and unknown server keys
  (`Modifier N`) are kept. Malformed text never throws.
- `T11Catalog` holds the ZoneModifiers catalog (key, name, Gear* rating), the weapon
  sub-grades (S = 16 ... F- = 1) and the tier estimate from the wield gates. The item-aug
  gate gives T11-T15 (2,000 = T11, 2,500 = T12, +500 per tier, frozen at 4,000 from T15).
  From T16 the server adds a Triune Weave gate of 500 x (tier - 15), which gives T16-T25
  (500 = T16 ... 5,000 = T25, capped at 25).
- `T11PlayerAugs` / `T11AugReportParser` read the `/aug` reply (item augmentations,
  Triune Weave, the four charms) and decide whether every wield gate is met.
- `T11Keys` adds virtual long keys in the block `0x52590000`, so they can't collide with
  real properties:

  | Key | Value |
  |-----|-------|
  | T11: Is T11 | 1 / 0 |
  | T11: Tier (est.) | 11-25 |
  | T11: Weapon Grade | 16 = S ... 1 = F- (named in the editor) |
  | T11: Damage % of Max | 0-100 |
  | T11: Wield Item Augs / Wield Triune Weave | the gate amounts |
  | T11: Can Wield | 1 yes, 0 no, -1 unknown (no `/aug` read, or not appraised yet) |
  | T11: Modifier Count / Modifier Ratings / Avg Roll % / Best Roll % | from the modifier block |
  | T11: Has Slot Special / Cast on Strike Count / Zone Locked | flags and counts |
  | T11 Mod: <name> | one key per catalog modifier (its rolled value) |

- `LootRuleText`: the T11 keys and NetherResistRating (377) are now in the long-key picker.
  The Use (14) and LongDesc (16) string keys and wield requirement 13 "Int64 Property" were
  added, and Weapon Grade gets a value table.

### RynthAi plugin

- `WorldObject.Values(int)` answers the T11 virtual keys. For T11-named items it restores the
  stripped keys when the live value is absent or 0:
  158 = 13, 159 = 9008, 160 = the item-aug gate, and 370-379 from the modifier lines.
  As a result, existing wield-requirement rules, `TotalRatings` and `Gear*` rules work on
  T11 gear without being edited. A value the item really has always wins.
- `T11ItemSupport` caches parsed text per object (re-parses only when the text changes,
  drops entries on object delete and logout) and holds the player's counters.
- The `/aug` reply is read from chat. The ILT Hub already requests it at login.
- New command `/ra t11`:
  - `/ra t11` shows the counters the loot rules use;
  - `/ra t11 augs <n|auto>` saves an item-augmentation override (`T11ItemAugsOverride`,
    -1 = automatic);
  - `/ra t11 refresh` sends `/aug`;
  - `/ra t11 item [0xId]` shows how the selected item was read and its T11 key values.
- `StringValueKey.Use = 14` added.

## Tests

- `Tools/RynthCore.LootSdkTests`: new `LootT11Tests.cs` covers the parser on server-format
  weapon and armor text, non-T11 and malformed input, tier estimates, the `/aug` reader and
  its time window, wield checks, every virtual key, the stripped-key fallbacks and the
  editor names. The wield-requirements length check is now 14. The T16 armor fixture uses
  the server's real gate (500 Triune Weave), and T16-T25 cases cover the Triune gate,
  the fallback without one, the T25 cap and a T25 weapon. Result: 8022 assertions, 0 failed.
- `Tools/RynthCore.RynthAiHostTests`: new `T11LootTests.cs` runs T11 items through
  `VTankLootEvaluator`, covering virtual keys, restored 158/159/160, TotalRatings from
  modifiers, live values winning, retail items untouched, and Can Wield from `/aug` and the
  override. All 6 pass. The suite's one failure (map bake against local dats) fails the
  same way without this change.
- The RynthAi NativeAOT publish (win-x86) builds with no warnings.

## Limits

- The tier estimate uses the server's default weapon-scaling table (ACECustom
  `WeaponScalingManager.BuildDefaults`). A server owner who retunes it changes the gates,
  but not the parser. The admin `/weaponscaling` command prints the live table, which a
  later change could read.
- Without a Triune Weave gate (before an ID, or on a server that disables it) T16+ items
  read as T15.
- A `/aug` line format change on the server needs a parser update. `/ra t11 augs <n>` is
  the fallback.
- Item text is only available after an ID. Before that, a T11 item reads as T11 (from its
  name) with unknown gates.
