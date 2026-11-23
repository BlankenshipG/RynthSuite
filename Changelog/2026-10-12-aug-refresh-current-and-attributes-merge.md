# Augmentations Refresh current + merged Attributes section (RynthAi 0.5.53)

## Cause (aug Current stuck at 0)

`/aug` capture treated the banner lines (`---` and `Advanced Augmentation Levels:`) as
enough to start the 2s idle timer. With chat-hold / pump lag, the capture often finished
before `Creature:` / `Item:` lines arrived, so `ApplyAugLines` saw no level rows and Current
stayed 0. A second click while `/aug` was still pending was a silent no-op. Flight logs showed
no `Advanced Augmentation Levels` lines because those replies are eaten by the capture.

## Fix (RynthAi)

- `IltChatRequest.ArmsIdle`: idle completion waits until a progress line (an `AugLine`) arrives;
  the banner alone no longer ends `/aug` (login probe uses the same gate).
- Refresh current coalesces like UB when `/aug` is already in flight, with a visible status.
- Passive `/aug` window on chat (header opens an 8s window) so Current still updates if capture
  finishes early or the player types `/aug` by hand.
- Slightly looser `AugLine` regex (optional trailing text after the number).

## Attributes UI (RynthCore engine)

Skills > Progression no longer duplicates "XP planner" and "Attribute raiser". One
**Attributes** section shows the retail XP-table planner, then a segment for infinite
attributes (auto-raise past retail max) when the server supports `/xp` + `/attr`.

| Component | Version |
|---|---|
| RynthAi | 0.5.53-legacy-ui (was 0.5.52) |
| RynthCore Engine | 2026.10.5.44 (UI merge; bump on publish) |
