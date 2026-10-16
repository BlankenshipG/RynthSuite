// PetsTab.cs — The Mini Remote's Pets tab.
// Originally SilentKelpie's IltPets (ILT Hub). Summoning is every server's, so the tab stayed in
// RynthAi when the ILT Hub went to the RynthILT plugin (2026-10-13). The ACECustom bits it reads
// (the refill / mastery charm server flags, whether this is an ILT world) come from RynthILT
// (RynthILT.Hub GetFeature / IsIltWorld; unknown without it, which never spends pyreals); the
// /pets and /shinies logs moved to RynthILT, which draws them at the bottom of this tab.
//
//   * Pet roster (UtilityBelt Pets-tab style): every carried pet essence with three stat
//     lines (bond / level / craft · sex / mutations / potency / mastery · ratings / uses /
//     breeding), filtered by summon type (Combat / Healing / Cosmetic) and sortable.
//     Pets are detected by type (essence WCID block, summoning-gem icon underlay,
//     Summoning use requirement, ACECustom bond/potency) — a pet-like name only nominates
//     an item for appraisal. Healing Buddy / Dule box and cosmetic naming pick the type,
//     else Combat; any essence can be re-typed ("Add sel." or right-click a row).
//       - Combat: the tick list is the existing ConsumableRules Type=="Pet" list that
//         PetManager summons from (list order = summon priority) — no second summoner.
//       - Healing: the tick picks the heal pet the healing state machine uses.
//       - Cosmetic: the tick picks a display pet; "Keep cosmetic pet out" summons it in
//         peace mode and dismisses it when PetManager needs the slot for a fight.
//     Summon / Despawn act on the highlighted row (or the type's chosen pet).
//   * Pet charms: Summon Essence Refill (server refills an empty essence from banked
//     pyreals) and Universal Summoning Mastery, read from the charm appraisal text and the
//     player bools the server stamps. Feeds PetManager.AllowSummonOnEmpty.
//   * Healing pet (UB HealingBuddy subset): summons a heal pet when HP drops, dismisses it
//     after the heal lands, holds PetManager's combat summons while it is out.
//   * Auto siphon capture (ACECustom MonsterCapture): when enabled, uses the chosen lens on the
//     nearest attackable creature within 5 m once it is <=20% health or under 20 HP (Asheron's
//     Lens skips the health gate). Resonance still needs a prior failed capture on the server.
//   * /pets and /shinies logs: RynthILT's (RynthILT.Hub section "pets-logs", ILT only).
//   * Breeding sub-tab (Infinite Leaftide only): the tab becomes Roster | Breeding when RynthILT
//     offers it (an ILT world whose server hasn't reported pet breeding off). Its whole body is
//     RynthILT's pet breeding tracker (RynthILT.Hub section "pets-breeding"); this tab only hosts it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

internal sealed class PetsTab
{
    private static readonly string[] KindNames = { "Combat", "Healing", "Cosmetic" };
    private static readonly string[] SortNames = { "Priority", "Bond", "Potency", "Breed ready", "Level", "Uses", "Name" };

    /// <summary>Un-ID'd essences auto-appraised per 2 s scan (keeps the server request rate low).</summary>
    private const int AutoAppraisePerScan = 2;

    private readonly MiniRemoteContext _ctx;

    // Healing-pet state machine.
    private enum HealState { Idle, Summoning, Despawning }
    private HealState _heal = HealState.Idle;
    private long _healStartedAt;
    private long _despawnAt;
    private long _healCooldownUntil;
    private int _healEssenceId;
    private long _lastHealThink;
    private string _healStatus = "idle";

    // Auto siphon-lens capture (ACECustom MonsterCapture).
    private long _lastCaptureThink;
    private long _captureCooldownUntil;
    private volatile string _captureStatus = "off";
    private const long CaptureThinkMs = 250;
    private const long CaptureCooldownMs = 2500;
    private const long CaptureFailBackoffMs = 8000;

    // Roster snapshot (rebuilt on the pump thread, read by the render thread).
    private volatile PetStats[] _pets = Array.Empty<PetStats>();
    private long _lastPetScan;

    // Active-pet / cosmetic bookkeeping (pump thread; the ui* copies are for the render thread).
    private long _lastPresenceThink;
    private int _lastSummonEssenceId;
    private int _cosmeticOutEssenceId;
    private long _cosmeticNextAt;
    private volatile string _uiActivePet = string.Empty;
    private volatile string _cosmeticStatus = "off";
    private volatile string _hudPetName = string.Empty;
    private volatile string _hudPetStatus = string.Empty;

    /// <summary>Mini Remote pet line: the pet that is out, else the first combat essence in priority order.</summary>
    public string HudPetName => _hudPetName;
    /// <summary>Mini Remote pet status: "Out", "Ready", "Empty" or "Healing" (empty when there is no pet).</summary>
    public string HudPetStatus => _hudPetStatus;

    // Charm / supply status cached for the render thread.
    private volatile bool _uiRefillActive;
    private volatile bool _uiMasteryActive;
    private volatile bool _uiHasCasting = true;

    // Render-thread only: highlighted row and the sorted view cache.
    private int _highlightId;
    private PetStats[]? _viewSource;
    private int _viewKind = -1;
    private int _viewSort = -1;
    private PetStats[] _view = Array.Empty<PetStats>();

    public PetsTab(MiniRemoteContext ctx) => _ctx = ctx;

    private PetTabState S => _ctx.State.Pet;

    // ── Classification ──────────────────────────────────────────────────────

    // Pet-device type signals (client-visible assessment properties).
    private const uint IntMaxStructure = 91;
    private const uint IntUseRequiresSkill = 366;
    private const uint IntUseRequiresSkillSpec = 368;
    private const uint IntBondLevel = 9053;     // ACECustom pet essence
    private const uint IntPotencyStored = 9056; // ACECustom pet essence
    private const int SkillSummoning = 54;
    private const uint DidIconUnderlay = 52;
    /// <summary>Low 24 bits of the summoning-gem icon underlay (0x06007420) every pet device carries.</summary>
    private const uint SummonGemUnderlay = 0x007420;

    /// <summary>Icon-underlay verdict per object id (the DID never changes; only successful reads are cached).</summary>
    private readonly Dictionary<int, bool> _underlayCache = new();

    /// <summary>
    /// True when the item is a pet device by type, not by name: the ACECustom combat
    /// essence WCID block, the summoning-gem icon underlay, a Summoning-skill use
    /// requirement, or ACECustom bond/potency properties. Names (" Essence", heal /
    /// stamina / cosmetic pet naming) only make an item a candidate, which must then show
    /// charges (MaxStructure) — so augmentation gems like "Jibril's Essence" stay out.
    /// Spirits, charms, capture devices and summoned pet creatures are never pets.
    /// </summary>
    public bool IsPetDevice(WorldObject wo)
    {
        uint w = _ctx.Inventory.Wcid(wo);
        if (w == MiniRemoteInventory.WcidEncapsulatedSpirit) return false;
        if (w is >= 78780030 and <= 78780089) return false;     // charm block
        if (w is >= 78780001 and <= 78780012) return false;     // capture devices
        if (w is >= 787802001 and <= 787802072) return false;   // summoned combat pet creatures
        if (w is >= 787801001 and <= 787801072) return true;    // combat essence block

        if (HasSummonGemUnderlay(wo)) return true;
        if (RequiresSummoning(wo)) return true;
        if (_ctx.Inventory.Int(wo, IntBondLevel) > 0 || _ctx.Inventory.Int(wo, IntPotencyStored, -1) >= 0) return true;

        return IsPetNameCandidate(wo.Name) && _ctx.Inventory.Int(wo, IntMaxStructure) > 0;
    }

    /// <summary>Name hints UB's pet scan uses; never sufficient on their own (see <see cref="IsPetDevice"/>).</summary>
    private static bool IsPetNameCandidate(string n)
        => n.EndsWith(" Essence", StringComparison.OrdinalIgnoreCase)
           || LooksLikeHealPet(n) || LooksLikeStaminaPet(n) || LooksLikeCosmeticPet(n);

    /// <summary>UseRequiresSkill / UseRequiresSkillSpec is Summoning (appraisal data).</summary>
    private bool RequiresSummoning(WorldObject wo)
        => _ctx.Inventory.Int(wo, IntUseRequiresSkill) == SkillSummoning
           || _ctx.Inventory.Int(wo, IntUseRequiresSkillSpec) == SkillSummoning;

    /// <summary>Icon underlay is the summoning gem (network-populated, no appraisal needed; engine 2026.10.4.19+).</summary>
    private bool HasSummonGemUnderlay(WorldObject wo)
    {
        if (_underlayCache.TryGetValue(wo.Id, out bool known)) return known;
        var host = _ctx.Host;
        if (!host.HasGetObjectDataIdProperty
            || !host.TryGetObjectDataIdProperty(unchecked((uint)wo.Id), DidIconUnderlay, out uint did))
            return false;
        bool match = (did & 0x00FFFFFF) == SummonGemUnderlay;
        _underlayCache[wo.Id] = match;
        return match;
    }

    /// <summary>"Healing Buddy" / "Dule box" naming used by the server's heal pets.</summary>
    private static bool LooksLikeHealPet(string n)
        => (n.Contains("healing", StringComparison.OrdinalIgnoreCase) && n.Contains("buddy", StringComparison.OrdinalIgnoreCase))
           || (n.Contains("dule", StringComparison.OrdinalIgnoreCase) && n.Contains("box", StringComparison.OrdinalIgnoreCase));

    /// <summary>Stamina buddy / crate / pet naming.</summary>
    private static bool LooksLikeStaminaPet(string n)
        => n.Contains("stamina", StringComparison.OrdinalIgnoreCase)
           && (n.Contains("buddy", StringComparison.OrdinalIgnoreCase) || n.Contains("crate", StringComparison.OrdinalIgnoreCase)
               || n.Contains("pet", StringComparison.OrdinalIgnoreCase));

    /// <summary>Cosmetic / plush / display pet naming.</summary>
    private static bool LooksLikeCosmeticPet(string n)
        => n.Contains("cosmetic", StringComparison.OrdinalIgnoreCase) || n.Contains("plush", StringComparison.OrdinalIgnoreCase)
           || n.Contains("display pet", StringComparison.OrdinalIgnoreCase);

    /// <summary>Name candidate that hasn't been appraised yet, so its type can't be confirmed.</summary>
    private bool IsUnconfirmedCandidate(WorldObject wo)
        => IsPetNameCandidate(wo.Name) && !IsPetDevice(wo)
           && _ctx.Host.HasHasAppraisalData && !_ctx.Inventory.HasAppraisal(wo);

    private PetAssignment? FindAssignment(string name)
        => S.Assignments.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Summon type for a carried item: the user's override wins, then the configured heal
    /// pet. Anything else must be a pet device by type (<see cref="IsPetDevice"/>); its
    /// naming then picks Healing / Cosmetic, defaulting to Combat. Null = not a pet.
    /// </summary>
    private PetKind? ClassifyKind(WorldObject wo, out bool assigned)
    {
        var a = FindAssignment(wo.Name);
        if (a != null)
        {
            assigned = true;
            return (PetKind)Math.Clamp(a.Kind, 0, 2);
        }
        assigned = false;
        if (NameIs(wo, S.HealPetName)) return PetKind.Healing;
        if (!IsPetDevice(wo)) return null;
        if (LooksLikeHealPet(wo.Name)) return PetKind.Healing;
        if (LooksLikeCosmeticPet(wo.Name)) return PetKind.Cosmetic;
        return PetKind.Combat;
    }

    private static bool NameIs(WorldObject wo, string name)
        => !string.IsNullOrWhiteSpace(name) && wo.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase);

    private WorldObject? FindCarried(string name)
        => string.IsNullOrWhiteSpace(name) ? null : _ctx.Inventory.Items().FirstOrDefault(wo => NameIs(wo, name));

    /// <summary>Heal-pet essence: the ticked heal pet, else the first Healing-typed essence.</summary>
    private WorldObject? FindHealEssence()
    {
        if (!string.IsNullOrWhiteSpace(S.HealPetName)) return FindCarried(S.HealPetName);
        return _ctx.Inventory.Items().FirstOrDefault(wo => ClassifyKind(wo, out _) == PetKind.Healing);
    }

    // ── Charms / gates ──────────────────────────────────────────────────────

    /// <summary>Summon Essence Refill charm is carried and switched on (or the server stamped the player bool).</summary>
    public bool RefillCharmActive()
    {
        if (_ctx.Inventory.PlayerBool(MiniRemoteInventory.BoolPyrealRefillActive)) return true;
        var charm = _ctx.Inventory.FindByWcid(MiniRemoteInventory.WcidSummonRefillCharm);
        return charm != null && _ctx.Inventory.CharmStatus(charm) == CharmState.On;
    }

    public bool MasteryCharmActive()
    {
        if (_ctx.Ilt.IsOff(RynthIltHub.FeatureUniversalMastery)) return false;
        if (_ctx.Inventory.PlayerBool(MiniRemoteInventory.BoolUniversalMasteryActive)) return true;
        var charm = _ctx.Inventory.FindByWcid(MiniRemoteInventory.WcidUniversalMasteryCharm);
        return charm != null && _ctx.Inventory.CharmStatus(charm) == CharmState.On;
    }

    /// <summary>
    /// PetManager hook: summon from an empty essence (spends banked pyreals) only when the
    /// player opted in, the refill charm is active, and the server has positively confirmed
    /// the feature: either /ilt features reported pet_refill on, or the server stamped the
    /// player's refill bool. An unknown feature state never spends.
    /// </summary>
    public bool AllowSummonOnEmpty()
    {
        if (!S.UsePyrealRefillPath || !_ctx.Ilt.IsIltWorld || !RefillCharmActive()) return false;
        bool confirmed = _ctx.Ilt.IsOn(RynthIltHub.FeaturePetRefill)
                         || _ctx.Inventory.PlayerBool(MiniRemoteInventory.BoolPyrealRefillActive);
        if (!confirmed) RynthLog.Trace(LogCat.Pets, "summon-on-empty refused: pet_refill not confirmed by the server");
        return confirmed;
    }

    /// <summary>PetManager hook: hold combat summons while the heal pet cycle is running.</summary>
    public bool HoldCombatSummons() => _heal != HealState.Idle;

    /// <summary>
    /// PetManager hook (pump thread): a pet already holds the slot and monsters are near.
    /// When that pet is the cosmetic pet, dismiss it so a combat pet can be summoned.
    /// Returns true when a dismiss was issued.
    /// </summary>
    public bool YieldPetForCombat()
    {
        if (_heal != HealState.Idle || string.IsNullOrWhiteSpace(S.CosmeticPetName)) return false;
        var pet = FindOwnPet();
        var essence = FindCarried(S.CosmeticPetName);
        if (pet == null || essence == null) return false;
        if (!PetMatchesEssence(pet, essence) && _cosmeticOutEssenceId != essence.Id) return false;
        if (_ctx.Inventory.IsBusy || !_ctx.Inventory.Use(essence)) return false;

        long now = MiniRemoteContext.NowMs;
        _cosmeticOutEssenceId = 0;
        _cosmeticNextAt = now + Math.Max(5, S.CosmeticRespawnSeconds) * 1000L;
        _cosmeticStatus = "dismissed for combat";
        RynthLog.Trace(LogCat.Pets, $"cosmetic pet '{pet.Name}' dismissed so a combat pet can be summoned");
        return true;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (nowMs - _lastPetScan >= 2000)
        {
            _lastPetScan = nowMs;
            RebuildPetSnapshot(autoAppraise: true);
            // Host reads (and appraisal requests) happen here, never on the render thread.
            _uiRefillActive = RefillCharmActive();
            _uiMasteryActive = MasteryCharmActive();
            _uiHasCasting = _ctx.Inventory.HasCastingSupplies();
        }
        if (nowMs - _lastHealThink >= 250)
        {
            _lastHealThink = nowMs;
            TickHealing(nowMs);
        }
        if (nowMs - _lastCaptureThink >= CaptureThinkMs)
        {
            _lastCaptureThink = nowMs;
            TickCapture(nowMs);
        }
        if (nowMs - _lastPresenceThink >= 1000)
        {
            _lastPresenceThink = nowMs;
            var pet = FindOwnPet();
            _uiActivePet = pet?.Name ?? string.Empty;
            TickCosmetic(nowMs, pet);
            UpdateHudPetLine(pet);
        }
    }

    /// <summary>Refreshes the Mini Remote's pet name / status pair (pump thread).</summary>
    private void UpdateHudPetLine(WorldObject? pet)
    {
        if (pet != null)
        {
            _hudPetName = ResolveActiveEssence(pet)?.Name ?? pet.Name;
            _hudPetStatus = _heal != HealState.Idle ? "Healing" : "Out";
            return;
        }
        var next = ChosenEssence(PetKind.Combat);
        if (next == null) { _hudPetName = string.Empty; _hudPetStatus = string.Empty; return; }
        _hudPetName = next.Name;
        _hudPetStatus = _ctx.Inventory.Uses(next) == 0 && !AllowSummonOnEmpty() ? "Empty" : "Ready";
    }

    public bool OnChat(string text)
    {
        // "<pet> casts Heal Other ... and restores N points of your health."
        if (_heal == HealState.Summoning
            && text.Contains("casts", StringComparison.OrdinalIgnoreCase)
            && text.Contains("heal other", StringComparison.OrdinalIgnoreCase)
            && text.Contains("restores", StringComparison.OrdinalIgnoreCase)
            && text.Contains("points of your health", StringComparison.OrdinalIgnoreCase))
        {
            if (S.AutoDespawnAfterHeal)
            {
                _heal = HealState.Despawning;
                _despawnAt = MiniRemoteContext.NowMs + 1000;
                _healStatus = "heal landed, dismissing";
            }
            else
            {
                EndHealCycle(MiniRemoteContext.NowMs, "heal landed");
            }
        }

        // ACECustom MonsterCapture replies — back off after a refuse so we don't burn lenses.
        if (S.AutoCaptureEnabled && text.Length > 0)
        {
            long now = MiniRemoteContext.NowMs;
            if (ContainsAny(text,
                    "successfully siphoned the essence",
                    "The echo resonates perfectly",
                    "The essence is captured"))
            {
                _captureStatus = "captured";
                _captureCooldownUntil = now + CaptureCooldownMs;
            }
            else if (ContainsAny(text,
                    "too strong",
                    "blind rage",
                    "cannot be siphoned",
                    "cannot be captured",
                    "No creatures nearby",
                    "already dead",
                    "already being captured",
                    "hasn't failed a capture",
                    "echo has faded",
                    "Failed to consume"))
            {
                _captureStatus = "refused: " + Truncate(text, 48);
                _captureCooldownUntil = now + CaptureFailBackoffMs;
            }
            else if (ContainsAny(text, "siphon failed", "lens shatters", "resisted your capture", "resonance fades"))
            {
                _captureStatus = "failed (creature may enrage)";
                _captureCooldownUntil = now + CaptureFailBackoffMs;
            }
        }
        return false;
    }

    private static bool ContainsAny(string text, params string[] parts)
    {
        foreach (string p in parts)
            if (text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void OnLogout()
    {
        _heal = HealState.Idle;
        _pets = Array.Empty<PetStats>();
        _uiActivePet = string.Empty;
        _lastSummonEssenceId = 0;
        _cosmeticOutEssenceId = 0;
        _cosmeticNextAt = 0;
        _captureCooldownUntil = 0;
        _captureStatus = "off";
    }

    // ── Healing pet ─────────────────────────────────────────────────────────

    private void TickHealing(long now)
    {
        if (!S.HealingEnabled)
        {
            if (_heal != HealState.Idle) { _heal = HealState.Idle; _healStatus = "off"; }
            return;
        }

        switch (_heal)
        {
            case HealState.Idle:
            {
                if (now < _healCooldownUntil) return;
                var (cur, max) = _ctx.Inventory.PlayerHealth();
                if (max == 0) return;
                int pct = (int)(cur * 100 / max);
                if (pct >= Math.Clamp(S.HealthThresholdPercent, 1, 99)) { _healStatus = $"HP {pct}% ok"; return; }
                if (S.MinHealthPoints > 0 && cur < S.MinHealthPoints) { _healStatus = "HP below minimum, not summoning"; return; }
                if (_ctx.Inventory.IsBusy) return;
                if (AnyOwnPetPresent()) { _healStatus = "another pet is out"; return; }
                var essence = FindHealEssence();
                if (essence == null) { _healStatus = "no heal-pet essence found"; return; }
                if (_ctx.Inventory.Uses(essence) == 0 && !AllowSummonOnEmpty()) { _healStatus = "heal-pet essence is empty"; return; }
                if (!_ctx.Inventory.Use(essence)) return;
                _healEssenceId = essence.Id;
                _lastSummonEssenceId = essence.Id;
                _healStartedAt = now;
                _heal = HealState.Summoning;
                _healStatus = $"summoning heal pet (HP {pct}%)";
                break;
            }
            case HealState.Summoning:
            {
                long timeout = Math.Max(5, S.PetTimeoutSeconds) * 1000L;
                if (now - _healStartedAt < timeout) return;
                // No heal seen in time: dismiss only if the pet is actually up.
                if (AnyOwnPetPresent()) UseHealEssence();
                EndHealCycle(now, "timed out waiting for heal");
                break;
            }
            case HealState.Despawning:
            {
                if (now < _despawnAt || _ctx.Inventory.IsBusy) return;
                if (AnyOwnPetPresent()) UseHealEssence();
                EndHealCycle(now, "dismissed");
                break;
            }
        }
    }

    private void EndHealCycle(long now, string why)
    {
        _heal = HealState.Idle;
        _healCooldownUntil = now + Math.Max(1, S.RespawnDelaySeconds) * 1000L;
        _healStatus = why;
    }

    private void UseHealEssence()
    {
        var cache = _ctx.Inventory.Cache;
        var essence = cache?[_healEssenceId];
        if (essence != null) _ctx.Inventory.Use(essence);
    }

    // ── Active pet ──────────────────────────────────────────────────────────

    // ── Auto siphon capture (ACECustom MonsterCapture) ───────────────────────

    /// <summary>
    /// When Auto Capture is on, use the chosen siphon lens on the nearest attackable creature
    /// within 5 m once ACECustom's health gate is met (≤20% or under 20 HP; Asheron's skips it).
    /// The server picks the nearest creature itself — we only fire when that nearest candidate is
    /// already weak enough, so a full-health neighbour closer than the wounded target won't waste a lens.
    /// </summary>
    private void TickCapture(long now)
    {
        if (!S.AutoCaptureEnabled)
        {
            _captureStatus = "off";
            return;
        }
        if (_ctx.Ilt.Loaded && _ctx.Ilt.IsOff(RynthIltHub.FeatureMonsterCapture))
        {
            _captureStatus = "monster capture disabled on this server";
            return;
        }
        if (now < _captureCooldownUntil)
        {
            long left = (_captureCooldownUntil - now + 999) / 1000;
            if (!_captureStatus.StartsWith("waiting", StringComparison.Ordinal))
                _captureStatus = $"waiting {left}s";
            return;
        }
        if (_ctx.Inventory.IsBusy) { _captureStatus = "busy"; return; }

        var lens = PetCaptureRules.LensAt(S.CaptureLensIndex);
        var lensItem = _ctx.Inventory.FindByWcid(lens.Wcid);
        if (lensItem == null)
        {
            _captureStatus = $"no {lens.Name} carried";
            return;
        }

        if (!TryFindNearestCaptureTarget(out WorldObject? target, out double distM, out uint curHp, out uint maxHp))
        {
            _captureStatus = "no attackable creature within 5 m";
            return;
        }

        if (!PetCaptureRules.InRange(distM))
        {
            _captureStatus = $"{target!.Name} at {distM:0.0} m (need ≤{PetCaptureRules.CaptureRangeM:0} m)";
            return;
        }

        if (!PetCaptureRules.CanAttempt(lens, curHp, maxHp))
        {
            float pct = maxHp > 0 ? curHp * 100f / maxHp : -1f;
            _captureStatus = pct >= 0
                ? $"{target!.Name} at {pct:0}% / {curHp} HP (need ≤{PetCaptureRules.CaptureMaxHealthFraction:P0} or <{PetCaptureRules.CaptureMaxHealthPoints} HP)"
                : $"{target!.Name}: health unknown";
            return;
        }

        if (!_ctx.Inventory.Use(lensItem))
        {
            _captureStatus = "UseObject refused";
            return;
        }

        float shown = maxHp > 0 ? curHp * 100f / maxHp : 0f;
        _captureStatus = $"using {lens.Name} on {target!.Name} ({shown:0}% / {curHp} HP)";
        _captureCooldownUntil = now + CaptureCooldownMs;
        RynthLog.Write(LogCat.Pets,
            $"[Pets] auto-capture: {lens.Name} -> 0x{target.Id:X8} '{target.Name}' d={distM:0.0}m hp={curHp}/{maxHp}");
    }

    /// <summary>
    /// Nearest attackable landscape creature within capture range, with vitals from the host when
    /// available else the cache health ratio (fraction-only gate).
    /// </summary>
    private bool TryFindNearestCaptureTarget(out WorldObject? best, out double bestDist, out uint curHp, out uint maxHp)
    {
        best = null;
        bestDist = double.MaxValue;
        curHp = maxHp = 0;
        var cache = _ctx.Inventory.Cache;
        if (cache == null) return false;
        uint playerId = _ctx.Inventory.PlayerId;
        if (playerId == 0) return false;
        int pid = unchecked((int)playerId);
        string ownPrefix = _ctx.CharName.Length > 0 ? _ctx.CharName + "'s " : string.Empty;

        foreach (var wo in cache.GetLandscape())
        {
            if (wo == null || wo.Id == pid) continue;
            if (wo.ObjectClass != AcObjectClass.Monster) continue;
            if (ownPrefix.Length > 0 && wo.Name.StartsWith(ownPrefix, StringComparison.Ordinal)) continue;
            // Skip corpses / non-attackable when the host can tell.
            if (_ctx.Host.HasObjectIsAttackable && !_ctx.Host.ObjectIsAttackable(unchecked((uint)wo.Id)))
                continue;

            double d = cache.Distance(pid, wo.Id);
            if (d < 0 || d > PetCaptureRules.CaptureRangeM || d >= bestDist) continue;

            if (!TryReadCreatureVitals(wo.Id, cache, out uint c, out uint m))
                continue;
            if (c == 0) continue; // dead

            best = wo;
            bestDist = d;
            curHp = c;
            maxHp = m;
        }
        return best != null;
    }

    /// <summary>
    /// Absolute vitals via TryGetTargetVitals when bound; else cache health ratio as a 0–1000 scale
    /// so only the fraction half of ACECustom's gate can fire.
    /// </summary>
    private bool TryReadCreatureVitals(int id, WorldObjectCache cache, out uint cur, out uint max)
    {
        cur = max = 0;
        if (_ctx.Host.HasGetTargetVitals
            && _ctx.Host.TryGetTargetVitals(unchecked((uint)id), out uint h, out uint mh, out _, out _, out _, out _)
            && mh > 0)
        {
            cur = h;
            max = mh;
            return true;
        }
        float ratio = cache.GetHealthRatio(id);
        if (ratio < 0f) return false;
        // Synthetic scale: ratio 0.20 → 200/1000 so IsWeakEnough's fraction half matches.
        max = 1000;
        cur = (uint)Math.Clamp((int)Math.Round(ratio * 1000f), 1, 1000);
        return true;
    }

    /// <summary>The live "&lt;Me&gt;'s …" creature (server pet naming), or null.</summary>
    private WorldObject? FindOwnPet()
    {
        var cache = _ctx.Inventory.Cache;
        if (cache == null || _ctx.CharName.Length == 0) return null;
        string prefix = _ctx.CharName + "'s ";
        return cache.GetLandscape().FirstOrDefault(wo => wo != null && wo.Name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private bool AnyOwnPetPresent() => FindOwnPet() != null;

    /// <summary>"Fire Banshee Essence (250)" → "Fire Banshee": the part the summoned creature is named after.</summary>
    private static string EssenceBaseName(string essenceName)
    {
        int i = essenceName.IndexOf(" Essence", StringComparison.OrdinalIgnoreCase);
        return (i > 0 ? essenceName[..i] : essenceName).Trim();
    }

    private static bool PetMatchesEssence(WorldObject pet, WorldObject essence) => PetMatchesEssence(pet, essence.Name);

    private static bool PetMatchesEssence(WorldObject pet, string essenceName)
    {
        string b = EssenceBaseName(essenceName);
        return b.Length > 0 && pet.Name.Contains(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Essence that summoned <paramref name="pet"/>: the longest essence base name contained
    /// in the creature's name, else the last essence this tab / the heal cycle used.
    /// </summary>
    private WorldObject? ResolveActiveEssence(WorldObject pet)
    {
        WorldObject? best = null;
        int bestLen = 0;
        WorldObject? last = null;
        foreach (var wo in _ctx.Inventory.Items())
        {
            if (wo.Id == _lastSummonEssenceId) last = wo;
            if (ClassifyKind(wo, out _) == null) continue;
            string b = EssenceBaseName(wo.Name);
            if (b.Length > bestLen && pet.Name.Contains(b, StringComparison.OrdinalIgnoreCase))
            {
                best = wo;
                bestLen = b.Length;
            }
        }
        return best ?? last;
    }

    // ── Cosmetic pet ────────────────────────────────────────────────────────

    /// <summary>Keeps the chosen cosmetic pet summoned while nothing else needs the pet slot.</summary>
    private void TickCosmetic(long now, WorldObject? pet)
    {
        if (!S.KeepCosmeticOut) { _cosmeticStatus = "off"; return; }
        if (string.IsNullOrWhiteSpace(S.CosmeticPetName)) { _cosmeticStatus = "no cosmetic pet ticked"; return; }
        if (pet != null)
        {
            _cosmeticStatus = PetMatchesEssence(pet, S.CosmeticPetName) ? "out" : "another pet is out";
            return;
        }
        if (now < _cosmeticNextAt || _heal != HealState.Idle || _ctx.Inventory.IsBusy) return;
        if (!_ctx.Inventory.InPeaceMode) { _cosmeticStatus = "waiting for peace mode"; return; }
        var settings = _ctx.Settings;
        if (settings != null && settings.IsMacroRunning && settings.SummonPets) { _cosmeticStatus = "combat summoner is active"; return; }

        var essence = FindCarried(S.CosmeticPetName);
        if (essence == null) { _cosmeticStatus = "essence not carried"; return; }
        if (_ctx.Inventory.Uses(essence) == 0 && !AllowSummonOnEmpty())
        {
            _cosmeticStatus = "essence is empty";
            _cosmeticNextAt = now + 30_000;
            return;
        }
        if (!_ctx.Inventory.Use(essence)) return;
        _cosmeticOutEssenceId = essence.Id;
        _lastSummonEssenceId = essence.Id;
        // Covers the summon round-trip so the pet isn't re-summoned before it appears.
        _cosmeticNextAt = now + Math.Max(5, S.CosmeticRespawnSeconds) * 1000L;
        _cosmeticStatus = "summoning";
        RynthLog.Trace(LogCat.Pets, $"cosmetic pet summon: {essence.Name}");
    }

    // ── Manual actions (pump thread) ────────────────────────────────────────

    /// <summary>Summons from <paramref name="essenceId"/>, or the current type's chosen pet when 0.</summary>
    private void SummonPet(int essenceId, PetKind kind)
    {
        if (FindOwnPet() is { } outPet) { _ctx.Chat($"[Mini Remote] {outPet.Name} is already out — Despawn it first."); return; }
        if (_heal != HealState.Idle) { _ctx.Chat("[Mini Remote] The heal-pet cycle is running."); return; }
        if (_ctx.Inventory.IsBusy) { _ctx.Chat("[Mini Remote] Busy — try again in a moment."); return; }

        WorldObject? essence = essenceId != 0 ? _ctx.Inventory.Items().FirstOrDefault(w => w.Id == essenceId) : ChosenEssence(kind);
        if (essence == null) { _ctx.Chat($"[Mini Remote] No {KindNames[(int)kind].ToLowerInvariant()} pet selected."); return; }
        if (_ctx.Inventory.Uses(essence) == 0 && !AllowSummonOnEmpty()) { _ctx.Chat($"[Mini Remote] {essence.Name} has no uses left."); return; }
        if (!_ctx.Inventory.Use(essence)) return;

        _lastSummonEssenceId = essence.Id;
        if (NameIs(essence, S.CosmeticPetName)) _cosmeticOutEssenceId = essence.Id;
        RynthLog.Trace(LogCat.Pets, $"manual summon: {essence.Name}");
    }

    /// <summary>Dismisses the pet that is out by using the essence that summoned it again.</summary>
    private void DespawnPet()
    {
        var pet = FindOwnPet();
        if (pet == null) { _ctx.Chat("[Mini Remote] No pet is out."); return; }
        if (_ctx.Inventory.IsBusy) { _ctx.Chat("[Mini Remote] Busy — try again in a moment."); return; }
        var essence = ResolveActiveEssence(pet);
        if (essence == null) { _ctx.Chat($"[Mini Remote] Couldn't find the essence for {pet.Name}."); return; }
        if (!_ctx.Inventory.Use(essence)) return;

        // A manual dismiss shouldn't be undone straight away by "Keep cosmetic pet out".
        _cosmeticNextAt = MiniRemoteContext.NowMs + Math.Max(5, S.CosmeticRespawnSeconds) * 1000L;
        if (essence.Id == _cosmeticOutEssenceId) _cosmeticOutEssenceId = 0;
        RynthLog.Trace(LogCat.Pets, $"manual despawn: {pet.Name} via {essence.Name}");
    }

    /// <summary>The pet the type's tick selects: first combat priority, the heal pet, or the cosmetic pet.</summary>
    private WorldObject? ChosenEssence(PetKind kind)
    {
        switch (kind)
        {
            case PetKind.Healing:
                return FindHealEssence();
            case PetKind.Cosmetic:
                return FindCarried(S.CosmeticPetName);
            default:
                var rules = PetRules();
                foreach (var r in rules)
                {
                    var wo = _ctx.Inventory.Items().FirstOrDefault(w => w.Id == r.Id || NameIs(w, r.Name));
                    if (wo != null) return wo;
                }
                return null;
        }
    }

    /// <summary>
    /// Requests appraisal for every carried pet (refreshes bond / potency after fights) and
    /// for un-appraised pet-named items, whose type is only known once the appraisal lands.
    /// </summary>
    private void ScanPack()
    {
        int pets = 0, candidates = 0;
        foreach (var wo in _ctx.Inventory.Items())
        {
            if (ClassifyKind(wo, out _) != null)
            {
                if (_ctx.Inventory.RequestAppraisal(wo)) pets++;
            }
            else if (IsUnconfirmedCandidate(wo) && _ctx.Inventory.RequestAppraisal(wo))
            {
                candidates++;
            }
        }
        if (pets == 0 && candidates == 0)
            _ctx.Chat("[Mini Remote] Pets were appraised recently.");
        else
            _ctx.Chat(candidates > 0
                ? $"[Mini Remote] Appraising {pets} pet(s) and checking {candidates} pet-named item(s)…"
                : $"[Mini Remote] Appraising {pets} pet(s)…");
        RebuildPetSnapshot(autoAppraise: false);
    }

    /// <summary>Gives the item selected in the game a summon type (pump thread).</summary>
    private void AddSelectedAs(PetKind kind)
    {
        if (!_ctx.Host.HasGetSelectedItemId) { _ctx.Chat("[Mini Remote] This client build can't read the selected item."); return; }
        int id = unchecked((int)_ctx.Host.GetSelectedItemId());
        var wo = _ctx.Inventory.Items().FirstOrDefault(w => w.Id == id);
        if (wo == null) { _ctx.Chat("[Mini Remote] Select a pet essence in your pack first."); return; }
        SetKind(wo.Id, wo.Name, kind);
        _ctx.Chat($"[Mini Remote] {wo.Name} added as a {KindNames[(int)kind].ToLowerInvariant()} pet.");
    }

    /// <summary>
    /// Re-types an essence (null = back to auto classification) and drops it from the other
    /// types' selections so a pet is only ever chosen under one type.
    /// </summary>
    private void SetKind(int id, string name, PetKind? kind)
    {
        S.Assignments.RemoveAll(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (kind is { } k) S.Assignments.Add(new PetAssignment { Name = name, Kind = (int)k });

        if (kind != PetKind.Combat && PetRules().Any(r => r.Id == id || r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            SetCombatSelected(id, name, false);
        if (kind != PetKind.Healing && S.HealPetName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            S.HealPetName = string.Empty;
        if (kind != PetKind.Cosmetic && S.CosmeticPetName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            S.CosmeticPetName = string.Empty;
        RebuildPetSnapshot(autoAppraise: false);
    }

    /// <summary>Applies a roster tick for the row's type (pump thread).</summary>
    private void SetSelected(int id, string name, PetKind kind, bool on)
    {
        switch (kind)
        {
            case PetKind.Healing:  S.HealPetName = on ? name : string.Empty; break;
            case PetKind.Cosmetic: S.CosmeticPetName = on ? name : string.Empty; break;
            default: SetCombatSelected(id, name, on); break;
        }
        RebuildPetSnapshot(autoAppraise: false);
    }

    // ── Combat summon list (ConsumableRules Type=="Pet") ────────────────────

    private List<ConsumableRule> PetRules()
        => _ctx.Settings?.ConsumableRules.Where(r => r.Type.Equals("Pet", StringComparison.OrdinalIgnoreCase)).ToList()
           ?? new List<ConsumableRule>();

    /// <summary>Adds or removes an essence from the PetManager list (pump thread).</summary>
    private void SetCombatSelected(int id, string name, bool on)
    {
        var settings = _ctx.Settings;
        if (settings == null) return;
        settings.ConsumableRules.RemoveAll(r => r.Type.Equals("Pet", StringComparison.OrdinalIgnoreCase)
                                                && (r.Id == id || r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (on) settings.ConsumableRules.Add(new ConsumableRule { Id = id, Name = name, Type = "Pet" });
        _ctx.SaveCombatSettings?.Invoke();
    }

    /// <summary>Moves a configured essence up (-1) or down (+1) in summon priority (pump thread).</summary>
    private void MovePriority(int id, int delta)
    {
        var settings = _ctx.Settings;
        if (settings == null) return;
        var list = settings.ConsumableRules;
        int idx = list.FindIndex(r => r.Id == id && r.Type.Equals("Pet", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        // Find the neighbouring Pet rule in the requested direction.
        int j = idx + delta;
        while (j >= 0 && j < list.Count && !list[j].Type.Equals("Pet", StringComparison.OrdinalIgnoreCase)) j += delta;
        if (j < 0 || j >= list.Count) return;
        (list[idx], list[j]) = (list[j], list[idx]);
        _ctx.SaveCombatSettings?.Invoke();
        RebuildPetSnapshot(autoAppraise: false);
    }

    // ── Roster snapshot (pump thread) ───────────────────────────────────────

    private void RebuildPetSnapshot(bool autoAppraise)
    {
        var rules = PetRules();
        long unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<PetStats>();
        int appraised = 0;
        foreach (var wo in _ctx.Inventory.Items())
        {
            var kind = ClassifyKind(wo, out bool assigned);
            if (kind == null)
            {
                // Pet-named but unconfirmed: its appraisal decides whether it is a pet device.
                if (autoAppraise && appraised < AutoAppraisePerScan && IsUnconfirmedCandidate(wo)
                    && _ctx.Inventory.RequestAppraisal(wo))
                    appraised++;
                continue;
            }

            var s = PetStatsReader.Read(_ctx.Host, _ctx.Inventory, wo, unixNow);
            s.Kind = kind.Value;
            s.KindAssigned = assigned;
            int pri = rules.FindIndex(r => r.Id == wo.Id || r.Name.Equals(wo.Name, StringComparison.OrdinalIgnoreCase));
            s.Priority = pri >= 0 ? pri : int.MaxValue;
            s.Selected = s.Kind switch
            {
                PetKind.Healing  => NameIs(wo, S.HealPetName),
                PetKind.Cosmetic => NameIs(wo, S.CosmeticPetName),
                _                   => pri >= 0,
            };
            rows.Add(s);

            // Stats beyond uses need an appraisal; trickle the requests for un-ID'd essences.
            if (autoAppraise && !s.HasId && appraised < AutoAppraisePerScan && _ctx.Inventory.RequestAppraisal(wo))
                appraised++;
        }
        _pets = rows.ToArray();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    /// <summary>Set from any thread: the next frame opens the Breeding sub-tab ("/ra hub open breeding").</summary>
    private volatile bool _selectBreeding;

    /// <summary>Shows the Breeding sub-tab on the next frame (any thread).</summary>
    public void SelectBreeding() => _selectBreeding = true;

    /// <summary>
    /// RynthILT offers the Breeding sub-tab: it calls this world Infinite Leaftide and the server
    /// hasn't reported pet breeding off. False without RynthILT. Any thread.
    /// </summary>
    public bool BreedingOffered => _ctx.Ilt.IsIltWorld && !_ctx.Ilt.IsOff(RynthIltHub.FeaturePetBreeding);

    /// <summary>Whether auto siphon-lens capture is on (Mini Remote Capture toggle; settings on the roster).</summary>
    public bool AutoCaptureEnabled
    {
        get => S.AutoCaptureEnabled;
        set => S.AutoCaptureEnabled = value;
    }

    /// <summary>
    /// Mini Remote Pet toggle on: enable combat auto-summon and summon the Pets tab's active
    /// combat essence now (first ticked priority, or the highlighted row). Pump thread.
    /// </summary>
    public void ActivateHudPet(LegacyUiSettings settings)
    {
        settings.SummonPets = true;
        _ctx.SaveCombatSettings?.Invoke();
        if (FindOwnPet() != null) return;   // already out — leave it
        SummonPet(0, PetKind.Combat);
    }

    /// <summary>Mini Remote Pet toggle off: stop combat auto-summon. Pump thread.</summary>
    public void DeactivateHudPet(LegacyUiSettings settings)
    {
        settings.SummonPets = false;
        _ctx.SaveCombatSettings?.Invoke();
    }

    /// <summary>
    /// The Pets tab: the universal roster, and on Infinite Leaftide the Roster | Breeding sub-tabs
    /// (Breeding is drawn by RynthILT).
    /// </summary>
    public void Render()
    {
        var settings = _ctx.Settings;
        if (settings == null) { ImGui.TextDisabled("Not logged in."); return; }

        if (!BreedingOffered)
        {
            RenderRoster(settings);
            return;
        }
        if (!ImGui.BeginTabBar("##petsub")) return;
        if (ImGui.BeginTabItem("Roster##petroster"))
        {
            RenderRoster(settings);
            ImGui.EndTabItem();
        }
        ImGuiTabItemFlags breedFlags = _selectBreeding ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        _selectBreeding = false;
        if (BeginTab(BreedingTabLabel, breedFlags))
        {
            // RynthILT's tracker; an older RynthILT (before 0.3.1) has no such section and draws nothing.
            if (!_ctx.Ilt.RenderSection(RynthIltHub.SectionPetBreeding))
                ImGui.TextDisabled("This RynthILT has no pet breeding tracker; update RynthILT to 0.3.1 or later.");
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private static readonly byte[] BreedingTabLabel = System.Text.Encoding.UTF8.GetBytes("Breeding##petbreeding\0");

    /// <summary>
    /// BeginTabItem with flags but no close button (ImGui.NET pairs the flags overload with p_open,
    /// which draws an X on the tab).
    /// </summary>
    private static unsafe bool BeginTab(byte[] labelUtf8Z, ImGuiTabItemFlags flags)
    {
        fixed (byte* label = labelUtf8Z)
            return ImGuiNative.igBeginTabItem(label, null, flags) != 0;
    }

    /// <summary>The universal roster: types, summons, ILT charms, healing pets and RynthILT's /pets logs.</summary>
    private void RenderRoster(LegacyUiSettings settings)
    {
        RenderRosterHeader(settings);
        RenderRosterTable();
        // The tab is universal; the charms and the /pets and /shinies logs are ILT's (the logs RynthILT's).
        bool ilt = _ctx.Ilt.IsIltWorld;
        if (ilt)
        {
            ImGui.Separator();
            RenderCharms();
            ImGui.Separator();
            RenderCapture();
        }
        ImGui.Separator();
        RenderHealing();
        _ctx.Ilt.RenderSection(RynthIltHub.SectionPetLogs);
    }

    /// <summary>ACECustom siphon-lens auto-capture (ILT only): enable + which lens to use.</summary>
    private void RenderCapture()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Monster capture");
        bool captureOff = _ctx.Ilt.IsOff(RynthIltHub.FeatureMonsterCapture);
        ImGui.BeginDisabled(captureOff);
        bool en = S.AutoCaptureEnabled;
        if (ImGui.Checkbox("Auto-capture when a creature is weak enough", ref en))
            S.AutoCaptureEnabled = en;
        if (ImGui.IsItemHovered())
        {
            SafeImGui.SetTooltip(
                "Uses the selected siphon lens on the nearest attackable creature within 5 m once it is\n" +
                "at or below 20% health or under 20 HP (ACECustom MonsterCapture). Asheron's Lens skips\n" +
                "the health gate. Resonance Lens still needs a prior failed capture on the server.");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        int lens = Math.Clamp(S.CaptureLensIndex, 0, PetCaptureRules.LensNames.Length - 1);
        if (ImGui.Combo("##petcapturelens", ref lens, PetCaptureRules.LensNames, PetCaptureRules.LensNames.Length))
            S.CaptureLensIndex = lens;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Which siphon lens to use. Must be carried in your pack.");
        ImGui.EndDisabled();
        if (captureOff)
            SafeImGui.TextDisabled("Monster capture is disabled on this server.");
        else
            SafeImGui.TextDisabled("Status: " + _captureStatus);
    }

    /// <summary>Type / Sort combos, action buttons, the active-summon line and per-type options.</summary>
    private void RenderRosterHeader(LegacyUiSettings settings)
    {
        var kind = (PetKind)Math.Clamp(S.RosterKind, 0, 2);

        ImGui.TextUnformatted("Type");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        int k = (int)kind;
        if (ImGui.Combo("##iltpetkind", ref k, KindNames, KindNames.Length)) { S.RosterKind = k; kind = (PetKind)k; _highlightId = 0; }
        ImGui.SameLine();
        ImGui.TextUnformatted("Sort");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        int sort = Math.Clamp(S.RosterSort, 0, SortNames.Length - 1);
        if (ImGui.Combo("##iltpetsort", ref sort, SortNames, SortNames.Length)) S.RosterSort = sort;

        int target = _highlightId;
        var summonKind = kind;
        if (ImGui.Button("Summon")) _ctx.Post(() => SummonPet(target, summonKind));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Summon the highlighted pet (or this type's ticked pet).");
        ImGui.SameLine();
        if (ImGui.Button("Despawn")) _ctx.Post(DespawnPet);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Dismiss the pet that is out.");
        ImGui.SameLine();
        if (ImGui.Button("Scan pack")) _ctx.Post(ScanPack);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Appraise every pet to refresh bond, potency and breeding, and check pet-named items\n(an \"... Essence\" name alone isn't enough — the item must be a summoning pet device).");
        ImGui.SameLine();
        if (ImGui.Button("Add sel.")) _ctx.Post(() => AddSelectedAs(summonKind));
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"Make the item selected in your pack a {KindNames[(int)kind].ToLowerInvariant()} pet.");

        string active = _uiActivePet;
        if (active.Length > 0) SafeImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Active summon: " + active);
        else ImGui.TextDisabled("Active summon: none");

        switch (kind)
        {
            case PetKind.Combat:
            {
                bool summon = settings.SummonPets;
                if (ImGui.Checkbox("Summon when monsters are near", ref summon)) _ctx.Post(() => { settings.SummonPets = summon; _ctx.SaveCombatSettings?.Invoke(); });
                ImGui.SameLine();
                int minMobs = settings.PetMinMonsters;
                ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("Min monsters", ref minMobs)) _ctx.Post(() => { settings.PetMinMonsters = Math.Max(1, minMobs); _ctx.SaveCombatSettings?.Invoke(); });
                bool refill = settings.PetAutoRefill;
                if (ImGui.Checkbox("Refill empty essences with Encapsulated Spirit", ref refill)) _ctx.Post(() => { settings.PetAutoRefill = refill; _ctx.SaveCombatSettings?.Invoke(); });
                ImGui.TextDisabled("Tick pets to add them to the summon list; with Sort = Priority the arrows set the order.");
                break;
            }
            case PetKind.Healing:
                ImGui.TextDisabled("Tick one heal pet (none ticked = first Healing Buddy / Dule box). Settings are below.");
                break;
            case PetKind.Cosmetic:
            {
                bool keep = S.KeepCosmeticOut;
                if (ImGui.Checkbox("Keep cosmetic pet out", ref keep)) S.KeepCosmeticOut = keep;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Peace mode only. Dismissed automatically when the combat summoner needs the pet slot.");
                ImGui.SameLine();
                int resp = S.CosmeticRespawnSeconds;
                ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("Respawn (s)", ref resp)) S.CosmeticRespawnSeconds = Math.Clamp(resp, 5, 600);
                SafeImGui.TextDisabled("Tick one cosmetic pet. Status: " + _cosmeticStatus);
                break;
            }
        }
    }

    /// <summary>Sorted rows of the current type, cached until the snapshot / type / sort changes.</summary>
    private PetStats[] CurrentView()
    {
        var src = _pets;
        int kind = Math.Clamp(S.RosterKind, 0, 2);
        int sort = Math.Clamp(S.RosterSort, 0, SortNames.Length - 1);
        if (!ReferenceEquals(src, _viewSource) || kind != _viewKind || sort != _viewSort)
        {
            _view = PetStatsReader.Sort(src.Where(p => (int)p.Kind == kind), (PetSort)sort);
            _viewSource = src;
            _viewKind = kind;
            _viewSort = sort;
        }
        return _view;
    }

    /// <summary>One table row per pet: tick · three stat lines (highlight / right-click menu) · priority arrows.</summary>
    private void RenderRosterTable()
    {
        var rows = CurrentView();
        var kind = (PetKind)Math.Clamp(S.RosterKind, 0, 2);
        bool showArrows = kind == PetKind.Combat && S.RosterSort == (int)PetSort.Priority;

        if (rows.Length == 0)
        {
            SafeImGui.TextDisabled(kind == PetKind.Cosmetic
                ? "No cosmetic pets yet — select an essence in your pack and press Add sel., or right-click a pet in another type."
                : $"No {KindNames[(int)kind].ToLowerInvariant()} pet essences carried.");
            return;
        }

        float lineH = ImGui.GetTextLineHeightWithSpacing();
        float tableH = Math.Min(320f, rows.Length * (lineH * 3 + 6) + 8);
        if (!ImGui.BeginTable("##iltpets", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new Vector2(0, tableH)))
            return;
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed, 26);
        ImGui.TableSetupColumn("##pet");
        ImGui.TableSetupColumn("##ord", ImGuiTableColumnFlags.WidthFixed, showArrows ? 52 : 1);

        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool on = r.Selected;
            if (ImGui.Checkbox($"##psel{r.Id}", ref on))
            {
                var row = r;
                _ctx.Post(() => SetSelected(row.Id, row.Name, row.Kind, on));
            }

            ImGui.TableNextColumn();
            Vector2 start = ImGui.GetCursorPos();
            if (ImGui.Selectable($"##prow{r.Id}", _highlightId == r.Id, ImGuiSelectableFlags.AllowOverlap, new Vector2(0, lineH * 3 - ImGui.GetStyle().ItemSpacing.Y)))
                _highlightId = _highlightId == r.Id ? 0 : r.Id;
            RenderRowContextMenu(r);
            ImGui.SetCursorPos(start);
            SafeImGui.TextColored(DamageColor(r.DamageType), r.Line1);
            SafeImGui.TextColored(LegacyDashboardRenderer.ColAmber, "  " + r.Line2);
            if (r.UsesMax > 0 && r.UsesCur == 0) SafeImGui.TextColored(LegacyDashboardRenderer.ColHp, "  " + r.Line3);
            else SafeImGui.TextDisabled("  " + r.Line3);

            ImGui.TableNextColumn();
            if (showArrows && r.Selected)
            {
                int id = r.Id;
                if (ImGui.ArrowButton($"##pup{r.Id}", ImGuiDir.Up)) _ctx.Post(() => MovePriority(id, -1));
                ImGui.SameLine();
                if (ImGui.ArrowButton($"##pdn{r.Id}", ImGuiDir.Down)) _ctx.Post(() => MovePriority(id, +1));
            }
        }
        ImGui.EndTable();
    }

    /// <summary>Right-click menu on a roster row: summon / move to another type / clear override.</summary>
    private void RenderRowContextMenu(PetStats r)
    {
        if (!ImGui.BeginPopupContextItem($"##pctx{r.Id}")) return;
        var row = r;
        if (ImGui.MenuItem("Summon")) _ctx.Post(() => SummonPet(row.Id, row.Kind));
        ImGui.Separator();
        for (int i = 0; i < KindNames.Length; i++)
        {
            if (i == (int)r.Kind) continue;
            var to = (PetKind)i;
            if (ImGui.MenuItem("Move to " + KindNames[i])) _ctx.Post(() => SetKind(row.Id, row.Name, to));
        }
        if (r.KindAssigned && ImGui.MenuItem("Clear type override"))
            _ctx.Post(() => SetKind(row.Id, row.Name, null));
        ImGui.EndPopup();
    }

    /// <summary>Line-1 colour by the pet's damage type (white when unknown / not appraised).</summary>
    private static Vector4 DamageColor(string damageType) => damageType switch
    {
        "Fire"      => new Vector4(1.00f, 0.55f, 0.30f, 1f),
        "Cold"      => new Vector4(0.55f, 0.80f, 1.00f, 1f),
        "Acid"      => new Vector4(0.55f, 0.95f, 0.45f, 1f),
        "Lightning" => new Vector4(0.85f, 0.70f, 1.00f, 1f),
        "Nether"    => new Vector4(0.75f, 0.50f, 0.90f, 1f),
        _           => new Vector4(0.95f, 0.95f, 0.95f, 1f),
    };

    private void RenderCharms()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Pet charms");
        bool refillOff = _ctx.Ilt.IsOff(RynthIltHub.FeaturePetRefill);
        ImGui.TextUnformatted("Summon Essence Refill: " + (refillOff ? "disabled on this server" : _uiRefillActive ? "ON" : "off / not carried"));
        ImGui.BeginDisabled(refillOff);
        bool usePath = S.UsePyrealRefillPath;
        if (ImGui.Checkbox("Summon from empty essences while the refill charm is ON (spends banked pyreals)", ref usePath))
            S.UsePyrealRefillPath = usePath;
        ImGui.EndDisabled();
        ImGui.TextUnformatted("Universal Summoning Mastery: "
            + (_ctx.Ilt.IsOff(RynthIltHub.FeatureUniversalMastery) ? "disabled on this server" : _uiMasteryActive ? "ON" : "off / not carried"));
        if (!_ctx.Ilt.IsOff(RynthIltHub.FeatureRequireComps) && !_uiHasCasting)
            ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "No spell components or Infinite Casting Stone carried.");
    }

    private void RenderHealing()
    {
        if (!ImGui.CollapsingHeader("Healing pet settings")) return;
        bool en = S.HealingEnabled;
        if (ImGui.Checkbox("Summon heal pet when health is low", ref en)) S.HealingEnabled = en;
        string healPet = S.HealPetName;
        SafeImGui.TextDisabled("Heal pet: " + (string.IsNullOrWhiteSpace(healPet) ? "auto (Healing Buddy / Dule box)" : healPet)
                           + "  — tick one under Type = Healing.");
        int thr = S.HealthThresholdPercent;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Health % trigger", ref thr, 10, 99)) S.HealthThresholdPercent = thr;
        int minHp = S.MinHealthPoints;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Don't summon below HP", ref minHp, 10)) S.MinHealthPoints = Math.Max(0, minHp);
        bool desp = S.AutoDespawnAfterHeal;
        if (ImGui.Checkbox("Dismiss after the heal", ref desp)) S.AutoDespawnAfterHeal = desp;
        int resp = S.RespawnDelaySeconds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Respawn delay (s)", ref resp)) S.RespawnDelaySeconds = Math.Max(1, resp);
        int to = S.PetTimeoutSeconds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Heal timeout (s)", ref to)) S.PetTimeoutSeconds = Math.Max(5, to);
        SafeImGui.TextDisabled("Status: " + _healStatus);
    }
}
