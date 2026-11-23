# Changelog 3.1.3 - AutoPercentConfirmation parity

Date: 2026-04-22

## Added

- Added P1 `AutoPercentConfirmation` parity controls:
  - `AutoPercentConfirmationEnabled`
  - `AutoPercentConfirmationThreshold` (1-100)
  - `AutoPercentConfirmationCommand` (default `/yes`)
  - `AutoPercentConfirmationDebounceSeconds`
- Added P1 ImGui controls for percent confirmation in the main `ub-Rythai` window.
- Added `/ub autopercent` command surface:
  - `status`
  - `on` / `off`
  - `threshold <1-100>`
  - `debounce <sec>`
  - `cmd <chat command>`

## Behavior

- Watches incoming chat for:
  - `You determine that you have a <n> percent chance to succeed.`
- When enabled, if `<n>` meets/exceeds the configured threshold, executes the configured confirm command.
- Debounce prevents rapid re-firing during repeated prompt sequences.

## Fixes

- `AutoTradeAccept` and `AutoPercentConfirmation` checks now run independently of the P2 chat filter toggle, so disabling message filtering no longer disables those P1 automations.
