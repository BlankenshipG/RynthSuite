# ub-Rythai 3.1.9 — ILT mode gate + banking port

Date: 2026-04-23

## What changed

- Added UB-ILT style ILT gating in `ub-Rythai`:
  - `LeaftideFeaturesMode`: `Auto | On | Off`
  - `LeaftideAutoWorldNames`: world-name list used by `Auto`
  - `LeaftideManualAllowBanking`: manual override that allows banking when mode is `On`
- Added new commands:
  - `/ub leaftide on|off|auto`
  - `/ub leaftide` (status with effective ILT + banking state and world match result)
  - `/ub bank status|refresh|transfer <p|l|k|mk|e|we> <amount> <target>`
- Ported core UB-ILT banking behavior into `ub-Rythai`:
  - `/bank` refresh dispatch with throttling
  - `[BANK]` line parsing for balances (pyreals, luminance, keys, coins)
  - transfer command builder for all bank currencies
  - optional periodic auto-transfer based on parsed balances and percent
- Extended ImGui UI with ILT + banking controls:
  - ILT mode selector and auto world-name configuration
  - banking enablement/status/readout
  - manual transfer controls
  - auto-transfer settings

## Why

- Lets the same `ub-Rythai` build work across multiple shards without hardcoding ILT behavior everywhere.
- Brings UB-ILT banking utility into the RynthCore/Suite plugin path with explicit safety gates and manual override.
