# RynthOracle 0.2.8 — Charms + Pet Breeding tabs restored on Mini Remote layout

Date: 2026-10-16
Plugin: RynthCore.Plugin.RynthOracle 0.2.8 (previous 0.2.7)
Related: RynthILT Hub tabs (Gear / Games / Vaulted only); RynthAi Mini Remote menu links

## What was wrong

A hotbuild from the sk worktree had reintroduced Mini Remote **Banking** and **Charms** ILT tabs (upstream `aelrynth/main` still had that older layout). The matching Oracle build lacked the Character-tab charms list and the **Pet Breeding** tab that local already had after the 2026-10-13 move.

## Fix

- Align sk Oracle with local: Character tab `DrawCharms`, ILT-only **Pet Breeding** tab, `IltServerLink` / Shared `RynthILT.Server` + Data APIs linked in the csproj.
- Mini Remote ILT tabs stay Gear / Games / Vaulted Dungeons only; Banking → `/ginv bank`, Charms → Oracle Character (unchanged local layout).

## Verify

1. Mini Remote on ILT: no Banking / Charms tabs; Gear / Games / (optional) Vaulted only.
2. Oracle Character: charms list with Toggle when RynthILT is loaded.
3. Oracle Pet Breeding tab on ILT; Mini Remote Character menu → Banking / Pet Breeding / Character.
