# 2026.10.16.22 — sk-only work from 10-15 / 10-16 ported onto the test build

**Date:** 2026-10-16  
**Kind:** Hot push (test build). RynthAi `0.5.80` → `0.5.81-legacy-ui`, RynthOracle `0.3.3` → `0.3.4`,
RynthInventory `0.1.3` → `0.1.4`, RynthSort `0.3.1` → `0.3.2` (sk's).

## Why

A branch audit found features that only existed on `sk`, not in the test build or its
upstream merge. They are now cherry-picked onto `test/hotbuild-2026-10-16` (and merged into
`test/hotbuild-2026-10-16-on-aelrynth`).

## Brought over

From sk `6e40575`:
- Loot: slayer pet keys and the slayer-type picker (LootSdk `LootRuleText`, Loot Editor
  `LongValuePicker`, golden tests).
- Mini Remote Pet toggle helpers (`PetsTab.ActivateHudPet` / `DeactivateHudPet`).

From sk `eda93e9`:
- RynthOracle Skills / Attributes / Progress tabs (`SkillsTabs.cs`), Asheron's Lesser
  Benediction on the Augs tab, About tab command list.
- RynthAi autostack groups stacks by WCID; Mini Remote HUD / moved-tab links to Global Inventory.
- RynthInventory Global Inventory links; GInv and RSort off the top bar.
- (Its ILT server-interface restore and Handler ring evade were already in.)

## Kept from the test build

Newer test-side code wins where both changed the same lines: version strings, the Mini Remote
Options / Float buttons, RynthILT (already the live 0.2.15 build), and RynthInventory's Banking
tab (`/ginv bank` still opens it; sk's duplicate `bank` case dropped).

## Checks

All suite plugins build. Tests: RynthOracle 280, RynthInventory 252, RynthSort 676, LootSdk golden
tests pass. RynthAiHostTests 1058/1060: the two failures (map bake needs the dats; a Mini Remote
`/ra hub` help-line check) fail the same way without this port.
