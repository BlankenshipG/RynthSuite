# RynthAi 0.6.15 — ILT Hub shows as a separate window with the Avalonia UI

**Date:** 2026-10-04
**Branch:** `SK-local`
**Previous release:** RynthAi 0.6.14 (Item Info settings window)
**Needs:** RynthCore engine 2026.10.4.9 or later (new `RynthPluginRenderOverlay` export)

## Fixed
- **The ILT Hub window never appeared.** The logs showed the Hub was created and the server's
  ILT features were detected (`[ILT Hub] Refreshing server options...` /
  `=== ILT Custom Features ===`). But the engine force-disables its ImGui shell
  (`EngineSettings.EnableImGuiShell`, only on with the dev env var `RYNTHCORE_FORCE_IMGUI=1`)
  because the in-game UI is the Avalonia overlay. That also skipped every plugin's
  `RynthPluginRender`, and the Hub is an ImGui window with no Avalonia panel, so nothing drew it.
  The new Item Info settings window (0.6.14) had the same problem.

## Added
- **`RynthPluginRenderOverlay` export → `RynthAiPlugin.OnRenderOverlay()`.** While the engine's
  ImGui shell is off, the engine calls this instead of `RynthPluginRender`. RynthAi draws **only**
  the windows the Avalonia RynthAi panel doesn't have, as separate floating windows next to it:
  - the **ILT Hub** (Character / Pet / Banking / Gear / Games), its confirm popups and the games HUD;
  - the **Item Info** settings window (`/ra iteminfo settings`).

  The RynthAi ImGui dashboard, map, radar and chat stay hidden in this mode, so nothing duplicates
  the Avalonia panels.
- **Isolation:** the Hub windows only add to the main UI. RynthAi's macro, automation and Avalonia
  panel never depend on them. Exceptions are caught in the plugin (and again at the export
  boundary), and the engine disables only the overlay windows if one still escapes. The plugin is
  not marked failed, so it keeps ticking.
- One chat hint per session, once the server confirms ILT features:
  `[ILT Hub] ILT server features detected. Type /ra hub to open the ILT Hub window.`
  It is skipped when the window is already open. The window reopens where you left it.

## Changed
- `LegacyDashboardRenderer`: the window colour push is now a shared `PushDashboardStyle()`, so the
  overlay windows look the same as the dashboard. New `RenderOverlayWindows()`.

## How to open
- `/ra hub` (toggle), `/ra hub show` / `hide`. Item Info: `/ra iteminfo settings`.

## Build
- RynthAi `0.6.15` (`VersionPointer` `0.6.15-legacy-ui`): `dotnet build` and NativeAOT publish
  (via the installer build) succeed.
- Not yet verified in game.
