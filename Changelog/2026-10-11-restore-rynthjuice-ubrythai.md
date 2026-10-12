# Restore RynthJuice and UbRythai for the full installer

The installer (`RynthCore\installer\Build-Installer.ps1` and `RynthCore.iss`) ships RynthJuice and
UbRythai as optional experimental plugins. Neither project was on this branch, so the
2026.10.5.37 installer build stopped with "Project not found".

## What changed

- `Plugins/RynthCore.Plugin.RynthJuice` (0.1.2) and `Plugins/ub-Rythai` (UbRythai 3.1.10) are back.
  They were checked out unchanged from `fix/restore-sk-features` (`09056a7`), the same versions
  installer 2026.10.5.26 shipped.
- `RynthCore.Plugin.RynthJuice.csproj`: the shared `RynthInstallPaths.cs` is now linked from
  `Plugins\Shared\`, where it lives in this layout and where the other plugins link it from. It was
  linked from `Shared\`.

Both projects build in Release. No behaviour change.
