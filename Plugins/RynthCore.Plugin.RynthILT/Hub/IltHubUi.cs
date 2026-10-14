// IltHubUi.cs — The ILT sections' ImGui bodies (render thread only).
// Originally SilentKelpie's ILT Hub UI (RynthAi IltHub/); moved to RynthILT 2026-10-13, with the
// ILT parts of RynthAi's Mini Remote (the Remote tab's Bank section and Guardian line, the menu's
// Infinite Leaftide items) that read the Hub's state.
//
// The ILT sections are tabs of RynthAi's Mini Remote (its MiniRemoteHud draws the tab bar and
// calls RenderTab through RynthILT.Hub): Gear and Games. A tab is offered only while the server
// reports its feature on. Character, Charms and Banking left the Mini Remote on 2026-10-13:
//   - the session rates are RynthCore's Mini Remote "Session Stats" section (RenderRatesSection,
//     surface "section:ilt-rates"), below RynthAi's one session;
//   - the Leaftide feature list is RynthOracle's About tab ("Server features", from the profile);
//   - the charms are RynthOracle's Character tab, the bank balances RynthInventory's Global
//     Inventory "Banking" tab (both read "RynthILT.Data");
//   - the "Treat this world as ILT" override and the ILT world names are RynthCore's
//     Options > Diagnostics server selection only ("/ra hub force" and "/ra hub servers" remain).
// RenderSection draws the ILT pieces inside RynthAi's own surfaces (the Remote tab, the Quests and
// Pets tabs, the Mini Remote menu). The Guardian is a RynthCore ILT panel (the bar's ILT button,
// next to Bounties), drawn through RenderGuardianPanel. Render() keeps the floating extras: the
// Games HUD and the confirmation modal. Augmentations / Enlightenment / XP planning live on the
// engine Skills panel. All AC actions go through IltHubContext.Post / Confirm so nothing here
// touches the game directly.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltHubUi
{
    private const string ConfirmPopupId = "Confirm##iltconfirm";
    private static readonly Vector4 ColLabel = new(0.80f, 0.84f, 0.90f, 1f);

    /// <summary>Where a server is forced now (the only place: RynthCore's Options window).</summary>
    private const string ForceHint = "To force a server, use RynthCore Options > Diagnostics > server selection (/rc diagnostics).";

    /// <summary>RynthCore's command that opens Options > Diagnostics (RynthCore 2026.10.5.54+).</summary>
    private const string DiagnosticsCommand = "/rc diagnostics";

    private readonly IltHubController _hub;
    private readonly IltHubContext _ctx;
    private IltConfirmRequest? _confirm;
    private bool _confirmOpened;
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
            case IltSection.Gear: _hub.Gear.Render(); break;
            case IltSection.Games: _hub.Games.Render(); break;
            default:
                // A saved tab id from before the move: say where the section lives now.
                if (IltSections.MovedCommand(section) is string moved)
                    MovedLink(section, moved);
                break;
        }
    }

    /// <summary>
    /// The ILT session extras in RynthCore's Mini Remote "Session Stats" section (surface
    /// "section:ilt-rates", the Infinite Leaftide group): coins, pyreals and item conversions.
    /// XP, luminance and kills are RynthAi's session stats, drawn above it in the same section.
    /// </summary>
    public void RenderRatesSection()
    {
        if (!_hub.Available)
        {
            RenderHeader();
            RenderUnavailable();
            return;
        }
        _hub.Rates.Render(extrasOnly: true);
        ImGui.TextDisabled("XP planner, Augmentations and Enlightenment: Skills panel > Progression.");
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

    /// <summary>A button that opens a moved section's new home (posted: the command runs on the pump).</summary>
    private void MovedLink(IltSection section, string command)
    {
        string home = command == IltSections.GlobalBankCommand ? "Global Inventory > Banking" : "RynthOracle > Character";
        ImGui.TextDisabled($"{IltSections.Label(section)} moved to {home}.");
        if (ImGui.SmallButton($"Open {home}##moved{(int)section}"))
            _ctx.Post(() => _hub.OpenMoved(section, command));
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(command);
    }

    /// <summary>The world and when the server's ILT features were last read, with a Refresh.</summary>
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
    }

    private void RenderUnavailable()
    {
        var o = _ctx.Options;
        if (!o.IsIltLikeWorld)
        {
            SafeImGui.TextWrapped("This is not an Infinite Leaftide / ACECustom world, so the ILT tools are idle. " + ForceHint);
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
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reads your bank balances (/bank, reply hidden). Global Inventory > Banking (/ginv bank) has the details.");
            BankLink();
            return;
        }
        Stat("P:", Compact(b.Pyreals));
        Stat("Lum:", Compact(b.Luminance));
        BankLink();
        if (!ImGui.BeginTable("##mrbank", 2, ImGuiTableFlags.SizingStretchSame)) return;
        ImGui.TableNextColumn(); Stat("LK:", Compact(b.LegendaryKeys));
        ImGui.TableNextColumn(); Stat("MK:", Compact(b.MythicalKeys));
        ImGui.TableNextColumn(); Stat("EC:", Compact(b.EnlightenedCoins));
        ImGui.TableNextColumn(); Stat("WE:", Compact(b.WeaklyEnlightenedCoins));
        // Prestige Coins (ACECustom 2026-10-04): only once the server has reported some.
        if (b.PrestigeCoins > 0) { ImGui.TableNextColumn(); Stat("PC:", Compact(b.PrestigeCoins)); }
        ImGui.EndTable();
    }

    /// <summary>The Bank line's link to RynthInventory's Global Inventory "Banking" tab (every character here).</summary>
    private void BankLink()
    {
        ImGui.SameLine();
        if (ImGui.SmallButton("Banks##mrbanklink"))
            _ctx.Post(() => _hub.OpenMoved(IltSection.Banking, IltSections.GlobalBankCommand));
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Global Inventory > Banking (/ginv bank): deposits, withdrawals, transfers and\nevery character's bank on this server.");
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
    /// Under the Infinite Leaftide section while no ILT feature is on: why, where a server is
    /// forced (RynthCore Options > Diagnostics only) and a server-features refresh. False when
    /// features are on.
    /// </summary>
    private bool RenderIltStatus()
    {
        if (_hub.Available) return false;
        var o = _ctx.Options;
        SafeImGui.TextDisabled(!o.IsIltLikeWorld ? "Not an ILT world."
            : o.IsRefreshing || o.LastRefreshUtc == DateTime.MinValue ? "Checking server features..."
            : "No ILT features reported.");
        if (!o.IsIltLikeWorld && ImGui.MenuItem("Force a server... (Options > Diagnostics)##mrforce", DiagnosticsCommand))
            _ctx.Post(() => { if (_ctx.Host.HasInvokeChatParser) _ctx.Host.InvokeChatParser(DiagnosticsCommand); });
        if (!o.IsIltLikeWorld && ImGui.IsItemHovered()) SafeImGui.SetTooltip(ForceHint);
        if (ImGui.MenuItem("Refresh server features##mrrefresh")) _hub.RequestOptionsRefresh();
        return true;
    }

    /// <summary>
    /// RynthILT's own Infinite Leaftide entries, for when RynthCore sends no server list (older
    /// RynthCore, or its ImGui layer off): the tabs, where Character / Charms / Banking went, and
    /// the Guardian / Bounties panels.
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
        if (ImGui.MenuItem("Character and charms##mrlocal", IltSections.OracleCharacterCommand))
            _ctx.Post(() => _hub.OpenMoved(IltSection.Character, IltSections.OracleCharacterCommand));
        if (ImGui.MenuItem("Banking##mrlocal", IltSections.GlobalBankCommand, false, _hub.SectionAvailable(IltSection.Banking)))
            _ctx.Post(() => _hub.OpenMoved(IltSection.Banking, IltSections.GlobalBankCommand));
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
