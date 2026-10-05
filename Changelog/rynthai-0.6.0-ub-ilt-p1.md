# RynthAi 0.6.0 (UB-ILT P1 foundation)

## Previous release

- **0.5.x** — legacy ImGui dashboard track (`0.5.0-legacy-ui` plugin string).

## Summary

Delivers **P1** items from `Plugins/RynthCore.Plugin.RynthAi/docs/UB-ILT-RYNTHAI-PORT-PLAN.md`: login-time and periodic **chat command queues** (UtilityBelt `CharacterLoginCommands` / `PeriodicCommandsTool` parity), Advanced Settings UI, and `/ra ub` chat controls.

## User-visible changes

- **Advanced Settings** → new tab **UB-ILT port**: enable login queue, initial delay + line gap, multiline command list; enable periodic batch, interval, gap, multiline list (`#` / `//` line comments).
- **`/ra ub help`**, **`/ra ub loginq run`**, **`/ra ub periodic status`**.
- Plugin version string **0.6.0-ub-ilt-p1**.

## Technical

- New `UbIlt/UbIltChatCommandAutomation.cs`; settings fields on `LegacyUiSettings`; `CopySettings` extended; `RynthAiPlugin` wires `OnLoginComplete` / `OnTick`.

## Docs

- Full port matrix and priorities: `docs/UB-ILT-RYNTHAI-PORT-PLAN.md`.
