// IltCharms.cs — The Mini Remote's "Charms" ILT tab: every ACECustom registry charm in one table.
//
// Per charm (IltCharmTracker): acquired ("carried" now, "stored" = carried before and remembered
// per character, or no), status ON / OFF from the charm's appraisal or the player's ability flag,
// best tier, and the server's switch when the client has learned it. A carried charm has a
// Toggle (using a charm flips it). The Guardian Hand isn't a registry charm (no published WCID),
// so it is matched by name or a configured WCID and listed under the table with its own Toggle.
// This was the engine Settings panel's "Charms Tracking" tab plus the Gear tab's charm strip.
// Scans run on the pump thread every 3 s while the Mini Remote is on screen; Render reads the
// published arrays only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltCharms : IIltFeature
{
    private const long ScanIntervalMs = 3000;

    private readonly IltHubContext _ctx;
    private readonly IltCharmTracker _tracker;

    /// <summary>A Guardian Hand carried now (name or WCID match).</summary>
    private sealed record HandRow(int Id, string Name, IltTri Status, bool ServerDisabled);

    private volatile IltCharmTracker.CharmState[] _rows = Array.Empty<IltCharmTracker.CharmState>();
    private volatile HandRow[] _hands = Array.Empty<HandRow>();
    private long _lastScan;

    public IltCharms(IltHubContext ctx, IltCharmTracker tracker)
    {
        _ctx = ctx;
        _tracker = tracker;
    }

    private IltGearState G => _ctx.State.Gear;

    private bool IsGuardianHand(WorldObject wo)
    {
        if (G.GuardianHandWcid != 0 && _ctx.Inventory.Wcid(wo) == G.GuardianHandWcid) return true;
        string want = string.IsNullOrWhiteSpace(G.GuardianHandName) ? "Guardian Hand" : G.GuardianHandName.Trim();
        return wo.Name.Contains(want, StringComparison.OrdinalIgnoreCase)
            || (wo.Name.Contains("guardian", StringComparison.OrdinalIgnoreCase) && wo.Name.Contains("hand", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Pump thread: the registry table and the carried Guardian Hands.</summary>
    private void Scan()
    {
        _rows = _tracker.Evaluate().ToArray();
        var inv = _ctx.Inventory;
        var hands = new List<HandRow>();
        foreach (var wo in inv.Items().Where(IsGuardianHand))
            hands.Add(new HandRow(wo.Id, wo.Name, inv.CharmStatus(wo), _ctx.Options.IsOff(IltFeature.Charm("Guardian Hand"))));
        _hands = hands.ToArray();
    }

    /// <summary>Uses (toggles) a charm (pump thread).</summary>
    private void Toggle(int id)
    {
        var wo = _ctx.Inventory.Cache?[id];
        if (wo == null) return;
        if (_ctx.Inventory.IsBusy) { _ctx.Chat("[Mini Remote] Busy - try the charm again in a moment."); return; }
        _ctx.Inventory.Use(wo);
        _lastScan = 0;   // re-read the status soon (the appraisal refresh is throttled per item)
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (!_ctx.WindowOpen || nowMs - _lastScan < ScanIntervalMs) return;
        _lastScan = nowMs;
        Scan();
    }

    public bool OnChat(string text) => false;

    public void OnLogout()
    {
        _rows = Array.Empty<IltCharmTracker.CharmState>();
        _hands = Array.Empty<HandRow>();
        _lastScan = 0;
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        var rows = _rows;
        if (rows.Length == 0)
        {
            ImGui.TextDisabled(_ctx.WindowOpen ? "Reading your charms..." : "Open the Mini Remote to read your charms.");
            RenderGuardianHand();
            return;
        }

        int acquired = rows.Count(r => r.Acquired != "no"), active = rows.Count(r => r.Active == IltTri.On);
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, $"{acquired} of {rows.Length} acquired, {active} active");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##charms")) _ctx.Post(() => { _lastScan = 0; });

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit
                                      | ImGuiTableFlags.ScrollY;
        float h = Math.Max(140f, ImGui.GetContentRegionAvail().Y - 110f);
        if (ImGui.BeginTable("##iltcharms", 6, flags, new Vector2(0, h)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Charm", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Acquired", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("Tier", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Server", ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableSetupColumn("##toggle", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableHeadersRow();
            for (int i = 0; i < rows.Length; i++) RenderRow(i, rows[i]);
            ImGui.EndTable();
        }

        RenderGuardianHand();
        ImGui.TextDisabled("Status comes from the charm's appraisal or your ability flag; a charm not carried is inactive.");
        ImGui.TextDisabled("Stored = carried by this character before (remembered per character).");
    }

    private void RenderRow(int i, IltCharmTracker.CharmState r)
    {
        ImGui.PushID(i);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        bool carried = r.Acquired == "carried";
        if (carried) ImGui.TextUnformatted(r.Def.Name);
        else ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, r.Def.Name);
        if (ImGui.IsItemHovered() && r.Def.Effect.Length > 0) ImGui.SetTooltip(r.Def.Effect);

        ImGui.TableNextColumn();
        if (carried) ImGui.TextColored(LegacyDashboardRenderer.ColGreen, r.Count > 1 ? $"Carried x{r.Count}" : "Carried");
        else if (r.Acquired == "seen")
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Stored");
            if (ImGui.IsItemHovered() && r.LastSeenUtc != default)
                ImGui.SetTooltip("Last carried " + r.LastSeenUtc.ToString("yyyy-MM-dd"));
        }
        else ImGui.TextDisabled("No");

        ImGui.TableNextColumn();
        if (!carried) ImGui.TextDisabled("-");
        else if (r.Active == IltTri.On) ImGui.TextColored(LegacyDashboardRenderer.ColGreen, "ON");
        else if (r.Active == IltTri.Off) ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "OFF");
        else
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "?");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Waiting for the charm's appraisal.");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(r.Tier <= 0 ? "-" : r.MaxTier > 0 ? $"{r.Tier}/{r.MaxTier}" : r.Tier.ToString());

        ImGui.TableNextColumn();
        if (r.Server == IltTri.Off)
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColHp, "Disabled");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The server refused or reported this charm as disabled.");
        }
        else if (r.Server == IltTri.On) ImGui.TextColored(LegacyDashboardRenderer.ColGreen, "Enabled");
        else
        {
            ImGui.TextDisabled("-");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Not reported. The server only tells players when a charm is refused.");
        }

        ImGui.TableNextColumn();
        if (carried && r.ToggleId != 0 && r.Server != IltTri.Off)
        {
            int id = r.ToggleId;
            if (ImGui.SmallButton("Toggle")) _ctx.Post(() => Toggle(id));
        }
        ImGui.PopID();
    }

    /// <summary>The Guardian Hand (not a registry charm): each carried one with a Toggle, and how it is matched.</summary>
    private void RenderGuardianHand()
    {
        var hands = _hands;
        foreach (var h in hands)
        {
            ImGui.PushID(h.Id);
            if (h.ServerDisabled) ImGui.TextDisabled($"{h.Name} - disabled on this server");
            else
            {
                var col = h.Status == IltTri.On ? LegacyDashboardRenderer.ColGreen
                        : h.Status == IltTri.Off ? LegacyDashboardRenderer.ColTextMute : LegacyDashboardRenderer.ColAmber;
                ImGui.TextColored(col, h.Status == IltTri.On ? "ON " : h.Status == IltTri.Off ? "OFF" : " ? ");
                ImGui.SameLine();
                ImGui.TextUnformatted(h.Name);
                ImGui.SameLine();
                int id = h.Id;
                if (ImGui.SmallButton("Toggle")) _ctx.Post(() => Toggle(id));
            }
            ImGui.PopID();
        }

        if (ImGui.TreeNode("Guardian Hand matching"))
        {
            string name = G.GuardianHandName;
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputText("Name contains", ref name, 64u)) G.GuardianHandName = name;
            int wcid = unchecked((int)G.GuardianHandWcid);
            ImGui.SetNextItemWidth(140);
            if (ImGui.InputInt("WCID (0 = name only)", ref wcid, 0)) G.GuardianHandWcid = unchecked((uint)Math.Max(0, wcid));
            ImGui.TreePop();
        }
    }
}
