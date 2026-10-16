# 0.5.77-legacy-ui / 2026.10.16.11 - Mini Remote Float/Dock and undocked Options

## Why

Need a clear dock / undock path and room for the expanded Mini Remote Options
(sections, layout, slot size) when the engine window is floating.

## Changes

- Engine Mini Remote header: **Float** / **Dock** next to Options (`/rc miniremote undock|dock`).
- Undocked: Options left-click opens the expanded section/layout popup (Settings stays in that menu).
- Docked: Options left-click still opens RynthAi Settings; right-click = section popup.
- Options popup: Undock / Dock items; undocked also lists RynthAi Settings.
- Embed path passes engine `:popout` so the HUD knows docked vs floating.

Requires RynthCore 2026.10.16.11+ for the chat verbs.

## Files

- `Huds/MiniRemoteHud.cs`
- `Huds/HudController.cs`
- `PluginExports.cs`
- `RynthAiPlugin.LocalFeatures.cs`
