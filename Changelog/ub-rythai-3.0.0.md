# ub-Rythai 3.0.0 — 2026-04-22

## Summary

Introduces the **3.x** RynthCore plugin line **`RynthCore.Plugin.UbRythai`** under `RynthSuite/Plugins/ub-Rythai/`, separate from Decal Utility Belt **1.x/2.x** in `UB/` and UB-ILT in `ub-IT/`.

## Previous release line

- **Utility Belt ILT** (Decal): continues in `ub-IT` / `UB` with existing versioning (e.g. net48 1.9.x, net10 2.9.x). That assembly is **not** replaced by this plugin.

## What ships in 3.0.0

- Plugin loads in RynthCore; **ImGui** overview window (bar action toggle).
- Chat: `/ub help`, `/ub build`.
- Workspace folder `ub-Rythai/` at repo root with README describing port strategy.

## Next steps (not in 3.0.0)

- Port individual UB-ILT tools incrementally using `RynthCore.Plugin.RynthAi` patterns (world cache, chat hooks, `Host` APIs).
