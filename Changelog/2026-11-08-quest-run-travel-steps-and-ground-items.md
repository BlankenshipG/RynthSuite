# 2026-11-08 — Quest runs: travel steps finish, items off the ground

From Silentstorm's Acid Axe Quest run (09:01): the run went to Suntik Village and stopped there. It didn't pick
up the axe in the blacksmith's building or go on to the Warden.

## Travel steps
- A step that only travels ("Travel to Suntik Village at 16.2N 4.3E.": no stamp, no item, nobody to talk to,
  nothing to go into) is done on arrival. It had waited for Next, so the run stayed on step 1.
- A step goes to the coordinates in its own words. Coordinates that only its notes give for another way
  there ("Route 2: ... to 16.9N 3.5E, a section of the hill that can be climbed") are not a second stop.
  Next and the fallback for unknown names use the same places.

## Item steps
- A step that gets items and has no stamp ("grab the Acid Axe on the ground") asks RynthAi whether they are
  carried ("questloot have ..."). It is done when RynthAi says it has them all: "[RynthAi] Quest: got Acid Axe."
  (picked up) or "... (already in your pack)." (picked up by hand, or before the step).
- A pick-up step that names no place of its own is done where the run already is (the place of the step
  before). The run doesn't travel anywhere else first.
- "Enter the blacksmith building ..." walks into a building. It doesn't use the nearest portal.

## RynthAi: quest items off the ground
- While a quest run's items are on the quest loot list, RynthAi picks them up off the ground even with
  Ground Loot off (only those items), within 40 m (or the corpse range if it is wider). ACE walks the
  character to the item.
- Every quest-list item picked up (from a corpse or the ground) says "[RynthAi] Quest: got <name>.".
- "/ra questloot have <item> | <item>": says which ones are carried.
