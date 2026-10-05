# ub-Rythai 3.1.8 — RynthBundle installer inclusion

**Assembly / package version:** 3.1.8 (`RynthCore.Plugin.UbRythai.csproj`)

## Summary

- Version bump aligned with **RynthBundle 0.4.2**: the RynthCore **`installer\Build-Installer.ps1`** pipeline now publishes this project when the **`ub-Rythai`** repo sits next to **`RynthCore`** and **`RynthSuite`**, and stages **`RynthCore.Plugin.UbRythai.dll`** into the installer’s `Runtime\Plugins\` layout.

## RynthCore references

- `RynthCore/Changelog/2026-04-23-release-0.4.2.md` — full bundle release notes.
- `RynthCore/BUILD.md` — `-UbRythaiPluginProject`, `-SkipUbRythai`.
