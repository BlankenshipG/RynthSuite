# Oracle Pets tab restored; Mini Remote Pet / Capture

Date: 2026-10-15
Plugins: RynthOracle 0.2.2, RynthILT 0.2.11, RynthAi (HandWrittenVersion stays 0.5.0-legacy-ui on sk)

## What was wrong

Softkelpie's RynthOracle **Pet Breeding** tab (commit 3826420) was dropped when the
breeding tracker moved into RynthILT 0.3.1. On Infinite Leaftide there was no Oracle
Pets home; breeding only lived under Mini Remote > Pets > Breeding.

## Change

### RynthOracle 0.2.2

- **Pets** tab again on Infinite Leaftide (`/ro pets`, `/ro breeding`).
- Draws RynthILT's breeding tracker through `RynthILT.Data` (same JSON as before).
- Links to Mini Remote summon roster and capture settings.
- Server identity tags Infinite Leaftide so the tab can show (world name / engine `ilt`).

### Mini Remote (RynthAi)

- **Pet** toggle: turns on combat auto-summon and summons the active combat essence
  from the Pets roster (ticked priority).
- **Capture** toggle (ILT): turns auto siphon-lens capture on/off; lens choice stays
  on Mini Remote > Pets; tooltip points at Oracle Pets for breeding.
- Character menu → **Pets** opens `/ro pets` on ILT.

### RynthILT 0.2.11

- Feature catalog locations updated: breeding → RynthOracle > Pets; capture → Mini
  Remote Capture. Detection / dynamic sections unchanged — ILT still owns server
  pet features and only shows them when the world/profile says so.

## How to use

1. Mini Remote > Pets: tick the combat essence you want active.
2. Mini Remote **Pet**: summon that essence and keep auto-summon on.
3. Mini Remote **Capture** (ILT): auto-siphon with the lens chosen on the Pets roster.
4. `/ro pets` or Character menu → Pets: ILT breeding, supplies, server rules.
