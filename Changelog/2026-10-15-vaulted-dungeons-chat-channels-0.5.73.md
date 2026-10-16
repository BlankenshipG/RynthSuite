# Vaulted Dungeons Mini Remote tab + RynthChat ILT channels

Date: 2026-10-15
Plugins:
- RynthCore.Plugin.RynthAi 0.5.73-legacy-ui (previous 0.5.72-legacy-ui)
- RynthCore.Plugin.RynthILT 0.2.11 (previous 0.2.10)
- RynthCore.Plugin.RynthChat 0.3.1 (previous 0.3.0)
Engine: Settings schema adds Options > Navigation > Vaulted Dungeons (build with 2026.10.5.68)

## Vaulted Dungeons (RynthILT)

Port of the UtilityBelt VaultedDungeons.lua script into a Mini Remote ILT tab:

- Keeps the character in one Vaulted Dungeon via a tied Portal Recall Gem (first recall after 1 s out, then every 5 s).
- Optional Kill Client: no "You've banked N Luminance." system kill for N minutes (Advanced, default 2) → `/rc quit`.
- Per-character settings: `%APPDATA%\RynthCore\RynthILT\<world>\<char>\VaultedDungeons\settings.json`.
- Advanced: Auto Start On Arrival, Disable Meta/Nav, Repeat Attacks, Set Attack Power.
- Dungeons: Tyrant's Quarry, Tusgian Barracks, Wailing Cells, Chamber of Idols, Fiun Redoubt, Tumerok Tunnels.

Gating:

- ILT / ACECustom worlds only (same as Gear / Games).
- Options > Navigation > Vaulted Dungeons checkbox (off by default) shows the Mini Remote tab.

Commands: `/ilt vaulted [on|off|toggle|status]`, `/ra hub open vaulted`.

## RynthChat channels

ILT Trade / General (and other tagged channels) were landing on Other because ACECustom sends them as chat type 28 (0x1C) instead of 0x08/0x09. Classifier now:

- Maps type 0x1C to Channels.
- Prefers `[Trade]` / `[General]` / `[LFG]` / … prefixes when present.
- Always buffers lines even when an earlier plugin ate them for retail (so Hide/eat still feeds RynthChat).

## Files

- RynthILT: Hub/IltVaultedDungeons.cs (new), IltSections, IltHubController, IltHubUi, IltFeatureCatalog, RynthILTPlugin
- RynthAi: LegacyUiSettings, LegacyDashboardRenderer, MiniRemoteHud, SectionsManifest, LocalFeatures, version
- RynthChat: ChatClassifier, ChatBuffer, RynthChatPlugin
- Engine: UI/Data/SettingsData.cs (Navigation > Vaulted Dungeons)
