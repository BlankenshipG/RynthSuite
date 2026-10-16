# Changelog 3.1.2 - AutoTradeAccept parity

Date: 2026-04-22

## Added

- Added P1 `AutoTradeAccept` parity controls to `ub-Rythai` settings:
  - `AutoTradeAcceptEnabled`
  - `AutoTradeAcceptWhitelist` (regex list, comma/semicolon separated)
  - `AutoTradeAcceptCommand` (chat command fallback to trigger acceptance)
  - `AutoTradeAcceptDebounceSeconds` (re-fire guardrail)
- Added P1 ImGui controls under **P1 Foundation** for auto trade acceptance.
- Added `/ub autotradeaccept` command surface:
  - `status`
  - `on` / `off`
  - `debounce <sec>`
  - `cmd <chat command>`
  - `whitelist <regex list>`

## Behavior

- When enabled, incoming trade-related chat lines are parsed for common partner-name phrases.
- If the extracted partner name matches whitelist policy (or whitelist is empty), `ub-Rythai` invokes the configured trade-accept chat command.
- A debounce window blocks rapid repeat accepts to reduce accidental loops.

## Notes

- RynthCore currently does not expose a dedicated native trade-accept callback in the plugin host surface used here, so this parity slice uses a chat-signal + command-dispatch strategy.
- This is an incremental parity step intended to be safe-by-default and shard-command configurable.
