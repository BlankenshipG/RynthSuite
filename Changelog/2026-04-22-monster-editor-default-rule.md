# RynthCore.MonsterEditor 0.1.2 — Default (catch-all) row and RynthAi JSON parity

## Summary

RynthAi always uses a monster rule with **Name = `Default`** as the **fallback** when no other row matches a target. The external editor now documents that, prevents renaming the reserved row, blocks a duplicate “Default” add, and **persists `Category` and `MatchExpression`** so hand-edited profiles are not stripped when you save from the tool.

## Changes

- `Models.cs`: add **`Category`** and **`MatchExpression`** to mirror `RynthCore.Plugin.RynthAi`’s `MonsterRule` JSON.
- `MainWindow.axaml`: top hint describing the **Default** row; **Type (Category)** and **Match (expression)** columns; **Name** is read-only for the `Default` row; slightly wider window.
- `MainWindow.axaml.cs`: `EnsureDefault()` inserts a full `Default` template (empty category/match, `DamageType = Auto`); **Add** rejects a second `Default` name.
- `EnsureDefault` behavior unchanged: keep one `Default` at row 0, dedupe if needed.

## Versioning

- `RynthCore.MonsterEditor` **0.1.2** (assembly/file **0.1.2.0**)
