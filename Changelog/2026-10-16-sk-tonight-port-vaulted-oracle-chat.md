# SK port: Vaulted Dungeons, Mini Remote Options, Oracle, Chat

Date: 2026-10-16
Branch: sk (upstream standing)
HandWrittenVersion: RynthAi remains `0.5.0-legacy-ui` on upstream sk.

## Summary

Tonight's local Suite work ported onto standing `sk` for rynth release prep (surgical Ai/ILT wiring so sk API stays intact):

- **Vaulted Dungeons** Mini Remote ILT tab (`IltVaultedDungeons`), gated by Options > Navigation > Vaulted Dungeons (`EnableVaultedDungeons` in LegacyUiSettings / MiniRemoteHud / LocalFeatures).
- **Mini Remote Options** left-click opens RynthAi Settings (`/rc ui open Settings`); right-click keeps the section popup.
- **RynthChat** ILT Trade/General via chat type 0x1C + tag prefixes.
- **RynthOracle** Charms / Pet Breeding tabs and shared ILT server APIs (0.2.8-era).
- Loot slayer picker / pet-key helpers where they apply on sk.

## Enable Vaulted tab

1. Options > Navigation > Vaulted Dungeons (on).
2. ILT / ACECustom world with RynthILT loaded.
3. Mini Remote Hub tabs should include Vaulted Dungeons.

Commands: `/ilt vaulted [on|off|toggle|status]`, `/ra hub open vaulted`.
