# RynthNav: a known portal taken again is not "learned" again (2026-11-01)

Silentkelpie's log for 09:13-09:31 showed the problem. Every portal a route took logged "custom
portal learned" and said "learned portal ..." in chat, though it and its landing were already in
custom-portals.txt: Vaulted Dungeons, AC Storage, Bandit Castle, Portal to Town Network, and so on.
Each one also rewrote the file and rebuilt the route hub ("learned dungeons for ..."). No
duplicates were saved: the existing entry was found and its landing written over with the same one.

- CustomPortalStore.Learned / LearnedInDungeon now compare the landing first. When it is the same,
  nothing is written and LastLearn is AlreadyKnown. A map landing counts as the same within
  30 yd; a dungeon landing within 6 m in the same landblock.
- The log says "already known", and chat says nothing. A new portal still says "learned"; a known
  portal that now lands somewhere else says "updated".
- A known portal's walk inside a dungeon is still kept when it is shorter, and chat says so.
- A scan that sees a saved portal where it was no longer rewrites the file.
- Test: RouteTests KnownPortalTakenAgainIsNotRelearned.
