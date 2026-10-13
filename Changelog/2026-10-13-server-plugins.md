# RynthSuite: one plugin per server (16 new server plugins, RynthILT 0.1.2)

Branch `feat/dynamic-sections`, after df464b9 (RynthAi 0.5.56, RynthILT 0.1.1). It pairs with
RynthCore engine 2026.10.5.48 on the branch of the same name; build them together.

| Component | Previous | Now |
|---|---|---|
| RynthILT | 0.1.1 | 0.1.2 |
| 16 server plugins (below) | (new) | 0.1.0 |

Release builds stamp every plugin with the release number (`Publish-Update.ps1` passes
`-p:Version`); the versions above are the plugins' own.

## Server plugins

Every server in the request, plus Aelrynth, has its own plugin, built the way RynthILT is: its own
project, its own version, its own feed entry and its own folder (`C:\Games\RynthSuite\<Name>\`).
So one server's plugin can be updated without touching the others.

| Plugin | Server | Connect host | Notes |
|---|---|---|---|
| RynthAChard | AChard | a-chard.ddns.net | PvP |
| RynthAelrynth | Aelrynth | aelrynth.com:9020 | not on the public list |
| RynthColdeve | Coldeve | play.coldeve.ac | |
| RynthConquest | Conquest | acconquest.ddns.net | |
| RynthDerptide | Derptide | ac.derptide.net | |
| RynthDreamWeave | DreamWeave | play.acdreamweave.com | wiki link |
| RynthEversong | Eversong | eversong.abrdns.com | |
| RynthFrostACE | FrostACE | 172.111.230.127 | development server |
| RynthFrostcull | Frostcull | frostcull.ddns.net | |
| RynthLeafDawn | LeafDawn | leafdawn.hopto.org | |
| RynthLeafdawning | Leafdawning | leafdawning.duckdns.org | 3 account limit (enforced) |
| RynthLevistras | Levistras | levistras.acportalstorm.com | 2 accounts; botting is not allowed |
| RynthPortalStorm | PortalStorm | ac.portalstorm.us.com:9020 | |
| RynthReefcull | Reefcull | reefcull.gdleac.com | GDLE |
| RynthSundering | Sundering | ace.sunderingac.com | 3 active characters |
| RynthThistlecrown | Thistlecrown | thistlecrown.ddns.net | 3 account limit |

Port 9000 unless shown. The data comes from the public server list
(github.com/acresources/serverslist, Servers.xml, checked 2026-10-13).

### What each one does

- **Loads only for its server.** Each csproj names its server (id, hosts, world names). RynthCore
  2026.10.5.48 reads that from the plugin's manifest and loads only the server plugin that claims
  the client's connect host. If no plugin claims it, they all load (fail-open).
- **Shows a server card.** It sits in the Char menu's server section and in its own window
  (`/rserver show`). The card shows:
  - RynthCore's verdict on whether you are on this server, with the reason;
  - the per-character override (Auto/On/Off), which sends `/rc server <id> auto|on|off`;
  - the server's facts: address, emulator, rule set, status, account limit, botting policy;
  - the server's known commands and links, each with a Copy button;
  - the steps to set the server up in the launcher.
- **Warns about botting rules.** On a server that forbids automation (Levistras), activation prints
  a warning in chat and the card shows it in red.
- **Works with older engines.** Before RynthCore's verdict arrives (or with an engine older than
  2026.10.5.48), the plugin decides by the world name.
- **Chat command:** `/rserver [id] show|hide|toggle|auto|on|off|info`.

### Shared code (`Plugins/Shared/ServerPack/`)

- `ServerPlugin.props`: the common NativeAOT build. A server plugin's csproj sets its name, version,
  server properties and description, then imports this file.
- `ServerPackInfo.cs`: a server's facts (`ServerData.cs` in each plugin fills them in).
- `ServerPackModel.cs`: RynthCore's "servermenu" verdict, the world-name fallback, override
  commands, and the sections manifest (the server and its server-only card).
- `ServerPackUi.cs`, `ServerPackPlugin.cs`, `ServerPackExports.cs`: the card, the plugin and the
  exports.
- **Server-specific features** go in the plugin's own files through partial hooks
  (`ServerInitialize`, `ServerTick`, `ServerChatCommand`, `ServerInfoLines`, ...), without forking
  the shared code.

## RynthILT 0.1.2

- RynthILT declares itself as Infinite Leaftide's server plugin (id `ilt`, host
  `infiniteleaftide.online`, worlds `InfiniteLeaftide` and `*Leaftide*`, the same as RynthCore's
  ILT defaults). With RynthCore 2026.10.5.48 it loads only for clients connecting to ILT, or when
  no installed server plugin claims the host.

## Tests

`Tools/RynthServerPack.Tests` (new):

- The shared model: the engine's push, a push for another server, the world-name fallback, the
  override commands, the sections manifest, logout, and the card's botting line and address.
- Every server plugin's csproj:
  - all 17 servers have a plugin;
  - ids, plugin names and hosts are unique;
  - no server's host is claimed by another server's list;
  - each plugin has `ServerData.cs` and imports the shared props.
- The launcher's known-server picker (RynthCore `KnownServerCatalog.cs`): each entry names a server
  plugin that claims its host, with the same port and emulator.
