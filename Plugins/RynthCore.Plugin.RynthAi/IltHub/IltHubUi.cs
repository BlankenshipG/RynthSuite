// IltHubUi.cs — The ILT Hub ImGui window (render thread only).
//
// Tabs: Character · Quests · Pet · Banking · Gear · Games. A tab is hidden when every feature
// in it is reported off by the server. Quests and Pets can also live in their own windows;
// Augmentations / Enlightenment / XP planning moved to the engine Skills panel. A header strip shows the world / server-options
// status with Refresh, the Force-ILT override and Hub profiles. All AC actions go
// through IltHubContext.Post / Confirm so nothing here touches the game directly.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltHubUi
{
    // Indices are persisted (IltHubState.SelectedTab): append new tabs, reorder via TabOrder.
    private static readonly string[] TabNames = { "Character", "Pet", "Banking", "Gear", "Games", "Quests" };
    private static readonly int[] TabOrder = { 0, 5, 1, 2, 3, 4 };
    private const int TabCharacter = 0, TabPet = 1, TabBanking = 2, TabGear = 3, TabGames = 4, TabQuests = 5;
    private const string ConfirmPopupId = "Confirm##iltconfirm";

    private readonly IltHubController _hub;
    private readonly IltHubContext _ctx;
    private IltConfirmRequest? _confirm;
    private bool _confirmOpened;
    private string _profileName = string.Empty;
    private bool _profileShared;
    private string[] _profileList = Array.Empty<string>();
    private int _profileIdx;
    private bool _wasDrawn; // last frame's window state, for the shown/hidden log events
    private bool _drawLogged; // window geometry logged for the current show

    public IltHubUi(IltHubController hub, IltHubContext ctx)
    {
        _hub = hub;
        _ctx = ctx;
    }

    public void Render()
    {
        // Keep the dashboard launcher flag and the persisted flag in sync.
        if (DashWindows.ShowIltHub != _ctx.State.WindowVisible)
            _ctx.State.WindowVisible = DashWindows.ShowIltHub;

        bool drawn = _ctx.State.WindowVisible;
        if (drawn != _wasDrawn)
        {
            // Logged on the first frame the window is (or stops being) drawn, so the line also
            // proves the overlay is actually rendering it.
            _wasDrawn = drawn;
            _drawLogged = false; // log geometry again on the next show
            if (drawn)
                RynthLog.Event(LogEvents.IltWindowShown, $"window shown (world='{_ctx.Options.WorldName}', available={_hub.Available})");
            else
                RynthLog.Event(LogEvents.IltWindowHidden, "window hidden");
        }

        if (drawn) RenderWindow();
        RenderPetsWindow();
        _hub.Games.RenderHud();
        _hub.Quests.RenderFloatingWindows();
        RenderConfirm();
    }

    private void RenderWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(640, 720), ImGuiCond.FirstUseEver);
        bool open = true;
        bool expanded = ImGui.Begin("ILT Hub##ilthub", ref open);
        if (!_drawLogged)
        {
            _drawLogged = true;
            LogAndRescueGeometry(expanded);
        }
        if (expanded)
        {
            RenderHeader();
            ImGui.Separator();
            if (_hub.Available) RenderTabs();
            else RenderUnavailable();
        }
        ImGui.End();
        if (!open) _hub.SetVisible(false);
    }

    /// <summary>
    /// First frame of each show (called between Begin and End): logs where the window is and, if a
    /// saved imgui.ini position leaves it with less than 40 px on screen, moves it back to (40, 40).
    /// </summary>
    private void LogAndRescueGeometry(bool expanded)
    {
        Vector2 pos = ImGui.GetWindowPos();
        Vector2 size = ImGui.GetWindowSize();
        Vector2 display = ImGui.GetIO().DisplaySize;
        const float MinVisible = 40f;
        bool onScreen = pos.X + size.X >= MinVisible && pos.Y + size.Y >= MinVisible
                     && pos.X <= display.X - MinVisible && pos.Y <= display.Y - MinVisible;

        RynthLog.Write(LogCat.IltHub,
            $"[IltHub] window drawn: expanded={expanded} collapsed={ImGui.IsWindowCollapsed()} " +
            $"pos=({pos.X:0},{pos.Y:0}) size=({size.X:0},{size.Y:0}) display=({display.X:0},{display.Y:0}) onScreen={onScreen}");

        if (!onScreen && display.X > MinVisible * 2 && display.Y > MinVisible * 2)
        {
            ImGui.SetWindowPos(new Vector2(MinVisible, MinVisible));
            RynthLog.Write(LogCat.IltHub, "[IltHub] window was off-screen - moved to (40,40).");
        }
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

    /// <summary>
    /// Button-strip tabs (selection is fully controlled by the persisted SelectedTab, so the
    /// Hub reopens on the tab the player left it on).
    /// </summary>
    private void RenderTabs()
    {
        int sel = _ctx.State.SelectedTab;
        if (sel < 0 || sel >= TabNames.Length || !TabVisible(sel)) sel = 0;

        bool first = true;
        foreach (int i in TabOrder)
        {
            if (!TabVisible(i)) continue;
            if (!first) ImGui.SameLine();
            first = false;
            bool active = i == sel;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, LegacyDashboardRenderer.ColBtnOn);
            if (ImGui.Button(TabNames[i] + "##ilttab" + i, new Vector2(92, 0))) sel = i;
            if (active) ImGui.PopStyleColor();
        }
        _ctx.State.SelectedTab = sel;

        ImGui.Separator();
        ImGui.BeginChild("##iltTabBody", new Vector2(0, 0), ImGuiChildFlags.None);
        RenderTab(sel);
        ImGui.EndChild();
    }

    private bool TabVisible(int tab) => tab switch
    {
        TabBanking => !_ctx.Options.IsOff(IltFeature.Bank),
        TabGames => _hub.Games.AnyGameAvailable,
        _ => true,
    };

    private void RenderTab(int tab)
    {
        var cs = _ctx.State.Character;
        switch (tab)
        {
            case TabCharacter:
                if (ImGui.CollapsingHeader("Session rates", ImGuiTreeNodeFlags.DefaultOpen)) _hub.Rates.Render();
                ImGui.Spacing();
                ImGui.TextDisabled("XP planner, Augmentations and Enlightenment are on the Skills panel's Progression tab");
                ImGui.TextDisabled("(right-click the dashboard's Char button > Progression).");
                break;
            case TabQuests:
                if (cs.QuestTrackerPoppedOut)
                {
                    ImGui.TextDisabled("Quests are open in their own window.");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Dock back##quests")) cs.QuestTrackerPoppedOut = false;
                    break;
                }
                if (ImGui.CollapsingHeader("Quest tracker", ImGuiTreeNodeFlags.DefaultOpen)) _hub.Quests.RenderQuestTracker();
                if (ImGui.CollapsingHeader("Quest bonus (/qb)")) _hub.Quests.RenderQb();
                break;
            case TabPet:
                if (cs.PetsWindowOpen)
                {
                    ImGui.TextDisabled("Pets are open in their own window.");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Dock back##pets")) cs.PetsWindowOpen = false;
                    break;
                }
                if (ImGui.SmallButton("Pop out##pets")) cs.PetsWindowOpen = true;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open the pet roster in its own window (/ra pets).");
                _hub.Pets.Render();
                break;
            case TabBanking: _hub.Banking.Render(); break;
            case TabGear: _hub.Gear.Render(); break;
            case TabGames: _hub.Games.Render(); break;
        }
    }

    /// <summary>The undocked "Pets" window; closing it docks the roster back into the Pet tab.</summary>
    private void RenderPetsWindow()
    {
        var cs = _ctx.State.Character;
        if (!cs.PetsWindowOpen || !_hub.Available) return;
        ImGui.SetNextWindowSize(new Vector2(560, 620), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("Pets##iltpetswin", ref open)) _hub.Pets.Render();
        ImGui.End();
        if (!open) cs.PetsWindowOpen = false;
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
