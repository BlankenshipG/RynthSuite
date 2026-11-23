# RynthILT 0.2.13 - Restore RynthILT.Server for Oracle / Mini Remote readers

Previous: HandWritten 0.2.12, ProductVersion 2026.10.16.4.

## Problem

Oracle (and anything else that polls `RynthILT.Server`) showed Infinite Leaftide
as detected — RynthCore's server pick is correct — but never received feature bits,
fellowship rules, or the `/qb` list from RynthILT. Mini Remote Hub tabs still worked
(`RynthILT.Hub`); charms/bank use `RynthILT.Data`. The feature-profile channel was
gone.

## Cause

When `RynthServer.Profile` (identity + salvage) was added, `ServerInterface.cs` and
the Hub's `ProfileJson` rebuild were dropped from `RynthPluginQueryInterface`. Oracle's
`IltServerLink` kept asking for `RynthILT.Server` and always got a null table.

## Fix

- Restore `ServerInterface` and export it again from `PluginExports.QueryInterface`.
- Rebuild `IltHubController.ProfileJson` about once a second (features, fellowship, qb).
- Restore `IltQuestBonus.Snapshot()` for the qb object in that JSON.
- Bank quiet-refresh defaults (AutoRefresh 0 + typed `/bank` may show) from the same
  hot push so Mini Remote open does not spam `[BANK]` while balances still update on
  login / Refresh.

## Files

- Plugins/RynthCore.Plugin.RynthILT/ServerInterface.cs (restored)
- Plugins/RynthCore.Plugin.RynthILT/PluginExports.cs
- Plugins/RynthCore.Plugin.RynthILT/Hub/IltHubController.cs
- Plugins/RynthCore.Plugin.RynthILT/Hub/IltQuestBonus.cs
- Plugins/RynthCore.Plugin.RynthILT/Hub/IltBanking.cs
- Plugins/RynthCore.Plugin.RynthILT/Hub/IltHubState.cs
- Plugins/RynthCore.Plugin.RynthILT/RynthILTPlugin.cs

## Deploy

`dotnet publish` RynthILT `-p:Version=2026.10.16.5` → `C:\Games\RynthSuite\RynthILT\`
(reload plugins / re-enter world). Confirm Oracle Fellowship / Quests qb and About
see RynthILT features after a few seconds.
