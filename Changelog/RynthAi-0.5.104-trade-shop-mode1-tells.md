# RynthAi 0.5.104 — Trade Shop Mode 1 (tells + trade window)

## Mode 1 flow

Mode 1 is tell-driven and uses the Rynth trade API end-to-end:

1. Buyer tells `!help` / `!list` / `!price <id|item>` / `!buy <id|item>`.
2. Prices come from the Trade Shop catalog + UB-IT conversion rates (`TradeShopRates`).
3. On `!buy`: tell the price alternatives, `TradeOpen` the buyer (or use an open trade), `TradeReset` + `TradeAdd` so the stock item appears in the trade window.
4. When partner payment covers that listing, `TradeAccept`.

Spontaneous trade opens greet with the help line and wait for `!buy` (no auto-sell on random payment).

## Mode 2

Unchanged: payment in the window → tell listing text → accept. `!list` / `!price` still answer.

## Commands

Buyer tells: `!help` `!list` `!price <id|item>` `!buy <id|item>` (`#3`, `id:3`, quoted name).
Seller: `/ra tradeshop …` as before.
