# RynthAi — Empyrean Handler lethal-ring evade fix

Previous ProductVersion: 2026.10.16.6. HandWritten on `sk` stays `0.5.0-legacy-ui`.

## Problem

The Handler channel line

> The handler briefly channels infused Empyrean hatred to release a lethal ring.

never started an evade: it arrives as Emote (0x0C), and `ChatSource.IsPlayerAuthored`
treats Emote as player speech, so combat skipped every real line. Separately, the
jump faced *away* from the Handler then used Jumper `xs` (back + run), which leaped
*into* the ring.

## Fix

- Match the unique phrase as a line start (player copies are `Name says, "..."`).
- Face the Handler, then back-jump (`xs`) so the body moves out of ring range.
- Slightly longer jump charge (750 ms) so the run-back clears ~12 m.

Outside ring danger (max of 12 m and Skills RingRange) combat still stays put;
after the jump settles it re-engages the same target.

## Files

- `Combat/CombatManager.cs`
- `RynthAiPlugin.cs` (wire comment)

## Deploy

`dotnet publish` RynthAi `-p:Version=2026.10.16.7` → `C:\Games\RynthSuite\RynthAi\`
(reload plugins / re-enter world). Watch `[CombatEvade]` in the RynthCore log.
