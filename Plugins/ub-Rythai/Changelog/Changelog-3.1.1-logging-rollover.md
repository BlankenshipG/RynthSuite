# ub-Rythai 3.1.1 — logging to local files with rollover

## Summary

Adds built-in granular file logging to `ub-Rythai` with local rollover under the install `Logs` folder, plus configurable runtime log level from the plugin UI.

## Changes

- Added `UbRythaiFileLogger`:
  - writes to `AppContext.BaseDirectory/Logs/ub-Rythai.log`
  - rolls files at 5 MB
  - keeps the newest 10 rolled files
- Added `UbRythaiLogLevel` enum (`Trace`, `Debug`, `Info`, `Warn`, `Error`).
- Added persisted setting `UbRythaiSettings.LogLevel` with default `Info`.
- Added **Logging** section in the base plugin ImGui window:
  - level dropdown for minimum file log level
  - shows active logs folder path
- Routed automation/plugin operational logs through centralized `LogAt(...)`:
  - still sends to host log
  - now also writes to rolling file output based on configured level
- Added `/ub log status` to report active log level and local log folder path.

## Build / verification

- Target remains `net10.0-windows` (`RynthCore` plugin model, not net48 Decal).
- Verified with:
  - `dotnet publish -c Release`
  - no linter errors in `ub-Rythai/RynthCore.Plugin.UbRythai`
