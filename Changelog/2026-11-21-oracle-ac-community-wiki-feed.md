# 2026-11-21 — RynthOracle: its own wiki feed from the AC Community Wiki; Wiki, Kill Tasks, Society and Contracts tabs on every server

Silentkelpie: "under orcale duplicate itl functions for pulling in overall quests, and orginization and lay out tabs
for quests, kill task, socity quests, contracts, pull a dynamic feed from https://asheron.fandom.com/wiki/Category:Quests
for the quests, .../Category:Kill_Tasks for kill task, .../Category:Society_Quests for socity quests,
.../Category:Contracts for contracts. each quest should follow the logic to auto build routes, and loot profiles,
based on quest info, and steps based on the quest info and any stamps provided like itl, but should not be gated
behind any server check."

## The feed (Data/FandomFeed.cs, Data/MediaWikiJson.cs)
- RynthOracle now reads asheron.fandom.com itself, on every server. It uses the MediaWiki API anonymously; no
  login is needed.
- It is a copy of RynthILT's wiki reader (that wiki is a fork of this one, with the same templates). What it
  reads:
  - **Quests:** every page with the Quest Summary box (779 today), plus the pages under Category:Quests. Its
    sub-categories are followed, except item, NPC and image lists (about 1,050 pages in all).
  - **Kill tasks:** Category:Kill_Tasks and its sub-categories (145).
  - **Society quests:** Category:Society_Quests. Each page's rank comes from the sub-category it is in
    ("Society Quests: Initiate Rank" is Initiate; "Joining a Society Quests" is Joining), or from the box's
    "Society Rank".
  - **Contracts:** Category:Contracts' item pages (242). For each it takes the name, description, recommended
    level, where it comes from ("Purchased from" or "Given by" the person, from the notes) and the quests it is
    for (the Intro box's "Related Quests").
- It reads at login when the saved copy is a day old (or none), and on Refresh.
- Only pages edited since the last read are fetched, by their revision ids. Every page is fetched again when the
  page reader changes.
- The copy is saved to %APPDATA%\RynthCore\RynthOracle\fandom-quests.json.
- The source line above the list says how many quests and contracts it has and when it read them, or how a read
  is going.

## The tabs (Quests > Guide)
- Row 1: All, Story, Dailies, Weekly, and **AC Wiki**, which lists every wiki quest. On Infinite Leaftide it is
  "ILT Wiki" once RynthILT has read that wiki.
- Row 2: **Kill Tasks**, **Society**, **Contracts**, plus QB and Stamps on Infinite Leaftide.
- The wiki in use is the AC Community Wiki's on every server. On Infinite Leaftide RynthILT's own wiki is used
  once it has quests. A contract's or society quest's page missing there is taken from the AC Community Wiki.
- **Wiki quests in All, Story, Dailies and Weekly:** these tabs now list wiki quests on every server. The quest
  list's entry of the same name gives way to the wiki quest, which has steps.
- **Every wiki quest has the same tools as Infinite Leaftide's:**
  - Run this quest: RynthNav's route step by step, talking to people and handing in.
  - The loot map, and the quest's own loot profile (base-quest-dynamic plus the items its steps loot).
  - Go on each place.
  - `/ro lootprofiles` uses the wiki in use.
  - The quest offer and hand-in views use it too.
- **Kill Tasks:** as on Infinite Leaftide (counters from /myquests, the kill lines in chat), now on every server.
- **Society:** the society quests by rank (Joining, Initiate, Adept, Knight, Lord, Master), each the usual wiki
  quest view.
- **Contracts:** each contract with the state of the quest it is for.
  - Opened, it shows the description, the level, where it comes from, and "Go to" the person (RynthNav).
  - It lists the quests it is for, with Open, which shows the quest with Run this quest and its loot profile.
- **Quests with no stamps:** the AC Community Wiki has no stamp templates. A wiki quest without stamps takes its
  state (Completed, Done 5h, Ready) from the quest list's flag of the same name, where there is one. Stamps are
  still read wherever a page has them.

## Also fixed
- A repeat timer written "Non Repeatable" (the AC Community Wiki's way) now reads as one-time. Before, it read
  as unknown, so those quests weren't in Story.

Tests: RynthOracle.Tests Fandom (a kill task, society and contract page; ranks, skipped categories, a category
answer, pages to read, the merge, when to read; the guide file; the tabs off Infinite Leaftide with Run this
quest, loot and loot profile; a stampless quest's state from the quest list). The guide and kill task tests now
expect the wiki tabs everywhere.
