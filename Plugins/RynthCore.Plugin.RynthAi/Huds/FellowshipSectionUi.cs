// FellowshipSectionUi.cs - RynthCore's Fellowship section (RynthPluginRenderEmbed
// "section:fellowship"): the fellowship you are in, from FellowshipTracker. Render thread only.
//
// Read only: name, leader, open / locked / XP sharing, and the members (you and the leader
// marked). The tracker reads AC's fellowship object, which AC's main thread (the render thread
// here) owns, and caches the member list for two seconds; this view re-reads it once a second.
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Huds;

internal sealed class FellowshipSectionUi
{
    private const long RefreshMs = 1000;

    private static readonly Vector4 ColLeader = new(0.95f, 0.80f, 0.35f, 1f);
    private static readonly Vector4 ColMute = new(0.80f, 0.84f, 0.90f, 1f);

    private readonly Func<FellowshipTracker?> _tracker;
    private readonly Func<uint> _playerId;

    // The last read (render thread only).
    private long _readAt = long.MinValue / 2;
    private bool _inFellowship;
    private string _name = string.Empty;
    private int _leaderId;
    private bool _open, _locked, _shareXp;
    private readonly List<(int Id, string Name)> _members = new();

    public FellowshipSectionUi(Func<FellowshipTracker?> tracker, Func<uint> playerId)
    {
        _tracker = tracker;
        _playerId = playerId;
    }

    /// <summary>Draws the section's body into the current window. Render thread.</summary>
    public void Render()
    {
        FellowshipTracker? t = _tracker();
        if (t == null)
        {
            ImGui.TextDisabled("Fellowship tracking starts at login.");
            return;
        }
        Refresh(t);
        if (!_inFellowship)
        {
            ImGui.TextDisabled("Not in a fellowship.");
            return;
        }

        SafeImGui.TextColored(ColLeader, _name.Length > 0 ? _name : "(unnamed fellowship)");
        SafeImGui.TextDisabled($"{_members.Count} member(s)   {(_open ? "Open" : "Closed")}   {(_locked ? "Locked" : "Unlocked")}   XP {(_shareXp ? "shared" : "not shared")}");
        ImGui.Separator();

        uint me = SafePlayerId();
        if (ImGui.BeginTable("##fellows", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, MathF.Max(60f, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupColumn("Member", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach ((int id, string name) in _members)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool leader = id == _leaderId;
                if (leader) SafeImGui.TextColored(ColLeader, name);
                else SafeImGui.Text(name);
                ImGui.TableNextColumn();
                string role = (leader ? "Leader" : string.Empty) + ((uint)id == me && me != 0 ? (leader ? ", you" : "You") : string.Empty);
                SafeImGui.TextColored(ColMute, role);
            }
            ImGui.EndTable();
        }
    }

    private void Refresh(FellowshipTracker t)
    {
        long now = Environment.TickCount64;
        if (now - _readAt < RefreshMs) return;
        _readAt = now;
        _members.Clear();
        _inFellowship = t.IsInFellowship;
        if (!_inFellowship) return;
        _name = t.FellowshipName ?? string.Empty;
        _leaderId = t.LeaderId;
        _open = t.IsOpen;
        _locked = t.IsLocked;
        _shareXp = t.ShareXP;
        int count = Math.Clamp(t.MemberCount, 0, 64);
        for (int i = 0; i < count; i++)
        {
            int id = t.GetMemberId(i);
            if (id == 0) continue;
            string name = t.GetMemberName(i);
            _members.Add((id, name.Length > 0 ? name : $"0x{id:X8}"));
        }
    }

    private uint SafePlayerId()
    {
        try { return _playerId(); }
        catch { return 0; }
    }
}
