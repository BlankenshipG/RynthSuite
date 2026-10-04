# RynthAi 0.6.17 — AutoStack merge results

Previous release: 0.6.16 (typed item classification).

Requires RynthCore engine API v68 for the full fix (older engines fall back to
the previous grace-timeout behaviour).

## Problem

AutoStack logged a failure for nearly every merge. Off-thread stack counts read
as 1 (see RynthCore changelog "PWD int snapshot and merge results"), so
RynthAi kept asking the engine to merge into stacks that were already full; the
engine skipped silently and RynthAi only found out after a 10 s timeout.

## Changes (`Loot/InventoryManager.cs`)

* Stack counts are now real off-thread (engine snapshot), so full targets are
  no longer chosen in the first place.
* Each pending merge records the source and target counts at enqueue time.
  Success is either the source disappearing **or** units moving (source count
  dropped / target count rose) — partial merges no longer count as failures.
* While a merge is pending, RynthAi polls `GetMergeStackResult`:
  * `TargetFull` → the pair is put on a cooldown (no failure counted) and
    logged as "engine skipped … target already full".
  * `Failed` / `QueueFull` → failure registered immediately instead of after
    the 10 s grace window.
  * Anything else → existing grace-window check.
* New `SetCooldown` helper extends a pair's retry time without incrementing
  its attempt counter.

## Also fixed by the engine snapshot

AutoCram amounts, missile-crafting head/shaft counts and ExpressionEngine
item-count expressions all read stack sizes through the same path and now see
real values.
