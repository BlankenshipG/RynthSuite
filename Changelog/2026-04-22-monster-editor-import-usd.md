# RynthCore.MonsterEditor 0.1.3 — Import Virindi Tank user profiles (.usd)

## Summary

The external **Monster Editor** can **import** Virindi Tank **user settings** files with extension **`.usd`** (macro profiles in `VirindiPlugins\VirindiTank`). The importer opens **SQLite**-backed files (current VT style), maps the best-scoring table to Rynth `MonsterRule` fields, and falls back to scanning the file for **vTank-style match expressions** when there is no readable table.

## Behavior

- **Import Virindi .usd** on the toolbar: file picker for `*.usd`, merge into the current list, then save.
- **Default** row: if the VT data includes a `Default` row, its combat fields are copied onto the Rynth `Default` row; no second `Default` is added.
- **Merge**: named rules and expression-only rules are skipped if a duplicate name or match expression already exists in the grid.
- **Dependencies**: `Microsoft.Data.Sqlite` (read-only) for database profiles.

## Versioning

- `RynthCore.MonsterEditor` **0.1.3** (file **0.1.3.0**)

## Note

- VT’s internal column names are not public; the SQLite path uses heuristics (table/column name scoring and hints). If import fails, try re-saving the profile in-game or from the VT UI; expression-only fallbacks are best-effort and mainly recover **MatchExpression** data.

## Previous

- `2026-04-22-monster-editor-default-rule.md` — 0.1.2 and related notes.
