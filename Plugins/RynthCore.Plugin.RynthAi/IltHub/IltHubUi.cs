// IltHubUi.cs — The ILT Hub's ImGui windows (render thread only).
//
// The Mini Remote is the Hub. Each former tab is its own window, opened from the Mini Remote's
// Options or "/ra hub open <section>": ILT Character (world status, Force-ILT, profiles, session
// rates), Quests (IltQuests), Pets, Banking, Gear, Games, Guardian (IltGuardian: Temple riddle
// translator, hand-in, attribute turn-in tracker) and Bounties (IltBounties: every ACECustom
// bounty and this character's progress). A section window draws only while the
// server reports its feature on. Augmentations / Enlightenment / XP planning live on the engine
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
    private static readonly int SectionCount = IltSections.All.Length;

    private readonly IltHubController _hub;
    private readonly IltHubContext _ctx;
    private IltConfirmRequest? _confirm;
    private bool _confirmOpened;
    private string _profileName = string.Empty;
    private bool _profileShared;
    private string[] _profileList = Array.Empty<string>();
    private int _profileIdx;

    // Per section (indexed by IltSection): drawn last frame (for shown/hidden log events) and
    // whether the off-screen check already ran for the current show.
    private readonly bool[] _wasDrawn = new bool[SectionCount];
    private readonly bool[] _rescueChecked = new bool[SectionCount];

    // Cached so the per-frame window calls don't allocate a delegate each time.
    private readonly Action _characterBody, _petsBody, _bankingBody, _gearBody, _gamesBody, _guardianBody, _bountiesBody;

    public IltHubUi(IltHubController hub, IltHubContext ctx)
    {
        _hub = hub;
        _ctx = ctx;
        _characterBody = RenderCharacterBody;
        _petsBody = () => _hub.Pets.Render();
        _bankingBody = () => _hub.Banking.Render();
        _gearBody = () => _hub.Gear.Render();
        _gamesBody = () => _hub.Games.Render();
        _guardianBody = () => _hub.Guardian.Render();
        _bountiesBody = () => _hub.Bounties.Render();
    }

    public void Render()
    {
        // Distinct first-use offsets so sections opened together don't stack exactly.
        RenderSectionWindow(IltSection.Character, "ILT Character##iltcharwin", new Vector2(520, 480), Vector2.Zero, _characterBody);
        RenderSectionWindow(IltSection.Pet, "Pets##iltpetswin", new Vector2(560, 620), Vector2.Zero, _petsBody);
        RenderSectionWindow(IltSection.Banking, "Banking##iltbankwin", new Vector2(560, 560), new Vector2(80, 80), _bankingBody);
        RenderSectionWindow(IltSection.Gear, "Gear##iltgearwin", new Vector2(560, 560), new Vector2(120, 120), _gearBody);
        RenderSectionWindow(IltSection.Games, "Games##iltgameswin", new Vector2(520, 480), new Vector2(160, 160), _gamesBody);
        RenderSectionWindow(IltSection.Guardian, "Guardian##iltguardianwin", new Vector2(540, 520), new Vector2(200, 120), _guardianBody);
        RenderSectionWindow(IltSection.Bounties, "Bounties##iltbountywin", new Vector2(560, 420), new Vector2(240, 160), _bountiesBody);
        LogSectionTransition(IltSection.Quests, _ctx.State.Character.QuestTrackerPoppedOut && _hub.SectionAvailable(IltSection.Quests));
        _hub.Games.RenderHud();
        _hub.Quests.RenderFloatingWindows();
        RenderConfirm();
    }

    /// <summary>
    /// One section window: drawn while its open flag is set and the section is available, centred
    /// on first use and moved back on screen once per show if a saved spot left it off-screen.
    /// Closing it with the title-bar X clears the open flag.
    /// </summary>
    private void RenderSectionWindow(IltSection section, string title, Vector2 size, Vector2 offset, Action body)
    {
        var cs = _ctx.State.Character;
        int i = (int)section;
        bool drawn = IltSections.IsOpen(cs, section) && _hub.SectionAvailable(section);
        LogSectionTransition(section, drawn);
        if (!drawn) { _rescueChecked[i] = false; return; }

        UiPlacement.CenterFirstUse(offset);
        ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin(title, ref open))
        {
            if (UiPlacement.RescueOncePerShow(ref _rescueChecked[i]))
            {
                Vector2 moved = ImGui.GetWindowPos();
                RynthLog.Write(LogCat.IltHub, $"[IltHub] {IltSections.Label(section)} window was off-screen - moved to ({moved.X:0},{moved.Y:0}).");
            }
            body();
        }
        ImGui.End();
        if (!open) IltSections.SetOpen(cs, section, false);
    }

    /// <summary>Logs the first frame a section window is (or stops being) drawn, proving the overlay renders it.</summary>
    private void LogSectionTransition(IltSection section, bool drawn)
    {
        int i = (int)section;
        if (drawn == _wasDrawn[i]) return;
        _wasDrawn[i] = drawn;
        string label = IltSections.Label(section);
        if (drawn)
            RynthLog.Event(LogEvents.IltWindowShown, $"{label} window shown (world='{_ctx.Options.WorldName}', available={_hub.Available})");
        else
            RynthLog.Event(LogEvents.IltWindowHidden, $"{label} window hidden");
    }

    /// <summary>ILT Character window: world / options header, then session rates (or why the Hub is idle).</summary>
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
            ImGui.SetTooltip("World-identity override only: the Hub asks the server which features exist.\nIt never turns a feature on by itself.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Profiles...")) { ImGui.OpenPopup("##iltprofiles"); _ctx.Post(() => _profileList = _ctx.Store.ListProfiles().ToArray()); }
        RenderProfilesPopup();
    }

    private void RenderProfilesPopup()
    {
        if (!ImGui.BeginPopup("##iltprofiles")) return;
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Hub profiles (settings only; no balances)");
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
                _ctx.Chat(_ctx.Store.SaveProfile(_ctx.State, n, sh, out string err) ? $"[ILT Hub] Saved profile '{n}'." : "[ILT Hub] Save failed: " + err);
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
                _ctx.Confirm("Load Hub profile", $"Replace the current Hub settings with profile '{n}'?",
                    () => _ctx.Chat(_ctx.Store.LoadProfile(_ctx.State, n, out string err) ? $"[ILT Hub] Loaded profile '{n}'." : "[ILT Hub] Load failed: " + err),
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
            ImGui.TextWrapped("This is not an Infinite Leaftide / ACECustom world, so the ILT Hub is idle. "
                              + "If this shard runs ACECustom under another name, tick 'Treat this world as ILT' and press Refresh.");
        }
        else if (o.IsRefreshing || o.LastRefreshUtc == DateTime.MinValue)
        {
            ImGui.TextWrapped("Checking which ILT features this server has enabled...");
        }
        else
        {
            ImGui.TextWrapped("The server did not report any ILT Hub features as enabled. Press Refresh to check again.");
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
