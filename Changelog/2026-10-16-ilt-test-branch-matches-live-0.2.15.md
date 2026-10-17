# RynthILT 0.2.15 on the test branch — builds again, same code as the live install

**Date:** 2026-10-16  
**Kind:** Build fix (no behaviour change against the live `2026.10.16.17` ILT).

## Problem

RynthILT on `test/hotbuild-2026-10-16` didn't compile (`PollServerState` missing,
`IltHubUi.RenderQuestBonusSection` missing, `ServerSalvage` without `Order` / `AppliedInt`):
ports of older build branches had overwritten parts of upstream's newer ILT. The ILT running
in Games all day was built from the `sk` worktree instead, so a full installer from the test
branch couldn't ship ILT.

## Fix

- `Plugins/RynthCore.Plugin.RynthILT` now matches the `sk` worktree build that produced the
  live ILT 0.2.15 (`sk` `eda93e9` plus the aug Refresh current fix: `AfterExternalAugCapture`,
  `DrainAugRefreshAgain`, longer `/aug` timeouts, `IltHub.AugLoad` log).
- `Plugins/Shared/ServerPack/ServerPackInfo.cs` takes upstream's `ServerSalvage` record
  (adds optional `AppliedInt`, `Prepared`, `Order`, `AfterCap`, `StackGroup`, `StackCap`;
  only RynthILT links it).
- Every other `Plugins/Shared` file stays the test branch's: RynthAi, RynthOracle,
  RynthInventory, RynthSort, RynthChat, RynthNav and UbRythai build unchanged.

## Known

`Tools/RynthILT.Tests` still doesn't build on this branch (its tests expect upstream's
`ServerSalvageView` / settings parser); that predates this change.
