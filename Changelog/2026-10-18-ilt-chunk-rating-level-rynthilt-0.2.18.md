# RynthILT 0.2.18: rating level for aetheria chunking (2026-10-18)

ProductVersion 2026.10.18.21. Previous release: 0.2.17 (2026.10.18.20).

## Added

- **"Keep when the rating is at least"** on the Gear tab's Chunking section, under the
  "Never chunk aetheria with: C / CD / CDR" switches. A C / CD / CDR rating at or above this level
  keeps the aetheria. A lower rating no longer protects it, so it chunks if the level cap and the
  other rules allow. Example: at level 2, a `[C 1]` Coalesced Aetheria chunks and a `[C 2]` one is
  kept.
  - It applies to Coalesced Aetheria and to revealed aetheria.
  - Each switch still decides whether its rating counts at all. A switch that is off ignores the level.
  - A low rating does not override the level cap: a piece at or above the cap is still kept.
  - The default is 1 (any rating keeps), so existing settings behave the same until the level is raised.
  - The range is 1 to 50.

## Code

- `Hub/IltHubState.cs`: `IltGearState.KeepAetheriaRatingAtLeast` (default 1).
- `Hub/IltChunkClap.cs`: `KeptForRatings` compares each rating to the level; new `ClampRatingLevel` and
  `MaxRatingLevel`; the new input and its tooltip.

## Tests

`RynthILT.Tests/AetheriaChunkTests.cs`: a new "rating level" test covers the default, below / at /
above the level, mixed ratings, a switch that is off, clamping, and Coalesced pieces through
`AetheriaSkip`, including that the level cap still applies. The "move: ... redirect to RynthOracle"
failure was already on the branch before this change.
