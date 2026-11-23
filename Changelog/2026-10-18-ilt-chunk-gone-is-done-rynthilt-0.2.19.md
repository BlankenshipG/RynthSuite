# RynthILT 0.2.19: a chunked item that leaves the pack counts as chunked (2026-10-18)

ProductVersion 2026.10.18.22. Previous release: 0.2.18 (2026.10.18.21).

## Fixed

- **A chunk that worked was filed as "skipped".** Silentkelpie's log (14:46) shows the Lugian
  Aetheria Hammer used on a Coalesced Aetheria. The server finished the use with no error and a Red
  Aetheria Chunk arrived, but ILT sent no result line that RynthILT knows. RynthILT waited the full
  10 s, then logged "no result line ... skipped until a manual pass". Every pass that hit this lost
  10 s.
  - Now, while a chunk is in flight, a target that has left the pack counts as chunked straight
    away: the hammer destroys it.
  - The log says `chunk: 0x... left the pack with no result line; counted as chunked`.
  - Only a target that is still carried after 10 s is skipped.

## Log review (no change needed)

The rest of what Kelpie wasn't chunking is kept by a rule:

- **14 Coalesced Aetheria with item XP** (ItemTotalXp > 0, from mana stones or wearing). ub-IT
  never chunks these either (UB-ILT 2.0.468), so RynthILT keeps them.
- **Level at or above the cap** (cap 9): level 9 and 10 pieces.
- **Rating keeps** at 14:45, while C was on with rating level 4: `[C 4]` was kept and `[C 2]` was
  chunked. The C / CD / CDR checkboxes were turned off after that, and the remaining rated pieces
  chunked at 14:46.
- **8 corrupted weapons, tinkered or imbued.** This is ub-IT's hard rule too.

## Tests

`RynthILT.Tests/AetheriaChunkTests.cs` has a new test for the wait: still carried and waiting, gone
(chunked), gone after the timeout, and still carried at 10 s (timed out). The "move: ... redirect to
RynthOracle" failure was already on the branch before this change.
