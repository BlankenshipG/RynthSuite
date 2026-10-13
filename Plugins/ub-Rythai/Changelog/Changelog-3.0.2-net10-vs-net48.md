# ub-Rythai 3.0.2 — .NET 10 vs net48

## Summary

Documents and enforces that **ub-Rythai** builds as **`net10.0-windows`** (RynthCore NativeAOT plugin). **net48** remains the domain of **Utility Belt / UB-ILT** in `UB/` and `ub-IT/` (Decal host), not this project.

## Technical

- `.csproj`: explicit comment block; no `net48` multi-target.
- Restored full plugin sources under `ub-Rythai/RynthCore.Plugin.UbRythai/` (settings store, chat automation, exports).
- `UbRythaiSettingsStore.cs`: added `using System;` for `Environment`.

## Previous release

- **3.0.1** — version bump prior to TFM documentation pass.
