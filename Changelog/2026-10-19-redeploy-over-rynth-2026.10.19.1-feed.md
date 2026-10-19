# Redeploy the merged test branch over rynth's 2026.10.19.1 feed release (2026-10-19)

ProductVersion 2026.10.19.2 for the engine, RynthOracle 0.6.2, RynthAi 0.5.92-legacy-ui, RynthILT 0.2.24
and RynthChat 0.3.5. Previous releases: RynthOracle 0.6.1, RynthAi 0.5.91, RynthILT 0.2.23 and
RynthChat 0.3.4. The launcher had installed rynth's 2026.10.19.1 engine, Oracle and RynthAi; those
are backed up under `PreviousReleases\2026.10.19.1`.

## Why

"Oracle isn't seeing the server detected, nor is RynthAi" (owner, 2026-10-19). The logs showed:

- **The engine detects the server correctly on every client.**
  - Infinite Leaftide: `infiniteleaftide.online matches "infiniteleaftide"`, or picked by hand.
  - Aelrynth: `aelrynth.com:9020`.
- **RynthAi reads it** ("creature data for server 'InfiniteLeaftide'").
- **RynthILT runs its ILT world check** and loads the server options.
- **The launcher had replaced our builds at about 9 AM** with rynth's official 2026.10.19.1 release
  (manifest `RynthOracle 2026.10.19.1, API 88+`, `RynthAi 2026.10.19.1`). His calver is higher than
  our 2026.10.18.29-.31 builds.
- **His Oracle has none of our Infinite Leaftide pages.** It logs "Server: no server-specific
  features (RynthCore: Infinite Leaftide ...)". His RynthAi likewise lacks our ILT work.

## Changed

- **Built from the test branch merge** (`b751a9a` RynthCore, `d2be2c5` RynthSuite). That is
  rynth's 2026.10.19.1 code together with ours, stamped 2026.10.19.2 so the build is newer than
  his release.
- **RynthOracle's plugin export version** said 0.4.0 while the plugin said 0.6.1. Both now say 0.6.2.
- **Not redeployed:** the other plugins the launcher updated (RynthNav, RynthInventory, RynthSort,
  RynthVision, RynthLua and so on) stay on rynth's 2026.10.19.1 builds. They are managed (API 88)
  and use `Runtime\PluginShared`, which the engine still provides.

## Note

The launcher installs any feed release with a higher calver. Rynth's next release (2026.10.20.x or
later) will replace these builds again unless the test branch is merged and redeployed above it.
