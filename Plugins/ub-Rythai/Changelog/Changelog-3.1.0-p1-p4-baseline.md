# ub-Rythai 3.1.0 — P1-P4 baseline

## Summary

Implements a working **P1-P4 baseline** inside the dedicated `ub-Rythai` repo on **net10.0-windows** (RynthCore NativeAOT), keeping UB-ILT migration code out of Decal/net48 plugin lines.

## P1 (foundation automation)

- Login command queue and periodic command batch scheduler (`InvokeChatParser`-based).
- Login macro parity toggles: open main pack, optional chat-resize command.
- Log-out-on-death automation with configurable cooldown.
- Chat commands: `/ub help`, `/ub build`, `/ub loginq run`, `/ub periodic status`.

## P2 (high-traffic parity baseline)

- Selective chat filtering toggles:
  - busy lines
  - spell fizzle lines
  - vendor spam lines
  - monster death lines
- Client FPS policy settings wired to `Host.SetFpsLimit`.
- P2 runtime counters surfaced via `/ub p2 status`.
- Window mover flags persisted as placeholders for deeper host integration.

## P3 (ILT gameplay gates)

- Persisted feature gates added for spellcraft queues, temple/guardian tools,
  fellowship games, and quest/economy tooling.
- UI controls added so each surface can be turned on incrementally.

## P4 (deferred host-dependent gates)

- Persisted toggles added for VHS hotkey bridge, Decal HUD compatibility layer,
  and legacy autopack import compatibility.
- These remain opt-in placeholders pending deeper host/API work.

## Technical

- Version bumped: `3.1.0` (`InformationalVersion: 3.1.0-p1-p4-baseline`).
- Build verified via `dotnet publish -c Release` on `RynthCore.Plugin.UbRythai.csproj`.
