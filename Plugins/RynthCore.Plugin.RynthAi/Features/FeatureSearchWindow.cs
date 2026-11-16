// ============================================================================
//  RynthAi - Features/FeatureSearchWindow.cs
//  The Find a Feature window (2026-11-13): a search box (focused when the
//  window opens), the count line, and the results: each with its name, the
//  plugin it is in, what it does, where to find it (the button path), its
//  command (Copy puts it on the clipboard) and Open when the catalog says how
//  to open it. No words typed: the groups, each a header to fold open.
//
//  RynthCore hosts the window (the sections manifest's "featuresearch" window,
//  plugin API v89: header, pop-out, resize, remembered place; no bar button)
//  and calls Draw through RynthPluginRenderEmbed. AC's render thread: the
//  catalog is read once and never changes; Open goes to the tick as a chat
//  command. Looks: Shared/RynthUi (the player's theme), as Active Gear.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Features;

internal sealed class FeatureSearchWindow
{
    private readonly Func<IReadOnlyList<FeatureEntry>> _entries;
    private readonly Action<string> _run;

    private string _query = string.Empty;
    private string? _builtFor;
    private List<FeatureHit> _hits = new();
    private List<FeatureGroup> _groups = new();
    private readonly HashSet<string> _openGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _focus = true;
    private bool _scrollTop;
    private string _copied = string.Empty;
    private long _copiedAtMs;

    private float _k = 1f;
    private float Px(float v) => v * _k;

    private const string HelpText =
        "Type what you are looking for: every word has to match, in any order (\"death hunt\", \"vend\", \"loot rares\").\n\n" +
        "Each result says where to find it (the buttons to click, from the RynthCore bar, the RynthAi window's ⋯ menu " +
        "or a window's title), its chat command, and Open when it can open the place for you.\n\n" +
        "Clear the box to browse everything by group.\n\n" +
        "/ra find <words> opens this window with the words in the box.";

    /// <param name="entries">The catalog (read once).</param>
    /// <param name="run">Runs an Open command (a chat command, on the tick).</param>
    public FeatureSearchWindow(Func<IReadOnlyList<FeatureEntry>> entries, Action<string> run)
    {
        _entries = entries;
        _run = run;
    }

    /// <summary>The words in the box (/ra find's words); the box gets the keyboard on the next draw.</summary>
    public void SetQuery(string query)
    {
        _query = query ?? string.Empty;
        _focus = true;
        _scrollTop = true;
    }

    /// <summary>The window was off screen and is back: the box gets the keyboard.</summary>
    public void Reopened() => _focus = true;

    /// <summary>Draws the window body into the current ImGui window and returns the size it would like. Render thread.</summary>
    public Vector2 Draw()
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui11).FontSize / 11f);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        var want = new Vector2(Px(560), Px(460));
        if (size.X < 60 || size.Y < 40) return want;
        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        using (PushFont(RynthUiFont.Ui11))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
            ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
            ImGui.Indent(6);
            float w = size.X - 12;
            DrawTop(w);
            Build();
            ImGui.PushTextWrapPos(ImGui.GetCursorScreenPos().X + w);
            Label(FeatureSearch.CountLine(Words() ? _hits.Count : _entries().Count, _query) + ".", Mute);
            ImGui.PopTextWrapPos();

            // The host's resize grip sits over the bottom-right corner: the rows end above it.
            float bodyH = Math.Max(Px(30), origin.Y + size.Y - 6 - ImGui.GetCursorScreenPos().Y - Px(10));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Field);
            ImGui.BeginChild("##fs_rows", new Vector2(w, bodyH));
            if (_scrollTop) { ImGui.SetScrollY(0); _scrollTop = false; }
            ImGui.Indent(Px(6));
            ImGui.Dummy(new Vector2(0, Px(2)));
            if (_entries().Count == 0) Label("The feature list didn't load (the RynthCore log says why).", Highlight);
            else if (Words()) DrawHits();
            else DrawGroups();
            ImGui.Unindent(Px(6));
            ImGui.EndChild();
            ImGui.PopStyleColor();

            ImGui.Unindent(6);
            ImGui.PopStyleVar();
        }
        frame.AddRect(origin, origin + size, Border, 4);
        return want;
    }

    private bool Words() => FeatureSearch.Words(_query).Count > 0;

    // ── Top: the box, Clear, help ───────────────────────────────────────

    private void DrawTop(float w)
    {
        Vector2 start = ImGui.GetCursorScreenPos();
        float h = Px(22), iconW = Px(22);
        float clearW = ButtonWidth("Clear");
        float boxW = Math.Max(Px(60), w - clearW - iconW - Px(8));
        ImGui.SetNextItemWidth(boxW);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Field);
        if (_focus)
        {
            ImGui.SetKeyboardFocusHere();
            _focus = false;
        }
        if (ImGui.InputTextWithHint("##fs_query", "Find a feature: type a word (death, vendor, buffs, loot...)", ref _query, 96u))
            _scrollTop = true;
        ImGui.PopStyleColor();
        float x = start.X + boxW + Px(4);
        if (Button("##fs_clear", "Clear", new Vector2(x, start.Y), new Vector2(clearW, h), Text, ButtonFill, enabled: _query.Length > 0))
        {
            _query = string.Empty;
            _focus = true;
            _scrollTop = true;
        }
        ImGui.SetItemTooltip("Clear the box: every feature, by group.");
        x += clearW + Px(4);
        IconButton("##fs_help", RynthIcons.Info, new Vector2(x, start.Y), new Vector2(iconW, h), Mute, ButtonFill, font: RynthUiFont.Ui11);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
            ImGui.TextUnformatted(HelpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h + Px(4)));
        ImGui.Dummy(Vector2.Zero);
    }

    private void Build()
    {
        if (_builtFor == _query) return;
        _builtFor = _query;
        IReadOnlyList<FeatureEntry> all = _entries();
        _hits = FeatureSearch.Search(all, _query);
        if (_groups.Count == 0 && all.Count > 0) _groups = FeatureSearch.Grouped(all);
    }

    // ── Results ─────────────────────────────────────────────────────────

    private void DrawHits()
    {
        if (_hits.Count == 0)
        {
            ImGui.PushTextWrapPos(0);
            Label("Nothing matches every word. Try fewer words, or the start of one (\"vend\" finds Vendors).", Mute);
            ImGui.PopTextWrapPos();
            return;
        }
        for (int i = 0; i < _hits.Count; i++) DrawEntry(_hits[i].Entry, i);
    }

    private void DrawGroups()
    {
        int n = 0;
        foreach (FeatureGroup g in _groups)
        {
            ImGui.PushID("g_" + g.Name);
            bool open = _openGroups.Contains(g.Name);
            Vector2 p = ImGui.GetCursorScreenPos();
            float w = Math.Max(1, ImGui.GetContentRegionAvail().X - Px(4));
            float h = Px(22);
            bool clicked = ImGui.InvisibleButton("##hdr", new Vector2(w, h));
            bool hot = ImGui.IsItemHovered();
            if (clicked)
            {
                if (open) _openGroups.Remove(g.Name); else _openGroups.Add(g.Name);
                open = !open;
            }
            SafeImGui.SetItemTooltip(open ? "Click to fold " + g.Name + " away." : "Click for the " + g.Name + " features.");
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(p, p + new Vector2(w, h), hot ? Selected : ButtonFill, 3);
            DrawCentered(dl, Font(RynthUiFont.Ui11), open ? RynthIcons.CaretDown : RynthIcons.CaretRight,
                new Vector2(p.X + Px(2), p.Y), new Vector2(Px(16), h), Mute);
            ImFontPtr bold = Font(RynthUiFont.UiBold11);
            dl.AddText(bold, bold.FontSize, new Vector2(p.X + Px(20), p.Y + (h - bold.FontSize) * 0.5f), Text, g.Name);
            string count = g.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            float cw = ImGui.CalcTextSize(count).X;
            dl.AddText(new Vector2(p.X + w - cw - Px(8), p.Y + (h - ImGui.GetFontSize()) * 0.5f), Mute, count);
            if (open)
            {
                ImGui.Dummy(new Vector2(0, Px(2)));
                foreach (FeatureEntry e in g.Entries) DrawEntry(e, n++);
            }
            ImGui.PopID();
        }
    }

    /// <summary>One feature: name and plugin tag, Copy and Open; what it does; where; the command.</summary>
    private void DrawEntry(FeatureEntry e, int index)
    {
        ImGui.PushID(index);
        ImGui.PushID(e.Id);
        float w = Math.Max(Px(120), ImGui.GetContentRegionAvail().X - Px(4));
        Vector2 p = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float lineH = Px(22);

        // The buttons on the name's line, at the right: Copy (the command), Open.
        bool hasOpen = e.Open.Length > 0, hasCommand = e.Command.Length > 0;
        float openW = hasOpen ? ButtonWidth("Open") : 0, copyW = hasCommand ? ButtonWidth("Copy") : 0;
        float bx = p.X + w;
        if (hasOpen)
        {
            bx -= openW;
            if (Button("##fs_open", "Open", new Vector2(bx, p.Y), new Vector2(openW, lineH), Text, AccentFill))
                _run(e.Open);
            SafeImGui.SetItemTooltip("Opens it: runs " + e.Open);
            bx -= Px(4);
        }
        if (hasCommand)
        {
            bx -= copyW;
            if (Button("##fs_copy", "Copy", new Vector2(bx, p.Y), new Vector2(copyW, lineH), Text, ButtonFill))
            {
                ImGui.SetClipboardText(e.Command);
                _copied = e.Id;
                _copiedAtMs = Environment.TickCount64;
            }
            bool justCopied = _copied == e.Id && Environment.TickCount64 - _copiedAtMs < 2500;
            SafeImGui.SetItemTooltip(justCopied ? "Copied: paste it in chat (Ctrl+V)." : "Copy the command: " + e.Command);
            bx -= Px(4);
        }

        // Name (bold) and the plugin's tag, clipped short of the buttons.
        ImFontPtr bold = Font(RynthUiFont.UiBold11);
        float ty = p.Y + (lineH - bold.FontSize) * 0.5f;
        float textRight = bx - Px(4);
        dl.PushClipRect(p, new Vector2(Math.Max(p.X + 1, textRight), p.Y + lineH), true);
        dl.AddText(bold, bold.FontSize, new Vector2(p.X, ty), Accent, e.Name);
        float nameW = bold.CalcTextSizeA(bold.FontSize, float.MaxValue, 0f, e.Name).X;
        if (e.Plugin.Length > 0)
            Badge(dl, new Vector2(p.X + nameW + Px(8), p.Y + (lineH - Px(14)) * 0.5f), Px(14), e.Plugin, RowAlt, Mute);
        dl.PopClipRect();
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + lineH + Px(1)));

        ImGui.PushTextWrapPos(p.X + w);
        if (e.Summary.Length > 0) Label(e.Summary, Text);
        foreach (string where in e.Where) Label("Where: " + where, Mute);
        if (hasCommand) Label("Command: " + e.Command, Highlight);
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0, Px(2)));
        Vector2 q = ImGui.GetCursorScreenPos();
        dl.AddLine(new Vector2(p.X, q.Y), new Vector2(p.X + w, q.Y), Border);
        ImGui.Dummy(new Vector2(0, Px(4)));
        ImGui.PopID();
        ImGui.PopID();
    }
}
