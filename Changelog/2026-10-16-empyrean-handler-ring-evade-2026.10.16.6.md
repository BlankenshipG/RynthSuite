# RynthAi — Empyrean Handler lethal-ring back-jump evade

Previous ProductVersion: 2026.10.16.5. HandWritten on `sk` stays `0.5.0-legacy-ui`.

## What

When combat hears the Empyrean Handler channel line

> The handler briefly channels infused Empyrean hatred to release a lethal ring.

and the character is still inside the ring danger radius (max of 12 yards and
Skills RingRange), combat cancels the current attack and back-jumps away
(Jumper `xs` — run backward), then re-engages the same target. Outside that
range it logs and stays put. The ring cannot be melee-evaded.

## Files

- `Combat/CombatManager.cs` — chat trigger, range check, Think hold, re-engage
- `RynthAiPlugin.cs` — wires Jumper + routes chat into `HandleChatForRingEvade`

## Deploy

`dotnet publish` RynthAi `-p:Version=2026.10.16.6` → `C:\Games\RynthSuite\RynthAi\`
(reload plugins / re-enter world).
