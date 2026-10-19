# RynthILT 0.2.21: Temple guardian hand-ins after a failed one, and vendor retries (2026-10-19)

ProductVersion 2026.10.18.28. Previous release: 0.2.20 (2026.10.18.23).

## What happened (Silentkelpie, 2026-10-19 07:38 to 07:40)

RynthILT translated every riddle the Guardian of the Temple of Enlightenment asked (Myrrh,
Bistort, Eyebright, Mugwort, Yarrow, Saffron) and started the auto hand-in, but never gave anything:

- **The first riddle (Myrrh):** Kelpie didn't carry it, so RynthILT used the vendor Archmage Yuzai
  Zhen to buy one, 12 s after entering the world. The server never answered that use (no vendor
  window, no UseDone), and RynthILT gave up after one try, 10 s later: "the vendor didn't open".
- **Every riddle after it:** "hand-in failed: timed out" 2 ms after it started. A hand-in that fails
  waiting for the vendor leaves its step deadline behind, and the next hand-in started with that
  expired deadline, so it failed on its first tick.

## Fixed

- **A hand-in starts with no step deadline.** Starting, failing and stopping a hand-in all clear
  it, so one failed hand-in no longer fails every one after it.
- **The vendor gets 3 tries.**
  - If the vendor hasn't opened 10 s after a use, RynthILT uses it again, up to 3 times, within the
    hand-in's 45 s.
  - A use the client won't send (busy) is retried shortly instead of being counted as sent.
  - The log says the vendor's distance and which try it is
    (`vendor for 'Myrrh': Archmage Yuzai Zhen ... at 12.3 m (use 1 of 3)`).

## Tests

RynthILT.Tests `GuardianTests`: a new hand-in does not inherit the last one's expired step deadline
(it is cleared, and the first step is not failed as "timed out"). The
"move: ... redirect to RynthOracle" failure was already on the branch before this change.
