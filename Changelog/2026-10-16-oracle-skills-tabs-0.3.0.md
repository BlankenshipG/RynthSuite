# RynthOracle 0.3.0 / RynthAi — Skills tabs + buffing cleanup

Calver stamp with engine: **2026.10.16.1**. Previous Oracle handwritten: 0.2.9.

## RynthOracle 0.3.0

Adds **Skills**, **Attributes**, and **Progress** tabs. The engine paints the
former Skills panel body into those tabs (raise/train, attributes/vitals, ILT
planners). Commands: `/ro skills`, `/ro attrs`, `/ro progress`.

Augs / Character / Society stay as they were (no duplicate raise UI).

## RynthAi

Options → Buffing: removed the **Protections and Banes** tick grid. Spell buff
profiles own which prots/banes to cast. `DisabledBuffs` still loads for older
profiles. Mini Remote Character menu opens Oracle Skills / Attributes / Progress.

On standing branch `sk`, RynthAi `HandWrittenVersion` stays `0.5.0-legacy-ui`.

## Files

- `Plugins/RynthCore.Plugin.RynthOracle/Views/OracleView.cs`, `SkillsTabs.cs`
- `Plugins/RynthCore.Plugin.RynthOracle/RynthOraclePlugin.cs`, `OracleContext.cs`
- `Plugins/RynthCore.Plugin.RynthOracle/PluginExports.cs`, `Views/AboutTab.cs`
- `Plugins/RynthCore.Plugin.RynthAi/LegacyUi/LegacyAdvancedSettingsUi.cs`
- `Plugins/RynthCore.Plugin.RynthAi/Huds/MiniRemoteHud.cs`
