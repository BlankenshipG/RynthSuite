// MiniRemoteHud.cs — The Mini Remote (UtilityBelt-style) for RynthAi. Render thread only.
//
// One window with tabs: Remote (the sections below), the universal tabs Pets and Crafting (every
// world, MiniRemote/; the Quests tab moved to RynthOracle 2026-10-13), and on Infinite Leaftide the RynthILT plugin's ILT tabs (Character,
// Banking, Gear, Charms, Games: RynthILT.Hub GetTabs / RenderTab through IltClient; an ILT tab
// shows once the server reports its feature on, and never without RynthILT). The tab bar's
// selection is saved (HudState.MiniRemoteTab) and "/ra hub open <section>" or RynthCore's
// dashboard switch it (RequestTab). The Guardian and Bounties are RynthILT's RynthCore ILT
// panels (the bar's ILT button).
// Right-click on the remote: "Character" (Spells, Pets, Crafting, RynthOracle's Character, Quests,
// Quest Timers and Fellowship, Skills, Inventory, Item Count, Inventory HUDs: windows every server
// has), then "Server: <name>", i.e. the installed server plugins' windows for the world you're on,
// which RynthCore sends (ServerMenuModel), or a link to RynthCore's Options > Diagnostics (where a
// server is forced) when none matches. Options: docked left-click opens RynthAi Settings
// (right-click = Mini Remote section/layout popup); undocked left-click opens that expanded
// Options popup. Float / Dock next to Options (and in the popup) call /rc miniremote undock|dock.
// The V/H button stacks the sections (vertical) or lays them out in three columns (horizontal).
//
// Remote sections (each can be hidden from the Options menu):
//   Stats    session time and per-hour XP / luminance / kills, plus coins / pyreals (RynthILT's rates on ILT)
//   Target   the creature combat is attacking: name, health bar, distance
//   Pet      the summon that is out (health bar, time left), else the next combat essence and
//            whether it is ready
//   Slots    5 × 6 quick-use item grid; click uses the item (an empty slot takes the selected
//            pack item), right-click assigns / clears, and items dragged from the game's
//            inventory or the RynthCore Inventory window drop straight into a slot. Ctrl+wheel
//            over the grid (or Options > Slot size) grows / shrinks the boxes.
//   Toggles  macro and subsystem switches (same as the dashboard buttons; not in the drawer)
//   Bank     ILT bank balances (pyreals, luminance, keys, coins; drawn by RynthILT)
//   Rebuff   force rebuff / cancel rebuff
//   Translate chat translator on/off and the receive <-> send language swap
//   Guardian the last Temple guardian answer with a Give button (only while it is recent; RynthILT)
// All game actions are posted to the pump thread.
//
// RenderEmbedded draws the same sections and Options menu inside the engine's RynthAi dashboard
// (its Mini Remote drawer, opened by the drawer tab) and
// reports the size it wants; the drawer is sized from that. The drawer leaves out Toggles (the
// dashboard it sits on already has those switches) and the tabs (it is too narrow for them).
// RenderEmbedded(window: true) draws it in the engine's Mini Remote window instead (bar button,
// /rc miniremote): tabs and Toggles included, and the floating window stays hidden meanwhile
// (HudController), so that window is the one Mini Remote. RynthCore 2026.10.5.46+ shows the tabs
// as its own sections (RynthPluginGetSectionsJson) and asks for the remote alone (tabs: false).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Huds;

internal sealed class MiniRemoteHud
{
    private const string OptionsPopup = "##miniremoteopts";
    private const string MenuPopup = "##miniremotemenu";
    private const string SlotPopup = "##miniremoteslot";
    /// <summary>Pixels per Ctrl+wheel notch over the slot grid.</summary>
    private const float SlotSizeStep = 2f;
    /// <summary>Narrowest horizontal-layout column (fits the stat lines and the translate row).</summary>
    private const float MinColumnWidth = 190f;
    private const float HeaderButtonWidth = 80f;

    private static readonly Vector4 ColLabel = new(0.80f, 0.84f, 0.90f, 1f);
    private static readonly Vector4 ColReady = new(0.40f, 0.95f, 0.45f, 1f);
    private static readonly Vector4 ColOut = new(0.45f, 0.85f, 0.95f, 1f);
    private static readonly Vector4 ColEmpty = new(0.95f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 ColBusy = new(0.95f, 0.80f, 0.35f, 1f);

    private readonly HudController _hud;
    private int _slotMenuIndex = -1;
    private bool _rescueChecked; // off-screen check done for the current show
    private bool _columnOpen; // horizontal layout: a column was already started this frame (next one goes SameLine)
    private bool _embedded;   // true only inside RenderEmbedded (an engine surface)
    private bool _inWindow;   // with _embedded: the engine's Mini Remote window, not the dashboard drawer
    private bool _popped;     // engine Mini Remote is in its own floating window (:popout)
    // A tab switch asked for by a command or the engine (any thread), applied by the next tab bar;
    // null = none. Starts as the saved tab so the first tab bar restores it.
    private string? _pendingTab;
    private bool _pendingTabFromState = true;
    // This frame's tab bar showed the Remote tab (right-click Options belongs to it only).
    private bool _onRemoteTab = true;
    // The Options button was clicked this frame (the popup opens at window level).
    private bool _openOptions;
    /// <summary>One tab after Remote: a universal one (drawn here) or RynthILT's.</summary>
    private readonly record struct TabEntry(string Id, string Label, Vector2 Size, bool Ilt);

    // The tabs offered this frame (reused, so a frame doesn't allocate).
    private readonly List<TabEntry> _tabs = new();

    public MiniRemoteHud(HudController hud) => _hud = hud;

    /// <summary>The remote was not drawn this frame: check its position again on the next show.</summary>
    public void OnHidden() => _rescueChecked = false;

    /// <summary>
    /// Switches to tab <paramref name="tab"/> ("" = the remote, else a tab id) on the next
    /// frame the tab bar draws. Any thread.
    /// </summary>
    public void RequestTab(string tab)
    {
        _pendingTabFromState = false;
        Volatile.Write(ref _pendingTab, tab ?? string.Empty);
    }

    public void Render()
    {
        var s = _hud.State;
        var flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoFocusOnAppearing;
        if (s.MiniRemoteLocked) flags |= ImGuiWindowFlags.NoMove;

        // Left of and above centre, mirroring the Item HUD's first spot.
        UiPlacement.CenterFirstUse(new Vector2(-220, -160));
        ImGui.SetNextWindowBgAlpha(0.70f);
        bool open = true;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 3));
        bool visible = ImGui.Begin("Mini Remote##rynthminiremote", ref open, flags);
        ImGui.PopStyleVar();
        if (!open) s.ShowMiniRemote = false;
        if (!visible) { ImGui.End(); return; }
        UiPlacement.RescueOncePerShow(ref _rescueChecked);

        RenderBody(s, _hud.Remote(), allowTabs: true);

        // Right-click on the remote opens the Character / server menu, unless a slot's own menu is
        // open (the ILT tabs have their own right-click menus).
        if (_onRemoteTab && _slotMenuIndex < 0 && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
            ImGui.OpenPopup(MenuPopup);
        RenderOptions(s, embedded: false);
        RenderMenu(s);
        _slotMenuIndex = -1;
        ImGui.End();
    }

    /// <summary>
    /// The same sections drawn into the caller's current window (the engine's RynthAi dashboard
    /// drawer, or with <paramref name="window"/> the engine's Mini Remote window; both size
    /// themselves from the result) instead of a window of their own. Returns the content size it
    /// wants: the selected tab's width, and the height drawn.
    /// </summary>
    public Vector2 RenderEmbedded(bool window = false, bool tabs = true, bool popped = false)
    {
        var s = _hud.State;
        _slotMenuIndex = -1;
        _embedded = true;
        _inWindow = window;
        _popped = popped;
        float startY = ImGui.GetCursorPosY();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 3));
        try
        {
            // Without tabs (RynthCore's sections show them instead) the remote alone.
            float width = RenderBody(s, _hud.Remote(), allowTabs: tabs && !InDrawer);

            if (_onRemoteTab && _slotMenuIndex < 0 && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                ImGui.OpenPopup(MenuPopup);
            RenderOptions(s, embedded: true);
            RenderMenu(s);

            float height = ImGui.GetCursorPosY() - startY - ImGui.GetStyle().ItemSpacing.Y;
            return new Vector2(width, Math.Max(0f, height) + 2f);
        }
        finally
        {
            ImGui.PopStyleVar();
            _slotMenuIndex = -1;
            _embedded = false;
            _inWindow = false;
            _popped = false;
        }
    }

    // ── Tabs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tab bar (when <paramref name="allowTabs"/>: the universal tabs on every world, RynthILT's
    /// tabs on Infinite Leaftide) and the selected tab: the remote, or a tab's body in a child of its
    /// fixed size. Returns the width the content wants (never narrower than the tab bar's labels).
    /// </summary>
    private float RenderBody(HudState s, MiniRemoteController? remote, bool allowTabs)
    {
        _onRemoteTab = true;
        _tabs.Clear();
        if (allowTabs && remote != null)
        {
            foreach (string id in MiniRemoteTabs.Universal)
                _tabs.Add(new TabEntry(id, MiniRemoteTabs.Label(id), MiniRemoteTabs.Size(id), Ilt: false));
            bool vaultedOn = _hud.Dashboard.Settings.EnableVaultedDungeons;
            foreach (IltTabInfo t in remote.Ilt.Tabs())
            {
                // Vaulted Dungeons: ILT-only (RynthILT TabShown) and gated by Options > Navigation.
                if (!t.Shown) continue;
                if (string.Equals(t.Id, "vaulted", StringComparison.OrdinalIgnoreCase) && !vaultedOn) continue;
                _tabs.Add(new TabEntry(t.Id, t.Label, new Vector2(t.Width, t.Height), Ilt: true));
            }
        }
        if (_tabs.Count == 0)
        {
            RenderRemote(s, remote);
            return EmbeddedWidth(s);
        }

        if (_pendingTabFromState)
        {
            _pendingTabFromState = false;
            Interlocked.CompareExchange(ref _pendingTab, s.MiniRemoteTab ?? string.Empty, null);
        }
        string? pending = Interlocked.Exchange(ref _pendingTab, null);
        float width = TabBarWidth();
        _onRemoteTab = false;
        if (ImGui.BeginTabBar("##mrtabs", ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.NoTooltip))
        {
            if (BeginTab(RemoteTabLabel, TabFlags(MiniRemoteTabs.Remote, pending)))
            {
                _onRemoteTab = true;
                s.MiniRemoteTab = MiniRemoteTabs.Remote;
                RenderRemote(s, remote);
                width = Math.Max(width, EmbeddedWidth(s));
                ImGui.EndTabItem();
            }
            foreach (TabEntry tab in _tabs)
            {
                if (!BeginTab(TabLabel(tab), TabFlags(tab.Id, pending))) continue;
                s.MiniRemoteTab = tab.Id;
                // The tab draws into a child of its own size; tables in it scroll inside it.
                ImGui.BeginChild(TabBodyId(tab.Id), tab.Size, ImGuiChildFlags.None, ImGuiWindowFlags.None);
                bool drawn = tab.Ilt ? remote!.Ilt.RenderTab(tab.Id) : remote!.RenderTab(tab.Id);
                if (!drawn) SafeImGui.TextDisabled(tab.Ilt ? "RynthILT isn't drawing this tab (not loaded, or not ready yet)." : "Not ready.");
                ImGui.EndChild();
                width = Math.Max(width, tab.Size.X);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        return width;
    }

    // Tab labels as NUL-terminated UTF-8 and the tab body child ids, built once per tab so the tab
    // bar doesn't allocate per frame (RynthILT's tabs arrive by id at run time).
    private static readonly byte[] RemoteTabLabel = Utf8Z("Remote##mrtab");
    private readonly Dictionary<string, byte[]> _tabLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tabBodyIds = new(StringComparer.Ordinal);

    private byte[] TabLabel(TabEntry tab)
    {
        string key = tab.Label + "##mrtab";
        if (!_tabLabels.TryGetValue(key, out byte[]? label)) _tabLabels[key] = label = Utf8Z(key);
        return label;
    }

    private string TabBodyId(string id)
    {
        if (!_tabBodyIds.TryGetValue(id, out string? body)) _tabBodyIds[id] = body = "##mrtabbody_" + id;
        return body;
    }

    private static byte[] Utf8Z(string text)
    {
        byte[] bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(text) + 1];
        System.Text.Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    /// <summary>
    /// BeginTabItem without a close button: ImGui.NET only wraps the flags overload together with
    /// p_open, and a non-null p_open draws an X on the tab.
    /// </summary>
    private static unsafe bool BeginTab(byte[] labelUtf8Z, ImGuiTabItemFlags flags)
    {
        fixed (byte* label = labelUtf8Z)
            return ImGuiNative.igBeginTabItem(label, null, flags) != 0;
    }

    /// <summary>SetSelected for the tab a command asked for this frame.</summary>
    private static ImGuiTabItemFlags TabFlags(string id, string? pending) =>
        pending != null && string.Equals(pending, id, StringComparison.OrdinalIgnoreCase)
            ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    /// <summary>Width of every tab label side by side (the embedded window shouldn't be narrower, or tabs scroll).</summary>
    private float TabBarWidth()
    {
        var style = ImGui.GetStyle();
        float w = ImGui.CalcTextSize("Remote").X + style.FramePadding.X * 2f;
        foreach (TabEntry tab in _tabs)
            w += style.ItemInnerSpacing.X + ImGui.CalcTextSize(tab.Label).X + style.FramePadding.X * 2f;
        return w;
    }

    /// <summary>The Remote tab: the [V/H] [Options] row, then the sections in the chosen layout.</summary>
    private void RenderRemote(HudState s, MiniRemoteController? remote)
    {
        RenderHeaderRow(s);
        if (s.MiniRemoteHorizontal) RenderHorizontal(s, remote);
        else RenderVertical(s, remote);
    }

    /// <summary>In the dashboard drawer (not the floating window or the engine's Mini Remote window).</summary>
    private bool InDrawer => _embedded && !_inWindow;

    /// <summary>The Toggles section is drawn: chosen in Options, and never in the dashboard drawer.</summary>
    private bool ShowToggles(HudState s) => s.MiniShowToggles && !InDrawer;

    /// <summary>
    /// The width the sections need: one column stacked, else one per non-empty column side by side.
    /// Never measured from what was drawn, since widgets fill the width they are given.
    /// </summary>
    private float EmbeddedWidth(HudState s)
    {
        float colW = ColumnWidth(s);
        if (!s.MiniRemoteHorizontal) return colW;
        int cols = 0;
        if (s.MiniShowStats || s.MiniShowTarget || s.MiniShowPet || s.MiniShowCapture) cols++;
        if (s.MiniShowGems || ShowToggles(s)) cols++;
        if (s.MiniShowBank || s.MiniShowRebuff || s.MiniShowTranslate || s.MiniShowGuardian) cols++;
        cols = Math.Max(1, cols);
        return cols * colW + (cols - 1) * ImGui.GetStyle().ItemSpacing.X;
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    /// <summary>[V/H], [Options], and (engine window) Dock/Float buttons. Fixed widths so auto-resize can't grow from them.</summary>
    private void RenderHeaderRow(HudState s)
    {
        if (ImGui.Button((s.MiniRemoteHorizontal ? "H" : "V") + "##mrlayout", new Vector2(HeaderButtonWidth, 0)))
            s.MiniRemoteHorizontal = !s.MiniRemoteHorizontal;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip(s.MiniRemoteHorizontal ? "Horizontal: sections in columns. Click to stack them." : "Vertical: sections stacked. Click to lay them out in columns.");
        ImGui.SameLine();
        // Undocked: left-click opens the expanded Mini Remote Options popup (sections / layout).
        // Docked: left-click opens RynthAi Settings; right-click always opens the Options popup.
        if (ImGui.Button("Options##mropts", new Vector2(HeaderButtonWidth, 0)))
        {
            if (_popped || !_hud.Host.HasInvokeChatParser)
                _openOptions = true;
            else
                _hud.RunEngineCommand(MovedTabs.Settings);
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _openOptions = true;
        if (ImGui.IsItemHovered())
        {
            SafeImGui.SetTooltip(_popped
                ? "Mini Remote sections, layout, slot size (undocked).\nSettings is in this Options menu."
                : "Open RynthAi Settings (Advanced Settings).\nRight-click: Mini Remote sections, layout, slot size.\nUse Float to undock for a larger Options window.");
        }
        // Engine Mini Remote window only: dock / undock without hunting the title ↗.
        if (_inWindow && _hud.Host.HasInvokeChatParser)
        {
            ImGui.SameLine();
            string dockLabel = _popped ? "Dock" : "Float";
            if (ImGui.Button(dockLabel + "##mrdock", new Vector2(HeaderButtonWidth, 0)))
                _hud.RunEngineCommand(_popped ? "/rc miniremote dock" : "/rc miniremote undock");
            if (ImGui.IsItemHovered())
                SafeImGui.SetTooltip(_popped
                    ? "Dock Mini Remote back into the game window (/rc miniremote dock)."
                    : "Undock Mini Remote into a floating window for expanded Options (/rc miniremote undock).");
        }
    }

    private void RenderVertical(HudState s, MiniRemoteController? remote)
    {
        RenderStatusSections(s, remote);
        RenderActionSections(s);
        RenderEconomySections(s, remote);
    }

    /// <summary>
    /// Three side-by-side columns: status (stats, target, pet), actions (slots, toggles) and
    /// economy (bank, rebuff, translate). Each column is a fixed-width child that grows only in
    /// height; widgets sized from the available width would otherwise make the auto-resizing
    /// window grow every frame.
    /// </summary>
    private void RenderHorizontal(HudState s, MiniRemoteController? remote)
    {
        float colW = ColumnWidth(s);
        _columnOpen = false;
        if (s.MiniShowStats || s.MiniShowTarget || s.MiniShowPet || s.MiniShowCapture)
        {
            BeginColumn("##mrcolstatus", colW);
            RenderStatusSections(s, remote);
            ImGui.EndChild();
        }
        if (s.MiniShowGems || ShowToggles(s))
        {
            BeginColumn("##mrcolactions", colW);
            RenderActionSections(s);
            ImGui.EndChild();
        }
        if (s.MiniShowBank || s.MiniShowRebuff || s.MiniShowTranslate || s.MiniShowGuardian)
        {
            BeginColumn("##mrcoleconomy", colW);
            RenderEconomySections(s, remote);
            ImGui.EndChild();
        }
    }

    /// <summary>Starts a column child (caller always calls EndChild, whatever BeginChild returned).</summary>
    private void BeginColumn(string id, float width)
    {
        if (_columnOpen) ImGui.SameLine();
        _columnOpen = true;
        ImGui.BeginChild(id, new Vector2(width, 0), ImGuiChildFlags.AutoResizeY, ImGuiWindowFlags.NoScrollbar);
    }

    /// <summary>Wide enough for the slot grid at its current size, and never narrower than MinColumnWidth.</summary>
    private static float ColumnWidth(HudState s)
    {
        int cols = HudState.MiniRemoteColumns;
        float grid = cols * SlotButtonSize(s) + (cols - 1) * ImGui.GetStyle().ItemSpacing.X;
        return Math.Max(MinColumnWidth, grid);
    }

    /// <summary>Slot icon edge in pixels (the saved size, clamped).</summary>
    private static float SlotSize(HudState s) =>
        Math.Clamp(s.MiniRemoteSlotSize, HudState.MinMiniRemoteSlotSize, HudState.MaxMiniRemoteSlotSize);

    /// <summary>Slot button edge including its 1 px frame padding on each side.</summary>
    private static float SlotButtonSize(HudState s) => SlotSize(s) + 2f;

    /// <summary>
    /// Grows (+) or shrinks (-) the slot boxes within the saved range (huds.json picks it up on
    /// the next SaveIfDirty, like every other HUD setting).
    /// </summary>
    private static void ResizeSlots(HudState s, float delta) =>
        s.MiniRemoteSlotSize = Math.Clamp(SlotSize(s) + delta, HudState.MinMiniRemoteSlotSize, HudState.MaxMiniRemoteSlotSize);

    private void RenderStatusSections(HudState s, MiniRemoteController? remote)
    {
        var combat = _hud.Combat;
        if (s.MiniShowStats) RenderStats(remote);
        if (s.MiniShowTarget) RenderTarget(combat);
        if (s.MiniShowPet) RenderPet(remote, combat);
        if (s.MiniShowCapture) RenderCapture(remote);
        if (s.MiniShowStats) RenderStatButtons(remote);
    }

    private void RenderActionSections(HudState s)
    {
        if (s.MiniShowGems) RenderSlots(s);
        if (ShowToggles(s)) RenderToggles(_hud.Dashboard.Settings);
    }

    private void RenderEconomySections(HudState s, MiniRemoteController? remote)
    {
        // Bank and Guardian are RynthILT's (nothing is drawn off ILT or without it).
        if (s.MiniShowBank) remote?.Ilt.RenderSection(RynthIltHub.SectionRemoteBank);
        if (s.MiniShowRebuff) RenderRebuff();
        if (s.MiniShowTranslate && _hud.Translate is { } translate) translate.RenderHubSection();
        if (s.MiniShowGuardian) remote?.Ilt.RenderSection(RynthIltHub.SectionRemoteGuardian);
    }

    // ── Sections ────────────────────────────────────────────────────────────

    /// <summary>An Infinite Leaftide world, as RynthILT sees it (false without RynthILT).</summary>
    private static bool IltActive(MiniRemoteController? remote) => remote != null && remote.Ilt.IsIltWorld;

    private static void Stat(string label, string value)
    {
        SafeImGui.TextColored(ColLabel, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    /// <summary>
    /// Session stats on every world: the shared SessionStats (RynthTracker's code, so these match the
    /// Tracker panel), plus ILT coins and pyreals (RynthILT's rates).
    /// </summary>
    private void RenderStats(MiniRemoteController? remote)
    {
        var v = _hud.SessionStats?.View ?? RynthCore.Plugin.Shared.SessionStatsView.Empty;
        Stat("Time:", MiniRemoteText.Duration(TimeSpan.FromSeconds(SessionClock(v))));
        Stat("XP:", HudDraw.Compact(v.XpPerHour) + "/h");
        Stat("Lum:", HudDraw.Compact(v.LumPerHour) + "/h");
        Stat("Lum 5m:", HudDraw.Compact(v.LumLast5Min));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Luminance earned in the last five minutes.");
        if (v.PassUpTracked)
        {
            Stat("Pass-up:", HudDraw.Compact(v.PassUpPerHour) + "/h");
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"{v.LumPassUp:N0} this session.\n{PassUpTooltip}");
        }
        Stat("Kills:", HudDraw.Compact(v.KillsPerHour) + "/h");
        if (remote == null || !remote.Ilt.TryGetRates(out double coins, out double pyreals)) return;
        Stat("Coins:", HudDraw.Compact(coins) + "/h");
        Stat("Pyr:", HudDraw.Compact(pyreals) + "/h");
    }

    /// <summary>
    /// RynthCore's Mini Remote "Session Stats" section (surface "section:session"): the one session,
    /// totals and per-hour rates, with Reset and Report. RynthTracker, RynthOracle and RynthILT read
    /// these same numbers (RynthAi.Hub SessionStats), so every XP/h and lum/h on screen agrees.
    /// Render thread.
    /// </summary>
    public void RenderSessionSection()
    {
        var v = _hud.SessionStats?.View ?? RynthCore.Plugin.Shared.SessionStatsView.Empty;
        bool running = !ReferenceEquals(v, RynthCore.Plugin.Shared.SessionStatsView.Empty);
        SafeImGui.TextDisabled(running
            ? "Session " + MiniRemoteText.Duration(TimeSpan.FromSeconds(SessionClock(v)))
            : "The session starts once you are in the world.");
        if (ImGui.BeginTable("##mrsession", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Stat", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Session", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Per hour", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();
            SessionRow("XP", HudDraw.Compact(v.Xp), HudDraw.Compact(v.XpPerHour));
            SessionRow("Luminance", HudDraw.Compact(v.Lum), HudDraw.Compact(v.LumPerHour));
            // The last five minutes' luminance, and that pace for an hour.
            SessionRow("  last 5 min", HudDraw.Compact(v.LumLast5Min), HudDraw.Compact(v.LumLast5Min * 12.0));
            if (v.PassUpTracked) SessionRow("  pass-up", HudDraw.Compact(v.LumPassUp), HudDraw.Compact(v.PassUpPerHour), PassUpTooltip);
            if (v.Rad > 0) SessionRow("Radiance", HudDraw.Compact(v.Rad), HudDraw.Compact(v.RadPerHour));
            SessionRow("Kills", v.Kills.ToString("N0"), HudDraw.Compact(v.KillsPerHour));
            if (v.XpPerKill > 0) SessionRow("  XP per kill", HudDraw.Compact(v.XpPerKill), string.Empty);
            if (v.Deaths > 0) SessionRow("Deaths", v.Deaths.ToString("N0"), string.Empty);
            ImGui.EndTable();
        }
        RenderStatButtons(_hud.Remote());
    }

    /// <summary>One Session Stats row: label, session total, per hour (and an optional tooltip on the label).</summary>
    private static void SessionRow(string label, string total, string perHour, string tooltip = "")
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        SafeImGui.TextColored(ColLabel, label);
        if (tooltip.Length > 0 && ImGui.IsItemHovered()) SafeImGui.SetTooltip(tooltip);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(total);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(perHour);
    }

    /// <summary>What the pass-up figure is (RynthILT's session rates say the same).</summary>
    private const string PassUpTooltip =
        "Luminance that reached your bank without a \"You've banked\" line: your vassals' pass-up.\n"
        + "Fellowship and Zone Share splits arrive the same silent way and are included.";

    /// <summary>Seconds since the session started; 0 before it has (no login yet).</summary>
    private static double SessionClock(RynthCore.Plugin.Shared.SessionStatsView v)
        => ReferenceEquals(v, RynthCore.Plugin.Shared.SessionStatsView.Empty)
            ? 0 : Math.Max(0, (DateTime.UtcNow - v.SessionStartUtc).TotalSeconds);

    private static void RenderTarget(CombatHudSnapshot c)
    {
        if (c.TargetId == 0) { ImGui.TextDisabled("Target: none"); return; }
        ImGui.TextColored(ColLabel, "Target:");
        ImGui.SameLine();
        ImGui.TextUnformatted(c.TargetName);
        if (c.TargetDistance >= 0)
        {
            ImGui.SameLine();
            SafeImGui.TextDisabled($"{c.TargetDistance:F0}yd");
        }
        HealthBar("##mrtargethp", c.TargetHealth);
    }

    /// <summary>The live summon when one is out, else the Pets tab's next-essence line (ILT worlds).</summary>
    private static void RenderPet(MiniRemoteController? remote, CombatHudSnapshot c)
    {
        if (c.PetId != 0)
        {
            RenderSummon(remote, c);
            return;
        }
        if (!IltActive(remote)) { ImGui.TextDisabled("Summon: none"); return; }
        string name = remote!.Pets.HudPetName;
        string status = remote.Pets.HudPetStatus;
        if (name.Length == 0) { ImGui.TextDisabled("No combat pet set"); return; }
        ImGui.TextUnformatted(name);
        var col = status switch { "Ready" => ColReady, "Out" => ColOut, "Healing" => ColBusy, _ => ColEmpty };
        SafeImGui.TextColored(col, status);
    }

    /// <summary>
    /// ILT siphon-lens auto-capture: same enable / lens as Pets > Monster capture, on the Remote
    /// so Options > Pet capture can show it without opening the Pets tab.
    /// </summary>
    private static void RenderCapture(MiniRemoteController? remote)
    {
        if (!IltActive(remote))
        {
            ImGui.TextDisabled("Capture: ILT only");
            return;
        }
        var pets = remote!.Pets;
        bool captureOff = remote.Ilt.IsOff(RynthIltHub.FeatureMonsterCapture);
        ImGui.BeginDisabled(captureOff);
        bool en = pets.AutoCaptureEnabled;
        if (ImGui.Checkbox("Auto-capture##mrcapture", ref en))
            pets.AutoCaptureEnabled = en;
        if (ImGui.IsItemHovered())
        {
            SafeImGui.SetTooltip(
                "Uses the selected siphon lens on the nearest attackable creature within 5 m once it is\n" +
                "at or below 20% health or under 20 HP (ACECustom MonsterCapture). Asheron's Lens skips\n" +
                "the health gate. Same setting as Mini Remote > Pets > Monster capture.");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        int lens = Math.Clamp(pets.CaptureLensIndex, 0, PetCaptureRules.LensNames.Length - 1);
        if (ImGui.Combo("##mrcapturelens", ref lens, PetCaptureRules.LensNames, PetCaptureRules.LensNames.Length))
            pets.CaptureLensIndex = lens;
        ImGui.EndDisabled();
        if (captureOff)
            SafeImGui.TextDisabled("Capture disabled on this server");
        else
            SafeImGui.TextDisabled(pets.CaptureStatus);
    }

    private static void RenderSummon(MiniRemoteController? remote, CombatHudSnapshot c)
    {
        ImGui.TextColored(ColLabel, "Summon:");
        ImGui.SameLine();
        ImGui.TextUnformatted(c.PetName);
        if (IltActive(remote) && remote!.Pets.HudPetStatus == "Healing")
        {
            ImGui.SameLine();
            ImGui.TextColored(ColBusy, "Healing");
        }
        HealthBar("##mrpethp", c.PetHealth);

        long now = Environment.TickCount64;
        if (c.PetExpiresAtMs != 0)
        {
            long leftMs = Math.Max(0, c.PetExpiresAtMs - now);
            var left = TimeSpan.FromMilliseconds(leftMs);
            string text = $"{FormatClock(left)} left";
            if (c.PetLifespanSec > 0)
            {
                float frac = Math.Clamp(leftMs / (c.PetLifespanSec * 1000f), 0f, 1f);
                var col = left.TotalSeconds <= 30 ? ColEmpty : left.TotalSeconds <= 90 ? ColBusy : ColOut;
                Bar("##mrpettime", frac, text, col);
            }
            else
            {
                SafeImGui.TextColored(left.TotalSeconds <= 30 ? ColEmpty : ColOut, text);
            }
        }
        else
        {
            // No RemainingLifespan from the server: show how long it has been out instead.
            var up = TimeSpan.FromMilliseconds(Math.Max(0, now - c.PetSeenAtMs));
            SafeImGui.TextDisabled($"Out {FormatClock(up)}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The server hasn't reported this summon's lifespan yet.");
        }
    }

    private static string FormatClock(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>Health ratio bar coloured green / yellow / red; "--" until the first health update.</summary>
    private static void HealthBar(string id, float ratio)
    {
        if (ratio < 0f) { Bar(id, 0f, "HP --", ColLabel); return; }
        var col = ratio > 0.6f ? ColReady : ratio > 0.3f ? ColBusy : ColEmpty;
        Bar(id, ratio, $"HP {ratio * 100f:F0}%", col);
    }

    private static void Bar(string id, float fraction, string overlay, Vector4 color)
    {
        float w = Math.Max(ImGui.GetContentRegionAvail().X, 140f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color with { W = 0.85f });
        ImGui.PushID(id);
        ImGui.ProgressBar(fraction, new Vector2(w, 14f), overlay);
        ImGui.PopID();
        ImGui.PopStyleColor();
    }

    private void RenderStatButtons(MiniRemoteController? remote)
    {
        float w = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        w = Math.Max(w, 70f);
        if (ImGui.Button("Reset XP", new Vector2(w, 0)))
            _hud.Post(() =>
            {
                _hud.SessionStats?.RequestReset();
                _hud.Remote()?.Ilt.Command("rates reset");   // RynthILT's coin / pyreal session too
            });
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Start the session stats over (RynthTracker, RynthOracle and RynthILT follow this session).");
        ImGui.SameLine();
        if (ImGui.Button("Report", new Vector2(w, 0))) _hud.Post(ReportToChat);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Print the session rates in your chat window (local only).");
    }

    /// <summary>One-line session summary written to local chat (pump thread).</summary>
    private void ReportToChat()
    {
        var v = _hud.SessionStats?.View ?? RynthCore.Plugin.Shared.SessionStatsView.Empty;
        string time = MiniRemoteText.Duration(TimeSpan.FromSeconds(SessionClock(v)));
        string line = $"[RynthAi] Session {time}: XP {HudDraw.Compact(v.XpPerHour)}/h, Lum {HudDraw.Compact(v.LumPerHour)}/h "
                      + $"({HudDraw.Compact(v.LumLast5Min)} last 5m)";
        if (v.PassUpTracked) line += $", Pass-up {HudDraw.Compact(v.PassUpPerHour)}/h";
        line += $", Kills {HudDraw.Compact(v.KillsPerHour)}/h ({v.Kills:N0})";
        if (_hud.Remote()?.Ilt.TryGetRates(out double coins, out double pyreals) == true)
            line += $", Coins {HudDraw.Compact(coins)}/h, Pyr {HudDraw.Compact(pyreals)}/h";
        _hud.Chat(line);
    }

    private void RenderSlots(HudState s)
    {
        var slots = s.MiniRemoteSlots.ToArray();
        var pack = _hud.Pack;
        float slotSize = SlotSize(s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 1));
        ImGui.BeginGroup();
        for (int i = 0; i < slots.Length; i++)
        {
            if (i % HudState.MiniRemoteColumns != 0) ImGui.SameLine();
            var e = slots[i];
            long count = e.IsEmpty ? 0 : pack.CountOf(e.Name);
            ImGui.PushID(i);
            bool clicked;
            IntPtr tex = e.IsEmpty ? IntPtr.Zero : _hud.Icons.GetTexture(e.IconDid);
            if (tex != IntPtr.Zero)
            {
                var tint = count == 0 ? new Vector4(1, 1, 1, 0.35f) : Vector4.One;
                clicked = ImGui.ImageButton("##slot", tex, new Vector2(slotSize, slotSize), Vector2.Zero, Vector2.One, Vector4.Zero, tint);
            }
            else
            {
                string label = e.IsEmpty ? string.Empty : e.Name.Substring(0, 1).ToUpperInvariant();
                if (e.IsEmpty) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.13f, 0.18f, 0.6f));
                clicked = ImGui.Button(label + "##slot", new Vector2(slotSize + 2, slotSize + 2));
                if (e.IsEmpty) ImGui.PopStyleColor();
            }
            if (ImGui.IsItemHovered())
                SafeImGui.SetTooltip(e.IsEmpty ? EmptySlotTip(i) : $"{e.Name}\n{count:N0} carried\nClick to use, right-click for options.\nCtrl+wheel: box size.");
            int slotIndex = i;
            if (clicked)
            {
                // Empty slot: click assigns the selected (or last selected) pack item.
                if (e.IsEmpty) _hud.Post(() => _hud.SetSlotFromSelection(slotIndex));
                else
                {
                    var entry = e;
                    _hud.Post(() => _hud.UseItem(entry));
                }
            }
            AcceptItemDrop(slotIndex, e.IsEmpty);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _slotMenuIndex = i;
                ImGui.OpenPopup(SlotPopup);
            }
            RenderSlotMenu(i, e);
            ImGui.PopID();
        }
        ImGui.EndGroup();
        ImGui.PopStyleVar();

        // Ctrl+wheel over the grid: bigger / smaller boxes (the window / drawer refits to them).
        var io = ImGui.GetIO();
        if (io.KeyCtrl && io.MouseWheel != 0f && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            ResizeSlots(s, io.MouseWheel > 0f ? SlotSizeStep : -SlotSizeStep);
    }

    /// <summary>
    /// ImGui drag-and-drop payload type for one inventory item (uint object id). Must match
    /// InventoryFace.ItemPayloadType in RynthCore.Engine, which publishes it while dragging.
    /// </summary>
    private const string ItemPayloadType = "RYNTH_INV_ITEM";

    /// <summary>
    /// Payload type for an AC-inventory drag whose item the engine guessed from the game selection
    /// (uint object id); engines that hook AC's drag start send the real item as ItemPayloadType
    /// instead. Must match ItemDragBridge.NativePayloadType in RynthCore.Engine. A guess can be the
    /// wrong item, so it is taken by empty slots only and never replaces an assigned one.
    /// </summary>
    private const string GameItemPayloadType = "RYNTH_GAME_ITEM";

    /// <summary>
    /// Makes the last slot button a drop target: RynthCore Inventory drags on any slot, game
    /// inventory drags on <paramref name="empty"/> slots.
    /// </summary>
    private unsafe void AcceptItemDrop(int slot, bool empty)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(ItemPayloadType);
        if (payload.NativePtr == null && empty) payload = ImGui.AcceptDragDropPayload(GameItemPayloadType);
        if (payload.NativePtr != null && payload.Data != IntPtr.Zero && payload.DataSize >= sizeof(uint))
        {
            uint objectId = *(uint*)payload.Data;
            _hud.Post(() => _hud.SetSlotFromItemId(slot, objectId));
        }
        ImGui.EndDragDropTarget();
    }

    private string EmptySlotTip(int index)
    {
        string candidate = _hud.AssignCandidate;
        return candidate.Length > 0
            ? $"Slot {index + 1:00}: empty\nClick to assign {candidate}.\nOr drag an item here from the game inventory or the RynthCore Inventory.\nCtrl+wheel: box size."
            : $"Slot {index + 1:00}: empty\nClick an item in your pack, then click here to assign it.\nOr drag an item here from the game inventory or the RynthCore Inventory.\nCtrl+wheel: box size.";
    }

    /// <summary>Per-slot right-click menu (opened inside the slot's ID scope).</summary>
    private void RenderSlotMenu(int index, HudItemEntry e)
    {
        if (!ImGui.BeginPopup(SlotPopup)) return;
        _slotMenuIndex = index;
        SafeImGui.TextDisabled(e.IsEmpty ? $"Slot {index + 1:00}" : $"Slot {index + 1:00}: {e.Name}");
        string candidate = _hud.AssignCandidate;
        if (ImGui.MenuItem(candidate.Length > 0 ? $"Assign {candidate}" : "Assign selected item"))
            _hud.Post(() => _hud.SetSlotFromSelection(index));
        if (!e.IsEmpty && ImGui.MenuItem("Clear slot")) _hud.Post(() => _hud.ClearSlot(index));
        ImGui.EndPopup();
    }

    private void RenderToggles(LegacyUiSettings settings)
    {
        var dash = _hud.Dashboard;
        if (!ImGui.BeginTable("##mrtoggles", 3, ImGuiTableFlags.SizingStretchSame)) return;
        Toggle("Macro", settings.IsMacroRunning, _ => dash.TogglePanelMacro());
        Toggle("Combat", settings.EnableCombat, on => dash.SetSubsystemEnabled(0, on));
        Toggle("Buff", settings.EnableBuffing, on => dash.SetSubsystemEnabled(1, on));
        Toggle("Nav", settings.EnableNavigation, on => dash.SetSubsystemEnabled(2, on));
        Toggle("Loot", settings.EnableLooting, on => dash.SetSubsystemEnabled(3, on));
        Toggle("Meta", settings.EnableMeta, on => dash.SetSubsystemEnabled(4, on));
        Toggle("Pet", settings.SummonPets, on => { settings.SummonPets = on; dash.SaveSettings(); });
        ImGui.EndTable();
    }

    /// <summary>A checkbox in the next table cell whose change is applied on the pump thread.</summary>
    private void Toggle(string label, bool current, Action<bool> apply)
    {
        ImGui.TableNextColumn();
        bool v = current;
        if (ImGui.Checkbox(label + "##mrt", ref v)) _hud.Post(() => apply(v));
    }

    private void RenderRebuff()
    {
        var dash = _hud.Dashboard;
        ImGui.TextColored(ColLabel, "Rebuff:");
        float w = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        w = Math.Max(w, 70f);
        if (ImGui.Button("FB", new Vector2(w, 0))) _hud.Post(dash.RequestForceRebuff);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Force rebuff: recast every buff now.");
        ImGui.SameLine();
        if (ImGui.Button("CFB", new Vector2(w, 0))) _hud.Post(dash.RequestCancelForceRebuff);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cancel the running rebuff.");
    }

    /// <summary>
    /// The Options popup. <paramref name="embedded"/>: drawn in an engine surface, where the
    /// floating window's lock and hide don't apply. The dashboard drawer offers the floating
    /// window as a separate choice; the engine's Mini Remote window replaces it, so it doesn't.
    /// </summary>
    private void RenderOptions(HudState s, bool embedded)
    {
        if (_openOptions)
        {
            _openOptions = false;
            ImGui.OpenPopup(OptionsPopup);
        }
        if (!ImGui.BeginPopup(OptionsPopup)) return;
        ImGui.TextDisabled("Mini Remote sections");
        Flag("Session stats", ref s.MiniShowStats);
        Flag("Attack target", ref s.MiniShowTarget);
        Flag("Pet / summon", ref s.MiniShowPet);
        Flag("Pet capture", ref s.MiniShowCapture);
        Flag("Item slots", ref s.MiniShowGems);
        // The drawer's dashboard has these switches already; the setting is for the windows.
        if (!InDrawer) Flag("Toggles", ref s.MiniShowToggles);
        Flag("Bank", ref s.MiniShowBank);
        Flag("Rebuff", ref s.MiniShowRebuff);
        Flag("Translate", ref s.MiniShowTranslate);
        Flag("Guardian answer", ref s.MiniShowGuardian);
        ImGui.Separator();
        Flag("Horizontal layout", ref s.MiniRemoteHorizontal);
        RenderSlotSizeSlider(s);
        if (embedded && _inWindow)
        {
            // Docked Options used to jump straight to Settings; undocked left-click opens this
            // popup, so keep a Settings entry here.
            if (_popped && _hud.Host.HasInvokeChatParser
                && ImGui.MenuItem("RynthAi Settings...", MovedTabs.Settings))
                _hud.RunEngineCommand(MovedTabs.Settings);
            if (_hud.Host.HasInvokeChatParser)
            {
                if (ImGui.MenuItem(_popped ? "Dock Mini Remote" : "Undock Mini Remote",
                        _popped ? "/rc miniremote dock" : "/rc miniremote undock"))
                    _hud.RunEngineCommand(_popped ? "/rc miniremote dock" : "/rc miniremote undock");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(_popped
                        ? "Dock back into the game window."
                        : "Pop out so this Options menu has more room.");
            }
            if (ImGui.MenuItem("Inventory HUDs setup...")) s.ShowSetup = true;
        }
        else if (embedded)
        {
            Flag("Floating window too", ref s.ShowMiniRemote);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Also show the Mini Remote as its own window (/ra remote).");
            if (ImGui.MenuItem("Inventory HUDs setup...")) s.ShowSetup = true;
        }
        else
        {
            Flag("Lock position", ref s.MiniRemoteLocked);
            if (ImGui.MenuItem("Inventory HUDs setup...")) s.ShowSetup = true;
            if (ImGui.MenuItem("Hide Mini Remote")) s.ShowMiniRemote = false;
        }
        ImGui.EndPopup();
    }

    /// <summary>"Slot size" slider plus a reset, the menu twin of Ctrl+wheel over the slots.</summary>
    private static void RenderSlotSizeSlider(HudState s)
    {
        float size = SlotSize(s);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderFloat("Slot size##mrslotsize", ref size, HudState.MinMiniRemoteSlotSize, HudState.MaxMiniRemoteSlotSize, "%.0f px"))
            s.MiniRemoteSlotSize = MathF.Round(size);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Item slot box size (or Ctrl+wheel over the slots).");
        ImGui.SameLine();
        bool isDefault = s.MiniRemoteSlotSize == HudState.DefaultMiniRemoteSlotSize;
        if (isDefault) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Default##mrslotsize")) s.MiniRemoteSlotSize = HudState.DefaultMiniRemoteSlotSize;
        if (isDefault) ImGui.EndDisabled();
    }

    // ── Right-click menu: Character, then this server's plugin windows ─────────

    /// <summary>RynthCore's server id for Infinite Leaftide (UI/ServerMenus.cs), the one RynthILT has tabs for.</summary>
    private const string IltServerId = "ilt";

    /// <summary>
    /// The right-click menu. Top, "Character": windows every server has. Bottom: the server
    /// section, i.e. the installed server plugins' windows for the world you're on (RynthCore
    /// sends them, see <see cref="ServerMenuModel"/>), or the "Treat this world as ..." choices
    /// when none matches.
    /// </summary>
    private void RenderMenu(HudState s)
    {
        if (!ImGui.BeginPopup(MenuPopup)) return;
        try
        {
            RenderCharacterMenu(s);
            RenderServerMenu(_hud.Remote());
        }
        finally { ImGui.EndPopup(); }
    }

    /// <summary>
    /// "Character": RynthAi's own windows, the universal Mini Remote tabs (Pets, Crafting), RynthOracle's
    /// quest and fellowship windows (the Quests and Fellowship tabs moved there 2026-10-13) and
    /// RynthCore's panels, when chat commands can be run.
    /// </summary>
    private void RenderCharacterMenu(HudState s)
    {
        ImGui.TextDisabled("Character");
        var settings = _hud.Dashboard.Settings;
        if (ImGui.MenuItem("Spells##mrspells", "/ra spells", settings.ShowSpellsWindow))
            settings.ShowSpellsWindow = !settings.ShowSpellsWindow;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The spell catalog and buff profiles.");
        foreach (string id in MiniRemoteTabs.Universal)
        {
            if (ImGui.MenuItem(MiniRemoteTabs.Label(id) + "##mrchar_" + id, "/ra hub open " + id))
                _hud.HandleTabCommand(id, "show");
        }
        if (_hud.Host.HasInvokeChatParser)
        {
            // RynthOracle's commands (it toggles its own windows; without RynthOracle nothing answers "/ro").
            // The character, its session and charms are RynthOracle's Character tab (2026-10-13).
            if (ImGui.MenuItem("Character##mrcharacter", MovedTabs.OracleCharacter)) _hud.RunEngineCommand(MovedTabs.OracleCharacter);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Character tab: the character, this session and the ILT charms list.");
            // Every character's saved packs / storage: RynthInventory's Global Inventory.
            // On ILT the old "Banking" label still opens the same window (/ginv bank).
            if (ImGui.MenuItem("Global Inventory##mrginv", MovedTabs.GlobalInventory))
                _hud.RunEngineCommand(MovedTabs.GlobalInventory);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Global Inventory (/ginv): every character's saved packs and storage.");
            if (IltActive(_hud.Remote()))
            {
                if (ImGui.MenuItem("Banking##mrbanking", MovedTabs.GlobalBank)) _hud.RunEngineCommand(MovedTabs.GlobalBank);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Global Inventory (/ginv bank): every character's saved packs and storage (banks when Include Storage is on).");
                // ILT pet breeding: RynthOracle's tab, fed by RynthILT (the Pets tab's Breeding sub-tab is the same tracker).
                if (ImGui.MenuItem("Pet Breeding##mrbreeding", MovedTabs.OracleBreeding)) _hud.RunEngineCommand(MovedTabs.OracleBreeding);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Pet Breeding tab: essences ready to breed, rest and charge timers, growth, boosters, supplies and this server's breeding rules.");
            }
            if (ImGui.MenuItem("Quests##mrquests", "/ro")) _hud.RunEngineCommand("/ro");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle: quest timers, tracking, rewards and the /qb list.");
            if (ImGui.MenuItem("Quest Timers##mrqtimers", "/ro timers")) _hud.RunEngineCommand("/ro timers");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Quest Timers window: your tracked quests and the time left on each.");
            if (ImGui.MenuItem("Fellowship##mrfellow", "/ro fellowship")) _hud.RunEngineCommand("/ro fellowship");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Fellowship tab: members, levels, XP sharing, and ILT's /fship buttons on Infinite Leaftide.");
            // Skills live under RynthOracle (Skills / Attributes / Progress tabs).
            if (ImGui.MenuItem("Skills##mrskills", "/ro skills")) _hud.RunEngineCommand("/ro skills");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Skills tab: raise and train skills.");
            if (ImGui.MenuItem("Attributes##mrattrs", "/ro attrs")) _hud.RunEngineCommand("/ro attrs");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Attributes tab: attributes and vitals.");
            if (ImGui.MenuItem("Progress##mrprogress", "/ro progress")) _hud.RunEngineCommand("/ro progress");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Progress tab: ILT progression planners.");
            if (ImGui.MenuItem("Inventory##mrinv", "/rc inventory")) _hud.RunEngineCommand("/rc inventory");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("This character's packs (Inventory panel). Count / GInv buttons there open Item Count and Global Inventory.");
            if (ImGui.MenuItem("Item Count##mritemcount", "/rc itemcount")) _hud.RunEngineCommand("/rc itemcount");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Item Count panel (/rc itemcount): how many of each named item you carry. Distinct from the floating pack item count HUD.");
        }
        if (ImGui.MenuItem("Inventory HUDs setup...##mrsetup")) s.ShowSetup = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show / hide the floating pack item count HUD and the Mini Remote (/ra huds).");
    }

    /// <summary>
    /// The server section. Each section RynthCore marks active is listed, and so is Infinite
    /// Leaftide while this character's "Treat this world as ILT" is on (RynthILT's override).
    /// Without RynthCore's list it falls back to RynthILT's own ILT entries.
    /// </summary>
    private void RenderServerMenu(MiniRemoteController? remote)
    {
        ServerMenuGroup[]? groups = ServerMenuModel.Groups;
        if (groups == null)
        {
            remote?.Ilt.RenderSection(RynthIltHub.SectionServerMenu);
            return;
        }
        if (groups.Length == 0) return;   // no server plugin installed

        bool any = false;
        foreach (ServerMenuGroup g in groups)
        {
            bool ilt = g.Id.Equals(IltServerId, StringComparison.OrdinalIgnoreCase);
            bool forcedHere = ilt && remote?.Ilt.ForceIlt == true;
            if (!g.Active && !forcedHere) continue;
            any = true;
            ImGui.Separator();
            SafeImGui.TextDisabled("Server: " + (g.Active ? g.Name : g.Name + " (forced)"));
            foreach (ServerMenuEntry item in g.Items)
            {
                string? tab = ServerMenuModel.TabOf(item.Feature);
                // Universal tabs are under Character; RynthCore 2026.10.5.30 and older list them here too
                // (with the retired Quests tab, now RynthOracle's).
                if (tab != null && (MiniRemoteTabs.Parse(tab) != null || tab.Equals("quests", StringComparison.OrdinalIgnoreCase))) continue;
                // Character / Charms / Banking (an older RynthCore still lists them) moved out: they are
                // under Character above (RynthOracle's Character tab, Global Inventory).
                if (tab != null && MovedTabs.CommandFor(tab) != null) continue;
                bool enabled = tab == null || TabAvailable(remote, tab);
                if (ImGui.MenuItem(item.Label + "##mrsrv_" + g.Id, item.Command, false, enabled))
                    RunServerItem(item, tab);
                if (item.Tooltip.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    SafeImGui.SetTooltip(enabled ? item.Tooltip : item.Tooltip + "\nShows once the server reports this feature on.");
            }
            if (ilt) remote?.Ilt.RenderSection(RynthIltHub.SectionIltStatus);
        }
        if (any) return;

        ImGui.Separator();
        ImGui.TextDisabled("Server: no installed server plugin matches this world");
        // Forcing a server lives in one place since 2026-10-13: RynthCore's Options > Diagnostics.
        if (_hud.Host.HasInvokeChatParser && ImGui.MenuItem("Force a server... (Options > Diagnostics)##mrforce", MovedTabs.Diagnostics))
            _hud.RunEngineCommand(MovedTabs.Diagnostics);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("RynthCore's server selection (Options > Diagnostics) shows a server's windows here anyway.\n"
                                 + "It only says which world this is: the server still decides which features exist.");
    }

    /// <summary>A Mini Remote tab switches here and now; anything else is a RynthCore command.</summary>
    private void RunServerItem(ServerMenuEntry item, string? tab)
    {
        if (tab != null) _hud.HandleTabCommand(tab, "show");
        else if (item.Command.Length > 0) _hud.RunEngineCommand(item.Command);
    }

    /// <summary>RynthILT reports that ILT tab's feature on (false without RynthILT).</summary>
    private static bool TabAvailable(MiniRemoteController? remote, string tab)
    {
        if (remote == null) return false;
        foreach (IltTabInfo t in remote.Ilt.Tabs())
            if (string.Equals(t.Id, tab, StringComparison.OrdinalIgnoreCase)) return t.Available;
        return false;
    }

    private static void Flag(string label, ref bool value)
    {
        bool v = value;
        if (ImGui.Checkbox(label, ref v)) value = v;
    }
}
