# 2026-11-14 — A corpse isn't finished before the client's contents are checked

Silentkelpie: "look at why silentkelpie missed looting necrolytic extract".

## What the log showed (RynthCore.7184.log)
- Three extracts that morning were looted fine (10:46, 10:57, 11:02:20). Each was found by the contents
  sweep ("found 1 item(s) ... the object cache had missed") and merged into the pack's stack.
- Two were missed: corpses 0xF0580C60 (11:02:25) and 0xF0523616 (11:02:31). Both opened with the cache
  already holding their other items ("cache pre-populated ... at 93 ms / 31 ms; skipping inv-wait"). All
  11 and 10 of those were judged, and each corpse was finished 130 and 230 ms after it opened. The
  extract in each was never judged. You found them by opening the corpses yourself (11:02:57 and
  11:03:42: "ignoring reopen of completed corpse"). The cache had never seen them created
  ("DELETE-BEFORE-CLASSIFY (seenCreate 0) e.g. 0xF080DD34").
- The one contents sweep on those corpses ran in the first tick. The extract wasn't readable in the
  client yet, so the sweep skipped it, and the corpse was finished before the next sweep 500 ms later.

## Fix (RynthAi, CorpseOpenController / WorldObjectCache)
- Before a corpse is finished, the client's list of its contents is checked once more:
  - The check comes at least 200 ms after the open (the floor an emptied corpse already had).
  - An item the cache hasn't got is looked up and judged first ("before finishing ..., found N item(s)
    the object cache had missed").
  - An item listed that can't be read yet (no name or position) holds the finish for up to 2.5 s after
    the open ("holds N item(s) that can't be read yet"). After that the corpse finishes as before.
- Corpses with nothing extra finish as before. One finished from the cache in under 200 ms waits until
  200 ms, the same floor an emptied corpse already had (jump pickups are no slower).

Tests: LootCorpseTests "loot chain (11-14)": an unread item holds the finish and is looted once it reads,
and the hold is capped.
