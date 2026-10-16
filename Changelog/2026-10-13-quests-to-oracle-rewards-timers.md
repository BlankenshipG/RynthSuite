# RynthOracle 0.2.2, RynthAi 0.5.57, RynthILT 0.2.3: quests merged into RynthOracle, with rewards and a timers window

All quest features now live in RynthOracle. It adds per-quest rewards (as UtilityBelt ILT
records them) and a Quest Timers window that lists the quests you track with the time left on
each. The Quests tab and its favorites HUD are gone from RynthAi's Mini Remote.

## What changed

- **RynthOracle Quests tab.**
  - A `*` column tracks a quest in the Quest Timers window. A "Tracked" filter shows only tracked
    quests.
  - A "Kill tasks" filter shows the kill-task counters you hold. Kill tasks show "N left" while
    counting, then the reset countdown, then "done" (UtilityBelt ILT's rule). Kill-task flags are
    recognised by name, as RynthAi's tracker did.
  - A "Rewards" column shows what you recorded, in UB's short form: `1.5%XP 2%Lum 3C 1T 2M 1Y 1R 1B 2D L275`.
  - Click a quest to open it below the list: flag, status, solves and notes; Track/Untrack; Print
    in chat; and the rewards editor. The editor has XP %, Lum %, Coins, Trinkets, Medallions,
    Yellow/Red/Blue powder and MMDs, with Save and Clear. "Level: N (XP % and Lum % are for this
    level)" shows the level the percentages belong to. Percentages are clamped to 0..1000 and
    counts to 0 or more. The level is recorded when the percentages change.
  - On Infinite Leaftide a "Quest bonus (/qb)" section shows RynthILT's `/qb list`: the count, the
    status, and the incomplete and completed flags. The Search box filters it, and up to 150 per
    list are shown. "Load /qb list" sends `/ilt qb`.
  - "Show quest timers" opens the new window.
- **RynthOracle Quest Timers window** (`RynthOracle/QuestTimers`). It lists the tracked quests,
  ready ones first and then by time left. Each row shows the name and "ready" (green), the
  countdown, "N left" (yellow), or done/maxed/completed (grey). Hovering a row shows the flag and
  the recorded rewards, and "x" stops tracking it. A Refresh button sends `/myquests`. Open it from
  the Quests tab, `/ro timers`, or RynthCore's Char menu. It reopens at login if you left it open.
- **Per-character quest book.** Tracked quests and rewards are saved in
  `%APPDATA%\RynthCore\RynthOracle\characters\<world>\<character>.json`. The file is plain ASCII,
  written by hand, and replaced atomically on every change. The first time a character has no
  book, UtilityBelt ILT's `quest_rewards.json` for the same world and character is imported once
  (`Documents\Decal Plugins\UtilityBelt\<world>\<character>`). The book loads once the world name
  is known, or after 15 seconds without one.
- **RynthILT.** The "RynthILT.Server" profile JSON gains a `questBonus` object on ILT worlds:
  `disabled`, `generation`, `count`, `status`, `completed[]` and `incomplete[]`. It is an optional
  field, so older readers ignore it. A new hub verb, `/ilt qb` (also `/ra hub qb`), loads the list.
  The feature list now says quests and the quest bonus live in "RynthOracle > Quests tab".
  RynthOracle reads up to 512 KB of profile (previously 64 KB).
- **RynthAi Mini Remote.**
  - The Quests tab, its floating favorites HUD and `MiniRemote/QuestsTab.cs` are removed. The
    universal tabs are now Pets and Crafting.
  - `/ra quests` (and `/ra quests refresh`) still sends the meta tracker's quiet `/myquests` with
    its 60-second cooldown. That logic moved into `MiniRemoteController.RefreshQuests`, so
    RynthILT's Temple tracker (RynthAi.Hub QuestRefresh) still works.
  - `/ra quests window|favhud|check` and `/ra hub open|tab quests` reply with where quests went.
  - The meta QuestTracker, the `/myquests` parser and `/ub quests check` are unchanged.
  - The right-click Character menu has "Quests" (`/ro`) and "Quest Timers" (`/ro timers`).
  - Saved `miniremote.json` quest fields are kept, so old files still load.

## Files

- RynthOracle:
  - New: `Data/QuestRewards.cs` (entry, UB summary format, parsing), `Data/QuestBook.cs`
    (per-character store, UB import), `Data/QuestTimer.cs` (timer and kill-task text),
    `Views/QuestTimersView.cs`.
  - Changed: `Views/QuestsTab.cs` (track column, filters, Rewards column, opened quest and editor,
    `/qb` section); `Data/IltServerProfile.cs` (`questBonus`, 512 KB buffer);
    `Data/QuestCatalog.cs` (`Quest.Label`).
  - Also changed: `OracleContext.cs`, `OracleSettings.cs` (`questTimersOpen`), and
    `RynthOraclePlugin.cs` (the timers window, `/ro timers`, book loading).
  - Version 0.2.2 (previous 0.2.1), in `RynthOraclePlugin.Version` and `PluginExports.cs`.
- RynthILT: `Hub/IltFeatureCatalog.cs` (`IltQuestBonusInfo`, `questBonus` JSON),
  `Hub/IltQuestBonus.cs` (snapshot accessors), `Hub/IltHubController.cs` (`qb` verb, profile with
  `/qb`), `RynthILTPlugin.cs` (version 0.2.3, previous 0.2.2).
- RynthAi:
  - `MiniRemote/QuestsTab.cs` (deleted).
  - `MiniRemote/MiniRemoteController.cs`: refresh logic, retired verbs, help text.
  - `MiniRemote/MiniRemoteContext.cs`: tabs.
  - `MiniRemote/MiniRemoteState.cs`: comment only.
  - `Huds/MiniRemoteHud.cs`: menu items.
  - `RynthAiVersionCommand.cs`: 0.5.57-legacy-ui (previous 0.5.56-legacy-ui).

## Tests

- RynthOracle.Tests: 191 checks pass. New checks:
  - UB's summary and percent format, parsing and clamping.
  - The quest book saving, reading back, staying ASCII, untracking and clearing.
  - The UB import running once.
  - Kill-task and timer states.
  - The `questBonus` parse.
  - The Quest Timers window.
  - The ILT Quests page with a quest opened and the `/qb` list open (136 ops, within the engine's
    1500).
- RynthILT.Tests: 55 pass, including "the /qb list rides the profile JSON on ILT worlds only".
- RynthAiHostTests: the Mini Remote tab list is Pets and Crafting, and the quest verbs point to
  RynthOracle. The one failure is the existing map-bake check, which needs a live plan for
  landblock 0x6346 on this machine.

Not yet tested in game.
