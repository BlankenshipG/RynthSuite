# Crafting tab phase 2: Trinkets, Rings chain, auto next batch (2026-10-12)

Base: 5af2417 (0.5.51-legacy-ui). Version 0.5.52-legacy-ui. RynthAi plugin only; needs no new
engine API (the craft-dialog answer uses API v81, already shipped in 2026.10.5.42).

## Trinkets (Crafting > Trinkets)

UB-IT's trinket imbues for Infinite Leaftide:

- Diamond, Ruby, Gromnie Hide and Pyreal, in that order. Each one goes on once; ints 384-387
  read 2 when it is on, and those rows are skipped.
- A trinket is jewelry named compass, goggles, scarab, watch, puzzle box or top (not topaz).
  The Jewelry tab leaves trinkets out, and the Trinkets tab scans only trinkets.
- **Tier** (Minor / Moderate / Major): the step uses a full bag of the material whose name has
  that tier word.
- **Prepare on Ammunition**: with no bag of the tier carried, a full raw bag is first used on
  that tier's Ammunition. The runner waits (up to 30 s) for the prepared bag, then applies it.
  At most 3 preparations are tried per trinket.
- Retail AC has no trinket tinkering; the tab says so in its Retail section.

## Rings (Crafting > Rings)

UB-IT's Ring 2 inscription chain:

1. Quill of Infliction on a Trophy (Royal Summons / Empyrean Trophy / Summoning Gem), which
   makes an Enlightened Quill of Infliction.
2. The Enlightened Quill on Ink of Partition, which makes the partition quill.
3. The partition quill on a Glyph (Bludgeoning, Lightning, Piercing, Nether, Corrosion, Frost,
   Slashing, Flame), which makes an "Inscription of ..." scroll.

- Each recipe has a run count (0-99). **Create** runs every Step 1, then every Step 2, then
  Step 3.
- Names match like UB-IT: case-insensitive, with the alternate names and one exclude
  (Step 1's quill isn't Enlightened, and Step 2's isn't the Partition one).
- **Scan** lists each queued recipe's ingredients as found, "(after Step 1/2)", or
  "(not found)".
- A combine is done when the tool or the target stack shrinks, or the product shows up. With
  no sign of it after 15 s the job stops instead of retrying, so nothing is crafted twice.
- A missing ingredient is looked for again for about 10 s (the earlier combine's product may
  still be arriving).
- **Inscription scrolls**: "Apply to selected" uses one scroll on the item selected in AC.
  "Apply all to the last Jewelry / weapon scan" pairs the scrolls with that scan's items the
  UB-IT way (scrolls by glyph, items by id).
- The tinker runner and the Rings chain never run together.

## Auto next batch (every tinker tab)

- Items a run finished are now left out of later scans this session. Leaftide weapon lines
  that don't show on the item can no longer go on twice by re-scanning. A "N finished this
  session - Forget" line brings them back.
- **Auto next batch**: when a batch ends, the next items are scanned, re-scanning while they're
  still being appraised. **and start it** starts them without asking again; it is off by
  default, so Start stays a manual click.
- Auto next batch stops after a batch that used no bag, so a pack of items that all lack
  salvage isn't walked batch after batch.

## Fixes

- The tinker runner never reached its "done" step: `Running` turned false as the last item
  finished. It now has an explicit job flag, so the batch-done status (and auto next batch)
  runs.

## Tests

`TinkerTests`: 4 new tests:

- trinket flags, order and the no-slot rule; tier bag, raw bag and Ammunition matching
- Rings ingredient names, alternates and excludes
- the queue's tier order and 99-run cap
- scroll glyph lookup and UB-IT pairing

Host tests: 674, 673 pass. The only failure is the known "map bake: 0x6346" test, which
needs the game data files.

## Files

- `IltHub/Crafting/TinkerCatalog.cs`: `TinkerBench.Trinkets`, trinket rows, flag ints 384-387,
  `TrinketTiers`, `TinkerMaterial.Prepared`.
- `IltHub/Crafting/TinkerPlanner.cs`: tier bag matching, `TierOf`, `IsRawTrinketBag`,
  `IsTierAmmunition`.
- `IltHub/Crafting/TinkerRunner.cs`: preparation step, finished-item memory, auto next batch,
  job flag.
- `IltHub/Crafting/RingChain.cs` (new): recipes, matching, queue, scroll pairing.
- `IltHub/Crafting/RingChainRunner.cs` (new): crafts the chain and applies scrolls.
- `IltHub/IltCrafting.cs`: Trinkets and Rings tabs, run options.
- `IltHub/IltHubState.cs`: `AutoNextBatch`, `AutoStartNextBatch`, `TrinketTier`,
  `TrinketPrep`, `RingRuns`.
- `RynthAiVersionCommand.cs`: 0.5.52-legacy-ui.
- `Tools/RynthCore.RynthAiHostTests/Tests/TinkerTests.cs`.