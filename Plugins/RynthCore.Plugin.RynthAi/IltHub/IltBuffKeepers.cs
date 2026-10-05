// IltBuffKeepers.cs — Rune-of-Dispel auto-dispel and Surging Strength keeper (Gear tab).
//
// Both read the player's enchantment list once a second (one shared read).
//   Dispel: classifies active spells by NAME (SpellDatabase has no debuff flag):
//     1. Mire Foot (3051 / "mire"+"foot")     → never (dispelling it is pointless)
//     2. user include list                     → dispel
//     3. beneficial pattern                    → keep
//     4. user exclude list                     → keep
//     5. known-harmful exact name or pattern   → dispel
//   and uses a Rune of Dispel (WCID 30133). Cooldown, verify after 2 s, up to 3 retries.
//   Surging Strength: uses the Empowered Volcanic Ember when the buff is missing.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltBuffKeepers : IIltFeature
{
    private const int SpellMireFoot = 3051;
    private const int MaxEnch = 256;

    private static readonly HashSet<string> HarmfulExact = BuildHarmfulExact();
    private static readonly Regex HarmfulPattern = new(
        @"\b(vulnerability|imperil|weakness|slowness|bane|exhaustion|mana burn|fester|curse of|blight of|burden of)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BeneficialPattern = new(
        @"\b(invulnerability|invulnerable|protection|ward|armor|blessing|strength|coordination|endurance|quickness|focus|willpower|"
        + @"regeneration|mana renewal|stamina renewal|health renewal|arcanum|cantrap|enhancement|augmentation|weave of|shroud of|"
        + @"mantle of|cloak of|veil of|master's blessing|society|fellowship|loyalty|devotion)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static HashSet<string> BuildHarmfulExact()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in new[] { "weakness other", "vulnerability other", "imperil other", "slowness other",
                                     "bane other", "exhaustion other", "mana burn other", "fester other" })
        {
            set.Add(s);
            set.Add("incantation of " + s);
        }
        foreach (string s in new[] { "belly of lead", "gelidite's gift", "inferno's gift", "senescence", "archer's gift",
                                     "swordsman's gift", "leaden feet", "heart of ice", "burning soul", "withering" })
            set.Add(s);
        return set;
    }

    private readonly IltHubContext _ctx;
    private readonly uint[] _ids = new uint[MaxEnch];
    private readonly double[] _exp = new double[MaxEnch];
    private long _lastRead;

    // Dispel state
    private long _dispelCooldownUntil;
    private long _verifyAt;
    private int _retries;
    private volatile string[] _harmfulNow = Array.Empty<string>();
    private volatile string _dispelStatus = "idle";

    // Surging state
    private long _surgeCooldownUntil;
    private volatile bool _surgeActive;
    private volatile string _surgeStatus = "idle";

    // Render-thread edit buffers
    private string _newInclude = string.Empty, _newExclude = string.Empty;

    public IltBuffKeepers(IltHubContext ctx) => _ctx = ctx;

    private IltGearState G => _ctx.State.Gear;

    /// <summary>True when this spell should be dispelled (precedence documented above).</summary>
    public bool IsHarmful(int spellId, string name)
    {
        if (spellId == SpellMireFoot || (name.Contains("mire", StringComparison.OrdinalIgnoreCase) && name.Contains("foot", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (G.DispelInclusions.ToArray().Any(i => i.Length > 0 && name.Contains(i, StringComparison.OrdinalIgnoreCase))) return true;
        if (BeneficialPattern.IsMatch(name)) return false;
        if (G.DispelExclusions.ToArray().Any(e => e.Length > 0 && name.Contains(e, StringComparison.OrdinalIgnoreCase))) return false;
        string bare = StripTier(name);
        return HarmfulExact.Contains(name) || HarmfulExact.Contains(bare) || HarmfulPattern.IsMatch(name);
    }

    /// <summary>"Vulnerability Other VII" → "vulnerability other" (roman numeral suffix removed).</summary>
    private static string StripTier(string name)
    {
        string n = name.Trim();
        int sp = n.LastIndexOf(' ');
        if (sp > 0)
        {
            string tail = n.Substring(sp + 1);
            if (tail.Length > 0 && tail.All(ch => "IVXL".IndexOf(char.ToUpperInvariant(ch)) >= 0)) n = n.Substring(0, sp);
        }
        return n.ToLowerInvariant();
    }

    private WorldObject? FindRune()
        => _ctx.Inventory.FindByWcid(IltInventory.WcidRuneOfDispel)
           ?? _ctx.Inventory.FindByName(n => n.Equals("Rune of Dispel", StringComparison.OrdinalIgnoreCase)
                                             || (n.Contains("dispel", StringComparison.OrdinalIgnoreCase) && n.Contains("rune", StringComparison.OrdinalIgnoreCase)));

    private WorldObject? FindEmber()
        => _ctx.Inventory.FindByName(n => n.Contains("empowered", StringComparison.OrdinalIgnoreCase)
                                          && n.Contains("volcanic", StringComparison.OrdinalIgnoreCase)
                                          && n.Contains("ember", StringComparison.OrdinalIgnoreCase));

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (!G.AutoDispel && !G.SurgingStrength) return;
        if (nowMs - _lastRead < 1000) return;
        _lastRead = nowMs;
        if (!_ctx.Host.HasReadPlayerEnchantments) return;

        int n = _ctx.Host.ReadPlayerEnchantments(_ids, _exp, MaxEnch);
        if (n < 0) return;
        n = Math.Min(n, MaxEnch);

        var harmful = new List<string>();
        bool surge = false;
        for (int i = 0; i < n; i++)
        {
            int id = unchecked((int)_ids[i]);
            string name = SpellDatabase.GetSpellName(id);
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Contains("Surging Strength", StringComparison.OrdinalIgnoreCase)) surge = true;
            if (G.AutoDispel && IsHarmful(id, name)) harmful.Add(name);
        }
        _harmfulNow = harmful.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _surgeActive = surge;

        if (G.AutoDispel) TickDispel(nowMs, harmful.Count > 0);
        if (G.SurgingStrength) TickSurge(nowMs, surge);
    }

    private void TickDispel(long now, bool anyHarmful)
    {
        // Verification / retry window after a rune use.
        if (_verifyAt > 0)
        {
            if (now < _verifyAt) return;
            if (!anyHarmful || _retries >= 3)
            {
                _dispelStatus = anyHarmful ? "debuffs remain after 3 retries" : "dispelled";
                _verifyAt = 0;
                _dispelCooldownUntil = now + Math.Max(2, G.DispelCooldownSeconds) * 1000L;
                return;
            }
            _retries++;
            if (UseRune(now)) _verifyAt = now + 2500;
            return;
        }

        if (!anyHarmful) { _dispelStatus = "no harmful spells"; return; }
        if (now < _dispelCooldownUntil) { _dispelStatus = "cooling down"; return; }
        _retries = 0;
        if (UseRune(now)) _verifyAt = now + 2000;
    }

    private bool UseRune(long now)
    {
        if (_ctx.Inventory.IsBusy) return false;
        var rune = FindRune();
        if (rune == null) { _dispelStatus = "no Rune of Dispel carried"; _dispelCooldownUntil = now + 30_000; _verifyAt = 0; return false; }
        if (!_ctx.Inventory.Use(rune)) return false;
        _dispelStatus = $"used {rune.Name} on {_harmfulNow.Length} debuff(s)";
        return true;
    }

    private void TickSurge(long now, bool active)
    {
        if (active) { _surgeStatus = "Surging Strength active"; return; }
        if (now < _surgeCooldownUntil || _ctx.Inventory.IsBusy) return;
        var ember = FindEmber();
        if (ember == null) { _surgeStatus = "no Empowered Volcanic Ember carried"; _surgeCooldownUntil = now + 30_000; return; }
        if (_ctx.Inventory.Use(ember)) _surgeStatus = "used " + ember.Name;
        _surgeCooldownUntil = now + Math.Max(5, G.SurgingCooldownSeconds) * 1000L;
    }

    public bool OnChat(string text)
    {
        if (!G.SurgingStrength) return false;
        if (text.Contains("surging strength", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("you cast", StringComparison.OrdinalIgnoreCase) && text.Contains("on yourself", StringComparison.OrdinalIgnoreCase))
                _surgeActive = true;
            else if (text.Contains("has expired", StringComparison.OrdinalIgnoreCase))
            {
                _surgeActive = false;
                _surgeCooldownUntil = 0; // re-apply on the next read
            }
        }
        return false;
    }

    public void OnLogout()
    {
        _verifyAt = 0;
        _harmfulNow = Array.Empty<string>();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Auto-dispel (Rune of Dispel)");
        bool ad = G.AutoDispel;
        if (ImGui.Checkbox("Use a Rune of Dispel on harmful debuffs", ref ad)) G.AutoDispel = ad;
        int cd = G.DispelCooldownSeconds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Cooldown (s)##disp", ref cd)) G.DispelCooldownSeconds = Math.Max(2, cd);
        ImGui.TextDisabled("Status: " + _dispelStatus);
        var harm = _harmfulNow;
        if (harm.Length > 0) ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Harmful now: " + string.Join(", ", harm));
        if (ImGui.TreeNode("Always / never dispel lists"))
        {
            EditList("Always dispel (name contains)", G.DispelInclusions, ref _newInclude, "inc");
            EditList("Never dispel (name contains)", G.DispelExclusions, ref _newExclude, "exc");
            ImGui.TreePop();
        }

        ImGui.Separator();
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Surging Strength");
        bool ss = G.SurgingStrength;
        if (ImGui.Checkbox("Keep Surging Strength up (Empowered Volcanic Ember)", ref ss)) G.SurgingStrength = ss;
        ImGui.TextDisabled("Status: " + (_surgeActive ? "active" : _surgeStatus));
    }

    private void EditList(string label, List<string> list, ref string buffer, string id)
    {
        ImGui.TextUnformatted(label);
        var snap = list.ToArray();
        for (int i = 0; i < snap.Length; i++)
        {
            ImGui.BulletText(snap[i]);
            ImGui.SameLine();
            string victim = snap[i];
            if (ImGui.SmallButton($"x##{id}{i}")) _ctx.Post(() => list.Remove(victim));
        }
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint($"##new{id}", "spell name text", ref buffer, 64u);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Add##{id}") && buffer.Trim().Length > 0)
        {
            string add = buffer.Trim();
            _ctx.Post(() => { if (!list.Contains(add, StringComparer.OrdinalIgnoreCase)) list.Add(add); });
            buffer = string.Empty;
        }
    }
}
