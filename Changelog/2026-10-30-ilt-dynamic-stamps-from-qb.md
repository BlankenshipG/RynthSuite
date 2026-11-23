# RynthILT: Dynamic Stamps come from the /qb list and the stamp lines

The Dynamic Stamps page only looked in /myquests, and Infinite Leaftide's /myquests leaves the
stamps out (only "dynamicquestflag" is there), so no stamp was ever shown done. RynthOracle's
version also looked in the /qb list; that part was lost when the page moved into RynthILT.

- A stamp counts when /myquests, the /qb list (completed or incomplete) or this session's
  "You've stamped Dynamic_..." line has it (the stamp shows done the moment the server says so).
- The page reads the /qb list once when it is first shown in a session, and again (with /myquests)
  a few seconds after each stamp; "Read quest flags" reads both. A /qb list refused for the server's
  once-a-minute limit is asked again when the minute is up.
- A sheet stamp copied with the server line's closing "!" also matches the flag without it.
- Guardian rows were "Ready" whatever the cooldown: that was RynthAi's /myquests parser (RynthSuite
  changelog 2026-10-30-rynthai-ilt-myquests-cooldowns).
- Tests: Tools/RynthILT.Tests StampTests "stamps: ILT's /myquests leaves stamps out ...".
