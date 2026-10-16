# RynthOracle 0.3.2 — Fellowship tab restores ILT dynamic features + /fship

## Problem

Oracle → Fellowship on Infinite Leaftide only showed retail status (or "Not in a fellowship").
The ILT feature rows (size limit, Zone Share, luminance share, Fellowship Blackjack) and the
`/fship` command buttons never appeared.

## Cause

The Fellowship tab had been slimmed to the upstream status list only. RynthILT's
`RynthILT.Server` profile and `IltFellowshipCommands` were still wired and polled, but
nothing drew them.

## Fix

Restore `DrawLeaftideFellowship`: feature states from the ILT server profile, plus the
ACECustom `/fship` buttons (create / invite / dismiss / landblock / list / leave / disband / help).

## Version

| Component | Version |
|---|---|
| RynthOracle HandWritten | **0.3.2** (was 0.2.9 on this branch) |
| ProductVersion stamp | **2026.10.16.17** |
