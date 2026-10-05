# RynthSuite local merge onto aelrynth 2026.10.5.1

Branch: `merge/aelrynth-2026.10.5.1` (local only, not pushed).

Upstream remote is `https://aelrynth.com/git/rynth/Rynthsuite`. The integration branch starts at upstream `53438c6` (release 2026.10.5.1) and brings in `SK-local`.

## What landed

- Upstream stays the base for shared files: combat, loot, navigation recovery, RynthLua, RynthNet, RynthOracle, RynthInventory, and the test projects.
- `SK-local`-only trees are in the branch: ILT Hub, floating HUDs, chat translation, item info, ground loot, nav overlay and breadcrumbs, RynthJuice, ub-Rythai, Changelog, and Docs.
- Those features are wired into upstream `RynthAiPlugin` (tick, chat, login, logout, `/ra` commands, and `RynthPluginRender`).
- RynthAi handwritten version is **0.5.1-legacy-ui** (previous: 0.5.0-legacy-ui).
- Monster Editor is **0.1.7** (previous: 0.1.6) so VirindiTank import and the UB damage-insights seed compile against the upstream monster schema, including `UseBlast`.
- Augmentation target inputs no longer throw when the server reports a level above the client cap.

## Build

`dotnet build` of `RynthCore.Plugin.RynthAi` succeeds against RynthCore upstream `c3e1548` (plugin API v76, release 2026.10.5.1).

The workspace RynthCore checkout is still `SK-local` (plugin API v69). Against that SDK the same project fails on `TradeState` / `TradeSide`, which arrived with the aelrynth snapshot (player trade is API v72). That failure is the engine being older than this suite snapshot, not a missing local file.
