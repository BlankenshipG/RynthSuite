# Local hotbuild — RynthAi / RynthILT / RynthChat

Date: 2026-10-16
Plugins:
- RynthCore.Plugin.RynthAi 0.5.74-legacy-ui (previous 0.5.73-legacy-ui)
- RynthCore.Plugin.RynthILT 0.2.12 (previous 0.2.11)
- RynthCore.Plugin.RynthChat 0.3.2 (previous 0.3.1)
- RynthCore.Plugin.RynthOracle 0.2.8 (already deployed)

## Why

Games was running mixed sk/local hotbuilds. Local Asherons Ai/ILT do not compile cleanly against this Core API (Salvage/`PollServerState` drift), so tonight's local feature deltas were applied on the sk build tree and published from there.

## Deployed to Games

| Piece | Version | Notes |
|---|---|---|
| Engine | 2026.10.5.69 | local: Vaulted Options, chat 0x1C, chrome |
| RynthChat | 0.3.2 | local: ILT `[Trade]`/`[General]` → Channels |
| RynthILT | 0.2.12 | sk + Vaulted Dungeons tab / `/ilt vaulted` |
| RynthAi | 0.5.0-legacy-ui (sk) | Vaulted Options gate + moved Banking/Charms menu |
| RynthOracle | 0.2.8 | Charms + Pet Breeding (earlier tonight) |

Reload plugins; reinject for the engine DLL.
