// IltSplitArrows.cs — Split-arrow imbuer (Gear tab, P2).
//
// ILT missile weapons carry a split-arrow count (int 9031). Each White Quartz salvage
// application adds one split and uses one tink. Per weapon:
//     needed = min(target - current, 10 - tinks, 10 - current)
// The imbuer applies full (100-unit) White Quartz bags, lowest workmanship first, with
// UseObjectOn(salvage, weapon). AC then shows its own craft confirmation — the player
// clicks it; the imbuer waits (20 s) for the "successfully applies" / "fails to apply"
// line before the next step. Nothing is ever applied without the Hub confirm first.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltSplitArrows : IIltFeature
{
    private const int MaterialWhiteQuartz = 46;
    private const uint IntNumItemsInMaterial = 170;
    private const uint IntItemWorkmanship = 105;
    private const long StepTimeoutMs = 20_000;

    private static readonly Regex Applied = new(
        @"^(?<character>[\w\s]+) successfully applies the (?<salvage>[\w\s\-]+?)(\sSalvage.*)(\s?\(100\))?\s\(workmanship (?<workmanship>\d+\.\d+)\) to the (?<item>[\w\s\'\-]+)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Failed = new(
        @"^(?<character>[\w\s]+) fails to apply the (?<salvage>[\w\s\-]+?)(\sSalvage.*)(\s?\(100\))?\s\(workmanship (?<workmanship>\d+\.\d+)\) to the (?<item>[\w\s\'\-]+)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IltHubContext _ctx;

    // Job state (pump thread)
    private int _weaponId;
    private int _remaining;
    private bool _waiting;
    private long _stepStartedAt;
    private volatile string _status = "idle";

    // Render snapshot of eligible weapons
    private volatile WeaponRow[] _weapons = Array.Empty<WeaponRow>();
    private long _lastScan;
    private sealed record WeaponRow(int Id, string Name, int Splits, int Tinks, bool Imbued, int Needed);

    public IltSplitArrows(IltHubContext ctx) => _ctx = ctx;

    private IltGearState G => _ctx.State.Gear;
    public bool Running => _weaponId != 0;

    private int Needed(WorldObject w)
    {
        var inv = _ctx.Inventory;
        int cur = inv.Int(w, IltInventory.IntSplitArrowCount);
        int tinks = inv.Int(w, IltInventory.IntNumTimesTinkered);
        int target = Math.Clamp(G.SplitArrowTarget, 0, 10);
        return Math.Max(0, Math.Min(target - cur, Math.Min(10 - tinks, 10 - cur)));
    }

    /// <summary>Full White Quartz bags, lowest workmanship first.</summary>
    private List<WorldObject> QuartzBags()
    {
        var inv = _ctx.Inventory;
        return inv.Items()
            .Where(wo => wo.ObjectClass == AcObjectClass.Salvage
                         && (inv.Int(wo, IltInventory.IntMaterialType) == MaterialWhiteQuartz || wo.Name.Contains("White Quartz", StringComparison.OrdinalIgnoreCase))
                         && inv.Uses(wo) >= 100)
            .OrderBy(Workmanship)
            .ToList();
    }

    private double Workmanship(WorldObject bag)
    {
        int total = _ctx.Inventory.Int(bag, IntItemWorkmanship);
        int items = Math.Max(1, _ctx.Inventory.Int(bag, IntNumItemsInMaterial, 1));
        return total / (double)items;
    }

    /// <summary>Starts imbuing one weapon (pump thread, after the UI confirm).</summary>
    public void Start(int weaponId)
    {
        var w = _ctx.Inventory.Cache?[weaponId];
        if (w == null) { _status = "weapon not found"; return; }
        if (_ctx.Inventory.IsEquipped(w)) { _status = "unequip the weapon first"; return; }
        int need = Needed(w);
        if (need <= 0) { _status = $"{w.Name} needs no more splits"; return; }
        _weaponId = weaponId;
        _remaining = need;
        _waiting = false;
        _status = $"imbuing {w.Name}: {need} application(s)";
    }

    public void Stop(string why)
    {
        _weaponId = 0;
        _waiting = false;
        _status = why;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (nowMs - _lastScan >= 3000 && _ctx.WindowOpen)
        {
            _lastScan = nowMs;
            ScanWeapons();
        }
        if (_weaponId == 0) return;

        if (_waiting)
        {
            if (nowMs - _stepStartedAt > StepTimeoutMs) Stop("no result within 20 s (craft dialog not confirmed?) - stopped");
            return;
        }
        if (_remaining <= 0) { Stop("done"); return; }
        if (_ctx.Inventory.IsBusy) return;

        var bag = QuartzBags().FirstOrDefault();
        if (bag == null) { Stop("out of full White Quartz bags"); return; }
        if (_ctx.Inventory.UseOn(bag, unchecked((uint)_weaponId)))
        {
            _waiting = true;
            _stepStartedAt = nowMs;
            _status = $"applying {bag.Name} - confirm the craft dialog in AC ({_remaining} left)";
        }
    }

    public bool OnChat(string text)
    {
        if (!_waiting) return false;
        if (Applied.IsMatch(text))
        {
            _waiting = false;
            _remaining--;
            _status = $"applied ({_remaining} left)";
        }
        else if (Failed.IsMatch(text))
        {
            // A failed tink still consumes a tink slot — recompute from the weapon.
            _waiting = false;
            var w = _ctx.Inventory.Cache?[_weaponId];
            _remaining = w != null ? Math.Min(_remaining, Needed(w)) : 0;
            _status = $"application failed ({_remaining} left)";
        }
        return false;
    }

    public void OnLogout() => Stop("idle");

    private void ScanWeapons()
    {
        var inv = _ctx.Inventory;
        var rows = new List<WeaponRow>();
        foreach (var wo in inv.Items())
        {
            if (wo.ObjectClass != AcObjectClass.MissileWeapon) continue;
            bool imbued = inv.Int(wo, IltInventory.IntImbuedEffect) != 0;
            if (imbued && G.SplitIgnoreImbued) continue;
            rows.Add(new WeaponRow(wo.Id, wo.Name, inv.Int(wo, IltInventory.IntSplitArrowCount),
                inv.Int(wo, IltInventory.IntNumTimesTinkered), imbued, Needed(wo)));
        }
        _weapons = rows.OrderByDescending(r => r.Needed).ThenBy(r => r.Name).ToArray();
        _bagCount = QuartzBags().Count;
    }

    private volatile int _bagCount;

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Split arrows (White Quartz)");
        int tgt = G.SplitArrowTarget;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("Target splits", ref tgt, 0, 10)) G.SplitArrowTarget = tgt;
        ImGui.SameLine();
        bool skip = G.SplitIgnoreImbued;
        if (ImGui.Checkbox("Skip imbued weapons", ref skip)) G.SplitIgnoreImbued = skip;
        ImGui.TextDisabled($"Full White Quartz bags carried: {_bagCount}");

        foreach (var w in _weapons)
        {
            ImGui.PushID(w.Id);
            ImGui.TextUnformatted($"{w.Name}  splits {w.Splits}, tinks {w.Tinks}{(w.Imbued ? ", imbued" : "")}");
            ImGui.SameLine();
            ImGui.BeginDisabled(w.Needed <= 0 || Running);
            if (ImGui.SmallButton($"Imbue +{w.Needed}"))
            {
                var row = w;
                _ctx.Confirm("Split-arrow imbue",
                    $"Apply {row.Needed} White Quartz bag(s) to {row.Name}?\n\nEach application uses a tink and can fail.\n"
                    + "AC will ask you to confirm every craft.",
                    () => Start(row.Id), "Start");
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        if (_weapons.Length == 0) ImGui.TextDisabled("No missile weapons carried.");
        if (Running && ImGui.SmallButton("Stop imbuing")) _ctx.Post(() => Stop("stopped by player"));
        ImGui.TextDisabled("Status: " + _status);
    }
}
