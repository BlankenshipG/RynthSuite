// MiniRemoteHud.cs — The Mini Remote (UtilityBelt-style) for RynthAi. Render thread only.
//
// One window with tabs: Remote (the sections below), the universal tabs Quests and Pets (every
// world), and on ILT worlds the ILT tabs Character, Banking, Gear and Games (IltHubUi draws
// their bodies; an ILT tab shows once the server reports its feature on). The tab bar's
// selection is saved (HudState.MiniRemoteTab) and "/ra hub open <section>" or RynthCore's
// dashboard switch it (RequestTab). The Guardian (ILT only) and Bounties are RynthCore ILT
// panels (the bar's ILT button).
// Right-click on the remote: "Character" (Spells, Quests, Pets, Skills, Inventory, Item Count,
// Inventory HUDs: windows every server has), then "Server: <name>", i.e. the installed server
// plugins' windows for the world you're on, which RynthCore sends (ServerMenuModel), or "Treat
// this world as ..." when none matches. The Options button holds the Mini Remote's own settings.
// The V/H button stacks the sections (vertical) or lays them out in three columns (horizontal).
//
// Remote sections (each can be hidden from the Options menu):
//   Stats    session time and per-hour XP / luminance / kills / coins / pyreals (ILT rates)
//   Target   the creature combat is attacking: name, health bar, distance
//   Pet      the summon that is out (health bar, time left), else the next combat essence and
//            whether it is ready
//   Slots    5 × 6 quick-use item grid; click uses the item (an empty slot takes the selected
//            pack item), right-click assigns / clears, and items dragged from the game's
//            inventory or the RynthCore Inventory window drop straight into a slot. Ctrl+wheel
//            over the grid (or Options > Slot size) grows / shrinks the boxes.
//   Toggles  macro and subsystem switches (same as the dashboard buttons; not in the drawer)
//   Bank     ILT bank balances (pyreals, luminance, keys, coins)
//   Rebuff   force rebuff / cancel rebuff
//   Translate chat translator on/off and the receive <-> send language swap
//   Guardian the last Temple guardian answer with a Give button (only while it is recent)
// All game actions are posted to the pump thread.
//
// RenderEmbedded draws the same sections and Options menu inside the engine's RynthAi dashboard
// (its Mini Remote drawer, opened by the Hub launcher's right-click or the drawer tab) and
// reports the size it wants; the drawer is sized from that. The drawer leaves out Toggles (the
// dashboard it sits on already has those switches) and the tabs (it is too narrow for them).
// RenderEmbedded(window: true) draws it in the engine's Mini Remote window instead (bar button,
// /rc miniremote): tabs and Toggles included, and the floating window stays hidden meanwhile
// (HudController), so that window is the one Mini Remote.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.Plugin.RynthAi.LegacyUi;

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
    // A tab switch asked for by a command or the engine (any thread), applied by the next tab bar;
    // null = none. Starts as the saved tab so the first tab bar restores it.
    private string? _pendingTab;
    private bool _pendingTabFromState = true;
    // This frame's tab bar showed the Remote tab (right-click Options belongs to it only).
    private bool _onRemoteTab = true;
    // The Options button was clicked this frame (the popup opens at window level).
    private bool _openOptions;
    // The ILT tabs offered this frame (reused, so a frame doesn't allocate).
    private readonly List<IltSection> _tabs = new();

    public MiniRemoteHud(HudController hud) => _hud = hud;

    /// <summary>The remote was not drawn this frame: check its position again on the next show.</summary>
    public void OnHidden() => _rescueChecked = false;

    /// <summary>
    /// Switches to tab <paramref name="tab"/> ("" = the remote, else an ILT tab id) on the next
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

        RenderBody(s, _hud.Hub(), allowTabs: true);

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
    public Vector2 RenderEmbedded(bool window = false)
    {
        var s = _hud.State;
        _slotMenuIndex = -1;
        _embedded = true;
        _inWindow = window;
        float startY = ImGui.GetCursorPosY();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 3));
        try
        {
            float width = RenderBody(s, _hud.Hub(), allowTabs: !InDrawer);

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
        }
    }

    // ── Tabs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tab bar (when <paramref name="allowTabs"/>: the universal tabs on every world, the ILT
    /// tabs on ILT worlds) and the selected tab: the remote, or a tab's body in a child of its
    /// fixed size. Returns the width the content wants (never narrower than the tab bar's labels).
    /// </summary>
    private float RenderBody(HudState s, IltHubController? hub, bool allowTabs)
    {
        _onRemoteTab = true;
        _tabs.Clear();
        if (allowTabs && hub != null)
        {
            foreach (IltSection section in IltSections.Tabs)
                if (hub.TabShown(section)) _tabs.Add(section);
        }
        if (_tabs.Count == 0)
        {
            RenderRemote(s, hub);
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
            if (BeginTab(RemoteTabLabel, TabFlags(IltSections.RemoteTab, pending)))
            {
                _onRemoteTab = true;
                s.MiniRemoteTab = IltSections.RemoteTab;
                RenderRemote(s, hub);
                width = Math.Max(width, EmbeddedWidth(s));
                ImGui.EndTabItem();
            }
            foreach (IltSection section in _tabs)
            {
                string id = IltSections.TabId(section);
                if (!BeginTab(TabLabels[(int)section], TabFlags(id, pending))) continue;
                s.MiniRemoteTab = id;
                Vector2 size = IltHubUi.TabSize(section);
                // The tab draws into a child of its own size; tables in it scroll inside it.
                ImGui.BeginChild(TabBodyIds[(int)section], size, ImGuiChildFlags.None, ImGuiWindowFlags.None);
                hub!.RenderTab(section);
                ImGui.EndChild();
                width = Math.Max(width, size.X);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        return width;
    }

    // Tab labels as NUL-terminated UTF-8 and the tab body child ids, indexed by (int)IltSection,
    // built once so the tab bar doesn't allocate per frame.
    private static readonly byte[] RemoteTabLabel = Utf8Z("Remote##mrtab");
    private static readonly byte[][] TabLabels = BuildPerSection(s => Utf8Z(IltSections.Label(s) + "##mrtab"));
    private static readonly string[] TabBodyIds = BuildPerSection(s => "##mrtabbody_" + IltSections.TabId(s));

    private static T[] BuildPerSection<T>(Func<IltSection, T> make)
    {
        var result = new T[(int)IltSection.Guardian + 1];
        foreach (IltSection section in IltSections.Tabs) result[(int)section] = make(section);
        return result;
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
        foreach (IltSection section in _tabs)
            w += style.ItemInnerSpacing.X + ImGui.CalcTextSize(IltSections.Label(section)).X + style.FramePadding.X * 2f;
        return w;
    }

    /// <summary>The Remote tab: the [V/H] [Options] row, then the sections in the chosen layout.</summary>
    private void RenderRemote(HudState s, IltHubController? hub)
    {
        RenderHeaderRow(s);
        if (s.MiniRemoteHorizontal) RenderHorizontal(s, hub);
        else RenderVertical(s, hub);
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
        if (s.MiniShowStats || s.MiniShowTarget || s.MiniShowPet) cols++;
        if (s.MiniShowGems || ShowToggles(s)) cols++;
        if (s.MiniShowBank || s.MiniShowRebuff || s.MiniShowTranslate || s.MiniShowGuardian) cols++;
        cols = Math.Max(1, cols);
        return cols * colW + (cols - 1) * ImGui.GetStyle().ItemSpacing.X;
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    /// <summary>[V/H] and [Options] buttons. Fixed widths so the auto-resizing window can't grow from them.</summary>
    private void RenderHeaderRow(HudState s)
    {
        if (ImGui.Button((s.MiniRemoteHorizontal ? "H" : "V") + "##mrlayout", new Vector2(HeaderButtonWidth, 0)))
            s.MiniRemoteHorizontal = !s.MiniRemoteHorizontal;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.MiniRemoteHorizontal ? "Horizontal: sections in columns. Click to stack them." : "Vertical: sections stacked. Click to lay them out in columns.");
        ImGui.SameLine();
        // Opened by RenderOptions: this row sits inside the Remote tab, whose ID the popup must not get.
        if (ImGui.Button("Options##mropts", new Vector2(HeaderButtonWidth, 0))) _openOptions = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mini Remote settings: sections, layout, slot size.\nRight-click the remote for the Character and server windows.");
    }

    private void RenderVertical(HudState s, IltHubController? hub)
    {
        RenderStatusSections(s, hub);
        RenderActionSections(s);
        RenderEconomySections(s, hub);
    }

    /// <summary>
    /// Three side-by-side columns: status (stats, target, pet), actions (slots, toggles) and
    /// economy (bank, rebuff, translate). Each column is a fixed-width child that grows only in
    /// height; widgets sized from the available width would otherwise make the auto-resizing
    /// window grow every frame.
    /// </summary>
    private void RenderHorizontal(HudState s, IltHubController? hub)
    {
        float colW = ColumnWidth(s);
        _columnOpen = false;
        if (s.MiniShowStats || s.MiniShowTarget || s.MiniShowPet)
        {
            BeginColumn("##mrcolstatus", colW);
            RenderStatusSections(s, hub);
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
            RenderEconomySections(s, hub);
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

    private void RenderStatusSections(HudState s, IltHubController? hub)
    {
        var combat = _hud.Combat;
        if (s.MiniShowStats) RenderStats(hub);
        if (s.MiniShowTarget) RenderTarget(combat);
        if (s.MiniShowPet) RenderPet(hub, combat);
        if (s.MiniShowStats) RenderStatButtons(hub);
    }

    private void RenderActionSections(HudState s)
    {
        if (s.MiniShowGems) RenderSlots(s);
        if (ShowToggles(s)) RenderToggles(_hud.Dashboard.Settings);
    }

    private void RenderEconomySections(HudState s, IltHubController? hub)
    {
        if (s.MiniShowBank) RenderBank(hub);
        if (s.MiniShowRebuff) RenderRebuff();
        if (s.MiniShowTranslate && _hud.Translate is { } translate) translate.RenderHubSection();
        if (s.MiniShowGuardian && IltActive(hub)) hub!.Guardian.RenderRemoteLine();
    }

    // ── Sections ────────────────────────────────────────────────────────────

    private static bool IltActive(IltHubController? hub) => hub != null && hub.Options.IsIltLikeWorld;

    private static void Stat(string label, string value)
    {
        ImGui.TextColored(ColLabel, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    /// <summary>
    /// Session stats on every world: the shared SessionStats (RynthTracker's code, so these match the
    /// Tracker panel), plus ILT coins and pyreals.
    /// </summary>
    private void RenderStats(IltHubController? hub)
    {
        var v = _hud.SessionStats?.View ?? RynthCore.Plugin.Shared.SessionStatsView.Empty;
        Stat("Time:", IltParse.Duration(TimeSpan.FromSeconds(SessionClock(v))));
        Stat("XP:", HudDraw.Compact(v.XpPerHour) + "/h");
        Stat("Lum:", HudDraw.Compact(v.LumPerHour) + "/h");
        Stat("Lum 5m:", HudDraw.Compact(v.LumLast5Min));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Luminance earned in the last five minutes.");
        if (v.PassUpTracked)
        {
            Stat("Pass-up:", HudDraw.Compact(v.PassUpPerHour) + "/h");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{v.LumPassUp:N0} this session.\n{IltSessionRates.PassUpTooltip}");
        }
        Stat("Kills:", HudDraw.Compact(v.KillsPerHour) + "/h");
        if (!IltActive(hub)) return;
        var r = hub!.Rates.Summary;
        Stat("Coins:", HudDraw.Compact(r.CoinsPerHour) + "/h");
        Stat("Pyr:", HudDraw.Compact(r.PyrealsPerHour) + "/h");
    }

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
            ImGui.TextDisabled($"{c.TargetDistance:F0}yd");
        }
        HealthBar("##mrtargethp", c.TargetHealth);
    }

    /// <summary>The live summon when one is out, else the ILT Hub's next-essence line.</summary>
    private static void RenderPet(IltHubController? hub, CombatHudSnapshot c)
    {
        if (c.PetId != 0)
        {
            RenderSummon(hub, c);
            return;
        }
        if (!IltActive(hub)) { ImGui.TextDisabled("Summon: none"); return; }
        string name = hub!.Pets.HudPetName;
        string status = hub.Pets.HudPetStatus;
        if (name.Length == 0) { ImGui.TextDisabled("No combat pet set"); return; }
        ImGui.TextUnformatted(name);
        var col = status switch { "Ready" => ColReady, "Out" => ColOut, "Healing" => ColBusy, _ => ColEmpty };
        ImGui.TextColored(col, status);
    }

    private static void RenderSummon(IltHubController? hub, CombatHudSnapshot c)
    {
        ImGui.TextColored(ColLabel, "Summon:");
        ImGui.SameLine();
        ImGui.TextUnformatted(c.PetName);
        if (IltActive(hub) && hub!.Pets.HudPetStatus == "Healing")
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
                ImGui.TextColored(left.TotalSeconds <= 30 ? ColEmpty : ColOut, text);
            }
        }
        else
        {
            // No RemainingLifespan from the server: show how long it has been out instead.
            var up = TimeSpan.FromMilliseconds(Math.Max(0, now - c.PetSeenAtMs));
            ImGui.TextDisabled($"Out {FormatClock(up)}");
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

    private void RenderStatButtons(IltHubController? hub)
    {
        float w = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        w = Math.Max(w, 70f);
        if (ImGui.Button("Reset XP", new Vector2(w, 0)))
            _hud.Post(() =>
            {
                _hud.SessionStats?.RequestReset();
                _hud.Hub()?.Rates.Reset();
            });
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Start the session stats over (the Tracker panel keeps its own session).");
        ImGui.SameLine();
        if (ImGui.Button("Report", new Vector2(w, 0))) _hud.Post(ReportToChat);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Print the session rates in your chat window (local only).");
    }

    /// <summary>One-line session summary written to local chat (pump thread).</summary>
    private void ReportToChat()
    {
        var v = _hud.SessionStats?.View ?? RynthCore.Plugin.Shared.SessionStatsView.Empty;
        string time = IltParse.Duration(TimeSpan.FromSeconds(SessionClock(v)));
        string line = $"[RynthAi] Session {time}: XP {HudDraw.Compact(v.XpPerHour)}/h, Lum {HudDraw.Compact(v.LumPerHour)}/h "
                      + $"({HudDraw.Compact(v.LumLast5Min)} last 5m)";
        if (v.PassUpTracked) line += $", Pass-up {HudDraw.Compact(v.PassUpPerHour)}/h";
        line += $", Kills {HudDraw.Compact(v.KillsPerHour)}/h ({v.Kills:N0})";
        var hub = _hud.Hub();
        if (IltActive(hub))
        {
            var r = hub!.Rates.Summary;
            line += $", Coins {HudDraw.Compact(r.CoinsPerHour)}/h, Pyr {HudDraw.Compact(r.PyrealsPerHour)}/h";
        }
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
                ImGui.SetTooltip(e.IsEmpty ? EmptySlotTip(i) : $"{e.Name}\n{count:N0} carried\nClick to use, right-click for options.\nCtrl+wheel: box size.");
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
        ImGui.TextDisabled(e.IsEmpty ? $"Slot {index + 1:00}" : $"Slot {index + 1:00}: {e.Name}");
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

    private static void RenderBank(IltHubController? hub)
    {
        if (!IltActive(hub) || hub!.Options.IsOff(IltFeature.Bank)) return;
        var b = hub.State.Bank;
        if (b.LastUpdated == DateTime.MinValue) { ImGui.TextDisabled("Bank: not loaded (Options > Banking)"); return; }
        Stat("P:", HudDraw.Compact(b.Pyreals));
        Stat("Lum:", HudDraw.Compact(b.Luminance));
        if (!ImGui.BeginTable("##mrbank", 2, ImGuiTableFlags.SizingStretchSame)) return;
        ImGui.TableNextColumn(); Stat("LK:", HudDraw.Compact(b.LegendaryKeys));
        ImGui.TableNextColumn(); Stat("MK:", HudDraw.Compact(b.MythicalKeys));
        ImGui.TableNextColumn(); Stat("EC:", HudDraw.Compact(b.EnlightenedCoins));
        ImGui.TableNextColumn(); Stat("WE:", HudDraw.Compact(b.WeaklyEnlightenedCoins));
        // Prestige Coins (ACECustom 2026-10-04): only once the server has reported some.
        if (b.PrestigeCoins > 0) { ImGui.TableNextColumn(); Stat("PC:", HudDraw.Compact(b.PrestigeCoins)); }
        ImGui.EndTable();
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

    /// <summary>RynthCore's server id for Infinite Leaftide (UI/ServerMenus.cs), the one RynthAi has features for.</summary>
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
            RenderServerMenu(_hud.Hub());
        }
        finally { ImGui.EndPopup(); }
    }

    /// <summary>
    /// "Character": RynthAi's own windows, the universal Mini Remote tabs (Quests, Pets), and
    /// RynthCore's panels when it runs /rc commands.
    /// </summary>
    private void RenderCharacterMenu(HudState s)
    {
        ImGui.TextDisabled("Character");
        var settings = _hud.Dashboard.Settings;
        if (ImGui.MenuItem("Spells##mrspells", "/ra spells", settings.ShowSpellsWindow))
            settings.ShowSpellsWindow = !settings.ShowSpellsWindow;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The spell catalog and buff profiles.");
        foreach (IltSection section in IltSections.Tabs)
        {
            if (!IltSections.IsUniversal(section)) continue;
            string id = IltSections.TabId(section);
            if (ImGui.MenuItem(IltSections.Label(section) + "##mrchar_" + id, "/ra hub open " + id))
                _hud.HandleTabCommand(id, "show");
        }
        if (_hud.Host.HasInvokeChatParser)
        {
            if (ImGui.MenuItem("Skills##mrskills", "/rc skills")) _hud.RunEngineCommand("/rc skills toggle");
            if (ImGui.MenuItem("Inventory##mrinv", "/rc inventory")) _hud.RunEngineCommand("/rc inventory");
            if (ImGui.MenuItem("Item Count##mritemcount", "/rc itemcount")) _hud.RunEngineCommand("/rc itemcount");
        }
        if (ImGui.MenuItem("Inventory HUDs setup...##mrsetup")) s.ShowSetup = true;
    }

    /// <summary>
    /// The server section. Each section RynthCore marks active is listed, and so is Infinite
    /// Leaftide while this character's "Treat this world as ILT" is on (RynthAi's own override).
    /// Without RynthCore's list it falls back to RynthAi's ILT entries.
    /// </summary>
    private void RenderServerMenu(IltHubController? hub)
    {
        ServerMenuGroup[]? groups = ServerMenuModel.Groups;
        if (groups == null)
        {
            RenderLocalIltMenu(hub);
            return;
        }
        if (groups.Length == 0) return;   // no server plugin installed

        bool any = false;
        foreach (ServerMenuGroup g in groups)
        {
            bool ilt = g.Id.Equals(IltServerId, StringComparison.OrdinalIgnoreCase);
            bool forcedHere = ilt && hub?.ForceIltWorld == true;
            if (!g.Active && !forcedHere) continue;
            any = true;
            ImGui.Separator();
            ImGui.TextDisabled("Server: " + (g.Active ? g.Name : g.Name + " (forced)"));
            foreach (ServerMenuEntry item in g.Items)
            {
                string? tab = ServerMenuModel.TabOf(item.Feature);
                // Universal tabs are under Character; RynthCore 2026.10.5.30 and older list them here too.
                if (tab != null && IltSections.TryParse(tab, out IltSection listed) && IltSections.IsUniversal(listed)) continue;
                bool enabled = tab == null || TabAvailable(hub, tab);
                if (ImGui.MenuItem(item.Label + "##mrsrv_" + g.Id, item.Command, false, enabled))
                    RunServerItem(item, tab);
                if (item.Tooltip.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(enabled ? item.Tooltip : item.Tooltip + "\nShows once the server reports this feature on.");
            }
            if (ilt) RenderIltStatus(hub);
        }
        if (any) return;

        ImGui.Separator();
        ImGui.TextDisabled("Server: no installed server plugin matches this world");
        foreach (ServerMenuGroup g in groups)
        {
            if (g.ForceCommand.Length == 0) continue;
            if (ImGui.MenuItem("Treat this world as " + g.Name + "##mrforce_" + g.Id, g.ForceCommand))
                ForceServer(g, hub);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shows this server's windows here anyway. It only says which world this is:\nthe server still decides which features exist.");
        }
    }

    /// <summary>A Mini Remote tab switches here and now; anything else is a RynthCore command.</summary>
    private void RunServerItem(ServerMenuEntry item, string? tab)
    {
        if (tab != null) _hud.HandleTabCommand(tab, "show");
        else if (item.Command.Length > 0) _hud.RunEngineCommand(item.Command);
    }

    /// <summary>
    /// Forces a server: RynthCore's session override, plus for Infinite Leaftide this character's
    /// "Treat this world as ILT" (so RynthAi's tabs come too).
    /// </summary>
    private void ForceServer(ServerMenuGroup g, IltHubController? hub)
    {
        if (g.Id.Equals(IltServerId, StringComparison.OrdinalIgnoreCase) && hub != null) hub.ForceIltWorld = true;
        _hud.RunEngineCommand(g.ForceCommand);
    }

    private static bool TabAvailable(IltHubController? hub, string tab) =>
        hub != null && IltSections.TryParse(tab, out IltSection section) && IltSections.IsTab(section) && hub.SectionAvailable(section);

    /// <summary>
    /// RynthAi's own Infinite Leaftide entries, for when RynthCore sends no server list (older
    /// RynthCore, or its ImGui layer off): the tabs, and the Guardian / Bounties panels.
    /// </summary>
    private void RenderLocalIltMenu(IltHubController? hub)
    {
        if (hub == null || !hub.OffersTabs) return;
        ImGui.Separator();
        ImGui.TextDisabled("Server: Infinite Leaftide");
        foreach (IltSection section in IltSections.Tabs)
        {
            if (IltSections.IsUniversal(section)) continue;   // under Character
            if (ImGui.MenuItem(IltSections.Label(section) + "##mrlocal", string.Empty, false, hub.SectionAvailable(section)))
                _hud.HandleTabCommand(IltSections.TabId(section), "show");
        }
        if (ImGui.MenuItem("Guardian##mrlocal", "/rc guardian", false, hub.SectionAvailable(IltSection.Guardian)))
            _hud.Post(() => hub.OpenEnginePanel("guardian", "Guardian", "show"));
        if (ImGui.MenuItem("Bounties##mrlocal", "/rc bounties"))
            _hud.Post(() => hub.OpenEnginePanel("bounties", "Bounties", "show"));
        RenderIltStatus(hub);
    }

    /// <summary>
    /// Under the Infinite Leaftide section while no ILT feature is on: why, this character's
    /// "Treat this world as ILT" override and a server-features refresh.
    /// </summary>
    private static void RenderIltStatus(IltHubController? hub)
    {
        if (hub == null || hub.Available) return;
        var o = hub.Options;
        ImGui.TextDisabled(!o.IsIltLikeWorld ? "Not an ILT world."
            : o.IsRefreshing || o.LastRefreshUtc == DateTime.MinValue ? "Checking server features..."
            : "No ILT features reported.");
        bool force = hub.ForceIltWorld;
        if (ImGui.Checkbox("Treat this world as ILT##mrforce", ref force)) hub.ForceIltWorld = force;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("World-identity override only: RynthAi asks the server which features exist.\nIt never turns a feature on by itself.");
        if (ImGui.MenuItem("Refresh server features##mrrefresh")) hub.RequestOptionsRefresh();
    }

    private static void Flag(string label, ref bool value)
    {
        bool v = value;
        if (ImGui.Checkbox(label, ref v)) value = v;
    }
}
