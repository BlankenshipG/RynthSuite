# 2026.10.16.5 - Pet summon: 45 s essence cooldown, no false “pet out”

Previous: RynthAi ProductVersion 2026.10.16.4 (HandWrittenVersion stays `0.5.0-legacy-ui` on `sk`).

## Problem

Kelpie: combat summon sometimes stuck or failed after a pet died / despawned. A Use the
server refused (still on the retail ~45 s PetDevice cooldown, incline, busy, etc.) never
drops Structure and never spawns a pet. The old path still assumed a pet for ~10 s and
parked the essence only ~30 s — short of the cooldown — so the next try timed out again
and left gaps between resummons.

## Fix (PetManager)

- Park every essence Use for **45 s** from IssueSummon (ACE / Summoning wiki PetDevice CD).
- On action timeout with no charge drop and no live pet: treat as **failed Use** — clear
  `_assumePetActiveUntil`, do **not** restart the 45 s park from the timeout (that would
  wait ~53 s).
- If the assume window is open but the pet is gone (death / despawn), clear it so the
  cooldown can finish and resummon.
- Ignore Corpse / non-creature landscape objects named like the pet.
- “Not enough charges” still clears / shortens the Use park so refill can run.
- Host test: `pets: failed summon … waits the 45 s essence cooldown`.

## Files

- Plugins/RynthCore.Plugin.RynthAi/Combat/PetManager.cs
- Tools/RynthCore.RynthAiHostTests/Tests/PetRefillTests.cs

## Deploy

`dotnet publish` RynthAi `-p:Version=2026.10.16.5` → `C:\Games\RynthSuite\RynthAi\`
