# RynthILT 2026.10.20.7: aetheria item-XP keep switch

## Fixed

- Silentkelpie: a Coalesced Aetheria with CDR 4 was never chunked, even with the CDR keep switch
  off and the cap at 9. The chunk log said `keep 0xF05D639E 'Coalesced Aetheria': has item XP`.
  The item-XP rule (from ub-IT's AetheriaChunkGuard: XP means the piece was levelled by use)
  runs before the C / CD / CDR switches and the level cap, so those never applied. 11 of the
  character's Coalesced pieces read with item XP on Infinite Leaftide.

## Added

- Chunking setting **Never chunk aetheria with item XP** (`KeepAetheriaWithItemXp`), on by default
  as in ub-IT, so other characters behave as before. Turned off, item XP no longer protects a
  piece: it chunks when the level cap and the C / CD / CDR switches allow.
- Item XP now counts only when it is above 0, as in ub-IT (a negative read used to keep a piece).

## Tests

- RynthILT test "aetheria: the item-XP keep switch": on by default; with Silentkelpie's settings
  a level 7 CDR 4 piece with XP is kept with the switch on and chunks with it off; the cap and the
  CDR switch still apply.
- The RynthILT test project has two stale files from before this change (`ProfileTests.cs`,
  `IltSettingsTests.cs`) that no longer build; they were set aside for this run and left as is.
