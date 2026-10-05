// IltGear.cs — ILT Hub "Gear" tab: charm strip (incl. Guardian Hand) plus the gear tools.
//
// Charm strip: every carried ACECustom charm with its ON/OFF state (from the appraisal
// Use text) and a toggle (using a charm flips it). Charms the server reports disabled are
// shown greyed out. Guardian Hand has no published WCID yet, so it is matched by name
// ("guardian" + "hand") unless a WCID is configured in the Hub.
// The remaining sections are separate modules (buff keepers, chunk/clap, split arrows,
// equipment suits, VTank import) that this tab just lays out.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltGear : IIltFeature
{
    private readonly IltHubContext _ctx;
    public IltBuffKeepers Keepers { get; }
    public IltChunkClap ChunkClap { get; }
    public IltSplitArrows Split { get; }
    public IltEquipSuits Suits { get; }
    public IltUsdImporter Usd { get; }

    private sealed record CharmRow(int Id, string Name, IltTri Status, bool ServerDisabled, bool IsGuardianHand);
    private volatile CharmRow[] _charms = Array.Empty<CharmRow>();
    private long _lastCharmScan;

    public IltGear(IltHubContext ctx)
    {
        _ctx = ctx;
        Keepers = new IltBuffKeepers(ctx);
        ChunkClap = new IltChunkClap(ctx);
        Split = new IltSplitArrows(ctx);
        Suits = new IltEquipSuits(ctx);
        Usd = new IltUsdImporter(ctx);
    }

    private IltGearState G => _ctx.State.Gear;

    private bool IsGuardianHand(WorldObject wo)
    {
        if (G.GuardianHandWcid != 0 && _ctx.Inventory.Wcid(wo) == G.GuardianHandWcid) return true;
        string want = string.IsNullOrWhiteSpace(G.GuardianHandName) ? "Guardian Hand" : G.GuardianHandName.Trim();
        return wo.Name.Contains(want, StringComparison.OrdinalIgnoreCase)
            || (wo.Name.Contains("guardian", StringComparison.OrdinalIgnoreCase) && wo.Name.Contains("hand", StringComparison.OrdinalIgnoreCase));
    }

    private void ScanCharms()
    {
        var inv = _ctx.Inventory;
        var rows = new List<CharmRow>();
        var seen = new HashSet<int>();
        foreach (var wo in inv.CarriedCharms().Concat(inv.Items().Where(IsGuardianHand)))
        {
            if (!seen.Add(wo.Id)) continue;
            bool gh = IsGuardianHand(wo);
            string defName = gh ? "Guardian Hand" : inv.CharmDefFor(wo)?.Name ?? wo.Name;
            rows.Add(new CharmRow(wo.Id, wo.Name, inv.CharmStatus(wo), _ctx.Options.IsOff(IltFeature.Charm(defName)), gh));
        }
        _charms = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Uses (toggles) a charm (pump thread).</summary>
    private void ToggleCharm(int id)
    {
        var wo = _ctx.Inventory.Cache?[id];
        if (wo == null) return;
        if (_ctx.Inventory.IsBusy) { _ctx.Chat("[ILT Hub] Busy - try the charm again in a moment."); return; }
        _ctx.Inventory.Use(wo);
        _lastCharmScan = 0; // re-read status soon (appraisal refresh is throttled per item)
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (_ctx.WindowOpen && nowMs - _lastCharmScan >= 3000)
        {
            _lastCharmScan = nowMs;
            ScanCharms();
        }
        Keepers.Tick(nowMs);
        ChunkClap.Tick(nowMs);
        Split.Tick(nowMs);
        Suits.Tick(nowMs);
    }

    public bool OnChat(string text)
    {
        bool eat = Keepers.OnChat(text);
        eat |= ChunkClap.OnChat(text);
        eat |= Split.OnChat(text);
        eat |= Suits.OnChat(text);
        return eat;
    }

    public void OnLogout()
    {
        Keepers.OnLogout();
        ChunkClap.OnLogout();
        Split.OnLogout();
        Suits.OnLogout();
        _charms = Array.Empty<CharmRow>();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        if (ImGui.CollapsingHeader("Charms", ImGuiTreeNodeFlags.DefaultOpen)) RenderCharms();
        if (ImGui.CollapsingHeader("Dispel / Surging Strength")) Keepers.Render();
        if (ImGui.CollapsingHeader("Chunking / Clap")) ChunkClap.Render();
        if (ImGui.CollapsingHeader("Split arrows")) Split.Render();
        if (ImGui.CollapsingHeader("Equipment suits")) Suits.Render();
        if (ImGui.CollapsingHeader("VirindiTank import")) Usd.Render();
    }

    private void RenderCharms()
    {
        var rows = _charms;
        if (rows.Length == 0) ImGui.TextDisabled("No charms carried.");
        foreach (var r in rows)
        {
            ImGui.PushID(r.Id);
            if (r.ServerDisabled)
            {
                ImGui.TextDisabled($"{r.Name} - disabled on this server");
                ImGui.PopID();
                continue;
            }
            var col = r.Status == IltTri.On ? LegacyDashboardRenderer.ColGreen
                    : r.Status == IltTri.Off ? LegacyDashboardRenderer.ColTextMute : LegacyDashboardRenderer.ColAmber;
            ImGui.TextColored(col, r.Status == IltTri.On ? "ON " : r.Status == IltTri.Off ? "OFF" : " ? ");
            ImGui.SameLine();
            ImGui.TextUnformatted(r.Name);
            ImGui.SameLine();
            int id = r.Id;
            if (ImGui.SmallButton("Toggle")) _ctx.Post(() => ToggleCharm(id));
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
