# RynthAi 0.6.10 — SK-local unified branch + ILT Hub load fixes

**Date:** 2026-10-04
**Branch:** `SK-local` (now contains `main`, `feat/ilt-hub`, `SK`, and PR #2 docs)

## Branch integration
- `feat/ilt-hub` merged into `SK-local`: ILT Hub (Character · Pet · Banking · Gear · Games),
  0.5.2 diagnostics/security, 0.5.3 install paths, 0.5.4 tool launch buttons, AutoVendor,
  experimental plugins, and the aelrynth 2026.9.27.3 snapshot.
- SK work preserved: ub-Rythai plugin, VirindiTank profile import (`ProfileImport/`),
  `SuiteToolPaths`, profile folder selector, missile ammo rules / auto-equip, Missile Combat tab,
  Monster Editor USD import, Loot Editor active-profile launch arguments.
- `origin/SK` (April-base copy of the SK upload) recorded as superseded; its only extra
  (Loot Editor Ctrl+S) is already bound in the merged editor.

## Fixed
- **ILT Hub never created on late settings load.** When `OnLoginComplete` could not read the
  character name (Decal coexistence / off-thread pump), the deferred settings load in `OnTick`
  established `CharFolder` but never created the Hub, so `/ra hub` reported "not ready" and the
  launcher tile stayed hidden all session. Hub creation is now a single `CreateIltHub()` helper
  called from both paths, followed by `OnLoginComplete` on the late path.
- **Server-options probe gave up on an unknown world.** `IltServerOptions.Tick()` marked the login
  refresh done 6 s after login even when the world name had not been reported yet, so every
  feature bit stayed Unknown and the Hub stayed hidden. It now keeps waiting (up to 60 s) for the
  world name and only skips the refresh for a known non-ILT world or after the timeout.
- `CombatManager`: restored the `_lastEquipDiagAt` `[EquipDiag]` throttle field that the SK upload
  removed while its uses remained (SK-local did not compile without it).
- `LegacyUiSettings`: duplicate `UseNativeAttack` removed; ilt-hub's documented default (`false`,
  explicit-target combat path) is kept.

## Changed
- Dashboard editor launch uses ilt-hub's AOT-safe `ExternalTool` (ShellExecuteExW, open-or-focus)
  with SK-local's arguments: Monster Editor gets the `MonsterProfiles\<char>.json` path as arg 2,
  Loot Editor gets the profiles folder plus the active `.json`. `SuiteToolPaths` is the fallback
  when the installer location has no exe. The Looting-tab toggle no longer uses `Process.Start`.
- Loot Editor 0.1.2: install-path defaults and AutoVendor folder (ilt-hub) combined with the
  launch-argument profile folder (SK-local); the folder hint shows the active folder.
- ub-Rythai 3.1.10: project now resolves the RynthCore SDK from the RynthSuite plugin layout via an
  overridable `RynthCoreRoot` property (previous paths assumed the standalone ub-Rythai repo).

## Versions
- RynthAi `0.5.4` (feat/ilt-hub) / `0.6.9` (SK-local) → `0.6.10` (`0.6.10-legacy-ui`). The unified
  version is above both lines so neither install treats it as older.
- Loot Editor `0.1.1` → `0.1.2`.
- Monster Editor `0.1.4` → `0.1.5` (SK USD import + ilt-hub install paths; duplicate
  `Tmds.DBus.Protocol` package reference from the merge removed).
- ub-Rythai `3.1.9` → `3.1.10`.
