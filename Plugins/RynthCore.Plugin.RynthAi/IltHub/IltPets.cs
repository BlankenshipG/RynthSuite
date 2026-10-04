// IltPets.cs — ILT Hub "Pet" tab.
//
//   * Essence picker: lists carried combat essences and edits the existing
//     ConsumableRules Type=="Pet" list that PetManager already summons from — no second
//     summoning loop. Order in that list is the summon priority.
//   * Pet charms: Summon Essence Refill (server refills an empty essence from banked
//     pyreals) and Universal Summoning Mastery, read from the charm appraisal text and the
//     player bools the server stamps. Feeds PetManager.AllowSummonOnEmpty.
//   * Healing pet (UB HealingBuddy subset): summons a heal pet when HP drops, dismisses it
//     after the heal lands, holds PetManager's combat summons while it is out.
//   * /pets and /shinies roster capture (read-only display).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltPets : IIltFeature
{
    private readonly IltHubContext _ctx;

    // Healing-pet state machine.
    private enum HealState { Idle, Summoning, Despawning }
    private HealState _heal = HealState.Idle;
    private long _healStartedAt;
    private long _despawnAt;
    private long _healCooldownUntil;
    private int _healEssenceId;
    private long _lastHealThink;
    private string _healStatus = "idle";

    // Render-thread snapshot of carried essences (rebuilt on the pump thread).
    private volatile EssenceRow[] _essences = Array.Empty<EssenceRow>();
    private long _lastEssenceScan;

    // Charm / supply status cached for the render thread.
    private volatile bool _uiRefillActive;
    private volatile bool _uiMasteryActive;
    private volatile bool _uiHasCasting = true;

    private sealed record EssenceRow(int Id, string Name, int Uses, int MaxUses, bool Configured, int Priority, bool IsHealPet);

    public IltPets(IltHubContext ctx) => _ctx = ctx;

    private IltPetState S => _ctx.State.Pet;

    // ── Classification ──────────────────────────────────────────────────────

    /// <summary>ACECustom combat essence (WCID block or "... Essence"), excluding spirits / charms / capture devices.</summary>
    public bool IsCombatEssence(WorldObject wo)
    {
        uint w = _ctx.Inventory.Wcid(wo);
        if (w == IltInventory.WcidEncapsulatedSpirit) return false;
        if (w is >= 78780030 and <= 78780089) return false; // charm block
        if (w is >= 78780001 and <= 78780012) return false; // capture devices
        if (w is >= 787801001 and <= 787801072) return true;
        return wo.Name.EndsWith(" Essence", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Heal-pet essence: configured name, else "Healing Buddy" / "Dule box" naming.</summary>
    private bool IsHealPet(WorldObject wo)
    {
        if (!string.IsNullOrWhiteSpace(S.HealPetName))
            return wo.Name.Equals(S.HealPetName.Trim(), StringComparison.OrdinalIgnoreCase);
        string n = wo.Name;
        return (n.Contains("healing", StringComparison.OrdinalIgnoreCase) && n.Contains("buddy", StringComparison.OrdinalIgnoreCase))
            || (n.Contains("dule", StringComparison.OrdinalIgnoreCase) && n.Contains("box", StringComparison.OrdinalIgnoreCase));
    }

    // ── Charms / gates ──────────────────────────────────────────────────────

    /// <summary>Summon Essence Refill charm is carried and switched on (or the server stamped the player bool).</summary>
    public bool RefillCharmActive()
    {
        if (_ctx.Inventory.PlayerBool(IltInventory.BoolPyrealRefillActive)) return true;
        var charm = _ctx.Inventory.FindByWcid(IltInventory.WcidSummonRefillCharm);
        return charm != null && _ctx.Inventory.CharmStatus(charm) == IltTri.On;
    }

    public bool MasteryCharmActive()
    {
        if (_ctx.Options.IsOff(IltFeature.UniversalMastery)) return false;
        if (_ctx.Inventory.PlayerBool(IltInventory.BoolUniversalMasteryActive)) return true;
        var charm = _ctx.Inventory.FindByWcid(IltInventory.WcidUniversalMasteryCharm);
        return charm != null && _ctx.Inventory.CharmStatus(charm) == IltTri.On;
    }

    /// <summary>
    /// PetManager hook: summon from an empty essence (spends banked pyreals) only when the
    /// player opted in, the refill charm is active, and the server has positively confirmed
    /// the feature: either /ilt features reported pet_refill on, or the server stamped the
    /// player's refill bool. An unknown feature state never spends.
    /// </summary>
    public bool AllowSummonOnEmpty()
    {
        if (!S.UsePyrealRefillPath || !_ctx.Options.IsIltLikeWorld || !RefillCharmActive()) return false;
        bool confirmed = _ctx.Options.IsOn(IltFeature.PetRefill)
                         || _ctx.Inventory.PlayerBool(IltInventory.BoolPyrealRefillActive);
        if (!confirmed) RynthLog.Trace(LogCat.IltPets, "summon-on-empty refused: pet_refill not confirmed by the server");
        return confirmed;
    }

    /// <summary>PetManager hook: hold combat summons while the heal pet cycle is running.</summary>
    public bool HoldCombatSummons() => _heal != HealState.Idle;

    // ── Rosters ─────────────────────────────────────────────────────────────

    /// <summary>Captures /pets or /shinies into the state (quiet).</summary>
    public void RequestRoster(bool shinies)
    {
        RynthLog.Trace(LogCat.IltPets, $"RequestRoster(shinies={shinies})");
        string cmd = shinies ? "/shinies" : "/pets";
        if (_ctx.Capture.IsPending(cmd)) return;
        string header = shinies ? "Your Shiny Log" : "Your Pet Log";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = cmd,
            IsResponseLine = t => t.Contains(header, StringComparison.OrdinalIgnoreCase) || IltParse.RosterLine.IsMatch(t),
            IsTerminator = t => t.Contains(header + " is empty", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 1500,
            FirstLineTimeoutMs = 10000,
            Eat = true,
            OnComplete = r =>
            {
                if (r.UnknownCommand) { _ctx.Options.Set(shinies ? IltFeature.Shinies : IltFeature.Pets, IltTri.Off); return; }
                if (r.Lines.Count == 0) return;
                _ctx.Options.Set(shinies ? IltFeature.Shinies : IltFeature.Pets, IltTri.On);
                ApplyRoster(shinies, r.Lines);
            },
        });
    }

    /// <summary>Also used by the login probe tap so the roster fills without a second command.</summary>
    public void ApplyRoster(bool shinies, List<string> lines)
    {
        RynthLog.Trace(LogCat.IltPets, $"ApplyRoster(shinies={shinies}, lines={lines.Count})");
        var list = new List<string>();
        foreach (string l in lines)
        {
            var m = IltParse.RosterLine.Match(l);
            if (m.Success) list.Add(m.Groups[2].Value.Trim());
        }
        if (shinies) S.ShinyLog = list; else S.PetLog = list;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (nowMs - _lastEssenceScan >= 2000)
        {
            _lastEssenceScan = nowMs;
            RebuildEssenceSnapshot();
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
                _despawnAt = IltHubContext.NowMs + 1000;
                _healStatus = "heal landed, dismissing";
            }
            else
            {
                EndHealCycle(IltHubContext.NowMs, "heal landed");
            }
        }
        return false;
    }

    public void OnLogout()
    {
        _heal = HealState.Idle;
        _essences = Array.Empty<EssenceRow>();
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
                var essence = _ctx.Inventory.Items().FirstOrDefault(IsHealPet);
                if (essence == null) { _healStatus = "no heal-pet essence found"; return; }
                if (_ctx.Inventory.Uses(essence) == 0 && !AllowSummonOnEmpty()) { _healStatus = "heal-pet essence is empty"; return; }
                if (!_ctx.Inventory.Use(essence)) return;
                _healEssenceId = essence.Id;
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

    /// <summary>A live "&lt;Me&gt;'s …" creature is in the world (server pet naming).</summary>
    private bool AnyOwnPetPresent()
    {
        var cache = _ctx.Inventory.Cache;
        if (cache == null || _ctx.CharName.Length == 0) return false;
        string prefix = _ctx.CharName + "'s ";
        return cache.GetLandscape().Any(wo => wo != null && wo.Name.StartsWith(prefix, StringComparison.Ordinal));
    }

    // ── Essence picker data ─────────────────────────────────────────────────

    private void RebuildEssenceSnapshot()
    {
        var settings = _ctx.Settings;
        var rules = settings?.ConsumableRules.Where(r => r.Type.Equals("Pet", StringComparison.OrdinalIgnoreCase)).ToList()
                    ?? new List<ConsumableRule>();
        var rows = new List<EssenceRow>();
        foreach (var wo in _ctx.Inventory.Items())
        {
            if (!IsCombatEssence(wo) && !IsHealPet(wo)) continue;
            int pri = rules.FindIndex(r => r.Id == wo.Id || r.Name.Equals(wo.Name, StringComparison.OrdinalIgnoreCase));
            rows.Add(new EssenceRow(wo.Id, wo.Name, _ctx.Inventory.Uses(wo), _ctx.Inventory.Int(wo, IltInventory.IntMaxStructure),
                pri >= 0, pri, IsHealPet(wo)));
        }
        _essences = rows.OrderBy(r => r.Configured ? r.Priority : int.MaxValue).ThenBy(r => r.Name).ToArray();
    }

    /// <summary>Adds or removes an essence from the PetManager list (pump thread).</summary>
    private void SetConfigured(int id, string name, bool on)
    {
        var settings = _ctx.Settings;
        if (settings == null) return;
        settings.ConsumableRules.RemoveAll(r => r.Type.Equals("Pet", StringComparison.OrdinalIgnoreCase)
                                                && (r.Id == id || r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (on) settings.ConsumableRules.Add(new ConsumableRule { Id = id, Name = name, Type = "Pet" });
        _ctx.SaveCombatSettings?.Invoke();
        RebuildEssenceSnapshot();
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
        RebuildEssenceSnapshot();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        var settings = _ctx.Settings;
        if (settings == null) { ImGui.TextDisabled("Not logged in."); return; }

        RenderCombatSummonOptions(settings);
        ImGui.Separator();
        RenderEssenceTable();
        ImGui.Separator();
        RenderCharms();
        ImGui.Separator();
        RenderHealing();
        ImGui.Separator();
        RenderRosters();
    }

    private void RenderCombatSummonOptions(LegacyUiSettings settings)
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Combat pets (uses RynthAi's pet summoner)");
        bool summon = settings.SummonPets;
        if (ImGui.Checkbox("Summon pets when monsters are near", ref summon)) _ctx.Post(() => { settings.SummonPets = summon; _ctx.SaveCombatSettings?.Invoke(); });
        bool refill = settings.PetAutoRefill;
        if (ImGui.Checkbox("Refill empty essences with Encapsulated Spirit", ref refill)) _ctx.Post(() => { settings.PetAutoRefill = refill; _ctx.SaveCombatSettings?.Invoke(); });
        int minMobs = settings.PetMinMonsters;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Min monsters", ref minMobs)) _ctx.Post(() => { settings.PetMinMonsters = Math.Max(1, minMobs); _ctx.SaveCombatSettings?.Invoke(); });
    }

    private void RenderEssenceTable()
    {
        var rows = _essences;
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, $"Carried essences ({rows.Length})");
        ImGui.TextDisabled("Tick an essence to add it to the summon list; arrows set priority.");
        if (ImGui.BeginTable("##iltess", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new Vector2(0, 170)))
        {
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 34);
            ImGui.TableSetupColumn("Essence");
            ImGui.TableSetupColumn("Uses", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();
            foreach (var r in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool on = r.Configured;
                if (ImGui.Checkbox($"##ess{r.Id}", ref on))
                {
                    var row = r;
                    _ctx.Post(() => SetConfigured(row.Id, row.Name, on));
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.IsHealPet ? r.Name + "  (heal pet)" : r.Name);
                ImGui.TableNextColumn();
                if (r.Uses == 0) ImGui.TextColored(LegacyDashboardRenderer.ColHp, $"0 / {r.MaxUses}");
                else ImGui.TextUnformatted($"{r.Uses} / {r.MaxUses}");
                ImGui.TableNextColumn();
                if (r.Configured)
                {
                    int id = r.Id;
                    if (ImGui.ArrowButton($"##up{r.Id}", ImGuiDir.Up)) _ctx.Post(() => MovePriority(id, -1));
                    ImGui.SameLine();
                    if (ImGui.ArrowButton($"##dn{r.Id}", ImGuiDir.Down)) _ctx.Post(() => MovePriority(id, +1));
                }
            }
            ImGui.EndTable();
        }
    }

    private void RenderCharms()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Pet charms");
        bool refillOff = _ctx.Options.IsOff(IltFeature.PetRefill);
        ImGui.TextUnformatted("Summon Essence Refill: " + (refillOff ? "disabled on this server" : _uiRefillActive ? "ON" : "off / not carried"));
        ImGui.BeginDisabled(refillOff);
        bool usePath = S.UsePyrealRefillPath;
        if (ImGui.Checkbox("Summon from empty essences while the refill charm is ON (spends banked pyreals)", ref usePath))
            S.UsePyrealRefillPath = usePath;
        ImGui.EndDisabled();
        ImGui.TextUnformatted("Universal Summoning Mastery: "
            + (_ctx.Options.IsOff(IltFeature.UniversalMastery) ? "disabled on this server" : _uiMasteryActive ? "ON" : "off / not carried"));
        if (!_ctx.Options.IsOff(IltFeature.RequireComps) && !_uiHasCasting)
            ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "No spell components or Infinite Casting Stone carried.");
    }

    private void RenderHealing()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Healing pet");
        bool en = S.HealingEnabled;
        if (ImGui.Checkbox("Summon heal pet when health is low", ref en)) S.HealingEnabled = en;
        string name = S.HealPetName;
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputTextWithHint("Heal-pet essence", "auto: Healing Buddy / Dule box", ref name, 128u)) S.HealPetName = name;
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
        ImGui.TextDisabled("Status: " + _healStatus);
    }

    private void RenderRosters()
    {
        RenderRoster("Pet log", false, IltFeature.Pets, S.PetLog);
        RenderRoster("Shiny log", true, IltFeature.Shinies, S.ShinyLog);
    }

    private void RenderRoster(string label, bool shinies, string key, List<string> list)
    {
        if (_ctx.Options.IsOff(key)) return;
        var snap = list.ToArray();
        if (ImGui.TreeNode($"{label} ({snap.Length})##{key}"))
        {
            if (ImGui.SmallButton($"Refresh##{key}")) _ctx.Post(() => RequestRoster(shinies));
            foreach (string s in snap) ImGui.BulletText(s);
            ImGui.TreePop();
        }
    }
}
