using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort.Views;

/// <summary>
/// The RynthSort window: the auto-sort box and status, the job buttons, warnings, the rule list
/// (first match wins: enable, select to edit, add, move up or down, remove) and the settings.
/// Recorded on the pump thread, replayed by the engine.
/// </summary>
internal sealed class SortPanel
{
    private static readonly string[] Kinds = { "Class", "Name has" };

    private readonly SortController _c;
    private readonly Action _save;
    private int _selected = -1;

    /// <summary>Extra warnings the plugin knows about (the Lua script set to start too).</summary>
    public Func<IReadOnlyList<string>> Warnings = () => Array.Empty<string>();
    /// <summary>The character (blank before login).</summary>
    public Func<string> Character = () => string.Empty;

    public SortPanel(SortController c, Action save)
    {
        _c = c;
        _save = save;
    }

    public int Selected => _selected;

    public void Draw(UiWindow w)
    {
        if (Character().Length == 0)
        {
            w.TextDisabled("Log in to sort your packs.");
            return;
        }
        SortSettings s = _c.Data.Settings;
        List<SortRule> rules = _c.Data.Rules;

        w.Checkbox("Auto-sort", "auto", s.Auto, v => _c.SetAuto(v));
        w.Tooltip("New items in the main pack go to their pack after the settle time");
        w.SameLine();
        w.TextColored(_c.Paused ? UiColors.Amber : UiColors.Grey, _c.Status);
        if (_c.Planner.View.Waiting > 0)
        {
            w.SameLine();
            w.TextDisabled($"({_c.Planner.View.Waiting} waiting)");
        }

        w.Button("Sort main pack", "job.main", () => _c.StartJob(SortScope.Main));
        w.Tooltip("Sort the main pack now, without waiting for items to settle");
        w.SameLine();
        w.Button("Tidy all packs", "job.all", () => _c.StartJob(SortScope.All));
        w.Tooltip("Also move items that are in the wrong side pack");
        w.SameLine();
        w.Button("Empty side packs", "job.unpack", () => _c.StartUnpack(null));
        w.Tooltip("Move everything from your side packs into the main pack. Turns auto-sort off first, so nothing moves back.");
        w.Button("Clean packs", "job.clean", () => _c.StartJob(SortScope.Clean));
        w.Tooltip("Side-pack items no rule wants go to the main pack; items in the wrong side pack go to theirs");

        SortView view = _c.Planner.View;
        if (view.MainFull) w.TextColored(UiColors.Amber, "The main pack is full");
        foreach (int p in view.Full) w.TextColored(UiColors.Amber, $"Pack {p} is full");
        foreach (int p in view.Missing) w.TextColored(UiColors.Amber, $"A rule uses pack {p}, which you don't have");
        if (s.Auto && _c.LastBot is RynthAiActivity bot && (bot & RynthAiActivity.AutoCram) != 0 && (bot & RynthAiActivity.MacroRunning) != 0)
            w.TextColored(UiColors.Amber, "RynthAi's AutoCram also moves loot out of the main pack; turn one of them off");
        foreach (string warning in Warnings()) w.TextColored(UiColors.Amber, warning);

        if (w.CollapsingHeader("Rules (first match wins)", "rules", defaultOpen: true))
            DrawRules(w, rules, view);

        if (w.CollapsingHeader("Settings", "settings"))
        {
            w.SliderInt("Settle (s)", "settle", s.SettleSec, SortSettings.SettleMin, SortSettings.SettleMax, v => { s.SettleSec = v; _save(); });
            w.Tooltip("How long a new item sits in the main pack before it is moved");
            w.SliderInt("Gap (ms)", "gap", s.GapMs, SortSettings.GapMin, SortSettings.GapMax, v => { s.GapMs = v; _save(); });
            w.Tooltip("Auto-sort's pause between two moves, to leave the bot room. Sort, Tidy, Clean and Unpack don't wait.");
            w.Checkbox("Sort during fights", "combat", s.SortInCombat, v => { s.SortInCombat = v; _save(); });
            w.Tooltip("Off: nothing moves while RynthAi fights. On: moves go between its attacks and casts (never while one is unanswered)");
        }

        if (_c.Log.Count > 0)
        {
            w.Separator();
            foreach (string line in _c.Log) w.TextDisabled(line);
        }
    }

    private void DrawRules(UiWindow w, List<SortRule> rules, SortView view)
    {
        if (_selected >= rules.Count) _selected = rules.Count - 1;
        for (int i = 0; i < rules.Count; i++)
        {
            SortRule r = rules[i];
            int index = i;
            w.Checkbox("", "on." + i, r.On, v => { r.On = v; _save(); });
            w.Tooltip("Rule on or off");
            w.SameLine();
            string pack = r.Pack >= 1 && r.Pack <= view.Packs.Count ? $"pack {r.Pack} ({view.Packs[r.Pack - 1].Name})" : $"pack {r.Pack}";
            w.Selectable($"{i + 1}. {SortRules.Describe(r)} -> {pack}", "row." + i, _selected == i,
                () => _selected = _selected == index ? -1 : index);
        }
        if (rules.Count == 0) w.TextDisabled("No rules: nothing moves.");

        w.Button("Add rule", "rule.add", () =>
        {
            if (rules.Count >= SortStore.MaxRules) return;
            rules.Add(SortRule.ForClass(ObjectClasses.SpellComponent, 1));
            _selected = rules.Count - 1;
            _save();
        });
        if (_selected < 0 || _selected >= rules.Count) return;

        int sel = _selected;
        w.SameLine();
        w.Button("Up", "rule.up", () =>
        {
            if (sel <= 0) return;
            (rules[sel - 1], rules[sel]) = (rules[sel], rules[sel - 1]);
            _selected = sel - 1;
            _save();
        });
        w.SameLine();
        w.Button("Down", "rule.down", () =>
        {
            if (sel >= rules.Count - 1) return;
            (rules[sel + 1], rules[sel]) = (rules[sel], rules[sel + 1]);
            _selected = sel + 1;
            _save();
        });
        w.SameLine();
        w.Button("Remove", "rule.remove", () =>
        {
            rules.RemoveAt(sel);
            _selected = Math.Min(sel, rules.Count - 1);
            _save();
        });

        SortRule rule = rules[sel];
        w.SeparatorText($"Rule {sel + 1}");
        w.Combo("Match", "rule.kind", rule.Kind == RuleKind.Name ? 1 : 0, Kinds, v =>
        {
            rule.Kind = v == 1 ? RuleKind.Name : RuleKind.Class;
            _save();
        });
        if (rule.Kind == RuleKind.Name)
        {
            w.InputText("Name has", "rule.text", rule.Text, 64, v => { rule.Text = v; _save(); });
            w.Tooltip("Part of the item's name, any case");
        }
        else
        {
            int ci = ObjectClasses.ChoiceIndex(rule.Class);
            var labels = new List<string>(ObjectClasses.ChoiceLabels);
            if (ci < 0)
            {
                // A class the editor doesn't offer (set by hand or by the Lua script): keep it selectable.
                labels.Add(ObjectClasses.Label(rule.Class));
                ci = labels.Count - 1;
            }
            int keep = rule.Class;
            w.Combo("Class", "rule.class", ci, labels, v =>
            {
                rule.Class = v < ObjectClasses.Choices.Length ? ObjectClasses.Choices[v] : keep;
                _save();
            });
        }
        w.Combo("Pack", "rule.pack", Math.Max(rule.Pack - 1, 0), PackChoices(view, rule.Pack), v =>
        {
            rule.Pack = Math.Clamp(v + 1, 1, SortStore.MaxPack);
            _save();
        });
        w.Tooltip("Pack numbers are your side packs in order, top to bottom");
    }

    /// <summary>"Pack 1: Name (used/cap)" for each side pack, then "(none)" up to at least 7.</summary>
    internal static List<string> PackChoices(SortView view, int atLeast)
    {
        int n = Math.Min(Math.Max(Math.Max(view.Packs.Count, 7), atLeast), SortStore.MaxPack);
        var list = new List<string>(n);
        for (int i = 1; i <= n; i++)
        {
            if (i <= view.Packs.Count)
            {
                PackLine p = view.Packs[i - 1];
                list.Add($"Pack {i}: {p.Name} ({p.Used}/{p.Capacity})");
            }
            else list.Add($"Pack {i} (none)");
        }
        return list;
    }
}
