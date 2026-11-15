# 2026-11-15 — Quick give: Amount and Every

Silentkelpie: "on item tool add in a option to give x number of a item and a timer of how often every x
seconds."

## Item Tool > Quick give
- **Amount (0 = all)**: how many a round gives.
  - 0 is the old behaviour: every matching item in your packs.
  - Whole items count one each. A stack bigger than what's left of the round is split, using AC's
    give with an amount: 20 from a stack of 50 leaves 30 in the pack.
- **Every (s, 0 = once)**: the seconds from the end of one round to the start of the next.
  - 0 is one round, as before.
  - Set above 0, the give goes on until you press Stop.
  - Between rounds, having none of the item, the target out of range or out of view, or a fight only
    delays the next round, and the status says why it's waiting. New ones that reach your packs go in
    the next round.
  - During a round, the target leaving, a fight, or a long wait on a busy client ends that round, not
    the run.
  - A changed target, three refusals in a row, or a client that won't send still end it.
- Both are fields under the item and target picks. A value goes on Enter or a +/- click. A running
  give takes a new Amount at its next give and a new Every at its next round.
- The status line shows the round ("Round 2: 10 of 20") and, between rounds, the countdown to the next.
- Commands: `/ra itemtool giveamount <units>` and `/ra itemtool giveevery <seconds>`. Either alone
  shows the current value. A meta may set them, because they only shape a give; starting one is still
  yours alone.
- They last for the session, like the picked item and target, and aren't saved in the profile.
- The fields show only when RynthCore and RynthAi are both this version or newer. An older RynthAi
  hides them (no "amountMax" in its data).

Tests: ItemToolTests "quick give (11-15)" (amount, stack split, rounds, waiting between rounds, mid-round
fight, changed target) and "item tool (11-15)" (the commands, the panel data, a real give of 2).
