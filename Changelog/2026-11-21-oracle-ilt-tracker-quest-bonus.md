# RynthOracle: the T11 Tracking tab is the ILT Tracker, with quest bonus levels, a bonus tracker and the wiki how-to

Silentkelpie, 2026-11-21: "look at acecustom add in itl qb bonus levels and bonus tracker under t11 tab, change t11
tab to itl tracker, add in the itl wiki how to to this tab as well".

## What ACECustom does (Source/ACE.Server, read 2026-11-21)

- The quest bonus (QB) is the account's count of quests with a completion row (AccountQuest, NumTimesCompleted >= 1).
  Each quest counts once per account, the first time any character finishes it (QuestManager).
- Each QB is +0.5% kill XP: `GetQuestCountXPBonus() = 1 + QB x 0.005` (Player_Xp.questToBonusRation).
- `/bonus` prints four XP multipliers: Quests; Equipment (1 + XP enchantments + 5% per Bonus XP aug); Enlightenment
  (1 + ENL x 0.1); and the Total, their product.
- `/qb` prints the count, and `/qb list` prints the completed and incomplete quest bonus lists. `/top qb` is the
  leaderboard. Portals can require a QB amount or a total multiplier.

## The tab

- **Renamed:** "T11 Tracking" is now **ILT Tracker** (Infinite Leaftide only, as before). A saved "T11 Tracking" tab
  opens it.
- **Quest bonus levels:** shows the QB and the quest XP multiplier. Bonus levels are every x0.25 of the quest
  multiplier, which is 50 QB. The tab shows a progress bar to the next level and the four levels after it. These
  levels are RynthOracle's own steps: the server has none, and the multiplier grows by 0.005 with every QB. The QB
  comes from RynthILT's /qb read, else a `/qb` count in chat, else the /bonus Quests multiplier.
- **XP multipliers:** the last `/bonus` reply (Quests, Equipment, Enlightenment, Total), with how each is made. The
  lines are read and never eaten. The tab asks for `/bonus` once a session.
- **Quest bonus tracker:** RynthILT's /qb list in two groups. "To finish" holds the started quests; each one finished
  is +1 QB. "Done" holds the counted ones. Each list has a filter and a toggle to hide Dynamic stamps, and a row links
  to its wiki quest when the guide has one. The tracker also shows what finishing every started quest would give. The
  tab asks RynthILT for the list once a session, since the server allows /qb list once a minute.
- Buttons: `/bonus`, Read /qb list (`/ilt qb`), `/top qb`. The tab's Refresh also asks for /bonus and the /qb list.
- **ILT wiki: how to get its login:** RynthILT's WikiLoginGuide steps, each with Open page for its leaftidewiki.com
  page. Only those three fixed addresses open, in the system browser. It also shows the wiki status and has
  RynthILT settings, Read the wiki now, and /ilt wiki help buttons. RynthOracle never sees the login.

## Tests

- `QuestBonusTests` (RynthOracle.Tests) covers:
  - the four /bonus lines, which are read as one reading at Total;
  - x2.35 = 270 QB;
  - the /qb count line;
  - quoted chat is not read;
  - 270 QB = level 5, 30 QB to level 6 (x2.50);
  - which count wins;
  - logout;
  - the guide opens only its own pages.
- BoundaryTests: the three new ILT-gated files are listed.
