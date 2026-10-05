# RynthBundle 0.4.8 — installer layout (RynthCore)

The **RynthCore** `Build-Installer.ps1` no longer stages a second copy of `RynthCore.Plugin.RynthAi.dll` under `{app}\Suite\RynthAi\`. Plugins load from **`{app}\Runtime\Plugins\`** only. See `RynthCore/Changelog/2026-04-23-release-0.4.8-bundle-installer-streamline.md`.
