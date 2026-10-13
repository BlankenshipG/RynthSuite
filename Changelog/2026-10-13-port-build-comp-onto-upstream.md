# Port: local build-comp-10-11 onto upstream 2026.10.13.2 (RynthAi 0.5.55, RynthILT 0.2.1)

Branch `port/build-comp-on-2026.10.13.2`, based on `aelrynth/main`. Upstream's version is kept for
everything rynth already took; only what upstream lacks is re-applied. Nothing is pushed.

## Versions

- RynthAi `HandWrittenVersion` 0.5.54-legacy-ui -> **0.5.55-legacy-ui** (previous release noted in source).
- RynthILT 0.2.0 -> **0.2.1** (previous release noted in source).

## ILT Hub: upstream's RynthILT plugin wins

Upstream moved the ILT Hub out of RynthAi into `RynthCore.Plugin.RynthILT` (Hub/*, Bounty/*).
The local copy in `RynthAi/IltHub/` was a duplicate and is removed. RynthILT already has the sigil
chain, Lum/h, session rates (Coins/h, Pyr/h), charms, banking (Prestige Coins), bounties, the
attribute raiser and aug costs.

Re-applied on top of upstream:

- **Crafting tab** (tinker benches, Rings chain) moved into RynthAi's Mini Remote as a universal
  tab: `MiniRemote/CraftingTab.cs`, `MiniRemote/Crafting/*` (namespace
  `RynthCore.Plugin.RynthAi.MiniRemote.Crafting`). `MiniRemoteTabs.Universal` is now
  Quests, Pets, Crafting; `/ra hub open crafting|craft|tinker` opens it. Settings live in
  `MiniRemoteState.Crafting` (copied from the old hub file on first load).
- **Pet roster damage-type colours** (UB-ILT palette, name fallback): `MiniRemote/PetDamageTint.cs`,
  used by `PetsTab` and `PetStats`.
- **Aug refresh / attributes WIP** (`2026-10-12-aug-refresh-current-and-attributes-merge.md`)
  applied to RynthILT's `Hub/IltChatCapture.cs`, `IltParse.cs`, `IltProgression.cs`,
  `IltServerOptions.cs` and `Tools/RynthILT.Tests/AttributeRaiserTests.cs` (the changelog's
  RynthAi `IltHub/` paths now mean RynthILT `Hub/`).

## Merge losses found and restored

A dropped-lines audit (lines the local branch added that the port lacked) found:

- `Combat/CombatManager.cs`: the environment-impact throttle wiring (heartbeat poll, macro-stop
  reset, scan skip, cast watch, projectile delete), the attack-power modes
  (Default/High/Reckless/Variable) and the launcher-swap stance hold. Re-merged three-way so
  upstream's ghost tracking and the local features both stay.
- `CombatManager.OnObjectCreated` existed on both sides: upstream's feeds the ghost tracker; the
  local one is renamed `OnEnvironmentObjectCreated` and both are called from `OnCreateObject`.
- `SpellManager.CopyKnownSpellIds` was duplicated; one copy removed.
- SafeImGui wrapping restored in `TranslateUi`, `TranslateLanguages`, `NavOverlayRenderer`,
  `HudSetupUi`, `ItemCountHud`, `LegacyItemInfoUi` (its tooltip holds a literal `%a / %md / %mc`)
  and the NavResumeNearest tooltip.
- `VirindiTankProfileImporter` imports `AttackPower` again.

## SafeImGui coverage for upstream code

`Plugins/Shared/SafeImGui.cs` is now compiled into RynthILT. 92 ImGui text/tooltip calls with
dynamic text (names, chat, server lines) in RynthILT `Hub/*`, RynthAi `MiniRemote/*` and `Huds/*`
go through SafeImGui, plus the buff "Skipping:" line in Advanced Settings.

## Tests

- Host tests: Tinker and PetDamageTint tests retargeted; ServerMenuTests expects three universal
  tabs; the AttributeRaiser / BankCommand / SigilChain copies removed (upstream runs them in
  `Tools/RynthILT.Tests`); FakeHost keeps upstream's `SetMotion` fake.
- `Tools/RynthCore.RynthAiHostTests`: 954 / 955 pass. The one failure, "map bake 0x6346 from the
  real dats", is unchanged upstream and needs the AC dat files on this machine.
- `Tools/RynthILT.Tests`: 50 / 50 pass (including the new aug-refresh test).
- All 14 plugin projects build in Release.
