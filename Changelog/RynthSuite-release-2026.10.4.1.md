# RynthSuite release package — 2026.10.4.1

**Build date:** 2026-10-04
**Source:** `feat/ilt-hub` @ `ad61194` (a local test build; not on `main`)
**Previous release:** RynthAi 0.5.0 (backup in `C:\Games\RynthSuite\RynthAi\PreviousReleases\0.5.0\`)
**Output:** `publish\release-2026.10.4.1\` (ignored by git)

This is the same package as `.github/workflows/release.yml`, built locally against the
sibling RynthCore `main` @ `eda3cdf`.

| File | Size | Notes |
|---|---|---|
| `RynthCore.Plugin.RynthAi.dll` | 12.1 MB | NativeAOT x86, RynthAi 0.5.1-legacy-ui (ILT Hub) |
| `RynthCore.LootEditor.zip` | 42.9 MB | Self-contained win-x64, version 2026.10.4.1 |
| `RynthCore.MonsterEditor.zip` | 42.8 MB | Self-contained win-x64, version 2026.10.4.1 |

## Validation gates

- LootSdk golden tests: 63 assertions, 0 failed (plus the deployed `LootSnobV4.utl`
  round-trip, 1628 rules).
- AutoVendor tests: 169 assertions, 0 failed.

## Security

- Code review of the ILT Hub branch: no medium, high or critical findings. Suggested
  hardening (not required):
  - Reject newlines and a leading `/` in bank transfer recipients.
  - Use `IsOn` rather than `!IsOff` for the gates on features that spend currency.
  - Add confirmations to `/ra hub suit load` and `/ra hub profile load`.
- Dependency advisory: `Tmds.DBus.Protocol` 0.20.0
  ([GHSA-xrw6-gwf8-vvr9](https://github.com/advisories/GHSA-xrw6-gwf8-vvr9)). It's an
  Avalonia dependency that the Loot Editor and Monster Editor pull in, and it's only used
  on Linux.

The bundled RynthCore installer (`RynthCore-Setup.exe` 2026.10.4.1) ships this same plugin.
