// IltPetLogs.cs — The ACECustom /pets and /shinies logs, shown in RynthAi's universal Pets tab
// (RynthILT.Hub section "pets-logs").
// Originally the roster-capture part of SilentKelpie's IltPets (ILT Hub, RynthAi); split out and
// moved to RynthILT 2026-10-13. The Pets tab itself (essence roster, combat / healing / cosmetic
// pets, the refill charm path) stayed in RynthAi (MiniRemote/PetsTab.cs): summoning is every
// server's, these two commands are ACECustom's.
using System;
using System.Collections.Generic;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltPetLogs : IIltFeature
{
    private readonly IltHubContext _ctx;

    public IltPetLogs(IltHubContext ctx) => _ctx = ctx;

    private IltPetState S => _ctx.State.Pet;

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

    public void Tick(long nowMs) { }
    public bool OnChat(string text) => false;
    public void OnLogout() { }

    // ── UI (render thread): the Pets tab's last section ─────────────────────

    public void RenderSection()
    {
        ImGui.Separator();
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
            foreach (string s in snap) SafeImGui.BulletText(s);
            ImGui.TreePop();
        }
    }
}
