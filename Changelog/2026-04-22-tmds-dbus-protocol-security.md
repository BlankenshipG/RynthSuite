# Changelog — Tmds.DBus.Protocol security bump (2026-04-22)

## Summary

- **Packages:** Added an explicit **Tmds.DBus.Protocol** **0.21.3** reference on Avalonia-based tools so the resolved version overrides the vulnerable **0.20.x** pulled in by Avalonia 11.2.3.
- **Reason:** NuGet audit **NU1903** — [GHSA-xrw6-gwf8-vvr9](https://github.com/tmds/Tmds.DBus/security/advisories/GHSA-xrw6-gwf8-vvr9). Patched backport line **0.21.3** matches Avalonia’s expected API surface without jumping to the **0.92.x** major line.
- **Projects:** `RynthCore.LootEditor`, `RynthCore.MonsterEditor`.

## User-visible effect

- Tooling restores cleanly without the high-severity advisory warning for these projects; runtime behavior unchanged for typical editor workflows on Windows.
