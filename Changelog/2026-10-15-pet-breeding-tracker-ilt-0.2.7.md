# Pet Breeding tracker (Infinite Leaftide)

Date: 2026-10-15

| Plugin | Version | Previous release |
|---|---|---|
| RynthCore.Plugin.RynthILT | 0.2.7 | 0.2.6 |
| RynthCore.Plugin.RynthOracle | 0.2.7 | 0.2.6 |
| RynthCore.Plugin.RynthAi | 0.5.68-legacy-ui | 0.5.67-legacy-ui |
| RynthCore.Plugin.RynthInventory | 0.1.3 | 0.1.2 (rebuilt for the shared RynthILT reader) |

## What's new

Infinite Leaftide (ACECustom) pet breeding now has a tracker. RynthILT does the work: it reads
the essences, learns the server's breeding rules and runs the actions. Two views draw the same
data:

- **RynthAi Mini Remote > Pets > Breeding.** The Pets tab now has two sub-tabs: Roster (the old
  Pets tab, unchanged) and Breeding.
- **RynthOracle > Pet Breeding.** The tab shows only on Infinite Leaftide.

Each view links to the other. In RynthAi, the Mini Remote's Character menu has a new
**Pet Breeding** item (Infinite Leaftide only) that opens RynthOracle's tab (`/ro breeding`).
RynthOracle's **Mini Remote** button opens the Pets > Breeding sub-tab (`/ra hub open breeding`).

### What it tracks

- **Every combat pet essence in your pack**, with:
  - its sex (from the server's own hash);
  - its tier, from ACECustom's per-family tier table;
  - its bond, and its stat and potency mutations;
  - the boosters already applied (incense, catalyst, offering, draught);
  - why it can't breed yet: resting (dam), no charges left (stud), still growing, neutered,
    shiny, bond under the minimum, tier under the minimum, or not yet identified.
- **Stud charges and the refill timer.** The refill is lazy, like the server's: a stud
  refills once the reset window has passed.
- **Dam rest timers.**
- **Growth (maturity)**: the stage name, the stage number, and the kills to the next stage
  and to Adult. This is read from the appraisal's "Growth:" line, or worked out from the kill
  count when that line is missing.
- **Ready pairs.** For each stud and dam pair, it shows the range of inherited mutations and
  the stat-mutation odds. The odds use the server's base chance, decay, floor and incense cap,
  with the incense from both parents.
- **Supplies (Ivo, Seedy Motel)**: all three Breeding Incenses, the Nurturing Draught, the
  Breeding Catalyst, the Guardian Offering, the Neutering Kit, the Essence Tailor (empty and
  filled), the Mutation Serum, Solidify and Fade.
  - Each shows how many you carry, its effect and its price.
  - Incense, Draught, Catalyst and Offering have a **Use** button for the selected essence.
    Use is offered only where the server would accept it: incense not on a neutered essence or
    over a stronger one, a catalyst only once, the draught only on a juvenile under 2x, and an
    offering only while guardians are on and not already weakened.
- **Recent events** (the last 60): rituals, charges spent, births, the spectral guardian
  (spawned, yields, dissolves), the Awakened Blessing, growth stages, adulthood, imprinting,
  courtship dances, draughts and neutering.

### Server-specific settings

Every breeding rule shows its value and where that value came from. A value the server showed
wins over RynthILT's Infinite Leaftide profile, which wins over ACE's default. The rules are:

- the breeding room (landblock and variant) and the dance window;
- the minimum tier and bond;
- stud charges and the refill hours, and the dam rest hours;
- the mutation base chance, decay and floor, the potency mutation chance, the incense cap and
  inheritance;
- guardian on/off and its timer;
- the maturity kills, stages and kill share.

RynthILT learns these values from:

- appraisals;
- breeding chat lines;
- the `@breed-debug` reply (the **Diagnostics** button). This checks whether you are in the
  breeding room, which pet is summoned, and how many partners are near.

What it observes also sets RynthILT's `pet_breeding` and `pet_maturity` server feature bits. So
"Pet breeding is off on this server" and "Pet bond is off here" show without any guessing.
Learned rules, stage names and the event log are saved per character in RynthILT's hub file.

### Starting a breed

The **Dance** button sends `@dance` (the courtship dance). Both owners must dance within the
dance window, with both pets summoned in the breeding room. Dance and every supply use ask
first.

## How it fits together

- **`RynthILT.Data` interface version 2.** Two functions were added at the end:
  `GetPetBreedingJson` and `PetBreedingCommand`. The verbs are `refresh`, `debug`, `dance` and
  `apply <supply> <essence id>`. Readers still ask for version 1 and accept any table at least
  as large as version 1. They use the breeding functions only when the table says version 2
  or later. So an older RynthOracle or RynthInventory keeps working with this RynthILT, and this
  RynthOracle says "update RynthILT" when RynthILT is older.
- **Scanning only while watched.** RynthILT reads the essences only while one of the two views
  is open (re-scanning every 2 s while either was shown in the last 6 s). RynthOracle reads the
  JSON every 2 s, and only while the Pet Breeding tab shows.
- **Section `pets-breeding`.** This is how RynthAi's Pets > Breeding sub-tab is drawn
  (`RynthIltHub.SectionPetBreeding`). Without RynthILT, off Infinite Leaftide, or with an
  older RynthILT, the sub-tab says why instead.
- **New commands:**
  - `/ro breeding` (also `/ro breed` and `/ro pets`) opens RynthOracle's tab.
  - `/ra hub open breeding` (also `/ra hub tab breeding`) opens the Mini Remote's Pets >
    Breeding sub-tab.

## Files

RynthILT:

- New: `Hub/IltBreedingRules.cs` (the rules, their sources and the parsers),
  `Hub/IltBreedingEssence.cs` (tiers, gates and the pair planner),
  `Hub/IltBreedingSupplies.cs` (Ivo's supplies and the refusal checks) and
  `Hub/IltPetBreeding.cs` (the tracker feature and its ImGui section).
- `Hub/IltHubState.cs`, `Hub/IltHubStore.cs`: the saved breeding state.
- `Hub/IltHubController.cs`, `Hub/IltHubUi.cs`, `DataInterface.cs`: wiring.
- `Hub/IltFeatureCatalog.cs`: where the breeding feature bits show.

Shared: `RynthIltDataApi.cs` (v2, `IltBreedingView`), `RynthIltHubApi.cs`
(`SectionPetBreeding`).

RynthOracle:

- New: `Views/PetBreedingTab.cs`.
- `Views/OracleView.cs`, `OracleContext.cs`, `RynthOraclePlugin.cs`.

RynthAi:

- `MiniRemote/PetsTab.cs` (the Roster and Breeding sub-tabs).
- `MiniRemote/MiniRemoteController.cs` (`/ra hub open breeding`).
- `Huds/MovedTabs.cs`, `Huds/MiniRemoteHud.cs` (the Character menu link).

## Tests

- `Tools\RynthILT.Tests`: 89 tests, 1491 checks, all passed. 12 are new breeding tests: the tier
  table, rule sources, mutation odds, appraisal, chat and `@breed-debug` parsing, every gate,
  the pair planner, supply refusals, the reader JSON round trip, saved state through the
  source-generated serializer, and v1 readers working against v2.
- `Tools\RynthOracle.Tests`: 254 passed, 0 failed. The new checks cover:
  - the tab showing only on Infinite Leaftide;
  - the JSON parser;
  - 50 essences with every section open (316 ops, under the 1500-op window budget);
  - Dance and Use asking first, then sending the right verb;
  - the Mini Remote link.
- `Tools\RynthCore.RynthAiHostTests`: 1006 of 1007 passed. New checks cover
  `/ra hub open|tab breeding`, which selects the Breeding sub-tab, and the Character menu's
  `/ro breeding` link. The one failure is the map bake check, which needs a live 0x6346 plan on
  disk.
- `Tools\RynthCore.RynthAiTests`: 39 passed. `Tools\RynthInventory.Tests`: 252 passed.

## Install

All four plugins were published as NativeAOT (`dotnet publish -c Release -r win-x86
-p:PublishAot=true`) and staged at `D:\rynth-merge\stage-suite\<Plugin>-<version>\`. Each is
installed to `C:\Games\RynthSuite\<Plugin>\`. The DLL (and PDB) it replaces is first saved to
that folder's `PreviousReleases\<previous version>\`:

- `RynthILT\PreviousReleases\0.2.6`
- `RynthOracle\PreviousReleases\0.2.6`
- `RynthInventory\PreviousReleases\0.1.2`
- `RynthAi\PreviousReleases\0.5.67-legacy-ui`

Install all four together: RynthOracle's tab and RynthAi's sub-tab need RynthILT 0.2.7, and
they say so if an older RynthILT is loaded.
