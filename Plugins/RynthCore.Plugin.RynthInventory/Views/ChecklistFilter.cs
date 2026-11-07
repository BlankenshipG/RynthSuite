using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthInventory.Scanning;
using RynthCore.Plugin.RynthInventory.Ui;

namespace RynthCore.Plugin.RynthInventory.Views;

/// <summary>
/// Global Inventory's own attribute filter (2026-10-24): the Inventory panel's checklist without
/// an item (LootSdk LootItemAttributes.ForBlank) drawn in the script window, so a search across
/// every character can require the same things a loot rule can: class, material, workmanship,
/// damage, ratings, T11 grades, requirements, and spells and cantrips picked from the standard
/// lists (LootSpellCatalog, from this plugin's spell table). Groups fold to keep the window's
/// display list small; a ticked row shows its comparison and value under it. Every change
/// rebuilds the rule (LootItemRules.BuildBlank) and the window filters with it at once.
/// </summary>
internal sealed class ChecklistFilter
{
    private readonly Dictionary<string, LootEditItemChoice> _choices = new();
    private readonly Dictionary<string, LootEditItemExtra> _extras = new();
    // One group open at a time keeps the window's display list well inside the engine's limits.
    private string _openGroup = LootItemAttributes.GroupItem;
    private List<LootEditItemAttribute> _rows = new();
    private List<string> _notes = new();
    private LootSpellCatalog? _catalog;
    private int _pickSchool, _pickFamily, _pickLevel = 6, _pickTier = 3, _pickCantrip;

    /// <summary>The rule's conditions now (empty: nothing ticked).</summary>
    public List<VTankLootCondition> Conditions { get; private set; } = new();

    /// <summary>Bumped on every change (the view rebuilds its filter).</summary>
    public int Version { get; private set; }

    public ChecklistFilter() => Rebuild();

    public void Clear()
    {
        _choices.Clear();
        _extras.Clear();
        Rebuild();
    }

    private void Rebuild()
    {
        LootItemRuleDraft d = LootItemRules.BuildBlank(new LootItemRuleOptions
        {
            Choices = new List<LootEditItemChoice>(_choices.Values),
            Extras = new List<LootEditItemExtra>(_extras.Values),
            Spells = Catalog,
        });
        _rows = d.Attributes.ConvertAll(r => r.ToWire());
        _notes = d.Notes;
        Conditions = new List<VTankLootCondition>(d.Rule.Conditions);
        Version++;
    }

    /// <summary>The standard spell lists from this plugin's spell table (built on first use).</summary>
    private LootSpellCatalog Catalog => _catalog ??= LootSpellCatalog.Build(SpellTable.AllSpells());

    private void Set(LootEditItemAttribute r, bool? on = null, int? op = null, string? value = null)
    {
        if (!_choices.TryGetValue(r.Id, out LootEditItemChoice? c))
            _choices[r.Id] = c = new LootEditItemChoice { Id = r.Id, On = r.On, Op = r.Op, Value = string.Empty };
        if (on.HasValue) c.On = on.Value;
        if (op.HasValue) c.Op = op.Value;
        if (value != null) c.Value = value;
        Rebuild();
    }

    private static string OpLabel(int op) => op switch
    {
        0 => "at least", 1 => "is", 2 => "at most", 3 => "is not", 4 => "has any", 5 => "any", 6 => "has all", 10 => "exact", 11 => "or better", 12 => "any tier", _ => "?",
    };

    public void Draw(UiWindow w)
    {
        string? group = null;
        foreach (LootEditItemAttribute r in _rows)
        {
            if (r.Group != group)
            {
                group = r.Group;
                string g = group;
                bool open = _openGroup == g;
                int ticked = _rows.FindAll(x => x.Group == g && x.On).Count;
                w.ToggleButton((open ? "- " : "+ ") + g + (ticked > 0 ? " (" + ticked.ToString(CultureInfo.InvariantCulture) + ")" : ""),
                    "cl.g." + g, open, () => _openGroup = _openGroup == g ? string.Empty : g, small: true);
                if (g == LootItemAttributes.GroupSpells && open) DrawSpellPickers(w);
            }
            if (_openGroup != r.Group && !r.On) continue;
            LootEditItemAttribute row = r;
            w.Checkbox(r.Label, "cl.on." + r.Id, r.On, v =>
            {
                if (!v && _extras.Remove(row.Id)) { _choices.Remove(row.Id); Rebuild(); }
                else Set(row, on: v);
            });
            if (r.Tip.Length > 0) w.Tooltip(r.Tip);
            if (!r.On) continue;
            // The comparison: one small toggle per choice, on the same line.
            if (r.Ops.Count > 1)
                foreach (int op in r.Ops)
                {
                    int o = op;
                    w.SameLine();
                    w.ToggleButton(OpLabel(o), "cl.op." + r.Id + "." + o.ToString(CultureInfo.InvariantCulture), r.Op == o, () => Set(row, op: o), small: true);
                }
            else if (r.Ops.Count == 1)
            {
                w.SameLine();
                w.TextDisabled(OpLabel(r.Ops[0]));
            }
            // The value: a list for named values, a box for numbers.
            if (r.Op == (int)LootItemAttributeOp.Any)
                w.TextDisabled("any value");
            else if (r.Flags && r.Op is (int)LootItemAttributeOp.HasAny or (int)LootItemAttributeOp.HasAll && r.Choices is { Count: > 0 })
            {
                // Several flags (multi imbues): a toggle per flag, the value their sum (2026-11-07).
                int cur = int.TryParse(r.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cv) ? cv : 0;
                int shown = 0;
                foreach (LootEditName c in r.Choices)
                {
                    if (c.Id == 0 || (c.Id & (c.Id - 1)) != 0) continue;   // single flags only
                    int bit = c.Id;
                    if (shown++ % 4 != 0) w.SameLine();
                    w.ToggleButton(c.Name, "cl.f." + r.Id + "." + bit.ToString(CultureInfo.InvariantCulture), (cur & bit) != 0, () =>
                    {
                        int next = (cur & bit) != 0 ? cur & ~bit : cur | bit;
                        if (next != 0) Set(row, value: next.ToString(CultureInfo.InvariantCulture), on: true);
                    }, small: true);
                }
            }
            else if (r.Choices != null && r.Choices.Count > 0)
            {
                var names = new List<string>(r.Choices.Count);
                int cur = int.TryParse(r.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cv) ? cv : int.MinValue;
                int index = 0;
                for (int i = 0; i < r.Choices.Count; i++)
                {
                    names.Add(r.Choices[i].Name);
                    if (r.Choices[i].Id == cur) index = i;
                }
                w.Combo("##" + r.Label, "cl.v." + r.Id, index, names, i =>
                {
                    if (i >= 0 && i < row.Choices!.Count) Set(row, value: row.Choices[i].Id.ToString(CultureInfo.InvariantCulture), on: true);
                });
            }
            else if (r.Editable)
                w.InputText("##" + r.Label, "cl.v." + r.Id, r.Value, 16, v => Set(row, value: v.Trim(), on: true));
        }
        foreach (string n in _notes) w.TextColored(UiColors.Stale, n);
    }

    private void DrawSpellPickers(UiWindow w)
    {
        LootSpellCatalog cat = Catalog;
        List<string> cantrips = cat.Cantrips;
        if (cat.Families.Count == 0) { w.TextDisabled("No spell table: the spell lists are empty."); return; }
        string[] schools = LootSpellCatalog.Schools;
        _pickSchool = Math.Clamp(_pickSchool, 0, schools.Length - 1);
        List<LootSpellFamily> fams = cat.Families.FindAll(f => f.School == schools[_pickSchool]);
        _pickFamily = fams.Count == 0 ? 0 : Math.Clamp(_pickFamily, 0, fams.Count - 1);
        LootSpellFamily? fam = fams.Count > 0 ? fams[_pickFamily] : null;

        w.TextDisabled("Add spell:");
        for (int i = 0; i < schools.Length; i++)
        {
            int s = i;
            w.SameLine();
            w.ToggleButton(schools[i], "cl.sch." + i.ToString(CultureInfo.InvariantCulture), _pickSchool == i, () => { _pickSchool = s; _pickFamily = 0; }, small: true);
        }
        var famNames = fams.ConvertAll(f => f.Name);
        w.Combo("Spell##fam", "cl.fam", _pickFamily, famNames, i => _pickFamily = i);
        if (fam != null)
        {
            int lo = Math.Max(1, fam.MinLevel), hi = Math.Min(8, fam.MaxLevel);
            _pickLevel = Math.Clamp(_pickLevel, lo, hi);
            var levels = new List<string>();
            for (int l = lo; l <= hi; l++) levels.Add("Level " + LootItemAttributes.RomanLevel(l));
            w.Combo("Level##lvl", "cl.lvl", _pickLevel - lo, levels, i => _pickLevel = lo + i);
            w.SameLine();
            LootSpellFamily f = fam;
            w.SmallButton("Add spell", "cl.addspell", () =>
            {
                var x = new LootEditItemExtra { Kind = LootEditItemExtra.Spell, Name = f.Name, Level = _pickLevel };
                _extras[LootItemAttributes.ExtraId(x)] = x;
                Rebuild();
            });
            w.Tooltip("Require this spell (Self or Other) at this level or better; pick exact or any tier in its row.");
        }

        if (cantrips.Count == 0) return;
        w.TextDisabled("Add cantrip:");
        string[] tiers = LootItemAttributes.CantripTiers;
        _pickTier = Math.Clamp(_pickTier, 0, tiers.Length - 1);
        for (int i = 0; i < tiers.Length; i++)
        {
            int t = i;
            w.SameLine();
            w.ToggleButton(tiers[i], "cl.tier." + i.ToString(CultureInfo.InvariantCulture), _pickTier == i, () => _pickTier = t, small: true);
        }
        _pickCantrip = Math.Clamp(_pickCantrip, 0, cantrips.Count - 1);
        w.Combo("Cantrip##can", "cl.can", _pickCantrip, cantrips, i => _pickCantrip = i);
        w.SameLine();
        w.SmallButton("Add cantrip", "cl.addcan", () =>
        {
            var x = new LootEditItemExtra { Kind = LootEditItemExtra.Cantrip, Name = cantrips[_pickCantrip], Level = _pickTier };
            _extras[LootItemAttributes.ExtraId(x)] = x;
            Rebuild();
        });
        w.Tooltip("Require this cantrip at this tier or better (Minor < Major < Epic < Legendary < Paragon).");
    }
}
