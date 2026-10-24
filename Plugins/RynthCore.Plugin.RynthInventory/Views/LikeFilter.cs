using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Plugin.RynthInventory.Data;

namespace RynthCore.Plugin.RynthInventory.Views;

/// <summary>
/// "Find like this in Global Inventory" (2026-10-24): the Inventory panel's right-click builds a
/// rule with the Add to loot profile checklist and sends it here as "/ginv like &lt;spec&gt;"
/// (LootSdk LootFindSpec). The search page then lists only the saved items the rule takes,
/// judged on what each item's file recorded (LootFactsMatcher). Items saved without the facts
/// (by RynthInventory 0.1.4 or older) or not identified when saved can only be judged on what
/// every item shows (class, name, value, burden, material...): when a rule needs more they are
/// left out and counted as unknown, until their character logs in and is scanned again.
/// </summary>
internal sealed class LikeFilter
{
    public string Label { get; }
    public List<string> Lines { get; }
    public bool NeedsIdentify { get; }

    private readonly LootFindSpec _spec;
    // Facts per saved item: records never change once read (a rescan makes new ones).
    private readonly ConditionalWeakTable<ItemRecord, LootItemFacts> _facts = new();

    private LikeFilter(LootFindSpec spec)
    {
        _spec = spec;
        Label = spec.Label.Length > 0 ? spec.Label : "like the item";
        Lines = spec.Describe();
        NeedsIdentify = LootFactsMatcher.NeedsIdentify(spec.Conditions);
    }

    /// <summary>A filter from conditions made here (Global Inventory's own checklist).</summary>
    public static LikeFilter From(string label, IEnumerable<RynthCore.Loot.VTank.VTankLootCondition> conditions)
    {
        var spec = new LootFindSpec { Label = label };
        spec.Conditions.AddRange(conditions);
        return new LikeFilter(spec);
    }

    public static LikeFilter? Parse(string text, out string error)
    {
        if (!LootFindSpec.TryDecode(text, out LootFindSpec spec, out error)) return null;
        return new LikeFilter(spec);
    }

    /// <summary>1 the rule takes the item, 0 it doesn't, -1 it can't tell (not identified / no facts saved).</summary>
    public int Judge(ItemRecord it)
    {
        LootItemFacts f = _facts.GetValue(it, Facts);
        bool identified = it.HasFacts && it.Identified;
        return LootFactsMatcher.Match(_spec.Conditions, f, identified) switch
        {
            LootFactsVerdict.Match => 1,
            LootFactsVerdict.Unknown => -1,
            _ => 0,
        };
    }

    /// <summary>The saved item as a loot rule sees it. An item saved without facts gets what its record has.</summary>
    internal static LootItemFacts Facts(ItemRecord it)
    {
        var f = new LootItemFacts
        {
            Id = unchecked((int)it.Id),
            Name = it.Name,
            StackSize = Math.Max(1, it.Stack),
            MaxStackSize = Math.Max(1, it.MaxStack),
        };
        if (it.HasFacts)
        {
            f.ObjectClass = it.ObjectClass;
            if (it.Ints != null) foreach (KeyValuePair<int, int> kv in it.Ints) f.Ints[kv.Key] = kv.Value;
            if (it.Doubles != null) foreach (KeyValuePair<int, double> kv in it.Doubles) f.Doubles[kv.Key] = kv.Value;
        }
        else
        {
            // An older file: the class from the item type alone, and the few properties it kept.
            f.ObjectClass = (int)AcItemClassifier.Classify(it.ItemType, it.Name, false, 0, 0);
            void Put(int key, int v) { if (v != 0) f.Ints[key] = v; }
            Put(1, unchecked((int)it.ItemType));
            Put(5, it.Burden);
            Put(19, it.Value);
            Put(131, it.Material);
            Put(105, it.Workmanship);
            Put(28, it.ArmorLevel);
            Put(91, it.MaxUses);
            Put(92, it.Uses);
            Put(265, it.SetId);
        }
        foreach (string s in it.SpellNames)
            if (!string.IsNullOrEmpty(s)) f.Spells.Add(s);
        return f;
    }
}
