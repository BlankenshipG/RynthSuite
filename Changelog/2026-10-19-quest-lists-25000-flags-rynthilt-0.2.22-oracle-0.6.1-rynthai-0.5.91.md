# Quest lists sized for 25,000+ flags: RynthILT 0.2.22, RynthOracle 0.6.1, RynthAi 0.5.91 (2026-10-19)

ProductVersion 2026.10.18.29 for all three. Previous releases: RynthILT 0.2.21 (2026.10.18.28),
RynthOracle 0.6.0 (2026.10.18.23), RynthAi 0.5.90 (2026.10.18.27). Goes with RynthCore engine
2026.10.18.29 (incoming chat queue raised to 65,536 lines).

## Why

Infinite Leaftide characters hold more than 15,000 quest flags, and the lists should cope with at
least 25,000. Silentkelpie's Dynamic Stamps rows on Oracle's Quests tab stopped showing progress:

- **Oracle read only part of `/myquests`.** It held 2,283 flags, and one reply after a hand-in
  arrived as 360 lines, which Oracle merged ("reply looked cut short"). Oracle ended a reply after
  1.5 s of silence, and a reply of thousands of lines reaches the client in pieces with pauses
  between them. One reply then became a short "complete" reply plus a merged tail. RynthAi's own
  reader stopped after just 1 s.
- **The `/qb` list could blank the whole RynthILT profile.** RynthILT put both full `/qb` lists
  into its "RynthILT.Server" profile JSON. Its readers (Oracle, and the shared `ServerStateClient`
  used by RynthAi and UbRythai) refuse anything over 512 KB, and 25,000 flags come to about 1 MB.
  Past that size every reader got an empty profile, losing the server features and fellowship
  rules along with the list. RynthILT also re-serialized both lists every second.
- **The parchment gives at 08:10-08:12 were refused by the NPC** (WeenieError 0x04CE then 0x04CD
  on 0xF03041C2), so no stamp was earned then. Oracle tracked that correctly.

## Changed

- **"RynthILT.Server" interface version 2** (`Plugins/Shared/RynthIltServerApi.cs`):
  - The profile's `questBonus` now carries only the summary, plus `completedCount` and `incompleteCount`.
  - The full lists come from the new `GetQuestBonusJson` call, built once per parsed `/qb list`.
  - Readers ask for `BaseVersion` 1 and accept a version-1-sized table (`V1Size`). Old and new
    plugins therefore keep working together in either order of deployment.
- **RynthILT 0.2.22:**
  - `QbHasFlag` (used by the Temple attribute tracker) is a hash lookup, not a scan of 25,000 strings.
  - The `/qb` panel draws at most 500 rows per list; the filter narrows them, and Copy still takes all.
  - Each parsed list is logged with its line count and JSON size.
- **RynthOracle 0.6.1:**
  - Reads the `/qb` lists through `GetQuestBonusJson` only when their generation changes, with a
    buffer that may grow to 8 MB. Lists still sent inline by an older RynthILT are used as before.
  - The Quests tab shows "Reading the list from RynthILT..." until the lists arrive.
  - It re-filters the `/qb` lists only when the list or the search changes, not on every draw.
  - `/myquests`: once a reply passes 500 lines, Oracle waits 5 s of silence before ending it.
    Short replies still end after 1.5 s.
  - Each long reply is logged with its line count and duration
    (`/myquests long reply: N flag line(s) over M ms`), to show where lines go missing if any still do.
- **RynthAi 0.5.91:** its `/myquests` reader (quest expressions, Meta Manager) uses the same rule:
  5 s of silence once a reply passes 500 lines, otherwise 1 s.

## Tests

- RynthOracle.Tests (337 passed):
  - 25,000 flags arriving in two pieces 3 s apart read as one full reply.
  - A short reply still ends after 1.5 s.
  - A counts-only profile waits for its lists; the lists fill it in; a new generation doesn't
    show stale lists; a broken list JSON is ignored.
- RynthILT.Tests `FeatureCatalogTests`:
  - The profile counts the lists instead of carrying them.
  - The list JSON keeps escaping.
  - A 25,000-flag profile stays under 64 KB while the list JSON holds every flag.
- The "move: ... redirect to RynthOracle" failure was already on the branch before this change.
