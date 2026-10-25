# ub-IT meta compatibility: every /ub verb and expression function answers

RynthAi 0.5.86-legacy-ui, ub-Rythai 3.1.15, RynthSort 0.3.3 (build 2026.10.18.9)

A meta written for ub-IT (with VTank and Mag-Tools) can send any ub-IT `/ub` command and call
any ub-IT expression function. Before this, about 100 `/ub` verbs and 30 functions were unknown
here: RynthAi printed "Unrecognized /ub command" or the function evaluated to nothing.

## /ub commands (RynthAi)

Mapped onto what RynthAi or another plugin already does:

| ub-IT | Here |
|---|---|
| `wear` / `wearp <item>` | wields the carried copy (skips one already worn, which a use would take off) |
| `getxy`, `stfl` / `stfr <ms>` | `/mt get xy`, `/mt strafe left/right <ms>` |
| `globalframelimit` / `bgframelimit <fps>` | RynthAi's frame limit (Settings, Display); 0 turns it off |
| `fellow dismiss[p]` / `leader[p] <name>`, `fellow status`, `fellow recruitp` | AC's `/fellowship` command; status is `/ra fellow status` |
| `arrow point <coords>`, `navgoto <ns> <ew>` | RynthNav: `/rnav arrow`, `/rnav go` (ub-IT's signed coordinates converted) |
| `packsort`, `packsortscansort`, `packsortshow` / `list` | RynthSort: `/rsort now`, `/rsort status` |
| `qblist`, `augs` | the server's `/qb list`, `/aug` |
| `window save` | `/rc window save` |
| `miniremote show/hide/toggle` | RynthAi's Mini Remote |
| `meta` / `combat` / `sloot on/off/status` | RynthAi's Enable Meta / Combat / Looting |
| `meta load <name>`, `meta state <name>` | VTank's meta load and setmetastate |
| `debug`, `lps` / `lootprofilestatus` | `/ra debug`, the current loot profile |

The ub-IT features with no equivalent here are accepted and ignored. These are ub-IT's own tool
diagnostics, Decal UI and client patches, and things RynthCore does its own way: `videopatch`,
`chat`, `playeroption`, `statushud`, `login`, `autologin`, `professor`, `xp`, `spell`, `chunk*`,
`lsd*`, `storm`, and the rest. The first use in a session prints one line saying so, and every
use is logged, so the meta keeps running. A verb that isn't in ub-IT at all is still unknown.

## RynthAi and ub-Rythai share /ub without double replies

Every plugin sees each `/ub` line.
- RynthAi now stays silent for ub-Rythai's verbs: autopercent, autotradeaccept, bank, build,
  guard, guardian, ilt, leaftide, log, loginq, p2, periodic, xpmeter.
- ub-Rythai no longer prints "Unknown command" for verbs RynthAi answers.
- `/ub portal use <gem>` and `/ub portal list` are ub-Rythai's portal gems. `/ub portal <name>`
  (ub-IT: use the nearest portal by name) is RynthAi's.

## A meta's /ub line has its /ub meaning

A meta's chat command went straight to the `/mt` handler, so `/ub give` gave one item, as
`/mt give` does. It now goes to the `/ub` dispatcher and gives every match, as typed. ub-Rythai's
verbs go on to the chat parser, which ub-Rythai sees.

## Expression functions (RynthAi)

- `actiontrymove[obj, container, slot, addToStack]` and `actiontrysplit[obj, count, container, slot]`
  return 1 when attempted and 0 when busy. `actiontrydrop` has no engine call and returns 0.
- `wobjectgethealthvalue`, `wobjectgetstaminavalue` and `wobjectgetmanavalue` give current values
  for yourself or the selected target, and -1 otherwise.
- `hascorpsebeenopenedbyme[obj]` and `getcorpsesunopenedbyme[]` read RynthAi's loot bookkeeping.
- `iscasting[]`, `getcooldownexpiration[id]` (shared cooldowns), `spelldata[id]` (a dictionary:
  Id, Name, Family, Difficulty, Category, Power, Level).
- `vtmacroenabled[]`, `canrefreshquests[]` (ub-IT's 60 s myquests cooldown),
  `enlightenmentmaterials[current, target]` (ub-IT's token count).
- `delayexec` now returns its id, as in ub-IT, and `clearexec[id]` cancels it.
- XP meter: `xpreset`, `xpmeter` (ub-IT's text), `xpduration`, `xptotal`, `lumtotal`, `xpavg`,
  `lumavg`.
- These return ub-IT's "nothing here" value: `statushud` / `statushudcolored` (1),
  `netclients` (empty list), `getcharacterindex` (-1), `setnextlogin` (0), `clearnextlogin` (1),
  `testquestbonus` / `isrefreshingquestbonuses` (0). The `ui*` view functions return 0 because
  Decal views don't exist here.

## ub-Rythai: ub-IT's periodic list

`/ub periodic add <minutes> <command>`, `remove <N>`, `toggle <N>`, `list` and `clear`, as in
ub-IT. Each entry has its own interval and is saved with the character's ub-Rythai settings. The
settings window's periodic batch is unchanged.

## RynthSort

Takes commands from other plugins (`RynthPluginApplyRemoteCommand`, action `rsort`), so
`/ub packsort` reaches it.

## Tests

- RynthAiHostTests: 4 new `/ub` compatibility tests, a meta `/ub give` test, the new expression
  functions, and `delayexec` ids with `clearexec`.
- The 2 known failures are unchanged: map bake needs the game data files, and the mini remote
  help line.
