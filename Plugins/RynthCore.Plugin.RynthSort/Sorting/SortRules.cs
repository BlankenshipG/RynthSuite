using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>Which test a rule makes.</summary>
internal enum RuleKind
{
    /// <summary>The item's ObjectClass equals <see cref="SortRule.Class"/>.</summary>
    Class = 0,
    /// <summary>The item's name contains <see cref="SortRule.Text"/> (any case).</summary>
    Name = 1,
}

/// <summary>
/// One rule: items it matches go to side pack <see cref="Pack"/> (1 = the first side pack, top to
/// bottom). The same fields as the Lua script's rules: { on, kind = "class" | "name", class, text, pack }.
/// </summary>
internal sealed class SortRule
{
    public bool On = true;
    public RuleKind Kind = RuleKind.Class;
    /// <summary>An ObjectClass number (<see cref="ObjectClasses"/>).</summary>
    public int Class = ObjectClasses.SpellComponent;
    public string Text = string.Empty;
    public int Pack = 1;

    public SortRule Clone() => new() { On = On, Kind = Kind, Class = Class, Text = Text, Pack = Pack };

    public static SortRule ForClass(int cls, int pack) => new() { Kind = RuleKind.Class, Class = cls, Pack = pack };
    public static SortRule ForName(string text, int pack) => new() { Kind = RuleKind.Name, Text = text, Pack = pack };
}

/// <summary>
/// The ObjectClass numbers, as RynthLua's ObjectClass enum (LuaEnums.ObjectClass: Decal/UtilityBelt
/// order), so rules saved by the Lua script mean the same thing here.
/// </summary>
internal static class ObjectClasses
{
    public static readonly string[] Names =
    {
        "Unknown", "MeleeWeapon", "Armor", "Clothing", "Jewelry", "Monster", "Food", "Money", "Misc",
        "MissileWeapon", "Container", "Gem", "SpellComponent", "Key", "Portal", "TradeNote", "ManaStone",
        "Plant", "BaseCooking", "BaseAlchemy", "BaseFletching", "CraftedCooking", "CraftedAlchemy",
        "CraftedFletching", "Player", "Vendor", "Door", "Corpse", "Lifestone", "HealingKit", "Lockpick",
        "WandStaffOrb", "Bundle", "Book", "Journal", "Sign", "Housing", "Npc", "Foci", "Salvage", "Ust",
        "Services", "Scroll",
    };

    public const int Unknown = 0, MeleeWeapon = 1, Armor = 2, Clothing = 3, Jewelry = 4, Monster = 5, Food = 6,
        Money = 7, Misc = 8, MissileWeapon = 9, Container = 10, Gem = 11, SpellComponent = 12, Key = 13, Portal = 14,
        TradeNote = 15, ManaStone = 16, Plant = 17, BaseCooking = 18, BaseAlchemy = 19, BaseFletching = 20,
        CraftedCooking = 21, CraftedAlchemy = 22, CraftedFletching = 23, Player = 24, Vendor = 25, Door = 26,
        Corpse = 27, Lifestone = 28, HealingKit = 29, Lockpick = 30, WandStaffOrb = 31, Bundle = 32, Book = 33,
        Journal = 34, Sign = 35, Housing = 36, Npc = 37, Foci = 38, Salvage = 39, Ust = 40, Services = 41, Scroll = 42;

    /// <summary>The classes the rule editor offers, in the Lua script's order.</summary>
    public static readonly int[] Choices =
    {
        SpellComponent, Salvage, Gem, ManaStone, Key, HealingKit, Lockpick, Food, Scroll, TradeNote, Money, Plant,
        BaseAlchemy, BaseCooking, BaseFletching, CraftedAlchemy, CraftedCooking, CraftedFletching, Jewelry, Armor,
        Clothing, MeleeWeapon, MissileWeapon, WandStaffOrb, Bundle, Book, Journal, Ust, Misc,
    };

    /// <summary>"Spell Component" for SpellComponent, as the Lua script showed them.</summary>
    public static readonly string[] ChoiceLabels = BuildLabels();

    private static string[] BuildLabels()
    {
        var labels = new string[Choices.Length];
        for (int i = 0; i < Choices.Length; i++) labels[i] = Spaced(Names[Choices[i]]);
        return labels;
    }

    /// <summary>A space before each capital that follows a small letter ("ManaStone" -> "Mana Stone").</summary>
    public static string Spaced(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>The index of <paramref name="cls"/> in <see cref="Choices"/>, or -1.</summary>
    public static int ChoiceIndex(int cls) => Array.IndexOf(Choices, cls);

    public static string Label(int cls) =>
        cls >= 0 && cls < Names.Length ? Spaced(Names[cls]) : "class " + cls;
}

internal static class SortRules
{
    /// <summary>The Lua script's defaults: components, salvage, then gems, stones and keys together.</summary>
    public static List<SortRule> Defaults() => new()
    {
        SortRule.ForClass(ObjectClasses.SpellComponent, 1),
        SortRule.ForClass(ObjectClasses.Salvage, 2),
        SortRule.ForClass(ObjectClasses.Gem, 3),
        SortRule.ForClass(ObjectClasses.ManaStone, 3),
        SortRule.ForClass(ObjectClasses.Key, 3),
    };

    public static bool Matches(SortRule r, SortItem item)
    {
        if (!r.On) return false;
        if (r.Kind == RuleKind.Name)
        {
            string t = r.Text ?? string.Empty;
            return t.Length > 0 && (item.Name ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase);
        }
        return item.ObjectClass == r.Class;
    }

    /// <summary>The first rule that matches (rules are tried in order), or null.</summary>
    public static SortRule? RuleFor(IReadOnlyList<SortRule> rules, SortItem item)
    {
        for (int i = 0; i < rules.Count; i++)
            if (Matches(rules[i], item)) return rules[i];
        return null;
    }

    public static string Describe(SortRule r) =>
        r.Kind == RuleKind.Name ? $"name has \"{r.Text}\"" : ObjectClasses.Label(r.Class);
}
