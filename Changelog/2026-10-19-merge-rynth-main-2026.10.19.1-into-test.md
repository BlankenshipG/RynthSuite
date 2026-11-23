# Merge rynth main (2026.10.18.1 and 2026.10.19.1 snapshots) into the test branch (2026-10-19)

Brings rynth's two newest release snapshots (aelrynth main `5419838` and `39de36d`) into
`test/hotbuild-2026-10-16`, by way of `test/hotbuild-2026-10-16-on-aelrynth`. The owner asked for a
careful merge, keeping both sides' changes. The plain merge had 42 conflicting files (151 sections).

## How it was merged

- **Files rynth built on our published `sk` code (85% or more of it adopted).** These were merged
  against `upstream/sk` as the base, so his edits land on top of ours. Thirteen files, among them
  `IltChunkClap`, `IltVaultedDungeons`, `PetCaptureRules`, `PetManager` and the legacy settings UI.
- **Every other file.** Resolved section by section against the true merge base.

## Taken from rynth

- **RynthAi.**
  - `PetCaptureThrottle`: at most 5 lens uses per creature, and auto-capture switches off after 15
    in a row with no capture.
  - The `HandlerRingEvade` setting and its gate: the macro must be running with combat on.
  - The Options button and gear, with the header row sized to the narrowest remote.
  - Nullable settings mirrors; the attack-power rule lookup skipped when no rule sets a power;
    `MergeProgressed`; kite settings in expressions and limits; `/ub videopatch` forwarding to
    `/rc videopatch`.
- **RynthILT.**
  - The narrowed bank-dump backstop (`EatLeakedDump`). A typed `/bank` now also clears the quiet
    window, so the player's own read always shows.
  - The Vaulted Dungeons master switch (`VaultedDungeonsEnabled` in RynthAi.Hub), `RynthAiActivityApi`,
    `PetCaptureLines`, and ChunkTarget / VaultedDungeons tests.
- **RynthOracle.**
  - Refresh rows with help on every tab (`RefreshBar`).
  - The `SkillsEmbed` state machine. Our older `SkillsEmbedChanged` hook is removed: both sent the
    same `/rc oracle-skills` commands.
  - `ReloadQuestList`, `RereadCharacter` / `RereadTitles`, and a click on a quest printing its texts.
- **RynthSort.** `RemoteSortArgument`.

## Kept from the test branch

- **RynthAi.** The Handler / Chaser lethal-ring run-away escape (2026-10-17), now behind rynth's
  gate (setting, macro running, combat on).
- **RynthOracle.**
  - The Quests tab rewrite: trackers merged in, no Markers tab, guardian rows.
  - The 25,000-flag `/qb` code.
  - The Pet Breeding tab, with a refresh row saying it re-reads every few seconds.
  - Version 0.6.1 and the `/ro` command set, plus rynth's `/ro attribute` alias.
  - The `/myquests` throttle message, folded into rynth's `RequestQuests`.
- **RynthILT.**
  - Charms and Banking moved out of the ILT tabs.
  - The server-profile v2 `/qb` export.
  - Version 0.2.23.
  - The Oracle Pet Breeding note in the feature catalog.

## Tests

| Suite | Result | Notes |
|---|---|---|
| RynthOracle.Tests | 455/455 | Rynth's tests no longer expect a Markers tab |
| RynthILT.Tests | 122/123 | "move: ... redirect" was already failing; `IltSettingsTests` and `ProfileTests` are still stale |
| RynthAi host tests | 1160/1162 | "map bake" and "mini remote" were already failing |
| RynthLua script windows | 1012 passed, 4 failed | The same 4 as before (missing docs) |
| Everything else | all pass | |

- Ring-evade tests count an escape (back-jump or run-away), not only a back-jump.
- ub compat uses `statushud` as the no-equivalent verb.
- `TrackerSessionTests` counts rynth's `VaultedDungeonsEnabled` slot.
- Duplicate test registrations were removed.
