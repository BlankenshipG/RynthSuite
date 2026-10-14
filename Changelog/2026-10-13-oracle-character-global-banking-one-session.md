# Mini Remote tabs moved, one XP / luminance session, ILT aetheria level 10

Needs RynthCore 2026.10.5.54 for the engine side: the Mini Remote "Session Stats"
section, the `/rc diagnostics` command, and redirects for the old tab ids. On an
older engine the plugins still work; old links just land on the plugin commands
below.

| Plugin | Version | Previous release |
|---|---|---|
| RynthAi | 0.5.62-legacy-ui | 0.5.61-legacy-ui |
| RynthILT | 0.2.6 | 0.2.5 |
| RynthOracle | 0.2.5 | 0.2.4 |
| RynthInventory | 0.1.2 | 0.1.1 |
| RynthTracker | 0.4.1 | 0.4.0 |
| ub-Rythai | 3.1.13 | 3.1.12 |
| Server plugins (all 16: RynthAChard ... RynthThistlecrown) | 0.1.2 | 0.1.1 |

## Infinite Leaftide aetheria (RynthILT)

- Aetheria chunking now allows a max level of up to 10. The "Chunk aetheria with
  max level below" setting goes from 1 to 11; 11 chunks every level.
- Aetheria with a C, CD or CDR rating (gear ints 372 / 374 / 375) is never
  chunked. Each rating has its own switch in the Gear tab, and all three are on
  by default.

## Mini Remote "Char" is now "Session Stats" (RynthAi)

- The char section shows session stats only. RynthAi's new "section:session"
  view has the clock, XP, luminance, last 5 min, pass-up, Radiance, kills, XP per
  kill and deaths. RynthCore drops its own overview when this view is present.
- RynthILT's part of the section shows only the ILT extras (coins, pyreals,
  conversions), on RynthAi's session clock (surface "section:ilt-rates").
- The Character, Charms and Banking tabs are gone from the Mini Remote. Their old
  ids and links redirect (`MovedTabs`, `IltSections.MovedCommand`):
  - character, char, rates, charms: `/ro character`
  - banking, bank: `/ginv bank`

## Charms in RynthOracle's Character tab

- New "RynthILT.Data" v1 interface (`Plugins/Shared/RynthIltDataApi.cs`):
  GetCharmsJson, GetBankJson, ToggleCharm and BankCommand. Readers poll it only
  while they are showing the data.
- The Character tab has a dynamic Charms list: state, tier, acquired and the
  server's switch. Carried charms and Guardian Hands each have a Toggle, and
  RynthILT checks the item is the player's before using it.
- The Character tab also has a Session section with RynthAi's numbers.
- `/ro character` (also char, charms) shows the tab; `/ro about` (also features)
  shows the About tab. Both open the window and never toggle it closed.
- The About tab's "Server features" section is built from RynthILT's feature
  list, which RynthILT saves. A fix was needed so this section compiles.

## Banking in Global Inventory (RynthInventory)

- New "Banking" tab, opened with `/ginv bank`. It lists every character on this
  server: those RynthILT has balances for (any account on this PC), plus those
  Global Inventory has recorded on the same server but with no balance read yet.
  A Total row is at the bottom.
- Refresh, Deposit, Withdraw (amount or trade notes) and Transfer. A transfer
  asks for confirmation first. RynthILT's last reply and the recent transfers
  are shown.
- RynthILT keeps the balances refreshing while this tab is open, the same as it
  did for the Mini Remote.

## Forcing a server lives only in RynthCore Options > Diagnostics

- Removed the "Treat this world as ..." items from RynthAi's menu, the force
  checkboxes from RynthILT's header and settings, and the force radios from the
  server cards. Each place now shows the current state read-only, with a link to
  `/rc diagnostics`.
- Server cards moved to their own "Server" section (order 50) instead of "char".

## One XP / luminance session

- RynthAi's session is the source for the Mini Remote, RynthOracle, RynthILT,
  RynthTracker and ub-Rythai's XP meter.
- "RynthAi.Hub" gets a new function, SessionStatsMore (XP per kill, deaths,
  Radiance, level cap), added at the end of the table. Readers now check the
  table against `BaseStructSize`, so an older RynthAi is still accepted.
- RynthTracker shows RynthAi's session when RynthAi is loaded and falls back to
  its own. Reset restarts both. Per-hour rates are rounded instead of cut off.
- ub-Rythai's XP meter uses RynthAi's session when it can. Otherwise it uses the
  shared SessionStats formula, which handles the ACECustom bank (9005), deposits
  and transfers, and Aelrynth 9102 / 9103. Its old rule only summed raw rises.

## Tests

- RynthILT.Tests: 77 passed. New cases cover the moved-section commands, the
  charms reader JSON with "hands", and the aetheria rules.
- RynthOracle.Tests: 228 passed. New cases cover the Character tab with charms
  and session, and the Toggle routing.
- RynthInventory.Tests: 252 passed. New cases cover the roster merge, totals,
  amount parsing, and the Banking tab's draw, deposit, withdraw and confirmed
  transfer.
- RynthCore.TrackerSessionTests: 288 passed. New cases cover RynthAi's session
  in the Tracker, the older-RynthAi fallback, rounding and the hub base size.
- RynthServerPack.Tests: 14 passed.
- RynthCore.RynthAiHostTests: 967 of 968 passed. Updated cases cover the Session
  Stats manifest, the moved tabs and the redirects. The one failure is the map
  bake check, which needs the game's dat files and made no checks.
