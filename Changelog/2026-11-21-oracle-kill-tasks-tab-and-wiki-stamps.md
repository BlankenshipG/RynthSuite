# 2026-11-21 — RynthOracle: Kill Tasks tab; wiki stamps read right

Silentkelpie: "look at what is brought down from ilt wiki page does it update all tabs with changes also add a tab
to track kill tasks, the ilt wiki should have a list of kill tasks and stamps used to track what they are and you
should be able to track progress by looking at quests in the client".

## What a wiki read updates (before this change)
- All, Story, Dailies, Weekly, QB and ILT Wiki are built from the guide RynthILT saves, so they all change when
  it does.
- Stamps (the Dynamic Stamps by area) is not from the wiki: it is the dynstamps.csv shipped with RynthOracle
  (the players' sheet of 2026-10-13).
- A page the wiki hadn't edited was never read again. What an older RynthOracle took from it stayed, however
  often the wiki was read.
- 128 stamps were written with words after the flag, so they never matched /myquests:
  - 125 kill tasks write "{{stamped|KillTaskDrudgeLurkers_0507 on first completion}}";
  - three pages write "You've stamped X!".
  Every kill task's counter step stayed unticked.

## Fixes
- A stamp is cut to its flag: "X on first completion" is X, and "You've stamped X!" is X. A guide saved
  earlier reads right at once.
- The page reader has a version, saved in wiki-quests.json ("parser": 2). A guide from an older reader is
  read from the wiki again, every page:
  - at the next login on ILT, whatever its age;
  - or on Refresh or "Read the wiki now".
  Later reader changes reach every page the same way.

## Kill Tasks tab (Quests > Guide, Infinite Leaftide)
- The list is the wiki's kill tasks: the Quest Summary box's "Kill Task = Yes", 150 pages. Pages whose box
  doesn't say are added by their title ("... Kill Task", "Kill: ...").
- Each task shows its progress, the ones to act on first:
  - "Turn in": all kills made.
  - "37 / 100": counting.
  - "Ready": can be had again.
  - "0 / 100": not started, with the goal from the walkthrough.
  - "Done 5h": waiting on its timer.
  - "Completed".
- The kills come from the client's quests (/myquests): the task's counter flag, e.g.
  KillTaskDrudgeLurkers_0507 at 37 of 100. Seven tasks list no stamps; these find their counter by the
  creature's name.
- The server's kill line moves the count at once ("You have killed 37 Drudge Lurkers! You must kill 100 to
  complete your task."). While the tab is open, /myquests is read again after kills, at most once a minute
  (the server's limit).
- Kill counters /myquests holds that no wiki task names are listed too, with their flag.
- A summary line over the list: counting, to turn in, ready, and when /myquests was read.
- The opened task is the usual wiki quest view (steps, Go, Run this quest). Over it is the kill count, with
  where the count came from.
- Hide collected leaves out the tasks waiting or done.

Tests: RynthOracle.Tests KillTasks (stamp cutting, the old guide read right, kill task facts, kill lines, the
tab with counter flags, a chat count, turn in, waiting, a counter only /myquests has, hide collected).
