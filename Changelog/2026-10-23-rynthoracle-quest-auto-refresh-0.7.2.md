# RynthOracle 0.7.2: quest flags stay current on long sessions

Build 2026.10.20.10 (RynthOracle `Version` 0.7.2).

## Fixed

- Silentkelpie, 48 hours into one session: the Quests tab's filters (Done / Not done, Ready,
  Tracked) and the Quest Timers worked from stale data. RynthOracle read `/myquests` once at login
  and after that only took replies other things asked for (RynthAi's meta `refreshquests[]`, its
  Meta Manager poll, RynthILT's Temple tracker, or a typed `/myquests`). When those stopped, at a
  portal at 07:48 that morning, the last reply (07:49, 2644 flags) stayed on screen: quests done
  since never showed as done, and finished cooldowns only as far as the old flags allowed.
  Nothing else was wrong: no cache growth, no errors, the flag list itself stayed whole, and
  RynthOracle never showed up as slow.
- If the login read never got a reply, the quest pages stayed empty until something else asked.
- "Check once a day for a newer quest list" ran once per login only; on a multi-day session it
  now runs every day.

## Added

- About > Settings: **Read quest flags again when older than** Off / 5 / 10 / 15 / 30 minutes
  (default 10, `questRefreshMinutes` in rynthoracle.json). RynthOracle sends `/myquests` (reply
  hidden, as at login) once nothing has read it for that long. Any reply resets the clock,
  RynthAi's polls too, so it only fills the gaps; it never sends while a reply is on its way,
  where the server has `/myquests` off, or inside the server's once-a-minute limit. A lost reply
  is retried after the same wait, not every tick. The log says when it fires.

## Tests

- RynthOracle tests: the auto refresh rule (default on at 10 minutes; not before 10 minutes;
  Kelpie's 16 minutes is due; Off never sends; not while a reply is coming, where the server has
  `/myquests` off, or inside the server's minute). 573 passed, 0 failed.
