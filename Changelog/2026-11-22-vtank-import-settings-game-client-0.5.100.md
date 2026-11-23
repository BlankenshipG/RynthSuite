# RynthAi 0.5.100 — VirindiTank import on Settings > Game Client (all servers)

Previous: RynthAi 0.5.99-legacy-ui (2026.11.22.40). ProductVersion **2026.11.22.42**.

## Silentkelpie: move VTank import off ILT Mini Remote

The VirindiTank import lived under Mini Remote / ILT Hub Gear (`vtank-import`), so it only
showed on Infinite Leaftide and only imported one `.usd` into combat rules.

### Now (every server)

- **Settings → Game Client → VirindiTank → Import…** (confirm first), or **`/ra vtimport`**.
- Copies from `C:\Games\VirindiPlugins\VirindiTank` (and Documents / LocalAppData VirindiTank folders):
  - `.utl` → `RynthAi\LootProfiles`
  - `.nav` → `RynthAi\NavProfiles`
  - `.met` → `RynthAi\MetaFiles`
  - text MyMonsters `.usd` / `monsters.json` → merge into this character's combat settings (ring options too when every profile agrees)
- Name clashes keep the existing Rynth file and write `VT_n_<name>`.
- `/ra vtimport files` copies loot/nav/meta only (no monster merge).

### ILT Hub

RynthILT Gear no longer hosts the importer; it points at Settings → Game Client / `/ra vtimport`.
The Hub section still exists for a single-file `.usd` preview if something else draws it.
