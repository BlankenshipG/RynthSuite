# RynthAi 0.5.51: Crafting tab - armor, weapon, jewelry and item tinkering

Base: build-comp-10-11 at 0.5.50-legacy-ui. Version 0.5.51-legacy-ui. RynthAi plugin, with
RynthCore engine API v81 for answering the craft dialog (it works without it, see below).

## What was asked

Finish bringing UB-IT's gear section over: armor, jewelry, item and weapon tinkering. Each tab
tinkers what the connected server allows, with a Retail section and a section for the server's
own salvage (ACECustom / Infinite Leaftide). Move UB-IT's crafting tools that were on the Gear
tab to a new universal Crafting tab with a sub-tab per crafting type, opened from the Mini
Remote's Character menu and RynthCore's RynthAi Char button.

Choices made: the engine answers AC's craft dialog (Yes only at or above a percent set per
tab); the server section shows Infinite Leaftide's salvage plus rows the player adds per world;
this is phase 1 (trinkets, the rings inscription chain and auto next batch come later).

## What changed

- **Crafting tab** (`IltHub\IltCrafting.cs`, new): a universal Mini Remote tab (every server).
  - Sub-tabs: Armor, Weapons (Melee / Missile / Caster), Jewelry, Items, Chunking / Clap and
    Split arrows. The last two moved here from the Gear tab and note when the world isn't ILT.
  - Each tinker tab has filters (min chance %, max tinks, min free tinks, skip imbued; weapons:
    pierce rend for slash/pierce, rend last), then two sections:
    - Retail tinkering: stock ACE salvage with its effect. Tinks take a count (0-10); imbues
      and one-shot recipes are a checkbox.
    - Server tinkering - <world>: Infinite Leaftide's rows on ILT-like worlds, then "Your salvage
      for this server": rows the player adds (material, tink / imbue / once, tink slot or not,
      bag-name match, note), kept per world name.
  - Run options: answer AC's craft dialog, batch size (20), allow in combat mode.
  - Scan / Start (with a confirm) / Stop and a table of items: tinks, workmanship, plan, status.
- **Salvage tables** (`IltHub\Crafting\TinkerCatalog.cs`, new):
  - Retail, from ACE's RecipeManager: armor imbues, Steel and the protection tinks, Copper and
    Silver activations; weapon imbues, damage-type rend and stat tinks; caster tinks; the 12
    jewelry cantrip gems; item value, burden, mana, heritage, attunement and retain salvage.
  - Infinite Leaftide, from UB-IT's LeaftideImbueHelper: the weapon crit lines (Yellow Garnet,
    Turquoise, White Quartz, Abyssal-Touched Gems, Tourmaline, Wool), Tiger Eye on missile
    weapons, Leather after the 10 tinks, and the jewelry ratings (Silver, Copper, Green Jade,
    Tourmaline, Serpentine, Steel). The jewelry ratings use their own ints (370-376), not tink
    slots.
- **Retail chance** (`IltHub\Crafting\TinkerMath.cs`, new): ACE's GetTinkerChance: difficulty from
  the material, item and bag workmanship and the attempt multiplier, then the skill curve;
  imbues are a third; Foolproof bags are 100%.
- **Planner** (`IltHub\Crafting\TinkerPlanner.cs`, new): per item, in order: one imbue; tink
  rows cut to the free slots; ILT no-slot rows uncapped; ILT flag rows skipped once on; Steel +
  Serpentine 10 combined; ILT weapon Leather only once the item has 10 tinks; Moonstone only with
  mana, Leather only if not retained, Sandstone only if retained. Bags: full (100) only, Abyssal
  and Foolproof bags matched by name; with a minimum, the lowest-workmanship bag that reaches it,
  else the best chance.
- **Runner** (`IltHub\Crafting\TinkerRunner.cs`, new): one step at a time, re-planned from the
  item before each step. Waits for peace mode, not busy and the action pacer, arms the engine's
  answer (v81) and uses the bag on the item. The step ends on the craft chat line, the bag
  leaving the pack, a declined dialog, "cannot be used on", or a server error (busy retries up
  to 3 times). Without v81 (or with answering off) AC's own dialog shows and the runner waits up
  to 60 s for the player.
- **Gear tab** (`IltGear.cs`): now buff keepers, equipment suits and VTank import only.
- **Wiring**: `IltSection.Crafting` (universal, tab id `crafting`, also `craft` / `tinker`),
  `IltHubController` (feature list, runs on every world, `/ra hub clap` goes to the Crafting
  tab's clap, usage text), `IltHubUi` (680x600 tab), `IltHubState.Crafting` (bench settings and
  custom rows, saved with the Hub state).
- The Mini Remote's Character menu lists Crafting with Quests and Pets (it lists every universal
  tab). RynthCore's Char menu entry is in the engine change.

## Limits

- ILT weapon lines can't be read back from the item, so a run applies each one once; starting
  the same item again applies them again.
- ILT and custom rows have no client-side chance: the server's dialog percent is the check.
- Not tried in game yet.

## Tests

- New `TinkerTests` (10 tests): ACE difficulty and chance numbers, slot cap and one imbue,
  attempts this run, rend mapping and rend last, ILT weapon rows and Leather, ILT jewelry flags
  and the Steel + Serpentine cap, item rows, scan filters, bag matching and bag choice, the
  Crafting tab id and universality.
- `ServerMenuTests`: the universal tabs are now Quests, Pets and Crafting.

Full host suite: 670 tests, 669 pass. The one failure is `map bake: 0x6346`, which needs the game
data files on this machine, as before.