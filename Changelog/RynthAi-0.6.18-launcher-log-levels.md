# RynthAi 0.6.18 — Per-category log levels (launcher-editable)

Previous release: 0.6.17 (AutoStack merge results).

## What changed (`Diagnostics/RynthLog.cs`)

* Every trace category (`LogCat`) now has a level, like the key events already had:
  * **Off** — trace lines dropped.
  * **Trace** — trace lines go to `Logs\Diagnostics\Trace\<Category>_<date>.txt`
    (the old "traced" state).
  * **Info** — trace lines go to the trace file **and** the normal log (engine
    log as `[RynthAi:<Category>] ...` and the daily `rynthai_<date>.txt`).
    This includes key events set to Trace whose category is at Info.
* diagnostics.json gains a `Categories` list (name, level, description), rewritten
  on load with every category so the launcher can show and edit them.
  `Traces` is still written for compatibility; an older file with only `Traces`
  is migrated (listed categories → Trace).
* diagnostics.json is re-checked every 2 s (was 5 s) so launcher edits apply quickly.
* In-game toggles (`/ra trace`, Diagnostics tab) first adopt any launcher edit
  made since the last load, so they never revert the launcher's choice.
  Turning tracing on keeps a category that is already at Info at Info.
* Diagnostics tab marks categories at Info with "+log".

## Key events (also in this release)

* Named key events (`RynthLog.Event` + `LogEvents.Catalog`), each with its own
  Off / Trace / Info level persisted in diagnostics.json `Events`:
  ILT Hub login, world check, options refresh start/finish, window shown/hidden,
  commands, confirmations and logout. Login, world check, options refreshed and
  window shown default to Info, so they appear in the normal log.
* The ILT Hub call sites (`IltHubController`, `IltHubUi`, `IltServerOptions`)
  now log through these events instead of plain trace lines.

## ILT Hub remote command

* `ApplyRemoteCommand` handles `hub <args>` (e.g. `hub show`), used by the new
  ILT Hub button in the Avalonia RynthAi panel. Says "ILT Hub not ready" in chat
  before login.
