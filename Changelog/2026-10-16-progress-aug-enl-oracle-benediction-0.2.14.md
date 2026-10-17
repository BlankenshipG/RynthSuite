# Progress / Aug / Enl — character lum + Benediction + Enl calculator

Previous: RynthILT 0.2.13 (ProductVersion 2026.10.16.5), RynthOracle 0.3.0
(ProductVersion 2026.10.16.1). Engine Skills UI: 2026.10.16.9.

## RynthILT 0.2.14

- Augmentation and Enl planners use PropertyInt64 AvailableLuminance on the
  character (not `/bank` banked luminance) for costs and "still need".
- Enl JSON already emitted the calculator (`levels` / `materials` / `chain`);
  blockers and plan lines now name character lum explicitly.

## RynthOracle 0.3.1

- Asheron's Lesser Benediction is detected from the permanent quest flag
  `lesserbenedictionaug` (Donatello Linante / Blackmoor's Page), not left as "?".

## Deploy

- Engine: `Publish-EngineCoreClr.ps1 -EngineOnly -p:Version=2026.10.16.9`
- RynthILT: `dotnet publish` `-p:Version=2026.10.16.6` → `C:\Games\RynthSuite\RynthILT\`
- RynthOracle: `dotnet publish` `-p:Version=2026.10.16.2` → `C:\Games\RynthSuite\RynthOracle\`

Reload plugins / re-enter world. Open Skills → Progression for Aug + Enl;
Oracle → Augmentations for Benediction.
