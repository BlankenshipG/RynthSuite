# RynthILT 0.2.16 — Mini Remote follows RynthCore's server flag; Vaulted Dungeons settings save

**Date:** 2026-10-16  
**Kind:** Hot push (HandWritten `0.2.15` → `0.2.16`). ProductVersion `2026.10.16.24`.

## Problem

1. RynthAi's Mini Remote (ILT tabs, pet refill, crafting salvage) asks RynthILT whether this is
   Infinite Leaftide (`RynthILT.Hub` `IsIltWorld`). RynthILT never read RynthCore's server list,
   so the engine's verdict and this character's override (Options > Diagnostics > Server,
   `/rc server ilt on|off|auto`) never reached it: RynthILT's own world-name check decided
   instead. It only looked right while the two happened to agree.
2. Vaulted Dungeons: `settings load failed: Reflection-based serialization has been disabled`
   in the NativeAOT build (what the installer ships) — its settings never loaded or saved.

## Cause

1. `PollServerState` (upstream: every 2 s, `GetServerStateJson` → `IltSettingsModel.ApplyServerState`)
   was lost when the sk ILT was ported; `ApplyServerState` had no caller, so
   `IltEngineVerdict.Active` stayed null and `IltServerOptions.IsIltLikeWorld` fell back to
   `IsIltLikeWorldLocal`.
2. `IltVaultedDungeons` used `JsonSerializer.Deserialize<Cfg>(text, options)` (reflection).

## Fix

1. Restored `PollServerState`: at login (before the Hub starts) and every 2 s on the tick, parsed
   only when the engine's list changed; cleared at logout. The Hub, `/ra hub` and the Mini Remote
   now follow the engine's Active flag (Detected + override).
2. `IltVaultedDungeonsJsonContext` (source-generated, same camelCase / indented / no-null format).

## Deploy

Copied to `C:\Games\RynthSuite\RynthILT\` (previous kept as `.previous-2026.10.16.22`); loads at
the next logout / character select.
