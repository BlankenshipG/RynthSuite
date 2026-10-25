# RynthAi 0.5.84 — Empyrean Chasers get the same lethal-ring evade as Handlers

**Kind:** Hot push (RynthAi). Previous `0.5.83`. DLL stamp `2026.10.18.7`.

## Asked for

The Empyrean Chaser should use the same ring-spell logic as the Empyrean Handler.

## Before

The evade (the 0.5.83 run-away) looked only for "Empyrean Handler". A Chaser's ring found "no
Empyrean Handler nearby" and nothing happened.

## Change

- **Ring casters:** Empyrean Handler and Empyrean Chaser. The evade flees the nearest caster.
  When the emote names one ("The chaser …" / "The handler …"), it flees the nearest of that kind,
  even if the other kind is closer.
- **The line:** the Chaser's emote text isn't in any log yet, so the trigger is loose:
  - an emote starting "The handler" or "The chaser" that mentions a lethal ring; or
  - a non-speech line with "…infused Empyrean hatred to release a lethal ring" or "release a
    lethal ring".

  Player speech, tells and channels are still ignored.
- **Logging the wording:** the first ring line in a session is logged with its chat type:
  `[CombatEvade] lethal-ring line heard (type 0x..): "…"`. After the first Chaser fight, the log
  shows its exact wording.
- **Unchanged:** the same escape as the Handler's: face away and run, re-aim every 250 ms, stop
  4 m outside the ring (or after 5 s), hold attacks 3.5 s, run again if the caster closes in,
  release autorun on death or macro stop.
- Log lines now name the caster ("running away from 'Empyrean Chaser'"; "Ring run stopped …",
  "Caster back inside the ring").

## Files

- `Plugins/RynthCore.Plugin.RynthAi/Combat/CombatManager.cs`
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiVersionCommand.cs`: 0.5.84.
- `Tools/RynthCore.RynthAiHostTests/Tests/RingEscapeTests.cs`: +3 tests (9 in all).

## Checks

- RynthAiHostTests 1067/1069. All 9 ring tests pass.
- The new tests: a Chaser's ring flees the Chaser; "The chaser …" flees the Chaser over a closer
  Handler; a line naming no caster flees the nearest, logged once.
- The 2 known failures are unchanged.
