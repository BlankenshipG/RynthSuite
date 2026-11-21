# 2026-11-21 — RynthOracle: the Stamps tab uses the wiki's Dynamic Stamps page; the wiki's kill task list

Silentkelpie: "the dynamic stamp tab should be using this tab https://www.leaftidewiki.com/Dynamic_Stamps".

## The Stamps tab
- RynthILT now reads the wiki's Dynamic Stamps page with the quest pages (one more request) and saves its rows in
  wiki-quests.json ("dynStamps").
- The page as it stood today: 63 town and camp tables plus Miscellaneous, 585 stamps.
  - Each row has the NPC, coordinates, stamp, item reward and notes.
  - Rows with "NO STAMP" or "?" are left out, and so is the "Original Unsorted List" (stamps without an NPC).
  - A stamp listed twice counts once.
- Areas are the page's towns and camps ("Arwic", "Gear Knight Camp, Inner Sea", "Kor-Gursha / Bur"), plus
  "Miscellaneous", where the notes say the place.
- RynthNav goes to a row's coordinates first, then the place and the NPC, as before.
- The flag list's Dynamic Stamps view uses the same list.
- The sheet shipped with RynthOracle is used until RynthILT has read the page, and again off ILT. A line over
  the tab says which list it shows.

## Kill tasks
- RynthILT also reads the wiki's own list of kill tasks, Category:Kill Tasks (the page "Kill Tasks" redirects
  to it): 151 pages. Their pages are read like the quest pages.
- The Kill Tasks tab lists every page in that category, so it now includes the ones whose summary box doesn't
  say "Kill Task = Yes" (Weeding of the Deru Tree, Torgash's Tasks, Moarsmen Jailbreak, ...).

Tests: RynthOracle.Tests WikiDynamicStamps (the page's two table layouts, headings, NO STAMP, duplicates,
Miscellaneous, the unsorted list; the guide file; the Stamps tab from the wiki and back to the sheet), KillTasks
(the category); RynthILT.Tests "wiki (11-21)" (the page taken or kept, carried over merge and import).
