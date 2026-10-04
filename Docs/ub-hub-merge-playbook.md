# UB Hub → RynthCore / RynthSuite — Implementation Playbook

**Audience:** George Blankenship  
**Purpose:** Working checklist for the ILT Hub merge — what landed, what is still open.  
**Tracking copy:** This file is mirrored in both **RynthSuite** (`Docs/ub-hub-merge-playbook.md`) and **RynthCore** (`docs/ub-hub-merge-playbook.md`) so both repos can track Hub progress. **Hub product code lives in RynthSuite** (`Plugins/RynthCore.Plugin.RynthAi/IltHub/`). Full assessment / remotes notes live in the Cursor project docs store (`ub-hub-merge-plan.md`, `repo-remote-sync.md`).  
**This doc does not implement more product code.**

---

## Status (2026-10-04)

| Track | State |
|-------|--------|
| Assessment + playbook | **Done** (project store + this tracking copy) |
| Remotes / aelrynth overlay | **Done on `main`** — Core [#1](https://github.com/BlankenshipG/RynthCore/pull/1) `eda3cdf`, Suite [#1](https://github.com/BlankenshipG/RynthSuite/pull/1) `2a8571a` |
| ILT Hub P0–P2 | **Coded on `feat/ilt-hub`**, not on `main` — commit `ad61194` (RynthAi **0.5.1**). **No GitHub PR yet.** |
| ACECustom `/ilt features` dump | **Not done** — server still “Coming Soon”; client falls back to login probes |
| In-AC smoke on InfiniteLeaftide | **Not done in this project** — branch changelog claims NativeAOT x86 publish is clean |
| AutoXp / AutoTinker | **Out of scope** (unchanged) |

**Next:** open a Suite PR `feat/ilt-hub` → `main`, then in-game verify. Do not treat Hub as shipped until that merge + smoke.

**Home:** RynthSuite `Plugins/RynthCore.Plugin.RynthAi/IltHub/`. Core host APIs were already on merged `main` (`TryGetWorldName`, `InvokeChatParser`, `UseObject`). UB `ILT_Customizations` stays read-only source.

---

## When to use / desired result

**Use when** reviewing, merging, or finishing leftover Hub work.

**Desired result (unchanged):** ImGui **ILT Hub** with Character · Pet · Banking · Gear · Games, gated by ACECustom server options (not world-name alone), per-character Hub state, without replacing Rynth combat/loot.

---

## Prerequisites (locked — complete)

| Repo | `origin` | `upstream` | Hub notes |
|------|----------|------------|-----------|
| **RynthCore** | `BlankenshipG/RynthCore` | aelrynth git | Overlay on `main`. Do **not** force-replace. |
| **RynthSuite** | `BlankenshipG/RynthSuite` | aelrynth git | Overlay on `main`. Hub branch: **`feat/ilt-hub`**. |
| **UB** | `BlankenshipG/UB` | GitLab `utilitybelt/utilitybelt.gitlab.io` | Source: **`ILT_Customizations`**. |
| **ACECustom** | `BlankenshipG/ACECustom` | `rkroska/ACECustom` | Still needed for the `/ilt features` dump. |

Packaged installer **2026.10.4.3** may still be newer than public git. Prefer tree-overlay sync onto GitHub history (no force-replace of `main`).

**Locked decisions (still in force):** ImGui not Avalonia/VVS; `IltServerOptions` per feature (Force = world-identity only); Hub state ≠ `/ra settings`; Games greenfield; Pet = picker + item-check; Guardian Hand on Gear; AutoVendor already in Suite — not a Hub tab.

---

## Done vs remaining (phase order)

Branch file map: `Plugins/RynthCore.Plugin.RynthAi/IltHub/*.cs` + `Changelog/RynthAi-0.5.1-ilt-hub.md` (on `feat/ilt-hub`). Version: `VersionPointer` / csproj **0.5.1-legacy-ui**.

### Phase 1 — Hub shell + Hub state + IltServerOptions (P0) — **coded**

| | |
|--|--|
| **Landed** | `IltHubController` / `IltHubUi` / `IltHubState` / `IltHubStore` / `IltServerOptions` / `IltChatCapture`. Launcher button + `/ra hub show\|hide\|toggle\|refresh\|profile…\|force`. `ilt-hub.json`, profiles, last tab, window, `serverOptions` snapshot. One-time UB sidecar import (`bankbalances` / `transactions` / `itemconversions`). Login probes + `/ilt features` parser (treats “Coming Soon” as dump-missing). Force override. Empty games/bank tabs hidden. Risky spends stay off while bits are unknown. |
| **Still to do** | Open/merge PR to `main`. **ACECustom:** replace `/ilt features` stub with player-parseable `ILTFEATURE` lines (incl. charm `enabled` + Guardian Hand slot). Charm / `pet_refill` / `quest_info_enabled` bits cannot be probed — they stay Unknown until that dump. Enl is **assumed On** (never probe `/enl`). UB Hub **`settings.json` toggles are not imported** (sidecars only). In-game: Force + failed parse must keep clap/transfers/bets/auto-enl off. |

### Phase 2 — Banking (P0) — **coded**

| | |
|--|--|
| **Landed** | `IltBanking.cs` — `/bank` parse, auto-refresh, transfer `/bank t …` with confirm + 5s spacing + self-send block, transaction log, CSV/copy. Tab hidden when `/bank` unknown. |
| **Still to do** | Smoke: live balances, confirm on transfer, `bank_command_limit` behavior, sidecar import on a real UB char folder. |

### Phase 3 — Pet picker + item-check (P0 → P1) — **coded**

| | |
|--|--|
| **Landed** | `IltPets.cs` + `IltInventory.cs` (shared WCID/charm helper). Essence picker wires existing `SummonPets` rules. Refill charm `78780030` + universal mastery `78780031` + casting stone WCIDs. `PetManager.AllowSummonOnEmpty` / `HoldSummons`. HealingBuddy subsection. `/pets` + `/shinies` rosters. |
| **Still to do** | Smoke: empty-essence summon **blocked** when `pet_refill` is Off or charm missing. Bond/potency rows still wait on dump bits. |

### Phase 4 — Character (P1) — **coded**

| | |
|--|--|
| **Landed** | `IltSessionRates`, `IltQuests` (Timed/KillTask/Once UI, quiet `/myquests`, `/ra quests`), `IltProgression` (QB, Augs, Enl + session-armed auto-enl confirm, XP `/attr` raise on confirm). QuestTracker cache reused. |
| **Still to do** | **`quests.xml` is not in the repo** — optional file at `C:\Games\RynthSuite\RynthAi\quests.xml`. Smoke: tracker hidden when `quest_info_enabled` is false (stock ACECustom). UB AutoXp policy spender still **not** ported (Character has a small `/attr` confirm loop only). |

### Phase 5 — Gear (P1 → P2) — **coded**

| | |
|--|--|
| **Landed** | `IltGear` charm strip (Guardian Hand name-match + optional WCID), `IltBuffKeepers` (Dispel + Surging), `IltChunkClap`, `IltSplitArrows`, `IltEquipSuits` (`.utl`), `IltUsdImporter` (preview + confirm). |
| **Still to do** | Fill Guardian Hand WCID when ACECustom publishes the weenie. Smoke clap (`AutoCraftingEnabled` + live `/clap`), USD against a real `.usd`, `.utl` suit swap. AutoTinker / UB AutoVendor still **out**. |

### Phase 6 — Games (P2) — **coded**

| | |
|--|--|
| **Landed** | `IltGames.cs` — blackjack / fblackjack pads, Powerball buy/tickets, hand HUD, per-game gates, confirm on bets/tickets. |
| **Still to do** | Smoke: Powerball hidden when `powerball_enabled` is false; no local dealer logic. |

### Phase 7 — Hardening — **mostly coded; ship/verify open**

| | |
|--|--|
| **Landed** | Shared chat-eat (`IltChatCapture`), shared inventory (`IltInventory`), confirm modal (transfers, enl, suits, USD, split, games). Version **0.5.1** + `Changelog/RynthAi-0.5.1-ilt-hub.md`. |
| **Still to do** | PR + review. In-AC pass. Server dump companion. Keep commenting new gates if `/ilt features` lands later. |

---

## Remaining work (do in this order)

1. **PR** — `feat/ilt-hub` → `main` on BlankenshipG/RynthSuite. Branch has no open PR today.
2. **In-game smoke (Windows, ILT world)** — Hub chrome, per-tab hide, bank transfer confirm, pet empty-essence gate, quest tracker vs `quest_info_enabled`, clap stamp, games confirms. Retail/unknown world with Force off = no Hub.
3. **ACECustom companion** — implement `/ilt features` as stable `ILTFEATURE` lines (bank, powerball, quests, clap, pet_refill, charm enabled including Guardian Hand). Player-level, not Developer `/charms`.
4. **Guardian Hand WCID** — lock when the weenie/registry row exists; until then name-match is intended.
5. **Optional polish** — ship `quests.xml`; import UB Hub `settings.json` toggles if players still need them; verify USD on real VTank files.
6. **Later / not Hub** — AutoXp, AutoTinker. Re-overlay aelrynth if git catches **2026.10.4.3**.
7. **Keep this playbook current** — when Hub status changes, update **both** repo copies (Suite `Docs/` and Core `docs/`) in the same wave.

---

## Explicit non-goals (unchanged)

- Replace Rynth combat, nav, loot, meta, radar, or dungeon map
- Port VirindiViewService XML as-is
- Port UB AutoVendor / AutoTrade / Looter / Networking / maps under Hub (Suite already has aelrynth AutoVendor)
- Re-implement salvage or AutoCram/AutoStack
- Port a UB Games page (none exists)
- Replace `SummonPets` / `PetDamage`
- Treat `/ra settings` or `/myquests` meta as Hub-state / Quest Tracker coverage
- Host VTank / Mag-Tools in-process
- Invent Guardian Hand combat rules, or use retail Guardian-of-the-Clutch spells
- Use ACE `/config` or Developer `/charms` as the dump
- Touch Avalonia `RynthAiPanel` or Core Decal-era `Plugins/RynthAi/`
- Force-replace GitHub `main` with aelrynth git

---

## Version / changelog (already used on the Hub branch)

Keep George’s rules on the next wave (PR fixes, `/ilt features` client parse, WCID lock-in):

1. **Comment** new gates and confirm paths.
2. **Increment** RynthAi `VersionPointer` + csproj (`0.5.1` is the Hub landing; next wave is `0.5.2`). Keep `0.5.1` as the previous build. Do **not** bump Core `CurrentApiVersion` (67) unless host APIs change.
3. **Changelog folder** — add `Changelog/YYYY-MM-DD-<slug>.md` (or `Changelog/RynthAi-0.5.2-….md`) on the repo you change. Do not write Hub notes into UB `changelog/`.

---

## Quick gate cheat sheet (still the contract)

| Surface | Enable when | Keep off when |
|---------|-------------|----------------|
| Whole Hub | ILT-like or Force **and** ≥1 feature bit | Retail / unknown, Force off |
| Banking | `/bank` responds | Unknown command |
| Games → Powerball | `powerball_enabled` / `/pb` live | Flag false |
| Games → blackjack | Command exists | Unknown |
| Quest Tracker | `quest_info_enabled` | Flag false (stock default) |
| Auto-Clap | `/clap` + `AutoCraftingEnabled` | Stamp missing / clap unknown |
| Pet refill helper | Charm + not `pet_refill` Off | Server Off or charm missing |
| Guardian Hand | Charm registered + enabled | Off / unpublished (name-match UI OK) |
| USD / `.utl` / Hub state | Client-side | **Not** server-option gated |

---

## Pointers

- Implementation home: `Plugins/RynthCore.Plugin.RynthAi/IltHub/` (RynthSuite `feat/ilt-hub` @ `ad61194`)
- Release notes on that branch: `Changelog/RynthAi-0.5.1-ilt-hub.md`
- Sibling tracking copy: RynthCore `docs/ub-hub-merge-playbook.md` · RynthSuite `Docs/ub-hub-merge-playbook.md`
- Host APIs already on Core `main`: `RynthCoreHost.TryGetWorldName`, `InvokeChatParser`, `UseObject`
