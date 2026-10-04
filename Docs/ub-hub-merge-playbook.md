# UB Hub → RynthCore / RynthSuite — Implementation Playbook

**Audience:** George Blankenship  
**Purpose:** Working checklist for the ILT Hub merge — what landed, what is still open. Assessment detail lives in the Cursor project store (`ub-hub-merge-plan.md`, `repo-remote-sync.md`).  
**Code lives in RynthSuite.**  
**Repo tracking homes (keep uniform):**  
- Suite: `Docs/ub-hub-merge-playbook.md` on **`feat/ilt-hub`** and **`SK-local`**  
- Core: `docs/ub-hub-merge-playbook.md` on **`SK-local`** (Core has no `feat/ilt-hub`)  
Do **not** put the playbook only on a docs-only PR branch — keep it with the Hub / SK worktrees.  
**This doc does not implement more product code.**

---

## Status (2026-10-04)

| Track | State |
|-------|--------|
| Assessment + playbook | **Done** — plan + this playbook (Pet: SummonPets mechanics + breeding) |
| Remotes / aelrynth overlay | **Done on `main`** — Core [#1](https://github.com/BlankenshipG/RynthCore/pull/1), Suite [#1](https://github.com/BlankenshipG/RynthSuite/pull/1) |
| ILT Hub P0–P2 | **On Suite `feat/ilt-hub`** — `ad61194` (0.5.1 Hub) + `18b4579` (0.5.2 diagnostics/security). **Not on `main`. No Hub PR yet.** |
| Pet: SummonPets / breeding | **Partial** — picker/charms/HealingBuddy/rosters + hardened empty-summon. **Still open:** deepen PetManager; bond/potency UI; **breeding** if `pet_breeding_enabled` |
| ACECustom `/ilt features` | **Not done** — still “Coming Soon” |
| In-AC smoke | **Not done in this project** |
| AutoXp / AutoTinker | **Out of scope** |

**Next:** PR Suite `feat/ilt-hub` → `main`, smoke, then Pet follow-on (SummonPets + breeding). Keep this file current on **`feat/ilt-hub`** and **`SK-local`**.

**Home:** Suite `Plugins/RynthCore.Plugin.RynthAi/IltHub/` + `Combat/PetManager.cs`. ACECustom breeding: `PET_BREEDING_PLAYER_GUIDE.md` / `PET_BREEDING_REFERENCE.md`.

---

## When to use / desired result

**Use when** reviewing, merging, or finishing leftover Hub work.

**Desired result:** ImGui **ILT Hub** (Character · Pet · Banking · Gear · Games), server-option gated, per-char Hub state. Pet tab **updates** `SummonPets` / pet mechanics and adds **breeding only when `pet_breeding_enabled`**.

---

## Prerequisites (locked)

| Repo | Branches for this playbook | Notes |
|------|----------------------------|--------|
| **RynthSuite** | **`feat/ilt-hub`**, **`SK-local`** | Hub code + `Docs/ub-hub-merge-playbook.md` |
| **RynthCore** | **`SK-local`** | Tracking copy only (`docs/…`); no Hub product code |
| **UB** | `ILT_Customizations` (source) | Read-only for Hub |
| **ACECustom** | — | `/ilt features`, bond, potency, `pet_breeding_*` |

**Locked decisions:** ImGui not Avalonia/VVS; `IltServerOptions` per feature; Hub state ≠ `/ra settings`; Games greenfield; Pet = update SummonPets + picker + item-check + breeding if server on; Guardian Hand on Gear.

---

## Done vs remaining

### Phase 1 — Shell + state + IltServerOptions — **coded** (PR + `/ilt features` dump open)

### Phase 2 — Banking — **coded** (smoke open)

### Phase 3 — Pet — **partial**

| | |
|--|--|
| **Landed** | Essence picker → Pet rules; SummonPets / min-mobs / spirit refill; refill + mastery charms; hardened `AllowSummonOnEmpty`; HoldSummons; HealingBuddy; `/pets` `/shinies` |
| **Still to do** | Deepen PetManager (range UI, mastery usability, bond/potency columns); breeding panel iff `pet_breeding_enabled` (appraisal readiness, annex hint, dance QoL — no client litter math); smoke |

### Phases 4–6 — Character / Gear / Games — **coded** (smoke, Guardian Hand WCID, quests.xml open)

### Phase 7 — Hardening — **0.5.2 on branch** (diagnostics + security); ship/verify open

---

## Remaining work

1. PR Suite **`feat/ilt-hub`** → `main`
2. In-AC smoke on ILT
3. Pet follow-on: SummonPets mechanics + breeding when enabled → bump **0.5.3**
4. ACECustom `/ilt features` (include pet_breeding / bond / potency / refill)
5. Guardian Hand WCID when published
6. Optional: quests.xml, UB settings import, USD smoke
7. Keep playbook identical on Suite `feat/ilt-hub` + `SK-local` and Core `SK-local`

---

## Explicit non-goals

- Second pet summon system (update PetManager instead)
- Client breeding math when `pet_breeding_enabled` is false
- VVS XML / AutoXp / AutoTinker / Avalonia RynthAiPanel / force-replace `main`

---

## Version / changelog

- **0.5.1** — Hub landing · **0.5.2** — diagnostics + Hub security (on `feat/ilt-hub`)
- Next Pet wave: **0.5.3** + `Changelog/` note. Don’t bump Core API unless host APIs change.

---

## Gate cheat sheet

| Surface | Enable when | Keep off when |
|---------|-------------|----------------|
| Whole Hub | ILT-like or Force **and** ≥1 bit | Retail, Force off |
| Pet breeding | `pet_breeding_enabled` | Off / Unknown |
| Pet bond / potency | `pet_bond_enabled` / `pet_potency_enabled` | Flags false |
| Pet empty-summon | Charm + confirmed `pet_refill` | Not confirmed |
| Banking / Powerball / Quests / Clap | Per existing IltServerOptions rules | Unknown / Off |

---

## Pointers

- Plan/remotes (project store): `ub-hub-merge-plan.md`, `repo-remote-sync.md`
- Suite Hub branch: **`feat/ilt-hub`** @ `18b4579` (0.5.2) · also **`SK-local`**
- Changelogs: `Changelog/RynthAi-0.5.1-ilt-hub.md`, `Changelog/RynthAi-0.5.2-diagnostics-security.md`
- ACECustom: `pet_breeding_enabled`, `PetDevice_Breeding.cs`
