# RynthOracle 0.5.0 — Dynamic Stamps tab (Infinite Leaftide)

**Kind:** Major feature (new tab). Previous `0.4.0`. DLL stamp `2026.10.18.6`.

## Asked for

A Dynamic ("dyno") quest tracker tab in Oracle, like the Legendary quest tracker. It is an
Infinite Leaftide-only quest line, so the tab shows only on Leaftide.

## Source

The leaftidewiki Dynamic Stamps page needs a login. The list comes from the players' "ExtraDyn"
Google sheet (10.13.2026), one sheet per area. Each row has the NPC, the `Dynamic_1_…` stamp, the
reward ("X gives you Y") and notes, often coordinates.

- 243 stamps in 17 areas: Facility Hub, Looking Around, Odd Stamps, Odd Stamps 2, Bandit Castle,
  Bur, Gear Knight, Wai Jhou, Rynthid, Paradox Camp, Graveyard, Neftet, Frozen Valley, Hoshino Tent,
  Viridian Rise, Ayan, Candeth Keep.
- 26 rows without a stamp (NO STAMP NPCs, duplicates) are left out.

## New

- **Dynamic Stamps tab**, shown only when the server is Infinite Leaftide (`ServerTags.InfiniteLeaftide`,
  like Pet Breeding). Read from `/myquests`; a stamp is a one-time flag, so having it means done.
  - Progress bar: stamps you have out of the 243 listed.
  - Filters: Missing only, Have only, an area picker, and a search over NPC, place, reward, area
    and stamp.
  - One collapsing section per area with have/total. Each row shows have/need and NPC - place -
    reward, with the notes as a tooltip. Clicking a row prints it in chat: NPC, stamp, have or
    missing, area, place, reward and coordinates. RynthChat makes the coordinates a RynthNav arrow
    link.
  - "Not in the list (N)": Dynamic stamps on the character that the sheet doesn't have yet, so new
    NPCs still show.
- The sheet copied a few stamps without their closing `!`, or cut short. Those also match with `!`
  added and, when at least 14 characters long, the one Dynamic flag on the character starting with
  them.
- Commands: `/ro dynamic` (also `dyn`, `dyno`, `stamps`). The About tab lists it on Leaftide.
- `Tools/RynthOracle.QuestPackGen/Make-DynamicStamps.ps1` rebuilds `dynstamps.csv` from the sheet
  (downloads the .xlsx; or `-Xlsx` for a saved copy) when players add stamps.

## Files

- `Plugins/RynthCore.Plugin.RynthOracle/Data/DynamicStamps.cs` (new)
- `Plugins/RynthCore.Plugin.RynthOracle/Views/DynamicStampsTab.cs` (new)
- `Plugins/RynthCore.Plugin.RynthOracle/Resources/dynstamps.csv` (new)
- `Plugins/RynthCore.Plugin.RynthOracle/Views/OracleView.cs`: the tab, Leaftide only.
- `Plugins/RynthCore.Plugin.RynthOracle/Views/AboutTab.cs`
- `Plugins/RynthCore.Plugin.RynthOracle/RynthOraclePlugin.cs`: `/ro dynamic`, 0.5.0.
- `Plugins/RynthCore.Plugin.RynthOracle/RynthCore.Plugin.RynthOracle.csproj`: embeds the CSV.
- `Tools/RynthOracle.Tests/Program.cs` and `RynthOracle.Tests.csproj`
- `Tools/RynthOracle.QuestPackGen/Make-DynamicStamps.ps1` (new)

## Checks

- RynthOracle.Tests 314/314.
- The new checks cover: 243 stamps in 17 areas; the Felscuda row; matching through `/myquests`,
  including `! & # ^ $` in names; a stamp whose sheet copy lost its `!`; unlisted stamps; no prefix
  match on a short stem; the tab only on Leaftide.
- Every area open records 835 ops and 33 KB, under the engine's 1500 ops and 64 KB.
