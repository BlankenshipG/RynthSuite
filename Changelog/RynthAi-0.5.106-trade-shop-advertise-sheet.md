# RynthAi 0.5.106 — Trade Shop advertise, price sheet, multi-add

## Auto-advertise

Trade Shop window → **Auto-advertise**: pick the AC chat window (Trade / General / LFG / Fellowship / Allegiance / Local / Roleplay / Society), set frequency (30–3600 s), optional prefix, and **Advertise now**.

Commands: `/ra tradeshop advertise on|off|now|channel Trade|interval 180|prefix WTS`

## Buyer tells (queued)

Inbound tells are queued and answers are paced (~1.2 s) so AC does not drop lines.

- `!help` / `hi` / greeting → help + **exchange rates** + full **price sheet** (chunked)
- `!rates` → conversion rates only
- `!list` / `!sheet` → rates + price sheet
- `!price <slot|name|value>` — `#2` / `slot:2`, item name/SKU, or pyreal value (`250000` / `value:250000`)
- `!buy <…>` — same selectors (Mode 1)

## Multi-select → catalog

In Inventory: Ctrl/Shift-select items → right-click → **Add N to Trade Shop** (opens the shop window; listings start at 1p for editing). Also `/ra tradeshop addids <id,id…> [price]`.
