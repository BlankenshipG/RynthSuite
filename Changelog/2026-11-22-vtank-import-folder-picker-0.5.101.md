# RynthAi 0.5.101 — VirindiTank Import folder picker (+ Settings profiles)

Previous: RynthAi 0.5.100-legacy-ui (2026.11.22.42). ProductVersion **2026.11.22.43**.

## Silentkelpie: pick source folders; import VTank .usd as settings profiles

`/ra vtimport` and Settings → Game Client → VirindiTank → Import… open a window instead of
scanning immediately.

### Folders (each optional)

- Monsters (`.usd` / `monsters.json`) — merge into this character when the checkbox is on
- Loot profiles (`.utl`) → `LootProfiles`
- Nav profiles (`.nav`) → `NavProfiles`
- Meta files (`.met`) → `MetaFiles`
- **Settings profiles (`.usd` → Profiles)** — each top-level text `.usd` becomes a named
  settings profile under this character's `SettingsProfiles\<server>\<character>\` (MyMonsters
  + ring options on a copy of current settings). Does not switch the active profile. Name
  clashes get a `VT_n_` prefix; SQLite `.usd` is skipped.

Browse… uses the system folder picker. **Fill from VirindiTank default** fills all five rows
from `C:\Games\VirindiPlugins\VirindiTank` (or Documents) when present.

### Still available

- `/ra vtimport go [files]` — headless import from the default VT roots (no window; no Settings-profiles row)
