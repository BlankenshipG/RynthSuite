// MiniRemoteHud.cs — Floating Mini Remote (UtilityBelt-style) for RynthAi. Render thread only.
//
// The Mini Remote is the ILT Hub's main window: its Options menu (the Options button or a
// right-click) opens the Hub's section windows (Character, Quests, Pets, Banking, Gear, Games).
// The V/H button stacks the sections (vertical) or lays them out in three columns (horizontal).
//
// Sections (each can be hidden from the Options menu):
//   Stats    session time and per-hour XP / luminance / kills / coins / pyreals (ILT Hub rates)
//   Target   the creature combat is attacking: name, health bar, distance
//   Pet      the summon that is out (health bar, time left), else the next combat essence and
//            whether it is ready
//   Slots    5 × 6 quick-use item grid; click uses the item, right-click assigns / clears
//   Toggles  macro and subsystem switches (same as the dashboard buttons)
//   Bank     ILT bank balances (pyreals, luminance, keys, coins)
//   Rebuff   force rebuff / cancel rebuff
//   Translate chat translator on/off and the receive <-> send language swap
// All game actions are posted to the pump thread.
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.Huds;

internal sealed class MiniRemoteHud
{
    private const string OptionsPopup = "##miniremoteopts";
    private const string SlotPopup = "##miniremoteslot";
    private const float SlotSize = 28f;
    /// <summary>Slot button edge including its 1 px frame padding on each side.</summary>
    private const float SlotButtonSize = SlotSize + 2f;
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

    public MiniRemoteHud(HudController hud) => _hud = hud;

    /// <summary>The remote was not drawn this frame: check its position again on the next show.</summary>
    public void OnHidden() => _rescueChecked = false;

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

        var hub = _hud.Hub();
        RenderHeaderRow(s);
        if (s.MiniRemoteHorizontal) RenderHorizontal(s, hub);
        else RenderVertical(s, hub);

        // Right-click on the window body opens the options, unless a slot's own menu is open.
        if (_slotMenuIndex < 0 && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
            ImGui.OpenPopup(OptionsPopup);
        RenderOptions(s);
        _slotMenuIndex = -1;
        ImGui.End();
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    /// <summary>[V/H] and [Options] buttons. Fixed widths so the auto-resizing window can't grow from them.</summary>
    private static void RenderHeaderRow(HudState s)
    {
        if (ImGui.Button((s.MiniRemoteHorizontal ? "H" : "V") + "##mrlayout", new Vector2(HeaderButtonWidth, 0)))
            s.MiniRemoteHorizontal = !s.MiniRemoteHorizontal;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.MiniRemoteHorizontal ? "Horizontal: sections in columns. Click to stack them." : "Vertical: sections stacked. Click to lay them out in columns.");
        ImGui.SameLine();
        if (ImGui.Button("Options##mropts", new Vector2(HeaderButtonWidth, 0))) ImGui.OpenPopup(OptionsPopup);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("ILT Hub windows and Mini Remote settings (or right-click the remote).");
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
        float colW = ColumnWidth();
        _columnOpen = false;
        if (s.MiniShowStats || s.MiniShowTarget || s.MiniShowPet)
        {
            BeginColumn("##mrcolstatus", colW);
            RenderStatusSections(s, hub);
            ImGui.EndChild();
        }
        if (s.MiniShowGems || s.MiniShowToggles)
        {
            BeginColumn("##mrcolactions", colW);
            RenderActionSections(s);
            ImGui.EndChild();
        }
        if (s.MiniShowBank || s.MiniShowRebuff || s.MiniShowTranslate)
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

    /// <summary>Wide enough for the slot grid, and never narrower than MinColumnWidth.</summary>
    private static float ColumnWidth()
    {
        int cols = HudState.MiniRemoteColumns;
        float grid = cols * SlotButtonSize + (cols - 1) * ImGui.GetStyle().ItemSpacing.X;
        return Math.Max(MinColumnWidth, grid);
    }

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
        if (s.MiniShowToggles) RenderToggles(_hud.Dashboard.Settings);
    }

    private void RenderEconomySections(HudState s, IltHubController? hub)
    {
        if (s.MiniShowBank) RenderBank(hub);
        if (s.MiniShowRebuff) RenderRebuff();
        if (s.MiniShowTranslate && _hud.Translate is { } translate) translate.RenderHubSection();
    }

    // ── Sections ────────────────────────────────────────────────────────────

    private static bool IltActive(IltHubController? hub) => hub != null && hub.Options.IsIltLikeWorld;

    private static void Stat(string label, string value)
    {
        ImGui.TextColored(ColLabel, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    private static void RenderStats(IltHubController? hub)
    {
        if (!IltActive(hub)) { ImGui.TextDisabled("Session stats: ILT worlds only"); return; }
        var r = hub!.Rates.Summary;
        Stat("Time:", IltParse.Duration(DateTime.UtcNow - hub.Rates.SessionStartUtc));
        Stat("XP:", HudDraw.Compact(r.XpPerHour) + "/h");
        Stat("Lum:", HudDraw.Compact(r.LumPerHour) + "/h");
        Stat("Kills:", HudDraw.Compact(r.KillsPerHour) + "/h");
        Stat("Coins:", HudDraw.Compact(r.CoinsPerHour) + "/h");
        Stat("Pyr:", HudDraw.Compact(r.PyrealsPerHour) + "/h");
    }

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
        if (!IltActive(hub)) return;
        float w = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        w = Math.Max(w, 70f);
        if (ImGui.Button("Reset XP", new Vector2(w, 0))) _hud.Post(() => _hud.Hub()?.Rates.Reset());
        ImGui.SameLine();
        if (ImGui.Button("Report", new Vector2(w, 0))) _hud.Post(ReportToChat);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Print the session rates in your chat window (local only).");
    }

    /// <summary>One-line session summary written to local chat (pump thread).</summary>
    private void ReportToChat()
    {
        var hub = _hud.Hub();
        if (hub == null) return;
        var r = hub.Rates.Summary;
        string time = IltParse.Duration(DateTime.UtcNow - hub.Rates.SessionStartUtc);
        _hud.Chat($"[RynthAi] Session {time}: XP {HudDraw.Compact(r.XpPerHour)}/h, Lum {HudDraw.Compact(r.LumPerHour)}/h, "
                  + $"Kills {HudDraw.Compact(r.KillsPerHour)}/h ({r.Kills:N0}), Coins {HudDraw.Compact(r.CoinsPerHour)}/h, "
                  + $"Pyr {HudDraw.Compact(r.PyrealsPerHour)}/h");
    }

    private void RenderSlots(HudState s)
    {
        var slots = s.MiniRemoteSlots.ToArray();
        var pack = _hud.Pack;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 1));
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
                clicked = ImGui.ImageButton("##slot", tex, new Vector2(SlotSize, SlotSize), Vector2.Zero, Vector2.One, Vector4.Zero, tint);
            }
            else
            {
                string label = e.IsEmpty ? string.Empty : e.Name.Substring(0, 1).ToUpperInvariant();
                if (e.IsEmpty) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.13f, 0.18f, 0.6f));
                clicked = ImGui.Button(label + "##slot", new Vector2(SlotSize + 2, SlotSize + 2));
                if (e.IsEmpty) ImGui.PopStyleColor();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(e.IsEmpty ? $"Slot {i + 1:00}: empty\nRight-click to assign the selected item."
                                           : $"{e.Name}\n{count:N0} carried\nClick to use, right-click for options.");
            if (clicked && !e.IsEmpty)
            {
                var entry = e;
                _hud.Post(() => _hud.UseItem(entry));
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _slotMenuIndex = i;
                ImGui.OpenPopup(SlotPopup);
            }
            RenderSlotMenu(i, e);
            ImGui.PopID();
        }
        ImGui.PopStyleVar();
    }

    /// <summary>Per-slot right-click menu (opened inside the slot's ID scope).</summary>
    private void RenderSlotMenu(int index, HudItemEntry e)
    {
        if (!ImGui.BeginPopup(SlotPopup)) return;
        _slotMenuIndex = index;
        ImGui.TextDisabled(e.IsEmpty ? $"Slot {index + 1:00}" : $"Slot {index + 1:00}: {e.Name}");
        if (ImGui.MenuItem("Assign selected item")) _hud.Post(() => _hud.SetSlotFromSelection(index));
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

    private void RenderOptions(HudState s)
    {
        if (!ImGui.BeginPopup(OptionsPopup)) return;
        RenderHubMenu(_hud.Hub());
        ImGui.Separator();
        ImGui.TextDisabled("Mini Remote sections");
        Flag("Session stats", ref s.MiniShowStats);
        Flag("Attack target", ref s.MiniShowTarget);
        Flag("Pet / summon", ref s.MiniShowPet);
        Flag("Item slots", ref s.MiniShowGems);
        Flag("Toggles", ref s.MiniShowToggles);
        Flag("Bank", ref s.MiniShowBank);
        Flag("Rebuff", ref s.MiniShowRebuff);
        Flag("Translate", ref s.MiniShowTranslate);
        ImGui.Separator();
        Flag("Horizontal layout", ref s.MiniRemoteHorizontal);
        Flag("Lock position", ref s.MiniRemoteLocked);
        if (ImGui.MenuItem("Inventory HUDs setup...")) s.ShowSetup = true;
        if (ImGui.MenuItem("Hide Mini Remote")) s.ShowMiniRemote = false;
        ImGui.EndPopup();
    }

    /// <summary>
    /// ILT Hub group of the Options menu: one item per section window (checked when open, greyed
    /// out until the server reports the feature). Off ILT worlds it also offers the Force-ILT
    /// override and a server-options refresh.
    /// </summary>
    private static void RenderHubMenu(IltHubController? hub)
    {
        ImGui.TextDisabled("ILT Hub");
        if (hub == null) { ImGui.TextDisabled("Available after login."); return; }

        foreach (IltSection section in IltSections.All)
        {
            bool open = hub.IsSectionOpen(section);
            if (ImGui.MenuItem(IltSections.Label(section) + "##mrsec", string.Empty, open, hub.SectionAvailable(section)))
                hub.SetSectionOpen(section, !open);
        }
        ImGui.TextDisabled("Progression: Skills panel (Char right-click)");

        if (hub.Available) return;
        var o = hub.Options;
        ImGui.TextDisabled(!o.IsIltLikeWorld ? "Not an ILT world."
            : o.IsRefreshing || o.LastRefreshUtc == DateTime.MinValue ? "Checking server features..."
            : "No ILT features reported.");
        bool force = hub.ForceIltWorld;
        if (ImGui.Checkbox("Treat this world as ILT##mrforce", ref force)) hub.ForceIltWorld = force;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("World-identity override only: the Hub asks the server which features exist.\nIt never turns a feature on by itself.");
        if (ImGui.MenuItem("Refresh server features##mrrefresh")) hub.RequestOptionsRefresh();
    }

    private static void Flag(string label, ref bool value)
    {
        bool v = value;
        if (ImGui.Checkbox(label, ref v)) value = v;
    }
}
