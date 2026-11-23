# RynthAi 0.5.103 — Trade Shop (UB-IT conversion rates)

## Summary

Trade Shop is a priced catalog next to AutoTrade: list items with pyreal prices, accept payment in the trade window, then complete the sale. Mode 1 delivers stock from this character; Mode 2 is text / multi-account (tell the buyer, accept). The tool is ungated on every world. A banking hook appears only when the server hub reports this world supports banking payments.

## Pricing / conversion (from UB-IT InventoryControl)

- Listings stay priced in pyreals.
- Buyers may pay in Py, MMD (trade notes), Lum, LK, MK, EC, or WEC using the UB-IT EC conversion table defaults (`PyrealPerMmd=250000`, `ConvBuyEcMmd=4`, Lum 1000, LK 2, MK 1, WEC 10).
- Rates and accepted-currency flags are editable under Advanced Settings → AutoVendor/Trade → Trade Shop.
- Payment is accepted when any single accepted currency covers the listing, or when mixed payment reaches the pyreal-equivalent.

## Commands

- `/ra tradeshop [status|on|off|test|mode1|mode2|list|rates]`
- `/ra tradeshop add <pricePy> [name]` — uses the selected item when name is omitted; optional `sku=` / `acct=` / `desc=` tags

## Tests

- `RynthCore.RynthAiTests`: TradeShopRates conversion / payment coverage cases (wired into Run-AllTests via that project).
