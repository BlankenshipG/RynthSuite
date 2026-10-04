# UB Hub → RynthCore / RynthSuite — Implementation Playbook

**Audience:** George Blankenship  
**Purpose:** Working checklist for the ILT Hub merge — what landed, what is still open.  
**Tracking copy:** Suite `Docs/ub-hub-merge-playbook.md` (this file) mirrored to Core `docs/ub-hub-merge-playbook.md`. Hub code: `Plugins/RynthCore.Plugin.RynthAi/IltHub/`. Full assessment lives in the Cursor project store (`ub-hub-merge-plan.md`).  
**This doc does not implement more product code.**

---

## Status (2026-10-04)

| Track | State |
|-------|--------|
| Assessment + playbook | **Done** — [ub-hub-merge-plan.md](./ub-hub-merge-plan.md) (Pet scope updated: SummonPets mechanics + breeding) |
| Remotes / aelrynth overlay | **Done on `main`** — Core [#1](https://github.com/BlankenshipG/RynthCore/pull/1) `eda3cdf`, Suite [#1](https://github.com/BlankenshipG/RynthSuite/pull/1) `2a8571a` |
| ILT Hub P0–P2 | **Coded on `feat/ilt-hub`**, not on `main` — commit `ad61194` (RynthAi **0.5.1**). **No GitHub PR yet.** |
| Pet: SummonPets mechanics / breeding | **Partial on branch** (picker + charms + HealingBuddy + rosters). **Still open:** deepen PetManager mechanics; bond/potency UI; **breeding panel** gated on `pet_breeding_enabled` |
| ACECustom `/ilt features` dump | **Not done** — server still “Coming Soon”; client falls back to login probes |
| In-AC smoke on InfiniteLeaftide | **Not done in this project** |
| AutoXp / AutoTinker | **Out of scope** (unchanged) |

**Next:** open Suite PR `feat/ilt-hub` → `main`, smoke, then Pet follow-on (SummonPets hardening + breeding when server supports it).

**Home:** RynthSuite `Plugins/RynthCore.Plugin.RynthAi/IltHub/` + `Combat/PetManager.cs`. Core host APIs already on `main`. UB `ILT_Customizations` read-only. ACECustom breeding docs: `PET_BREEDING_PLAYER_GUIDE.md` / `PET_BREEDING_REFERENCE.md`.

---

## When to use / desired result

**Use when** reviewing, merging, or finishing leftover Hub work.

**Desired result:** ImGui **ILT Hub** with Character · Pet · Banking · Gear · Games, gated by ACECustom server options, per-character Hub state. Pet tab **updates** Rynth `SummonPets` / pet mechanics (one summon loop) and adds **breeding only when `pet_breeding_enabled`**.

---

## Prerequisites (locked — complete)

| Repo | `origin` | `upstream` | Hub notes |
|------|----------|------------|-----------|
| **RynthCore** | `BlankenshipG/RynthCore` | aelrynth git | Overlay on `main`. Do **not** force-replace. |
| **RynthSuite** | `BlankenshipG/RynthSuite` | aelrynth git | Overlay on `main`. Hub branch: **`feat/ilt-hub`**. |
| **UB** | `BlankenshipG/UB` | GitLab `utilitybelt/utilitybelt.gitlab.io` | Source: **`ILT_Customizations`**. |
| **ACECustom** | `BlankenshipG/ACECustom` | `rkroska/ACECustom` | `/ilt features`, bond, potency, **`pet_breeding_*`**. |

**Locked decisions:** ImGui not Avalonia/VVS; `IltServerOptions` per feature; Hub state ≠ `/ra settings`; Games greenfield; Pet = **update SummonPets mechanics** + picker + item-check + **breeding if server on**; Guardian Hand on Gear; AutoVendor not a Hub tab.

---

## Done vs remaining (phase order)

### Phase 1 — Hub shell + state + IltServerOptions (P0) — **coded**

| | |
|--|--|
| **Still to do** | PR to `main`. ACECustom `/ilt features` dump must include **`pet_breeding`**, `pet_bond`, `pet_potency`, `pet_refill`. |

### Phase 2 — Banking (P0) — **coded** (smoke open)

### Phase 3 — Pet (P0 → P1) — **partial**

| | |
|--|--|
| **Landed on `feat/ilt-hub`** | `IltPets` + `IltInventory`: essence picker → Pet consumable rules; `SummonPets` / min-mobs / spirit refill checkboxes; refill + mastery charms; `AllowSummonOnEmpty` / `HoldSummons`; HealingBuddy; `/pets` + `/shinies` rosters. |
| **Still to do — SummonPets / mechanics** | Deepen one PetManager loop: clearer priority UI, CustomPetRange on Pet tab, mastery/off-mastery usability, cooldown/chat handling, bond/potency columns on essence rows when flags On. Do **not** fork a second summoner. |
| **Still to do — Bond / potency** | Status UI when `pet_bond_enabled` / `pet_potency_enabled`; warn if breeding On but bond Off (min-bond gate). |
| **Still to do — Breeding** | Pet-tab subsection **iff `pet_breeding_enabled`**: readiness from sex/charges/cooldown/shiny/juvenile appraisal lines; annex location hint; optional dance QoL. **No client litter/mutation math.** Hide when Off/Unknown. Refs: ACECustom Seedy Motel / `PetDevice_Breeding.cs`. |
| **Still to do — Smoke** | Empty-essence summon blocked unless refill confirmed; breeding hidden on shards with breeding Off. |

### Phases 4–6 — Character / Gear / Games — **coded** (smoke + Guardian Hand WCID + quests.xml open)

### Phase 7 — Hardening — **mostly coded; ship/verify open**

---

## Remaining work (do in this order)

1. **PR** — `feat/ilt-hub` → `main` (Suite).
2. **In-game smoke** — Hub gates, bank confirm, pet empty-essence, quests, clap, games.
3. **Pet follow-on (George)** — update SummonPets/PetManager mechanics; bond/potency UI; **breeding panel when `pet_breeding_enabled`**.
4. **ACECustom `/ilt features`** — include pet_breeding / bond / potency / refill / charms.
5. **Guardian Hand WCID** when published.
6. **Optional** — `quests.xml`; UB Hub `settings.json` import; USD smoke.
7. **Later** — AutoXp, AutoTinker; aelrynth 2026.10.4.3 overlay if git catches up.
8. **Sync playbooks** — store + Suite [#2](https://github.com/BlankenshipG/RynthSuite/pull/2) + Core [#2](https://github.com/BlankenshipG/RynthCore/pull/2).

---

## Explicit non-goals

- Replace Rynth combat/nav/loot/meta (or a **second** pet summon system — update `SummonPets` / PetManager instead)
- Client-side breeding / litter / mutation simulation when `pet_breeding_enabled` is false
- Port VVS XML; UB AutoVendor under Hub; AutoXp / AutoTinker on this track
- Avalonia `RynthAiPanel` / Core Decal-era `Plugins/RynthAi/`
- Force-replace GitHub `main` with aelrynth git

---

## Version / changelog

Next Pet follow-on wave: comment gates, bump RynthAi **0.5.2**, add `Changelog/` note. Do not bump Core API version unless host APIs change.

---

## Quick gate cheat sheet

| Surface | Enable when | Keep off when |
|---------|-------------|----------------|
| Whole Hub | ILT-like or Force **and** ≥1 feature bit | Retail / unknown, Force off |
| Banking | `/bank` responds | Unknown |
| Pet breeding panel | `pet_breeding_enabled` | Off / Unknown |
| Pet bond / potency rows | `pet_bond_enabled` / `pet_potency_enabled` | Flags false |
| Pet refill empty-summon | Charm + confirmed `pet_refill` (or player bool) | Not confirmed |
| Games → Powerball | `powerball_enabled` / `/pb` | Flag false |
| Quest Tracker | `quest_info_enabled` | Flag false |
| Auto-Clap | `/clap` + `AutoCraftingEnabled` | Stamp/clap missing |
| Guardian Hand | Charm registered + enabled | Off / unpublished |
| USD / `.utl` / Hub state | Client-side | Not server-gated |

---

## Pointers

- Plan (project store): `ub-hub-merge-plan.md`  
- Sibling: RynthCore `docs/ub-hub-merge-playbook.md`  
- Branch: Suite **`feat/ilt-hub`** @ `ad61194` · `Changelog/RynthAi-0.5.1-ilt-hub.md`  
- ACECustom: `pet_breeding_enabled`, `PET_BREEDING_*` docs, `PetDevice_Breeding.cs`
