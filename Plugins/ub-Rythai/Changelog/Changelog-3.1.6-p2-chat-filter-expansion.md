# Changelog 3.1.6 - P2 chat filter expansion

Date: 2026-04-22

## Added

- Expanded P2 chat filter parity set with additional UB-style categories:
  - attack evades
  - defense evades
  - attack resists
  - defense resists
  - spell cast mine
  - spell cast others
  - spell expires
  - periodic healing
  - failed assess
  - kill task complete
- Added per-category counters for all P2 filter categories currently implemented.
- Added counter reset controls:
  - `/ub p2 reset`
  - `Reset P2 counters` button in the UI

## Updated

- `/ub p2 status` now reports the expanded category counters in addition to existing counters.
- P2 panel now includes toggle checkboxes for all newly added categories plus inline counter summaries.

## Notes

- This is an incremental parity step toward the full UB Mag-style chat filter matrix.
