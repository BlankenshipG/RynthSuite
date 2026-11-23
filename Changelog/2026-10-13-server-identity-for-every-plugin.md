# Server identity for every plugin: RynthCore's verdict, the server plugin's settings

Needs RynthCore 2026.10.5.53 (plugin API v85, GetServerStateJson) for the new
behaviour. On older engines each plugin keeps its previous detection.

| Plugin | Version | Previous release |
|---|---|---|
| RynthAi | 0.5.61-legacy-ui | 0.5.60-legacy-ui |
| RynthOracle | 0.2.4 | 0.2.3 |
| ub-Rythai | 3.1.12 | 3.1.11 |
| Server plugins (all 16: RynthAChard ... RynthThistlecrown) | 0.1.1 | 0.1.0 |

## Why

An audit of how the core plugins read the server identity found that each
plugin decided on its own. RynthOracle didn't follow the Mini Remote's
per-character overrides. ub-Rythai's ILT banking matched only the world name.
The server plugins' rules (botting policy, rule set, account limits, commands)
were visible only on their own card.

## Change

- `Plugins/Shared/RynthServerProfileApi.cs`: a new "RynthServer.Profile" v1
  interface. Like "RynthILT.Server", it is one function, GetProfileJson, which
  returns plain-ASCII JSON.
- Server plugins (`Shared/ServerPack`) export `RynthPluginQueryInterface` and
  serve the profile (`ServerPackProfile`, `ServerPackInterface`). The profile
  has the id, name, plugin, version, RynthCore's verdict and world, hosts,
  world names, port, emulator, rule set, status, botting and note, account
  limit, summary, facts, commands and links.
- `Plugins/Shared/EngineServerState.cs` (pure) parses RynthCore's
  GetServerStateJson. `ServerProfileView` reads either profile. The
  `MatchesNameList` method checks a user's server-name list against the
  engine's verdict.
- `Plugins/Shared/ServerStateClient.cs` polls the engine every 2 s. It asks the
  active server's owner plugin for its profile: "RynthServer.Profile", or
  "RynthILT.Server" when the owner is RynthILT. It re-queries a table whose
  Version drops to 0.
- RynthOracle follows RynthCore's active server when the engine reports one,
  including overrides and the Options > Diagnostics > Server pick. A server the
  engine lists but has off stays off, even if the world name matches. A pick
  re-applies the quest pack within 2 s. The About tab shows RynthCore's verdict
  and the owner plugin's settings.
- ub-Rythai ILT Auto (features and banking) uses the same verdict.
  `/ub leaftide` now also prints RynthCore's server, pick and owner plugin.
- RynthAi: new `/ra server` report. It prints the active server and why, every
  server with its override and owner, the owner's settings, and whether the
  Mini Remote ILT tabs are connected.

## Tests

- RynthOracle.Tests: 208 passed. New cases cover the engine-driven identity
  (detected, picked, forced off, unknown server), the profile parser, and the
  name-list matcher.
- RynthServerPack.Tests: 14 tests, all pass. New cases check the profile JSON
  fields, that it is plain ASCII, and that the reader parses what the plugin
  writes.
- RynthConquest published with NativeAOT; its exports include
  RynthPluginQueryInterface.
