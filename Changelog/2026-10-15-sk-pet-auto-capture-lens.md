# SK: pet auto-capture with the selected siphon lens

Date: 2026-10-15
Plugin: RynthCore.Plugin.RynthAi (HandWrittenVersion stays 0.5.0-legacy-ui on sk)
Also: RynthILT 0.2.10 (previous 0.2.9)

## What

On Infinite Leaftide (ACECustom), Mini Remote > Pets can auto-use Flawed / Pristine /
Perfect / Resonance / Asheron's siphon lenses when a nearby attackable creature is within
5 m and at ACECustom's health gate (<=20% or under 20 HP; Asheron's skips the gate).

## Files

- MiniRemote/PetCaptureRules.cs (new)
- MiniRemote/PetsTab.cs, MiniRemoteState.cs
- Shared/RynthIltHubApi.cs FeatureMonsterCapture
- RynthILT IltFeatureCatalog tip; RynthILT 0.2.10

## Tests

- RynthAiTests PetCaptureRulesTests (4)
