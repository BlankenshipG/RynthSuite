# RynthOracle 0.3.3 — Quest refresh no longer blanks the list

**Date:** 2026-10-16  
**Kind:** Hot push (HandWritten `0.3.2` → `0.3.3`)  
**ProductVersion:** `2026.10.16.20`

## Problem

Quest flags (Quests tab done marks, Markers, Quest Timers) refreshed fine once per
session. A second Refresh in the same session — Oracle window open — reported it had
read the flags, but nearly everything showed as not done.

## Cause

- A `/myquests` reply is one chat line per flag (hundreds on a played character) in one
  burst. The engine's plugin chat queue held only 256 lines and dropped the oldest when the
  plugin pump fell behind (a busy Oracle window slows it). Oracle then took the surviving
  tail as the complete list and replaced every flag with it.
- Infinite Leaftide (ACECustom) answers `/myquests` once a minute per session. A Refresh
  inside that minute (or after RynthAi's quiet poll) only got
  `[MyQuests] This command may only be run once every 60 seconds.` and sat on "Reading…".

## Fix

- A reply much smaller than the flags already held (under 80 %, with 20+ held) is merged:
  the lines it carried update, the rest are kept. A full or near-full reply still replaces.
  The merge is logged (`/myquests reply looked cut short …`).
- The throttle line is recognised: "Reading…" ends at once, the flags are kept, and the
  Quests tab shows when Refresh works again. Refresh doesn't spend a request the server
  would refuse.
- Engine side (RynthCore `2026.10.16.20`): the plugin chat queue is 4096 deep and logs drops.

## Test

1. Open Oracle → Quests, Refresh; wait a minute; Refresh again with the window open.
2. Done marks stay; within the minute the tab says how long until Refresh works.
