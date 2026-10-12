// IltHubUi.cs — The ILT sections' ImGui bodies (render thread only).
//
// The ILT sections are tabs of the Mini Remote (MiniRemoteHud draws the tab bar and calls
// RenderTab): Character (world status, Force-ILT, profiles, session rates), Quests (IltQuests),
// Pets, Banking, Gear, Charms and Games. A tab is offered only while the server reports its feature on.
// The Guardian (IltGuardian: Temple riddle translator, hand-in, attribute turn-in tracker) is a
// RynthCore ILT panel instead (the bar's ILT button, next to Bounties), drawn through
// RenderGuardianPanel. Render() keeps the floating extras: the Games HUD, the quest favorites HUD
// and the confirmation modal. Augmentations / Enlightenment / XP planning live on the engine
// Skills panel. All AC actions go through IltHubContext.Post / Confirm so nothing here touches
// the game directly.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltHubUi
{
    private const string ConfirmPopupId = "Confirm##iltconfirm";

    private readonly IltHubController _hub;
    private readonly IltHubContext _ctx;
    private IltConfirmRequest? _confirm;
    private bool _confirmOpened;
    private string _profileName = string.Empty;
    private bool _profileShared;
    private string[] _profileList = Array.Empty<string>();
    private int _profileIdx;
    // The last tab body drawn (for one shown log event per tab change); -1 = none yet.
    private int _lastTabLogged = -1;

    public IltHubUi(IltHubController hub, IltHubContext ctx)
    {
        _hub = hub;
        _ctx = ctx;
    }

    /// <summary>The floating extras: Games HUD, quest favorites HUD and the confirmation modal.</summary>
    public void Render()
    {
        _hub.Games.RenderHud();
        _hub.Quests.RenderFloatingWindows();
        RenderConfirm();
    }

    /// <summary>
    /// Size of a Mini Remote tab's body (the old section window's default size): the tab draws in a
    /// child this big, and the Mini Remote (or the engine window hosting it) fits to it.
    /// </summary>
    public static Vector2 TabSize(IltSection section) => section switch
    {
        IltSection.Character => new Vector2(520, 440),
        IltSection.Quests => new Vector2(700, 500),
        IltSection.Pet => new Vector2(560, 580),
        IltSection.Banking => new Vector2(560, 520),
        IltSection.Gear => new Vector2(560, 520),
        IltSection.Charms => new Vector2(560, 520),
        IltSection.Games => new Vector2(520, 440),
        _ => new Vector2(520, 440),
    };

    /// <summary>Draws one Mini Remote ILT tab's body into the current (child) window.</summary>
    public void RenderTab(IltSection section)
    {
        if ((int)section != _lastTabLogged)
        {
            _lastTabLogged = (int)section;
            RynthLog.Event(LogEvents.IltWindowShown, $"{IltSections.Label(section)} tab shown (world='{_ctx.Options.WorldName}', available={_hub.Available})");
        }
        switch (section)
        {
            case IltSection.Character: RenderCharacterBody(); break;
            case IltSection.Quests: _hub.Quests.RenderTab(); break;
            case IltSection.Pet: _hub.Pets.Render(); break;
            case IltSection.Banking: _hub.Banking.Render(); break;
            case IltSection.Gear: _hub.Gear.Render(); break;
            case IltSection.Charms: _hub.CharmsTab.Render(); break;
            case IltSection.Games: _hub.Games.Render(); break;
        }
    }

    /// <summary>
    /// The Guardian panel's body (RynthCore's ILT Guardian panel, RynthPluginRenderEmbed
    /// "guardian"), or why it's idle while the server hasn't reported ILT features.
    /// </summary>
    public void RenderGuardianPanel()
    {
        if (!_hub.SectionAvailable(IltSection.Guardian))
        {
            RenderHeader();
            ImGui.Separator();
            RenderUnavailable();
            return;
        }
        _hub.Guardian.Render();
    }

    /// <summary>Character tab: world / options header, then session rates (or why the ILT tabs are idle).</summary>
    private void RenderCharacterBody()
    {
        RenderHeader();
        ImGui.Separator();
        if (!_hub.Available) { RenderUnavailable(); return; }
        if (ImGui.CollapsingHeader("Session rates", ImGuiTreeNodeFlags.DefaultOpen)) _hub.Rates.Render();
        ImGui.Spacing();
        ImGui.TextDisabled("XP planner, Augmentations and Enlightenment are on the Skills panel's Progression tab");
        ImGui.TextDisabled("(right-click the dashboard's Char button > Progression).");
    }

    private void RenderHeader()
    {
        var o = _ctx.Options;
        string world = o.WorldName.Length > 0 ? o.WorldName : "unknown world";
        ImGui.TextColored(o.IsIltLikeWorld ? LegacyDashboardRenderer.ColTeal : LegacyDashboardRenderer.ColTextMute, world);
        ImGui.SameLine();
        ImGui.TextDisabled(o.IsRefreshing ? "checking server options..."
            : o.LastRefreshUtc == DateTime.MinValue ? "server options not checked yet"
            : $"options via {o.Source} @ {o.LastRefreshUtc.ToLocalTime():t}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##opts")) _ctx.Post(() => o.Refresh(manual: true));

        bool force = _ctx.State.ForceLeaftideFeatures;
        if (ImGui.Checkbox("Treat this world as ILT", ref force)) _ctx.State.ForceLeaftideFeatures = force;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("World-identity override only: the Mini Remote asks the server which ILT features exist.\nIt never turns a feature on by itself.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Profiles...")) { ImGui.OpenPopup("##iltprofiles"); _ctx.Post(() => _profileList = _ctx.Store.ListProfiles().ToArray()); }
        RenderProfilesPopup();
    }

    private void RenderProfilesPopup()
    {
        if (!ImGui.BeginPopup("##iltprofiles")) return;
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "ILT profiles (settings only; no balances)");
        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##profname", "profile name", ref _profileName, 64u);
        ImGui.SameLine();
        ImGui.Checkbox("shared", ref _profileShared);
        ImGui.SameLine();
        if (ImGui.SmallButton("Save"))
        {
            string n = _profileName; bool sh = _profileShared;
            _ctx.Post(() =>
            {
                _ctx.Chat(_ctx.Store.SaveProfile(_ctx.State, n, sh, out string err) ? $"[Mini Remote] Saved profile '{n}'." : "[Mini Remote] Save failed: " + err);
                _profileList = _ctx.Store.ListProfiles().ToArray();
            });
        }
        var list = _profileList;
        if (list.Length > 0)
        {
            _profileIdx = Math.Clamp(_profileIdx, 0, list.Length - 1);
            ImGui.SetNextItemWidth(180);
            ImGui.Combo("##proflist", ref _profileIdx, list, list.Length);
            ImGui.SameLine();
            if (ImGui.SmallButton("Load"))
            {
                string n = list[_profileIdx];
                _ctx.Confirm("Load ILT profile", $"Replace the current ILT settings with profile '{n}'?",
                    () => _ctx.Chat(_ctx.Store.LoadProfile(_ctx.State, n, out string err) ? $"[Mini Remote] Loaded profile '{n}'." : "[Mini Remote] Load failed: " + err),
                    "Load");
            }
        }
        else ImGui.TextDisabled("No saved profiles.");
        ImGui.EndPopup();
    }

    private void RenderUnavailable()
    {
        var o = _ctx.Options;
        if (!o.IsIltLikeWorld)
        {
            ImGui.TextWrapped("This is not an Infinite Leaftide / ACECustom world, so the ILT tabs are idle. "
                              + "If this shard runs ACECustom under another name, tick 'Treat this world as ILT' and press Refresh.");
        }
        else if (o.IsRefreshing || o.LastRefreshUtc == DateTime.MinValue)
        {
            ImGui.TextWrapped("Checking which ILT features this server has enabled...");
        }
        else
        {
            ImGui.TextWrapped("The server did not report any ILT features as enabled. Press Refresh to check again.");
        }
    }

    // ── Confirmation modal ──────────────────────────────────────────────────

    private void RenderConfirm()
    {
        if (_confirm == null)
        {
            if (!_ctx.TryTakeConfirm(out var next)) return;
            _confirm = next;
            _confirmOpened = false;
        }
        if (!_confirmOpened)
        {
            ImGui.OpenPopup(ConfirmPopupId);
            _confirmOpened = true;
        }

        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
        bool keepOpen = true;
        if (ImGui.BeginPopupModal(ConfirmPopupId, ref keepOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColAmber, _confirm.Title);
            ImGui.Separator();
            ImGui.TextWrapped(_confirm.Message);
            ImGui.Spacing();
            if (ImGui.Button(_confirm.YesLabel + "##iltyes", new Vector2(120, 0)))
            {
                var act = _confirm.OnYes;
                if (act != null) _ctx.Post(act);
                _confirm = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##iltno", new Vector2(120, 0)))
            {
                _confirm = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        else
        {
            // Closed with the title-bar X (or never opened): treat as cancel.
            _confirm = null;
        }
        if (!keepOpen) _confirm = null;
    }
}
