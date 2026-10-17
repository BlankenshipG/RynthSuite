# 2026.10.16.2 - AutoStack: group by WCID, respect max stack

Previous ProductVersion: 2026.10.16.1.
HandWrittenVersion: `0.5.0-legacy-ui` (upstream sk policy; unchanged).

## Why

Silentkelpie kept getting server "not compatible for stacking" spam. AutoStack
grouped by display name, so different Coalesced Mana weenies (WCID 42517 vs
42518) were merge-attempted forever. It also tried merges that overflowed
MaxStackSize (e.g. 67+50 into a 100 cap).

## What

- AutoStack groups by weenie class id (Decal Type / WCID); name is only a
  cold-start fallback when WCID is unknown.
- Candidate pairs must satisfy `source + target <= MaxStackSize`.

## Files

- `Plugins/RynthCore.Plugin.RynthAi/Loot/InventoryManager.cs`

## Deploy

`dotnet publish` RynthAi with `-p:Version=2026.10.16.2` → `C:\Games\RynthSuite\RynthAi\`
(reload plugins / reinject).
