# RynthAi 0.6.1 — profile folder selector

## Summary

Adds a profile-folder selection window to the dashboard profile loader flow so profile sets can be loaded from a chosen folder instead of only the default character folder.

## Changes

- Added **Profile Folder Selector** window in `LegacyDashboardRenderer`:
  - opened via **Profile -> Folder** button in the dashboard header.
  - supports direct path input and one-click selection from known folders under `SettingsProfiles`.
  - supports reset to the character-default folder.
- Profile load/save resolution now uses the selected profile folder for:
  - profile JSON file path (`<profile>.json`)
  - active profile marker (`active_profile.txt`)
  - profile list refresh.

## Versioning

- `RynthCore.Plugin.RynthAi.csproj`
  - `Version`: `0.6.1`
  - `AssemblyVersion`: `0.6.1.0`
  - `FileVersion`: `0.6.1.0`
  - `InformationalVersion`: `0.6.1-profile-folder-selector`
- `RynthAiPlugin` version pointer updated to `0.6.1-profile-folder-selector`.
