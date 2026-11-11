# 2026-11-10 — Quest runs: into the portal at the step's spot; the last item ends the step

Silentkelpie: "when doing a quest, and route gets to a the route isn't going in the portal like it should,
and when a quest pickups the last loot item it should move to the next step vs keep fighting even if the
dungeon".

## Into the portal
- A travel step whose place is a dungeon's portal (the next step kills or loots inside, or goes in) now takes
  the portal standing right there (within 15 m, no scan), instead of calling the step done on arrival. Getting
  into the dungeon ends the step; with no portal there after all, being there ends it as before. A step into
  a building ("Enter the blacksmith building ...") still doesn't look for a portal.
- The portal use is watched: if it wasn't taken (a fight stopped ACE's walk to it, the portal out of reach),
  it is used again every 7 s while you're still in the same landblock, up to 4 tries, then the run says to
  walk in or press Next. This covers "dungeon enter" steps too.

## The last item ends the step
- A step that loots items is done the moment RynthAi has them all, stamped or not (it used to wait for the
  stamp when the wiki listed one, and kept fighting the dungeon).
- Moving on to a step that happens outside the dungeon (a hand-in, "Return to ...", "Bring ... to ...", or
  plain travel with nothing to kill, loot or enter) stops the dungeon walk at once; RynthNav takes the run
  out to it. A next step that fights or loots in the dungeon keeps the walk, as before.
