# RynthAi 0.5.72 - auto-capture pets with the selected siphon lens

Date: 2026-10-15
Plugin: RynthCore.Plugin.RynthAi 0.5.72-legacy-ui (previous release 0.5.71-legacy-ui)
Also: RynthILT 0.2.10 (previous 0.2.9) - Features catalog tip for monster capture.

## What

On Infinite Leaftide (ACECustom), siphon lenses capture a creature's look into a pet essence
once the creature is weak enough. The server rules (ACECustom MonsterCapture.cs) are:

- Range: nearest attackable creature within 5 m
- Health: at or below 20% of max health or under 20 HP (whichever comes first)
- Asheron's Lens skips the health gate; Resonance still needs a prior failed capture

## Change

Mini Remote > Pets > Roster (ILT worlds):

- Checkbox Auto-capture when a creature is weak enough
- Combo to pick the lens: Flawed, Pristine, Perfect, Resonance, Asheron's
- Status line shows why it is waiting (no lens, too far, health too high, busy, refused)

When enabled, every 250 ms the Pets tab looks for the nearest attackable monster within 5 m,
reads its vitals (host target vitals, else the cache health ratio), and uses the chosen lens
once ACECustom's gate is met. Chat refuse / fail lines back off for 8 s so lenses are not burned.

Settings live in miniremote.json (Pet.AutoCaptureEnabled, Pet.CaptureLensIndex).

## Files

- MiniRemote/PetCaptureRules.cs (new): thresholds and lens WCIDs matching ACECustom
- MiniRemote/PetsTab.cs: TickCapture, RenderCapture, chat backoff
- MiniRemote/MiniRemoteState.cs: AutoCaptureEnabled, CaptureLensIndex
- Shared/RynthIltHubApi.cs: FeatureMonsterCapture
- RynthILT Hub/IltFeatureCatalog.cs: tip mentions the checkbox

## Tests

- RynthAiTests: PetCaptureRulesTests (4) - health gate, Asheron's bypass, range, WCIDs
