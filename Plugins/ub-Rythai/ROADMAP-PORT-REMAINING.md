# ub-Rythai Remaining Port Roadmap

This roadmap tracks what is still left to port from UB-ILT/UtilityBelt semantics into `ub-Rythai` on the RynthCore host.

## Current baseline (already in ub-Rythai)

- P1 baseline: login command queue, periodic command batch, login macro hooks, death logout hook.
- P2 baseline: selective chat filtering toggles, FPS policy, basic counters and status command.
- P3/P4 feature gates: persisted toggles and UI placeholders for staged rollout.
- Built-in rolling file logging and log-level controls.

## P1 - Remaining (foundation and safety)

- **AutoTradeAccept parity**: add robust, whitelist-driven trade acceptance flow.
- **AutoPercentConfirmation parity**: dialog recognition/confirmation pipeline with safe thresholds.
- **Busy-state guardrails**: centralize action throttling to prevent overlapping command dispatch.
- **Login command diagnostics**: per-line success/failure history in UI, not only log output.

## P2 - Remaining (high-traffic parity)

- **ChatFilter parity expansion**:
  - port the full UB Mag-style line set (26+3 categories), not current subset.
  - add per-category counters and reset controls in UI.
- **Window mover**:
  - implement save/list/restore/delete profile positions per world/account/character.
  - add `/ub window ...` command surface.
- **Tracker parity**:
  - corpse tracker persistence and summary panel.
  - player/profit tracker equivalents with export/import compatibility where feasible.

## P3 - Remaining (ILT gameplay systems)

- **Spellcraft queue execution**:
  - queue model + validation rules + cadence controls.
  - integration with inventory/object cache and action dispatch.
- **Temple/guardian tools**:
  - quest-state-driven actions and helper UI.
- **Fellowship games**:
  - blackjack/poker/trivia command handling and state.
- **Quest/economy modules**:
  - XP, bank/economy utilities, and character progression helpers mapped to available RynthCore APIs.

## P4 - Remaining (deferred/host-dependent)

- **VHS hotkey bridge**:
  - requires host/API strategy for key binding interoperability.
- **Decal HUD compatibility layer**:
  - evaluate if practical under RynthCore+ImGui; scope likely limited.
- **Legacy AutoPack profile import**:
  - parser + mapping from legacy formats into ub-Rythai native profile format.

## Architecture work still needed

- Move duplicated UB-port traces out of `RynthAi` into `ub-Rythai` (single ownership).
- Create shared port libraries (classification, trackers, command bridge) to avoid monolithic plugin file growth.
- Add profile/folder abstraction so nav/loot/meta/profile paths are configurable per character without hardcoded roots.

## Test/quality backlog

- Add automated smoke test checklist for each command family:
  - login queue, periodic queue, log config, filter toggles.
- Add regression test matrix for:
  - login/logout flows
  - character switch
  - missing-path handling
  - malformed profile files
- Add performance guardrails for per-tick work and logging volume.

## Suggested next milestone

**Milestone 3.2.0**
- Complete P2 chat-filter parity set.
- Implement window mover command/UI.
- Remove UB-port remnants from `RynthAi` and finalize repo boundary.
