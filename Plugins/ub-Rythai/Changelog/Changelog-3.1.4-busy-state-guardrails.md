# Changelog 3.1.4 - Busy-state guardrails

Date: 2026-04-22

## Added

- Added centralized chat command dispatch guardrails for P1 safety parity:
  - `BusyGuardrailsEnabled`
  - `BusyGuardrailsMinCommandGapMs`
  - `BusyGuardrailsBusyStateThreshold`
- Added `/ub guard` command surface:
  - `status`
  - `on` / `off`
  - `gap <ms>`
  - `threshold <n>`
  - `reset`
- Added P1 UI controls for guardrails and blocked-command counters.

## Behavior

- All plugin command dispatch now routes through one gate:
  - Login command queue
  - Periodic command queue
  - Login macros
  - Death logout
  - AutoTradeAccept
  - AutoPercentConfirmation
- Guard gate blocks dispatch when:
  - command interval is below configured min gap
  - host busy state is above configured threshold
- Block counters track busy and gap suppressions for diagnostics.

## Notes

- Guardrails default to enabled with conservative initial values.
- This step reduces overlapping command dispatch and prepares for later per-line diagnostics.
