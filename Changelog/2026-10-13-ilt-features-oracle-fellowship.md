# RynthILT 0.2.2, RynthOracle 0.2.1, RynthAi 0.5.56: Leaftide feature list and the ILT fellowship

RynthILT now lists every Infinite Leaftide feature it supports and hands that list, with the
server's fellowship rules, to other plugins. RynthOracle's Fellowship tab reads ILT fellowships
(up to 29 members) and shows the server's fellowship features.

## What changed

- **RynthILT: Leaftide feature list.** One catalog (`Hub/IltFeatureCatalog.cs`) of what the plugin
  supports on Infinite Leaftide, in four sections:
  - Character: Banking, Guardian (infinite base attributes), infinite raised attributes, infinite
    augmentations, Enlightenment.
  - Items: Paragon armor, Paragon weapons, Paragon jewelry, split arrows, T11 items.
  - Fellowship: large fellowships (up to 29), Zone Share, fellowship luminance share, Fellowship
    Blackjack.
  - Activities: quests, quest bonus, pets, shinies, charms, bounties, chunk clap, Powerball,
    Blackjack.

  Features the server answers for (`/bank`, `/aug`, `/xp`, `/enl`, `/fblackjack` and the rest) take
  their state from the login check. Features built into ACECustom (the guardians, Paragon gear,
  split arrows, T11, the 29-member fellowship) are on whenever the world is an ILT world. Off ILT,
  nothing is claimed.
- **Where to see it.** The Mini Remote's Character tab has a "Leaftide features" section (feature,
  server state, where it lives). `/ra hub features` prints the same list in chat.
- **New plugin interface "RynthILT.Server"** (`Plugins/Shared/RynthIltServerApi.cs`). One call,
  `GetProfileJson`, returns a JSON snapshot: whether this is an ILT world, the feature list with
  states, and the fellowship rules (`maxMembers`, `zoneShare`, `blackjack`, `luminanceShare`). The
  JSON is plain ASCII and rebuilt once a second on RynthILT's pump. "RynthILT.Hub" is unchanged,
  because RynthAi asks for it at an exact version.
- **Mini Remote bank line.** When no bank data has been read yet, the Remote tab shows "Bank: not
  loaded yet" with a Load button (a quiet `/bank`), instead of pointing at an options page that no
  longer has it.
- **Fellowship reader (RynthAi, shared with RynthOracle).** `FellowshipTracker` refused any
  fellowship over 9 members, so an ILT fellowship read as empty. The limit is now 32 members and
  256 hash buckets, enough for ACECustom's 29 (`Fellowship.MaxFellows`), and the header check is
  `IsPlausibleTable`. New `GetMembers()` returns every member (id, name) from one refresh, so a
  row can't pair one member's id with another's name.
- **RynthOracle: Infinite Leaftide.**
  - New server tag `InfiniteLeaftide`, detected from a world name containing "Leaftide" or from
    RynthILT's own verdict (its world list or the character's "Treat this world as ILT").
  - `Data/IltServerProfile.cs` reads "RynthILT.Server" every 2 seconds and parses it leniently.
  - The Fellowship tab uses the server's size limit (29 on ILT, 9 elsewhere; 29 by name alone when
    RynthILT is off), lists members from one snapshot, and adds an "Infinite Leaftide" section with
    the size limit, when RynthILT last checked, and each fellowship feature's state.

## Files

- `Plugins/Shared/RynthIltServerApi.cs` (new): the interface struct and constants.
- `RynthILT/Hub/IltFeatureCatalog.cs` (new): the catalog, states and profile JSON.
- `RynthILT/ServerInterface.cs` (new): the interface table and `GetProfileJson` export.
- `RynthILT/PluginExports.cs`: QueryInterface also answers "RynthILT.Server"; Shutdown releases it.
- `RynthILT/Hub/IltHubController.cs`: per-second profile rebuild, `FeatureStates`, `/ra hub features`.
- `RynthILT/Hub/IltHubUi.cs`: the Character tab list and the Remote tab bank line.
- `RynthILT/RynthILTPlugin.cs`: version 0.2.2 (previous 0.2.1).
- `RynthAi/Combat/FellowshipTracker.cs`: limits, `IsPlausibleTable`, `GetMembers`.
- `RynthAi/RynthAiVersionCommand.cs`: version 0.5.56-legacy-ui (previous 0.5.55-legacy-ui).
- `RynthOracle/Data/ServerIdentity.cs`: the InfiniteLeaftide tag.
- `RynthOracle/Data/IltServerProfile.cs` (new): the profile reader and parser.
- `RynthOracle/Views/FellowshipTab.cs`: server size, member snapshot, the ILT section.
- `RynthOracle/RynthOraclePlugin.cs`, `PluginExports.cs`, `OracleContext.cs`: polling, version 0.2.1
  (previous 0.2.0).

## Tests

- RynthILT.Tests `features:` (4 tests): every requested feature is listed, states follow the server
  bits, the JSON is valid ASCII with the fellowship rules, and the interface copy only writes when
  the buffer fits.
- RynthOracle.Tests: the ILT tag from world names, and profile parsing (an older profile and
  garbage fall back to a retail fellowship).
- RynthAiHostTests: `fellowship table` accepts 29 members and rejects torn headers.

Not yet tested in game.
