# RynthAi 0.5.2 — Diagnostics (UB-style logging) + security hardening

**Release date:** 2026-10-04
**Plugin version:** `0.5.2-legacy-ui` (file version `0.5.2.0`)
**Previous release:** `0.5.1` (backed up to `C:\Games\RynthSuite\RynthAi\PreviousReleases\0.5.1\`)
**Branch:** `feat/ilt-hub` (local test build, not on `main`)

## Security

### Tmds.DBus.Protocol advisory (NU1903 / GHSA-xrw6-gwf8-vvr9)
Avalonia 11.x pulls in `Tmds.DBus.Protocol` 0.20.x transitively, which has a high-severity
advisory. A direct `PackageReference` to **0.21.3** now overrides it in every affected project:

- `Tools/RynthCore.LootEditor/RynthCore.LootEditor.csproj`
- `Tools/RynthCore.MonsterEditor/RynthCore.MonsterEditor.csproj`
- RynthCore (separate repo, branch `fix/tmds-dbus-advisory`): `RynthCore.App.Avalonia.csproj`, `RynthCore.Engine.csproj`

All four now restore 0.21.3 with no NU190x warnings.

### ILT Hub hardening
- **Bank transfers:** the recipient must be a plain character name (letters, spaces, `'` and `-`, max 32 characters).
  This stops a pasted name from slipping extra arguments or a second command into `/bank`.
- **Bank transaction log:** lines that players wrote (tells, says, and the Allegiance, Fellowship, General, Trade, LFG
  and other channels) are no longer recorded as transactions. Someone typing a fake "[BANK] …" line
  can't spoof your ledger.
- **Pet summon-on-empty:** now requires all of the following:
  - you opted in;
  - you are on an ILT world;
  - a refill charm is active;
  - the server confirmed pyreal auto-refill, either through the feature flag or the player bool.

  An unknown server state no longer counts as permission.
- **Auto-clap:** runs only when the server has confirmed `/clap` is **on**. Before, it only checked that clap wasn't reported off.
- **Powerball:** ticket purchases stay locked until the server confirms `/pb`. Refresh unlocks them.
- **Chat confirmations:** `/ra hub suit load …` and `/ra hub profile load …` now ask you to confirm.
  Type `/ra hub confirm` within 20 seconds, or `/ra hub cancel`. The prompt expires on its own.

## Diagnostics: full logging and debugging (modelled on UtilityBelt's Logger)

New `Diagnostics/RynthLog.cs`. Logs live in `C:\Games\RynthSuite\RynthAi\Logs\Diagnostics\`.

| File | Contents |
|---|---|
| `rynthai_<date>.txt` | Every RynthAi log line, tagged with its category. On by default. |
| `Trace\<Category>_<date>.txt` | Verbose per-function trace, only written while that category is traced. |
| `exceptions_<date>.txt` | Full stack traces, throttled. |
| `diagnostics.json` | Saved switches. They persist across sessions. |

- **One trace category per function:**
  - Core: General, Commands, Combat, Buffing, Pets, WorldCache, Navigation, Doors, Jumper,
    Looting, Inventory, Salvage, Vendor, ManaStones, Meta, Expressions, Quests, Crafting, Raycast, Radar, UI,
    Remote, Chat.
  - ILT Hub: IltHub, IltOptions, IltChat, IltStore, IltBanking, IltPets, IltQuests, IltProgression,
    IltRates, IltGear, IltGames.
- **Every existing log call (~285)** now goes through `RynthLog.Write`. It still reaches the engine log
  exactly as before, and also lands in the daily file and the category's trace file.
  Log lines in `RynthAiPlugin` are sorted into categories by their `[Prefix]`, for example `[BuffDiag]` goes to Buffing and `[LOS]` goes to Raycast.
- **Raycasting/LOS helper logs:** these used to go only to `Debug.WriteLine`, which is invisible in-game. They now appear under `/ra trace raycast`.
- **Exceptions:** the main plugin hooks now write full stack traces, each under its feature's category:
  - OnTick, OnRender, OnCreateObject, OnDeleteObject, chat bar, remote commands, raycast init, and the ILT Hub tick, chat and logout handlers;
  - each ILT feature's Tick, OnChat and OnLogout;
  - the store's load and save, the probe taps, and the capture callbacks.
- **Global hooks:** `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` are recorded too.
- **Throttling** uses UB's budgets: a 5-second burst window per signature, 25 entries per signature per day, and 500 per day in total.
- **Housekeeping:** files roll over at 10 MB, are kept for 7 days, and each folder holds at most 30 files.
  Writes are buffered and thread-safe, and flushed every tick, on logout and on unload.
- **ILT trace points:**
  - chat capture: enqueue, send, each captured or eaten line, completion and timeout;
  - server-option detection: refresh, dump, each probe, and every feature on/off change;
  - feature actions: bank refresh and transfer, rosters, quests and QB, augs, XP, raise, enlighten,
    balance changes, suit load and cancel, chunk and clap, Powerball;
  - gate refusals, Hub commands, confirmations, login and logout.

### Commands
| Command | Effect |
|---|---|
| `/ra debug [on\|off\|toggle\|status]` | Echoes trace and error lines to chat, like UB's debug switch. Drained on the game's main update loop, at most 20 lines per tick. |
| `/ra trace list` | Lists every category; an asterisk marks the ones being traced. |
| `/ra trace <cat> [on\|off\|status]` | Traces one function. Without on/off it toggles. |
| `/ra trace all\|ilt on\|off` | Traces every category, or only the ILT Hub ones. |
| `/ra trace` / `/ra logs` | Status: debug, file log, traced categories, and the folder. |
| `/ra logs open\|prune\|flush\|file on\|off` | Opens the folder in Explorer, prunes now, flushes, or turns the daily file log on or off. |

### UI
**Advanced Settings → Diagnostics** (new tab) has:
- checkboxes for debug-to-chat and the daily file log;
- one trace checkbox per function;
- buttons: All on, All off, ILT Hub on, and Prune old.

## Build / deploy
- Built from `D:\rynth-build\RynthSuite` against RynthCore `fix/tmds-dbus-advisory`. That branch is based on `main` plus the pin.
  `feat/ilt-hub` needs the `main` line of RynthCore and does not build against `SK-local`.
- NativeAOT publish: 0 errors, no IL or AOT warnings.
- Deployed to `C:\Games\RynthSuite\RynthAi\RynthCore.Plugin.RynthAi.dll` (0.5.2.0). The old 0.5.1.0 DLL is kept
  in `PreviousReleases\0.5.1\`. To roll back, copy it back while the game is closed.

## Upgrade notes
- No settings migration is needed. Tracing is off by default, so normal play only adds the daily `rynthai_*.txt` file. Turn that off with `/ra logs file off`.
- To report a bug, run `/ra trace <feature> on`, reproduce the problem, then `/ra logs open`. Attach the trace file and `exceptions_<date>.txt`.
