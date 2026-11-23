# ub-Rythai

Workspace and **RynthCore** plugin for **Utility Belt / UB-ILT–inspired** automation on the **3.x** line. This repo is **.NET 10** only; it is **not** a .NET Framework 4.8 Decal assembly.

## .NET 10 vs .NET Framework 4.8

| Deliverable | Target framework | Host | Output |
|-------------|------------------|------|--------|
| **ub-Rythai** (`RynthCore.Plugin.UbRythai`) | **`net10.0-windows`**, win-x86, **NativeAOT** | RynthCore injected client | `RynthCore.Plugin.UbRythai.dll` → Engine `Plugins\` |
| **Utility Belt / UB-ILT** | **`net48`** (and net8 / net10 for other UB lines) | Decal | `UtilityBelt.dll` / `UBLoader` — see `UB/` and `ub-IT/` |

Do not add `net48` to this plugin project: it references **RynthCore.PluginCore** (modern .NET) and ships as an unmanaged **RynthPlugin** entry-point DLL, not a Decal add-on.

## How this relates to other repos

| Location | Role |
|----------|------|
| `UB/` | Upstream Utility Belt (Decal): net48 **1.x**, net8 **1.x**, net10 **2.x** (`UtilityBelt.csproj`). |
| `ub-IT/` | ILT fork: nested `utilitybelt.gitlab.io-ILT_Customizations/.../UtilityBelt` — Decal stack, UB-ILT docs. |
| **`ub-Rythai/`** (this repo) | **3.x** RynthCore plugin sources: `RynthCore.Plugin.UbRythai/`. |
| `RynthSuite/` | RynthAi and other RynthCore plugins (sibling repo). |

## Build (plugin)

Requires **.NET 10 SDK**, **x86**. `RynthCore` must be a **sibling** directory next to `ub-Rythai` (same parent folder).

```powershell
cd "D:\asherons call-int code\ub-Rythai\RynthCore.Plugin.UbRythai"
dotnet publish -c Release
```

Publish copies the DLL into `RynthCore\src\RynthCore.Engine\...\Plugins\` when those output paths exist.

## Adaptation reality

UB-ILT uses **Decal.Adapter**, Virindi views, WinForms, MoonSharp, VTank interop, etc. That does not run inside the **NativeAOT** RynthCore host. Features are re-implemented here against `RynthCoreHost` / **ImGui** as needed.

## Changelog

See `Changelog/` in this repo.

## Roadmap

See `ROADMAP-PORT-REMAINING.md` for remaining UB-ILT parity work by P1-P4 tier.
