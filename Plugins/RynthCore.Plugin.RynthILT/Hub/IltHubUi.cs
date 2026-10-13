// IltHubUi.cs — The ILT sections' ImGui bodies (render thread only).
// Originally SilentKelpie's ILT Hub UI (RynthAi IltHub/); moved to RynthILT 2026-10-13, with the
// ILT parts of RynthAi's Mini Remote (the Remote tab's Bank section and Guardian line, the menu's
// Infinite Leaftide items) that read the Hub's state.
//
// The ILT sections are tabs of RynthAi's Mini Remote (its MiniRemoteHud draws the tab bar and
// calls RenderTab through RynthILT.Hub): Character (world status, Force-ILT, server names,
// profiles, session rates), Banking, Gear, Charms and Games. A tab is offered only while the
// server reports its feature on. RenderSection draws the ILT pieces inside RynthAi's own
// surfaces (the Remote tab, the Quests and Pets tabs, the Mini Remote menu). The Guardian is a
// RynthCore ILT panel (the bar's ILT button, next to Bounties), drawn through
// RenderGuardianPanel. Render() keeps the floating extras: the Games HUD and the confirmation
// modal. Augmentations / Enlightenment / XP planning live on the engine Skills panel. All AC
// actions go through IltHubContext.Post / Confirm so nothing here touches the game directly.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltHubUi
{
    private const string ConfirmPopupId = "Confirm##iltconfirm";
    private static readonly Vector4 ColLabel = new(0.80f, 0.84f, 0.90f, 1f);

    private readonly IltHubController _hub;
    private readonly IltHubContext _ctx;
    private IltConfirmRequest? _confirm;
    private bool _confirmOpened;
    private string _profileName = string.Empty;
    private bool _profileShared;
    private string[] _profileList = Array.Empty<string>();
    private int _profileIdx;
    private string? _serverNamesEdit;
    // The last tab body drawn (for one shown log event per tab change); -1 = none yet.
    private int _lastTabLogged = -1;

    public IltHubUi(IltHubController hub, IltHubContext ctx)
    {
        _hub = hub;
        _ctx = ctx;
    }

    /// <summary>The floating extras: Games HUD and the confirmation modal.</summary>
    public void Render()
    {
        _hub.Games.RenderHud();
        RenderConfirm();
    }

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
            case IltSection.Banking: _hub.Banking.Render(); break;
            case IltSection.Gear: _hub.Gear.Render(); break;
            case IltSection.Charms: _hub.CharmsTab.Render(); break;
            case IltSection.Games: _hub.Games.Render(); break;
        }
    }

    /// <summary>
    /// One of the ILT pieces RynthAi draws in its own surfaces (RynthIltHub.Section*). False when
    /// there is nothing to draw (unknown id, or not an ILT world).
    /// </summary>
    public bool RenderSection(string id)
    {
        var o = _ctx.Options;
        switch (id)
        {
            case RynthIltHub.SectionRemoteBank:
                if (!o.IsIltLikeWorld || o.IsOff(IltFeature.Bank)) return false;
                RenderRemoteBank();
                return true;
            case RynthIltHub.SectionRemoteGuardian:
                if (!o.IsIltLikeWorld) return false;
                _hub.Guardian.RenderRemoteLine();
                return true;
            case RynthIltHub.SectionQuestBonus:
                if (!o.IsIltLikeWorld) return false;
                _hub.QuestBonus.RenderSection();
                return true;
            case RynthIltHub.SectionPetLogs:
                if (!o.IsIltLikeWorld) return false;
                _hub.PetLogs.RenderSection();
                return true;
            case RynthIltHub.SectionIltStatus:
                return RenderIltStatus();
            case RynthIltHub.SectionServerMenu:
                return RenderLocalMenu();
            default:
                return false;
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

    /// <summary>
    /// Character tab: world / options header, then session rates (or why the ILT tabs are idle), then
    /// the list of every Leaftide feature RynthILT supports with the server's state for each.
    /// </summary>
    private void RenderCharacterBody()
    {
        RenderHeader();
        ImGui.Separator();
        if (!_hub.Available) RenderUnavailable();
        else
        {
            if (ImGui.CollapsingHeader("Session rates", ImGuiTreeNodeFlags.DefaultOpen)) _hub.Rates.Render();
            ImGui.Spacing();
            ImGui.TextDisabled("XP planner, Augmentations and Enlightenment are on the Skills panel's Progression tab");
            ImGui.TextDisabled("(right-click the dashboard's Char button > Progression).");
        }
        ImGui.Spacing();
        RenderFeatureList();
    }

    /// <summary>
    /// "Leaftide features": IltFeatureCatalog grouped by section, each with the server's state and where
    /// the Rynth tools handle it. States come from the controller's per-second copy (FeatureStates).
    /// </summary>
    private void RenderFeatureList()
    {
        if (!ImGui.CollapsingHeader("Leaftide features", ImGuiTreeNodeFlags.None)) return;
        IltTri[] states = _hub.FeatureStates;
        bool ilt = _ctx.Options.IsIltLikeWorld;
        SafeImGui.TextDisabled(ilt
            ? $"Fellowships up to {IltFeatureCatalog.FellowshipMax(true)}. Built-in features are on with the world; the rest follow the server's check."
            : "Not an ILT world: nothing is asked of the server, so every feature reads 'checking'.");
        if (!ImGui.BeginTable("##iltfeatures", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Feature", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Server", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableHeadersRow();
        foreach (string group in IltFeatureCatalog.Groups)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            SafeImGui.TextColored(IltStyle.ColTeal, group);
            for (int i = 0; i < IltFeatureCatalog.All.Length; i++)
            {
                IltFeatureInfo f = IltFeatureCatalog.All[i];
                if (f.Group != group) continue;
                IltTri state = i < states.Length ? states[i] : IltTri.Unknown;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(f.Label);
                if (ImGui.IsItemHovered())
                    SafeImGui.SetTooltip(IltFeatureCatalog.Describe(f.Key).Tooltip);
                ImGui.TableNextColumn();
                SafeImGui.TextColored(state switch
                {
                    IltTri.On => IltStyle.ColGreen,
                    IltTri.Off => IltStyle.ColHp,
                    _ => IltStyle.ColTextMute,
                }, IltFeatureCatalog.StateText(state));
                ImGui.TableNextColumn();
                SafeImGui.TextDisabled(f.Where);
            }
        }
        ImGui.EndTable();
    }

    private void RenderHeader()
    {
        var o = _ctx.Options;
        string world = o.WorldName.Length > 0 ? o.WorldName : "unknown world";
        SafeImGui.TextColored(o.IsIltLikeWorld ? IltStyle.ColTeal : IltStyle.ColTextMute, world);
        ImGui.SameLine();
        SafeImGui.TextDisabled(o.IsRefreshing ? "checking server options..."
            : o.LastRefreshUtc == DateTime.MinValue ? "server options not checked yet"
            : $"options via {o.Source} @ {o.LastRefreshUtc.ToLocalTime():t}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##opts")) _ctx.Post(() => o.Refresh(manual: true));

        bool force = _ctx.State.ForceLeaftideFeatures;
        if (ImGui.Checkbox("Treat this world as ILT", ref force)) _hub.ForceIltWorld = force;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("World-identity override only: the Mini Remote asks the server which ILT features exist.\nIt never turns a feature on by itself.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Servers...")) { _serverNamesEdit = null; ImGui.OpenPopup("##iltservers"); }
        ImGui.SameLine();
        if (ImGui.SmallButton("Profiles...")) { ImGui.OpenPopup("##iltprofiles"); _ctx.Post(() => _profileList = _ctx.Store.ListProfiles().ToArray()); }
        RenderServersPopup();
        RenderProfilesPopup();
    }

    /// <summary>
    /// The world names that count as Infinite Leaftide (RynthAi's Settings > Misc "Server Features"
    /// list before the move; one list for every character now).
    /// </summary>
    private void RenderServersPopup()
    {
        if (!ImGui.BeginPopup("##iltservers")) return;
        ImGui.TextColored(IltStyle.ColTeal, "Infinite Leaftide world names");
        SafeImGui.TextDisabled(ServerFeatureGate.Describe(_ctx.Options.WorldName, _ctx.Settings, _ctx.State.ForceLeaftideFeatures));
        string text = _serverNamesEdit ?? ServerFeatureGate.FormatList(ServerFeatureGate.Patterns(_ctx.Settings));
        ImGui.SetNextItemWidth(320);
        bool enter = ImGui.InputTextWithHint("##iltservernames", "InfiniteLeaftide, *Leaftide*", ref text, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        _serverNamesEdit = text;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Comma-separated world names that turn on the ILT tabs, the Guardian, the Progression\n" +
                             "planners and the attribute raiser. * is a wildcard (*Leaftide* matches any\n" +
                             "name containing Leaftide). Case does not matter. Also: /ra hub servers <list>.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Save") || enter)
        {
            var list = ServerFeatureGate.ParseList(_serverNamesEdit);
            if (list.Count > 0)
                _ctx.Post(() => { _ctx.Settings.ServerNames = list; _ctx.SaveSettings(); });
            _serverNamesEdit = null;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Default"))
        {
            _ctx.Post(() => { _ctx.Settings.ServerNames = new(ServerFeatureGate.DefaultServerNames); _ctx.SaveSettings(); });
            _serverNamesEdit = null;
        }
        ImGui.EndPopup();
    }

    private void RenderProfilesPopup()
    {
        if (!ImGui.BeginPopup("##iltprofiles")) return;
        ImGui.TextColored(IltStyle.ColTeal, "ILT profiles (settings only; no balances)");
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
            SafeImGui.TextWrapped("This is not an Infinite Leaftide / ACECustom world, so the ILT tabs are idle. "
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

    // ── RynthAi's Mini Remote: the Remote tab's Bank section ────────────────

    private void RenderRemoteBank()
    {
        var b = _ctx.State.Bank;
        if (b.LastUpdated == DateTime.MinValue)
        {
            // Nothing read yet this install: offer a quiet /bank (the Banking tab has the full view).
            ImGui.TextDisabled("Bank: not loaded yet");
            ImGui.SameLine();
            if (ImGui.SmallButton("Load##mrbank")) _ctx.Post(() => _hub.Banking.RequestRefresh(quiet: true));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reads your bank balances (/bank, reply hidden). The Mini Remote's Banking tab has the details.");
            return;
        }
        Stat("P:", Compact(b.Pyreals));
        Stat("Lum:", Compact(b.Luminance));
        if (!ImGui.BeginTable("##mrbank", 2, ImGuiTableFlags.SizingStretchSame)) return;
        ImGui.TableNextColumn(); Stat("LK:", Compact(b.LegendaryKeys));
        ImGui.TableNextColumn(); Stat("MK:", Compact(b.MythicalKeys));
        ImGui.TableNextColumn(); Stat("EC:", Compact(b.EnlightenedCoins));
        ImGui.TableNextColumn(); Stat("WE:", Compact(b.WeaklyEnlightenedCoins));
        // Prestige Coins (ACECustom 2026-10-04): only once the server has reported some.
        if (b.PrestigeCoins > 0) { ImGui.TableNextColumn(); Stat("PC:", Compact(b.PrestigeCoins)); }
        ImGui.EndTable();
    }

    private static void Stat(string label, string value)
    {
        SafeImGui.TextColored(ColLabel, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    /// <summary>The Mini Remote's compact numbers (RynthAi's HudDraw.Compact): 12.3k, 4.5m, 1.2b.</summary>
    private static string Compact(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1e9) return (v / 1e9).ToString("0.0") + "b";
        if (a >= 1e6) return (v / 1e6).ToString("0.0") + "m";
        if (a >= 1e4) return (v / 1e3).ToString("0.0") + "k";
        return v.ToString("0");
    }

    // ── RynthAi's Mini Remote menu: the Infinite Leaftide section ──────────

    /// <summary>
    /// Under the Infinite Leaftide section while no ILT feature is on: why, this character's
    /// "Treat this world as ILT" override and a server-features refresh. False when features are on.
    /// </summary>
    private bool RenderIltStatus()
    {
        if (_hub.Available) return false;
        var o = _ctx.Options;
        SafeImGui.TextDisabled(!o.IsIltLikeWorld ? "Not an ILT world."
            : o.IsRefreshing || o.LastRefreshUtc == DateTime.MinValue ? "Checking server features..."
            : "No ILT features reported.");
        bool force = _hub.ForceIltWorld;
        if (ImGui.Checkbox("Treat this world as ILT##mrforce", ref force)) _hub.ForceIltWorld = force;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("World-identity override only: RynthILT asks the server which features exist.\nIt never turns a feature on by itself.");
        if (ImGui.MenuItem("Refresh server features##mrrefresh")) _hub.RequestOptionsRefresh();
        return true;
    }

    /// <summary>
    /// RynthILT's own Infinite Leaftide entries, for when RynthCore sends no server list (older
    /// RynthCore, or its ImGui layer off): the tabs, and the Guardian / Bounties panels.
    /// </summary>
    private bool RenderLocalMenu()
    {
        if (!_hub.OffersTabs) return false;
        ImGui.Separator();
        ImGui.TextDisabled("Server: Infinite Leaftide");
        foreach (IltSection section in IltSections.Tabs)
        {
            if (ImGui.MenuItem(IltSections.Label(section) + "##mrlocal", string.Empty, false, _hub.SectionAvailable(section)))
                _ctx.Ai.MiniRemoteTab(IltSections.TabId(section), "show");
        }
        if (ImGui.MenuItem("Guardian##mrlocal", "/rc guardian", false, _hub.SectionAvailable(IltSection.Guardian)))
            _ctx.Post(() => _hub.OpenEnginePanel("guardian", "Guardian", "show"));
        if (ImGui.MenuItem("Bounties##mrlocal", "/rc bounties"))
            _ctx.Post(() => _hub.OpenEnginePanel("bounties", "Bounties", "show"));
        RenderIltStatus();
        return true;
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
            SafeImGui.TextColored(IltStyle.ColAmber, _confirm.Title);
            ImGui.Separator();
            SafeImGui.TextWrapped(_confirm.Message);
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
