using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthThreads.Threads;
using RynthCore.Plugin.RynthThreads.Ui;

namespace RynthCore.Plugin.RynthThreads.Views;

/// <summary>
/// The RynthThreads window: a summary line and Export, then three tabs. Components: All presses
/// (the baseline), each component, each exact combination and No components, each opening to the
/// rate of every modifier seen in its presses (hits/presses, a 95% range while few, the rate
/// without it), modifiers per press and the results. Modifiers: each one's overall rate, its rate
/// with each component, its values by fragment level. Presses: the newest first, each opening to
/// its modifiers and raw chat lines.
/// Recorded on the pump thread, replayed by the engine.
/// </summary>
internal sealed class ThreadsPanel
{
    private enum Tab { Components, Modifiers, Presses }

    private const int MaxPressesShown = 60;
    private Tab _tab = Tab.Components;

    public Func<ThreadsReport> Report = () => new ThreadsReport();
    public Func<IReadOnlyList<Press>> Presses = () => Array.Empty<Press>();
    public Action Export = () => { };
    /// <summary>Extra status (pending writes, unreadable lines), or "".</summary>
    public Func<string> Notice = () => string.Empty;

    public void Draw(UiWindow w)
    {
        ThreadsReport r = Report();
        w.Text($"{r.Total} press{(r.Total == 1 ? "" : "es")}, {r.Usable} complete" + (r.Incomplete > 0 ? $", {r.Incomplete} incomplete" : "")
               + $" ({r.Characters} character{(r.Characters == 1 ? "" : "s")})");
        w.SameLine();
        w.SmallButton("Export CSV", "export", Export);
        w.Tooltip("Writes every press to a CSV file next to presses.jsonl (the folder is named in chat)");
        string notice = Notice();
        if (notice.Length > 0) w.TextColored(UiColors.Amber, notice);

        w.ToggleButton("Components", "tab.c", _tab == Tab.Components, () => _tab = Tab.Components, small: true);
        w.SameLine();
        w.ToggleButton("Modifiers", "tab.m", _tab == Tab.Modifiers, () => _tab = Tab.Modifiers, small: true);
        w.SameLine();
        w.ToggleButton("Presses", "tab.p", _tab == Tab.Presses, () => _tab = Tab.Presses, small: true);
        w.Separator();

        w.BeginChild("body", 0f, 0f, border: false);
        if (r.Total == 0)
            w.TextWrapped("No presses yet. Press a Raw Fragment at a Fragment Press: the result is read from chat and saved for every character on this PC.");
        else if (_tab == Tab.Components) DrawComponents(w, r);
        else if (_tab == Tab.Modifiers) DrawModifiers(w, r);
        else DrawPresses(w);
        w.EndChild();
    }

    // ── Components ──────────────────────────────────────────────────────────

    private static void DrawComponents(UiWindow w, ThreadsReport r)
    {
        w.TextWrapped("Each component gives a chance of each modifier. Rates are how often it came: hits/presses, "
                      + "with a 95% range while presses are few, and the rate without the component to compare.");
        if (r.All is { } all) DrawGroup(w, all, "c:(all)");
        if (r.Components.Count == 0)
            w.TextDisabled("No complete press with a component yet.");
        foreach (GroupReport c in r.Components) DrawGroup(w, c, "c:" + c.Name);
        foreach (GroupReport c in r.Combinations) DrawGroup(w, c, "k:" + c.Name);
        if (r.NoComponents is { } nc) DrawGroup(w, nc, "c:(none)");
    }

    private static void DrawGroup(UiWindow w, GroupReport g, string id)
    {
        if (!w.CollapsingHeader($"{g.Name}  ({g.Presses} press{(g.Presses == 1 ? "" : "es")})", id)) return;
        w.Indent(8f);
        if (g.Kind == GroupKind.All) w.TextDisabled("Every complete press: the baseline.");
        else if (g.Kind == GroupKind.Combination) w.TextDisabled("Presses with exactly these components.");
        else if (g.Kind == GroupKind.NoComponents) w.TextDisabled("The plain random draw: no component used.");
        w.TextDisabled(PressInference.ModifiersText(g));
        w.TextDisabled("Results: " + PressInference.ResultsText(g));
        foreach (Odds o in g.Odds) DrawOdds(w, PressInference.Display(o.Modifier), o, g);
        w.Unindent(8f);
    }

    /// <summary>"Guarded  75% (3/4, 30-95%); without: no data; x1.83 to x1.91", green when more likely.</summary>
    private static void DrawOdds(UiWindow w, string label, Odds o, GroupReport g)
    {
        var parts = new List<string> { $"{label}  {PressInference.OddsText(o)}" };
        string other = PressInference.OtherText(o, g.OtherLabel);
        if (other.Length > 0) parts.Add(other);
        if (o.Values.Length > 0) parts.Add(o.Values);
        string likely = PressInference.LikelyText(o, g);
        if (likely.Length > 0) parts.Add(likely);
        string line = string.Join("; ", parts);
        if (likely.Length > 0)
        {
            w.PushStyleColor(UiFormat.ColText, UiColors.Green);
            w.BulletText(line);
            w.PopStyleColor();
        }
        else w.BulletText(line);
        if (o.IsGroup && o.Split.Count > 0) w.TextDisabled("      " + PressInference.SplitText(o));
    }

    // ── Modifiers ───────────────────────────────────────────────────────────

    private static void DrawModifiers(UiWindow w, ThreadsReport r)
    {
        if (r.Modifiers.Count == 0) w.TextDisabled("No complete press yet.");
        foreach (ModifierReport m in r.Modifiers)
        {
            string tag = m.Tag.Length > 0 ? "  [" + m.Tag + "]" : "";
            if (!w.CollapsingHeader($"{PressInference.Display(m.Name)}  {PressInference.OddsText(m.Overall)}{tag}", "m:" + m.Name)) continue;
            w.Indent(8f);
            w.TextWrapped(m.Example);
            w.Text("All presses: " + PressInference.OddsText(m.Overall));
            if (m.NoComponents is { } nc && r.NoComponents is { } ncg) DrawOdds(w, "No components", nc, ncg);
            if (m.ByGroup.Count > 0)
            {
                w.SeparatorText("By component");
                foreach (var (g, o) in m.ByGroup) DrawOdds(w, g.Name, o, g);
            }
            if (m.IsGroup && m.Overall.Split.Count > 0)
            {
                w.SeparatorText("By material");
                foreach (var (mat, hits) in m.Overall.Split)
                    w.BulletText($"{mat}: {PressInference.OddsText(new Odds { Hits = hits, N = m.Overall.N })}");
            }
            if (m.Values.Length > 0) w.Text("Values: " + m.Values);
            if (m.ByLevel.Count > 1 || (m.ByLevel.Count == 1 && m.ByLevel[0].Level != null))
            {
                w.SeparatorText("By fragment level");
                foreach (var (level, count, values) in m.ByLevel)
                    w.BulletText($"{(level?.ToString(CultureInfo.InvariantCulture) ?? "?")}: {(values.Length > 0 ? values : "-")}  ({count} press{(count == 1 ? "" : "es")})");
            }
            w.Unindent(8f);
        }
    }

    // ── Presses ─────────────────────────────────────────────────────────────

    private void DrawPresses(UiWindow w)
    {
        IReadOnlyList<Press> presses = Presses();
        int shown = 0;
        for (int i = presses.Count - 1; i >= 0 && shown < MaxPressesShown; i--, shown++)
        {
            Press p = presses[i];
            if (!w.CollapsingHeader(PressTitle(p), "p:" + p.Id)) continue;
            w.Indent(8f);
            w.TextDisabled($"{p.Character} on {p.Server}, {p.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            if (p.Outcome.Length > 0) w.Text(p.Outcome);
            w.Text("Components: " + (p.ComponentsSeen ? p.ComponentsText : "(no line)"));
            foreach (GemModifier m in p.Modifiers) w.BulletText(m.Name + " - " + m.Description);
            if (p.ResultLevel != null || p.Entries != null)
                w.Text($"Level {p.ResultLevel?.ToString(CultureInfo.InvariantCulture) ?? "?"}, {p.Entries?.ToString(CultureInfo.InvariantCulture) ?? "?"} entries");
            if (!p.Complete) w.TextColored(UiColors.Amber, "Incomplete: " + EndText(p.End));
            w.SeparatorText("Chat lines");
            foreach (string line in p.Raw) w.TextDisabled(line);
            if (p.Extra.Count > 0)
            {
                w.SeparatorText("Other lines while it was open");
                foreach (string line in p.Extra) w.TextDisabled(line);
            }
            w.Unindent(8f);
        }
        if (presses.Count > MaxPressesShown)
            w.TextDisabled($"The newest {MaxPressesShown} of {presses.Count}; Export CSV has them all.");
    }

    internal static string PressTitle(Press p)
    {
        string lvl = p.FragmentLevel?.ToString(CultureInfo.InvariantCulture) ?? "?";
        string to = p.ResultLevel != null ? " -> " + p.ResultLevel.Value.ToString(CultureInfo.InvariantCulture) : "";
        string state = p.Complete ? $"{p.Modifiers.Count} modifiers" : "incomplete";
        return $"{p.TimeUtc.ToLocalTime():MM-dd HH:mm}  L{lvl}{to}  {p.ComponentsText}  ({state})";
    }

    private static string EndText(string end) => end switch
    {
        "timeout" => "the press's lines stopped before its Level line",
        "next use" => "another item was used before its Level line",
        "logout" => "logged out before its Level line",
        _ => end,
    };
}
