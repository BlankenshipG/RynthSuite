# RynthAi 0.5.67 - Crafting tab: server salvage comes from the server plugin

Date: 2026-10-15
Plugin: RynthCore.Plugin.RynthAi 0.5.67-legacy-ui (previous release 0.5.66-legacy-ui)

## What changed

The Mini Remote's Crafting tab no longer says "Not an Infinite Leaftide server: add this
server's own salvage below." on every other server. The "Server tinkering" section now follows
the active server:

- The header names the server RynthCore says is active ("Server tinkering - DreamWeave"). It
  falls back to the world name.
- On Infinite Leaftide, RynthAi's built-in Leaftide rows show as before, without the
  "Infinite Leaftide" caption.
- On any other server, the rows come from that server's plugin: the new `salvage` list in its
  `RynthServer.Profile` JSON (`ServerPackInfo.Salvage`).
- With nothing defined, the section says "This server doesn't have any dynamic salvage
  defined." If the server defines salvage for other tabs but not this one, it says "... for this
  tab."
- The player's own rows ("Your salvage for this server") are unchanged.

## Server plugins: defining salvage

A server plugin lists its recipes in `ServerData.cs`:

```csharp
Salvage = new[]
{
    new ServerSalvage("melee", 52, "+1 tink past the cap", "once", UsesTinkSlot: false),
    new ServerSalvage("armor", 64, "+30 armor level"),
},
```

- `Bench` is one of armor, melee, missile, caster, jewelry, items or trinkets.
- `Material` is the bag's MaterialType id.
- `Kind` is tink, imbue or once.
- `UsesTinkSlot` and `NameMatch` work the same way as on the player's custom rows.

The rows have their own origin and settings key (`p<id>`), so the Leaftide-only planner rules
(Leather after the cap, the Steel and Serpentine jewelry cap) never apply to them. Like custom
rows, their success chance is read from the server's confirmation dialog.

The field was added within profile schema 1, so older readers ignore it. A server plugin built
before this change sends no list, and the tab treats that as "none defined".

DreamWeave defines no salvage yet. Its custom tinkering (Tiger Eye on armor, the Prismatic Drift
Stone) applies several tinks in one use to an untouched item and then locks it, which a
one-tink-at-a-time row would get wrong.

## Files

- `Plugins/Shared/ServerPack/ServerPackInfo.cs`: the `ServerSalvage` record and the
  `Salvage` field.
- `Plugins/Shared/ServerPack/ServerPackProfile.cs`: the `salvage` list in the profile JSON.
- `Plugins/Shared/EngineServerState.cs`: `ServerSalvageView.ParseList`, a lenient reader.
- `MiniRemote/MiniRemoteContext.cs`: polls the server state (every 2 s, cheap until due).
  It keeps the server name and salvage as a snapshot the render thread can read safely, and
  clears it on logout.
- `MiniRemote/MiniRemoteController.cs`: polls on tick and clears on logout.
- `MiniRemote/Crafting/TinkerCatalog.cs`: `TinkerOrigin.Plugin` and `FromServerPlugin`.
- `MiniRemote/Crafting/TinkerPlanner.cs`: a `Rows` overload that takes the server rows.
- `MiniRemote/CraftingTab.cs`: the new section text and row source.

## Tests

- `Tools\RynthServerPack.Tests`: 14 tests, 830 checks, all passed. New checks cover the salvage
  JSON round trip, defaults, skipped rows, and a profile with no list.
- `Tools\RynthCore.RynthAiHostTests`: 1006 of 1007 passed. The new test "tinker: a server
  plugin's salvage" passed. The one failure is the map bake check, which needs the game dats.

## Install

The NativeAOT plugin was published with `dotnet publish -c Release` and installed to
`C:\Games\RynthSuite\RynthAi`. The 0.5.66 DLL and PDB are backed up in
`PreviousReleases\0.5.66`. Reload with RL, or restart the client.
