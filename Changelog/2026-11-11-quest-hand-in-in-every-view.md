# 2026-11-11 — One quest hand-in in every Quests view

Silentkelpie: "look at the stamp handing or quest hand in make sure all tabs under quests support the same
function to hand the quest item in after quest compeletion step".

Before this, only a full "Run this quest" handed items in (at its hand-in step), and only the flag list's
Dynamic Stamps had "Go and hand in". Now every view that has a quest to hand in offers the same two buttons:
- **Go and hand in.** Starts the quest run at its hand-in step. RynthNav goes to the person (outdoors, in a
  dungeon or in the Bur), and RynthAi hands over the quest's items there through the give queue. If this
  quest is already running, the run jumps to that step instead.
- **Hand in here.** For when you're already standing by them: RynthAi gives the items to that person
  (within 60 m).

The hand-in is the wiki step that hands something over ("Return the collar to Xsao", "Bring ... to ...").
When the wiki has no such step but the rewards are for handing items in ("For Hand in Female Tusker Tusk:"),
the items go to the person the quest starts with.

Where it is now:
- **The guide's All, Story, Dailies, Weekly, QB and ILT Wiki tabs:** an opened wiki quest, and an opened
  master-list quest. A master-list quest is matched to its wiki page by its flag (one of the page's stamps)
  or by its title.
- **The flag list:** an opened quest and an opened Legendary row (matched the same way). If no wiki page is
  found, the panel says who takes the items isn't known.
- **The guide's Stamps tab:** each stamp's Go is now the flag list's Go and hand in (the Parchment, bought
  from a vendor when you carry none), with its progress shown under the area.

Unchanged: the Temple guardians already hand in through RynthILT (Hand in / Auto hand-in), and the
exploration markers have nothing to hand in.
