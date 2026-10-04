# Changelog — 2026-10-04 (upstream snapshot)

## feat: port aelrynth git snapshot onto GitHub history

Applied `upstream/main` (`c33e24a`, RynthSuite as of 2026-09-27, release 2026.9.27.3) as a new commit on top of BlankenshipG `main`. Histories are unrelated, so this is a tree overlay — not a fast-forward or force-replace.

- Added AutoVendor, missile ballistics, ammo recipes, AutoVendor/MissileArc tests
- Overlapping plugin files taken from aelrynth (newer than origin 2026-07-01); unique BG strings (remote commands, skip-reason telemetry, AutoCram/AutoStack, OnLogout) are present in the aelrynth tree
- Kept unique origin-only `Docs/` review archive
- Re-applied dead-link fixes: README + CI clone/installer URLs point at aelrynth.com / `${{ github.repository_owner }}` siblings

No plugin version bump. Packaged aelrynth releases (2026.10.4.3) are newer than this git tip.
