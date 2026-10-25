# RynthAi 0.5.85 — the missing /vt and /mt commands ported; VTank distances fixed

**Kind:** Hot push (RynthAi). Previous `0.5.84`. DLL stamp `2026.10.18.8`.

## Asked for

Look at ub-IT and find the `/vt` and `/mt` commands not ported yet, then port them.

## How the list was made

- **`/mt`:** Mag-Tools' own handler (`Mag-Plugins\Mag-Tools\PluginCore.cs`,
  `ProcessMTCommand`) and ub-IT's `MagToUbCommandTranslator.cs`, against `MagToolsCommands.cs`.
- **`/vt`:** every `/vt` verb ub-IT uses or documents, and every one in the metas in
  `C:\Games\RynthSuite\RynthAi` (MetaFiles, MetaProfiles), against `MetaManager.TryHandleVtCommand`.

## /vt — added

| Command | Where it's used | Now |
|---|---|---|
| `/vt e` | ub-IT (switches VTank on or off) | toggles the macro (`/ra start` / `/ra stop`) |
| `/vt jump <heading> <true/false> <ms>` | universalremote.met, ub-IT | `/ra jump[s] <heading> <ms>` (RynthAi's jumper) |
| `/vt tapjump` | universalremote.met | the engine's tap jump |
| `/vt settings loadchar/savechar <name>` | universalremote.met (about 20 times) | `/ra settings …`. Before, it was swallowed silently. |
| `/vt looting load <file>.utl` | FoundryIzexi.met, Meta_Society.met | the same as `loot load` (was "Unrecognized") |
| `/vt equipitemsfor <monster>` | SASocietyTasks.met | new `/ra equipfor <monster>`: wields that monster rule's set weapon and off hand |
| `/vt <option> <value>` | DerethExploration (1).met: `/vt navclosestoprange 0.005` | the same as `/vt opt set` for mapped options |

## /vt opt — fixed and added

**Fixed: distances.** VTank distances are in landblock units (×240 = yards): `attackdistance
.01666` is 4 yd. RynthAi's `MonsterRange`, `ApproachRange` and `RingRange` setters only take whole
numbers, so every such meta value was dropped. `OpenDoorRange` took 0.005 as yards. Now
`attackdistance`, `attackrange`, `approachdistance`, `dooropenrange`, `opendoorrange`,
`ringdistance` and `corpseapproachrange[-max/-min]` are converted. A value over 1 is taken as yards
already, as when typed by hand.

**Newly mapped:**
- `attackrange` → MonsterRange
- `ringdistance` → RingRange
- `minimumringtargets` → MinRingTargets
- `corpseapproachrange`, `corpseapproachrange-max`, `corpseapproachrange-min` → CorpseApproachRange
- `navclosetoprange` (a misspelling in some metas) → the nav point reach
- `defaultmeleeattackheight`: VTank 1–3 → RynthAi 0–2, read back as 1–3
- `lootfellowcorpses` and `lootallcorpses` → loot ownership (my kills / fellowship / all)
- `usearcs`: VTank's mode number → on/off
- `minimumhealkitsuccesschance` → the kit success % (a fraction is scaled)

**Still stored as generic options** (readable by expressions; no RynthAi equivalent):
`autoattackpower`, the `blacklist*` timeouts, `dohelp`, `dojiggle`, `dooridrange`,
`droptopeacemoderetrycount`, `ghostmonsterspellattemptcount`, `manatankminimummana`, `recharge`,
`spellcompmin`, `usespecialammo`.

## /mt — added (as Mag-Tools)

- **`send`:** now presses keys in the game window, as Mag-Tools: `enter`, `pause`, `del`, `space`,
  `f4`, `f12`, `cntrl+<letter>`, or `msg <text>` to type a line. Before, it sent the words as a chat
  line. A line starting with `/` is still sent as a chat command.
- **`jump`, `sjump`** (and `jumpw/z/x/c`) `[ms]`: RynthAi's jumper; the key presses if the engine
  has no jump hook.
- **`trade add|addp <item> | accept | decline | reset | end`**
- **`vendor addbuy|addbuyp <item> [count] | addsell|addsellp <item> | buy | clearbuy | sell |
  clearsell`:** with a vendor open. The lists are sent in one transaction and cleared when the
  vendor closes.
- **`autopack`:** a cram + stack pass (RynthAi's equivalent of Mag's packer).
- **`dumpspells`:** the spell table to `RynthAi\spelldump.txt` (id, name, family, difficulty).
- **`click ok|yes|no|<x> <y>`:** AC's confirmation buttons, using Mag-Tools' positions, or window
  coordinates.
- **`get xy`** (and ub-IT's `cursor`): the cursor relative to the game window.
- **`movement <key> <ms>`:** hold a movement key.
- **`client minimize|restore`.** `client frame …` points to Settings > Display.
- **ub-IT's `config`** (= `opt`) and **`strafe left|right <ms>`** (holds Z / C).
- **`test`:** a no-op, as in Mag-Tools.

`GameInput.cs` (new) is the key and mouse layer: a port of Mag-Tools' PostMessageTools (same keys,
scan codes and click spots). It posts to this client's own AC window.

## Files

- `Plugins/RynthCore.Plugin.RynthAi/Meta/MetaManager.cs`: `/vt` verbs, option mapping and conversion.
- `Plugins/RynthCore.Plugin.RynthAi/MagToolsCommands.cs` and `MagToolsCommands.Extra.cs` (new): `/mt`.
- `Plugins/RynthCore.Plugin.RynthAi/GameInput.cs` (new)
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiPlugin.cs`: `/ra equipfor`.
- `Plugins/RynthCore.Plugin.RynthAi/CorpseOpenController.cs`: vendor lists cleared on close.
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiVersionCommand.cs`: 0.5.85.
- `Tools/RynthCore.RynthAiHostTests/Tests/MetaTests.cs`: a new test of the `/vt` verbs and options.

## Checks

- RynthAiHostTests 1068/1070. The new `/vt` test covers `e`, `jump`, `tapjump`, `settings`,
  `looting`, `equipitemsfor`, the distance conversions, the option-as-verb form and the special
  options. The 2 known failures are unchanged.
- The `/mt` verbs drive the live client (keys, clicks, vendor and trade); they build, but can only
  be checked in game.
