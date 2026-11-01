# RynthOracle: the quest guide, with the Leaftide wiki's quests (2026-11-01)

Silentkelpie asked for the Quests tab to pull in the extra quests from leaftidewiki.com
(Category:Quest_Index), shown like his screenshot: the quests on the left, the opened quest's steps
on the right with what you have done, a way to run the quest along its waypoints, and a loot map for
the quest's items.

## The wiki feed (RynthILT, RynthServers)

- leaftidewiki.com is MediaWiki 1.39, and every page needs a login. RynthILT signs in with the
  player's own bot password. On the wiki, Special:BotPasswords makes one; only "basic rights" is
  needed. The player types it into /ilt settings > Quest guide. It is kept on this PC only, in
  %APPDATA%\RynthCore\RynthILT\wiki-login.dat, with the password sealed by Windows DPAPI.
- RynthILT reads the Quest Index category (and its sub-categories) and every page with the Quest
  Summary box. It fetches only pages whose revision changed, and saves the result to
  wiki-quests.json. At login on ILT it reads the wiki again when the guide is over 24 h old.
- Without a login: Special:Export the pages, put the .xml in RynthILT\WikiExport, then run
  /ilt wiki import (or press Import in the settings).
- `/ilt wiki [status|refresh|import [file]|auto [on|off]|logout]`.
- "RynthILT.Server" v2 adds GetQuestGuideInfoJson and QuestGuideCommand at the end of the table.

## The page reader (RynthOracle Data/WikiQuestParser.cs, linked into RynthILT)

- It reads the Quest Summary box: level, recommended level, type, starts with/at, repeat timer, QB.
- It reads the Overview (or the Discord Description) and the Rewards tables.
- Walkthrough steps can be numbered `#` lines with their `#*` tips and `{{stamped|X}}`. They can
  also be plain paragraphs with the stamps listed apart in QB Stamps: a stamp is tied to the step
  that names it (NephriteTooth goes with the Nephrite Golem).
- Coordinates are read however they are written ("62.7s 66.6W", "85.8s,78.8w").
- The loot is the `{{Itemlink}}` items named in steps that kill, loot or find something. Items only
  handed in or upgraded are not loot.

## The Quests tab (RynthOracle)

- The Guide view is the default; "Flag list" brings back the full list.
- Tabs: All / Story / Dailies / Weekly. On ILT there are also QB / ILT Wiki / Stamps (the Dynamic
  Stamps by area).
- Hide collected, Refresh and search.
- Left: each quest with "n / total" stamps, "Completed", "Done 5h" or "Ready".
- Right: the opened quest shows its timer ("Daily (every 20h 00m)"), where it starts (Go), the
  overview, and numbered steps. Each step has a Go button per coordinate and its stamps, ticked
  "[x] You've stamped X!" when held. A stamp counts from the server's chat line at once. The loot
  and the rewards are listed after the steps.
- Run this quest (Data/QuestRunner.cs):
  - RynthNav goes to each step not yet stamped, in order. A step with several places goes to each
    in turn. A first step with no place goes to where the quest starts.
  - On arrival, the step's instructions go to chat. Its stamp moves the run on, or Next does. Back,
    Again and Stop are there too.
- Loot map: the quest's items go on RynthAi's new quest loot list while the run lasts.

## RynthAi

- New: the quest loot list, `/ra questloot add|remove|clear|list|on|off`, and the broker action
  "questloot". Items are matched by exact name, before the loot profile, on corpses and the ground.
  It lasts this session only.

## Also fixed

- RynthOracle's settings file: keys missing from its known list were written twice on every save.
- RynthOracle.Tests: the Quests column checks now follow upstream's column scaling (5c51ce6). They
  failed since that merge.
- Tests: RynthOracle.Tests WikiGuide (both sample pages, the guide JSON, export reading, the view,
  the runner); RynthILT.Tests WikiFeedTests (API answers, merge/import, files, /ilt wiki) and the v2
  table in FeatureCatalogTests; RynthAiHostTests QuestLootTests.
