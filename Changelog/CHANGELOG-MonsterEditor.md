# RynthCore.MonsterEditor

## 0.1.4 (2026-04-22)

- **Virindi `.usd` import — SCF `MyMonsters`:** The importer now reads line-oriented uTank2 user settings (format 8+ in `VirindiPlugins\VirindiTank`) before trying SQLite or binary expression scan. This matches production exports such as `MyMonsters` with typed `s` / `i` / `b` value lines. Reference profile: `--Silentkelpie_InfiniteLeaftide.usd`.

## 0.1.3 and earlier

- Prior behavior: SQLite-first with string-scan fallback for vTank match expressions.
