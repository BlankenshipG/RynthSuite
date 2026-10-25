# 0.5.78-legacy-ui / 2026.10.16.12 - Mini Remote: Pet capture checkbox

## Why

Options only had **Pet / summon** (show the combat pet row). Auto-capture lived only
under Mini Remote > Pets > Monster capture, so there was no capture checkbox on the
Remote Options list.

## Changes

- Options: **Pet capture** section flag (default on).
- Remote body: Auto-capture checkbox + lens combo + status (same
  `AutoCaptureEnabled` / lens as the Pets roster).
- ILT / ACECustom only; disabled when the server turns Monster Capture off.

## Files

- `Huds/HudState.cs`, `Huds/MiniRemoteHud.cs`
- `MiniRemote/PetsTab.cs` (public AutoCaptureEnabled / CaptureLensIndex / CaptureStatus)
