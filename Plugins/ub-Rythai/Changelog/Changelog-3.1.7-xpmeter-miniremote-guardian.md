# Changelog 3.1.7 - XPMeter + MiniRemote portal gems + Temple guardian phrasebook

Date: 2026-04-22

## Added

- **XP Meter (UB-ILT parity, RynthCore adaptation)**:
  - ImGui overlay window
  - Tracks deltas from `PropertyInt64.TotalExperience` (earned XP) and `PropertyInt64.AvailableLuminance` (LUM gains)
  - `/ub xpmeter on|off|reset|status`
- **Mini Remote portal gem quick use (UB-ILT parity)**:
  - ImGui button panel for the canonical UB portal gem names
  - Inventory BFS scan + `UseObject` when found
  - `/ub portal list` and `/ub portal use <gem name>`
- **Temple / Guardian logic (UB-ILT phrasebook parity)**:
  - Ported `TempleGuardianPhrasebook` mapping logic into `ub-Rythai`
  - `/ub guardian translate <text>`
  - Optional auto-chat translation when `EnableTempleGuardianTools` is on and `TempleGuardianAutoChat` is enabled
  - Optional **best-effort** auto hand-in pump toggle (`TempleGuardianAutoHandIn`) — see notes

## Notes / limitations

- UB-ILT `XPMeter` used a native client hook on int64 quality updates. RynthCore plugins instead poll `TryGetObjectQuadProperty` on the player, so updates are smooth but not hook-identical.
- UB-ILT guardian auto hand-in depends on UB-only tooling (`AutoVendor`, `UB_GiveItem`, vendor cache). On RynthCore, the auto hand-in path is **best-effort chat-command scaffolding only** and may be a no-op on shards without equivalent UB macros.
