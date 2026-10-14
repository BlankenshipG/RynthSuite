# RynthAi 0.5.59-legacy-ui - Item Info on yourself shows your ratings

Previous release: 0.5.58-legacy-ui.

## What changed

Selecting yourself with Item Info's "on select" turned on now identifies you
and prints your ratings in chat, using the Item Info prefix, chat colour and
one-line or multi-line layout:

```
[Item Info] Silentkelpie's ratings: D 45, DR 30, C 10, CD 20, CR 5, CDR 12
[Item Info] HB 8, NR 4, DoTR 2, LR 3
[Item Info] PK: PKD 5, PKDR 10
[Item Info] Lum augs (in the totals): D +5, DR +5, CD +3
```

- Combat ratings: Damage, Damage Resist, Crit, Crit Damage, Crit Resist and
  Crit Damage Resist.
- Other ratings: Heal Boost, Nether Resist, DoT Resist and Life Resist.
- PK ratings: PK Damage and PK Damage Resist.
- Luminance augmentations, shown as the part of each total they make up.

The server (ACECustom `AppraiseInfo.AddRatings`) includes your resolved totals
in any identify of a creature, yourself included. RynthAi waits until the
identify arrives (or the Item Info timeout passes) and then prints the
totals. If the identify never arrives, the line says "not identified - totals
may be incomplete".

## Usage

- Select yourself (or click yourself) with Item Info on select turned on.
- `/ra iteminfo me` (also `self` or `ratings`) does the same on demand.
- RynthAi's own self-selects, such as buffing, do not trigger it.
- The usual Item Info de-duplication applies, so reselecting yourself within a
  moment prints once.

## Files

- `Plugins/RynthCore.Plugin.RynthAi/ItemInfo/SelfRatings.cs` (new): the formatter.
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiSelfRatings.cs` (new): request, wait, print.
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiPlugin.cs`: self selection hook.
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiItemInfo.cs`: click, tick, command and preview.
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiVersionCommand.cs`: 0.5.59-legacy-ui.
- `Tools/RynthCore.RynthAiHostTests/Tests/SelfRatingsTests.cs` (new), `Program.cs`.

## Tests

RynthAiHostTests: the three new self-ratings tests pass. The one failing test,
the map bake test, is unrelated; it needs the AC dat files on disk.
