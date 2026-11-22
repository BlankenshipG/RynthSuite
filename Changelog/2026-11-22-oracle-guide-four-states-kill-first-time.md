# RynthOracle: the guide's four quest states, and first time or done before on kill tasks

Silentkelpie, 2026-11-22:

> for all items on the guide tab in oracle regardless of server gating the kill task tab should flag if the kill task
> has been done before or if it is the 1st time. On all the quest, society, contracts tabs it should flag 4 states,
> quest fully done(no extra flags still needed), quest partially done(Stamps to still get) redo timer not up, quest
> partially done (Stamps to still get) timer up, or quest not done(no stamps done).

This applies on every server and to every guide: RynthILT's wiki, the AC Community Wiki, and a server plugin's guide
such as RynthConquest's.

## The four states

These show on the All, Story, Dailies, Weekly, Wiki, Society and Contracts tabs. A Contracts row shows the state of
the quest the contract is for.

A stamp counts as held when /myquests has it from any run, the account's /qb list names it, or it was stamped this
session. The timer is the longest redo time left on any held stamp. That is the flag's own timer, or else the quest's
repeat time counted from when the flag was stamped.

| State | List | Colour | When |
|---|---|---|---|
| Fully done | `Done`, `Done 5h` (timer running) or `Done, redo` (timer up) | green | every stamp held |
| Partly done, timer not up | `2/6 wait 5h` | yellow | some stamps held and some still to get, with the held stamps' timer still running |
| Partly done, timer up | `2/6 ready` | gold | some stamps held and some still to get, with no timer left |
| Not done | `Not done` | grey | no stamp held |

- **Tooltip:** hovering the status gives the full sentence, for example "Partly done: 2 of 6 stamps held, 4 still to
  get. The redo timer isn't up: 5h left." The opened quest shows the same sentence under its timer.
- **Quests with no stamps:** a quest the wiki lists no stamps for, and a quest-list quest, has one flag. It shows Done
  or Not done.
- **Hide collected:** hides only Done quests with nothing to do now. That means one-time quests, quests whose timer is
  running, and quests where every stamp was stamped on this run.
- **Status column:** widened to fit "2/6 wait 17h 59m".

## Kill tasks: first time or done before

- **The tag:** each Kill Tasks row gets a tag before its status:
  - `1st` (gold) for the first time;
  - `Again` (green) when the task was done before.

  Hovering the tag says which. The opened task says "(first time)" or "(done before)" after its kill count.
- **What counts as done before:**
  - a stamp of the task is held, apart from the stamps a first run holds too: the live kill counter, a "start" flag,
    and the first step's stamps (taking the task);
  - a flag of the task is on a redo timer;
  - a kill-task timer flag naming its creature is held;
  - the quest-list flag of its name is held;
  - the task is waiting on its timer, or completed.
- **Counters only /myquests knows:** `Again` once its timer is running or up, otherwise `1st`.

## Code and tests

- **New `Data/GuideProgress.cs`:** the states, the list words and tooltips, the short times, and the kill tag.
- **`QuestGuideTab.cs`:**
  - `WikiRow` and `CatalogRow` now give the four states.
  - New `HeldWait` and `ProgressColor`.
  - `GuideRow` gets `State`, `Tip` and `Tag`.
  - The list draws the tag and tooltips.
- **`KillTasksTab.cs`:** `KillDoneBefore` and `SetKillTag`. "Ready" now comes from the row's Done state.
- **Tests:**
  - `GuideProgressTests`: the states, the words and the short times.
  - WikiGuideTests: Not done, 1/6 ready, and 4/6 wait 18h with its sentence.
  - KillTaskTests: first time, counting or holding the start flag still counts as the first time, and a turned-in task
    is done before.
  - FandomTests: a stampless quest is fully done from its flag.
