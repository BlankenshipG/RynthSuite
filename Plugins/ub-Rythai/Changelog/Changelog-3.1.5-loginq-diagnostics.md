# Changelog 3.1.5 - Login queue diagnostics

Date: 2026-04-22

## Added

- Added per-line login queue diagnostics in `ub-Rythai` automation.
- Each login queue dispatch attempt now records:
  - UTC timestamp
  - source line number from the login command text area
  - command preview
  - success/failure state
  - detail (`dispatched`, `dispatch blocked or failed`, or exception message)
- Added diagnostics status and history panel in the main UI under P1 Foundation.
- Added login diagnostics commands:
  - `/ub loginq diag`
  - `/ub loginq clear`

## Behavior

- Diagnostic history keeps the most recent 100 login queue attempts.
- History persists for the current runtime session and can be manually cleared.
- Existing `/ub loginq run` replay behavior remains unchanged.
