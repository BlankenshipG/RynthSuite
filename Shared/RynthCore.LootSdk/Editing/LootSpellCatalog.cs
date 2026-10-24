using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Loot.Editing;

/// <summary>One spell table row as the catalog needs it: name, family (category) and power (difficulty).</summary>
public readonly record struct LootSpellEntry(string Name, int Family, int Power);

/// <summary>
/// One leveled spell family a checklist can add ("Strength", Creature): every spell of the
/// family by level, I to VIII. Levels VII and VIII have their own names in the spell table
/// ("Might of the Lugians", "Incantation of Strength Self"), so a rule lists the names.
/// </summary>
public sealed class LootSpellFamily
{
    /// <summary>The family without " Self" / " Other" and without the level: "Strength", "Aura of Defender", "Impenetrability".</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary><see cref="LootSpellCatalog.Creature"/>, <see cref="LootSpellCatalog.Life"/> or <see cref="LootSpellCatalog.Item"/>.</summary>
    public string School { get; init; } = string.Empty;
    /// <summary>The spells at each level (1-8).</summary>
    public SortedDictionary<int, List<string>> Levels { get; } = new();

    public int MinLevel => Levels.Count == 0 ? 1 : FirstKey();
    public int MaxLevel
    {
        get
        {
            int max = 1;
            foreach (int l in Levels.Keys) max = Math.Max(max, l);
            return max;
        }
    }

    private int FirstKey()
    {
        foreach (int l in Levels.Keys) return l;
        return 1;
    }

    /// <summary>
    /// The spell condition's pattern: the family's spells at <paramref name="level"/> (exact), at it
    /// or above (or better), or at any level, each name exactly.
    /// </summary>
    public string Pattern(int level, LootItemAttributeOp op)
    {
        var names = new List<string>();
        foreach (KeyValuePair<int, List<string>> kv in Levels)
        {
            bool take = op switch
            {
                LootItemAttributeOp.SpellExact => kv.Key == level,
                LootItemAttributeOp.SpellAnyTier => true,
                _ => kv.Key >= level,
            };
            if (take) names.AddRange(kv.Value);
        }
        if (names.Count == 0) return LootItemAttributes.FamilyPattern(Name, level, op);
        var sb = new StringBuilder("^(");
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(LootItemRules.EscapeText(names[i]));
        }
        return sb.Append(")$").ToString();
    }
}

/// <summary>
/// The standard spell lists the Add to loot profile / Filter popups offer (2026-10-24): the
/// leveled (I-VIII) creature, life and item enchantments, and the cantrips (Minor, Major, Epic,
/// Legendary, Paragon), built from a spell table (RynthAi's and RynthInventory's
/// SpellData.txt: name, family, power). Buffs only: debuffs, war and void projectiles and
/// one-off spells are left out. "Self" and "Other" versions are one family.
/// </summary>
public sealed class LootSpellCatalog
{
    public const string Creature = "Creature", Life = "Life", Item = "Item";
    public static readonly string[] Schools = { Creature, Life, Item };

    public List<LootSpellFamily> Families { get; } = new();
    /// <summary>Cantrip names without their tier ("Coordination").</summary>
    public List<string> Cantrips { get; } = new();

    public static readonly LootSpellCatalog Empty = new();

    public LootSpellFamily? Find(string name) =>
        Families.Find(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    // Debuffs and attacks: a family containing any of these words isn't offered.
    private static readonly string[] Excluded =
    {
        "Ineptitude", "Ignorance", "Vulnerability", "Weakness", "Clumsiness", "Slowness", "Bafflement", "Feeblemind",
        "Frailty", "Enfeeble", "Exhaustion", "Fester", "Harm", "Drain", "Depletion", "Imperil", "Defenselessness",
        "Unfamiliarity", "Benightedness", "Faithlessness", "Leaden", "Yield", "Lure", "Loather", "Brittlemail", "Hermetic Void",
        "Turn Blade", "Weaken", "Corrosion", "Corruption", "Curse", "Blight", "Hecatomb", "Expose", " Arc", "Blast", "Bolt",
        "Streak", "Volley", "Stream", "Wave", "Whirling", "Metamorphi", "Rare ", "Sigil", "Weave", "Martyr", "Paragon",
        "Answer of", "Call of", "Nuhmudira", "Virindi", "Arcanum", "Infuse", " to ",
    };

    private static readonly string[] LifeFamilies =
        { "Armor", "Regeneration", "Rejuvenation", "Mana Renewal", "Heal", "Revitalize", "Mana Boost" };

    /// <summary>Spell level from power (difficulty): 1 I, 50 II ... 250 VI, 300 VII, 400 VIII; 0 for anything else.</summary>
    public static int LevelFromPower(int power) => power switch
    {
        1 => 1, 50 => 2, 100 => 3, 150 => 4, 200 => 5, 250 => 6, 300 => 7, 400 => 8, _ => 0,
    };

    /// <summary>The catalog of <paramref name="spells"/>.</summary>
    public static LootSpellCatalog Build(IEnumerable<LootSpellEntry> spells)
    {
        var cat = new LootSpellCatalog();
        var cantrips = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        // Per spell family (category): its base name (from a "<name> [Self|Other] I..VI" spell), Self seen, spells by level.
        var byFamily = new Dictionary<int, (string? Base, bool Self, List<(int Level, string Name)> Spells)>();
        foreach (LootSpellEntry e in spells)
        {
            string name = (e.Name ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (LootItemAttributes.TryCantrip(name, out _, out string cantrip))
            {
                if (!LootItemAttributes.TryLevel(cantrip, out _, out _)) cantrips.Add(cantrip);
                continue;
            }
            int level = LevelFromPower(e.Power);
            if (level == 0 || e.Family <= 0) continue;
            if (!byFamily.TryGetValue(e.Family, out var fam)) fam = (null, false, new List<(int, string)>());
            if (LootItemAttributes.TryLevel(name, out int romanLevel, out string rest) && romanLevel <= 6)
            {
                string b = StripTarget(rest, out bool self);
                fam.Base ??= b;
                fam.Self |= self;
            }
            fam.Spells.Add((level, name));
            byFamily[e.Family] = fam;
        }
        var merged = new Dictionary<string, LootSpellFamily>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, fam) in byFamily)
        {
            if (fam.Base == null) continue;
            string? school = SchoolOf(fam.Base, fam.Self);
            if (school == null) continue;
            if (!merged.TryGetValue(fam.Base, out LootSpellFamily? f))
                merged[fam.Base] = f = new LootSpellFamily { Name = fam.Base, School = school };
            foreach (var (level, name) in fam.Spells)
            {
                if (!f.Levels.TryGetValue(level, out List<string>? list)) f.Levels[level] = list = new List<string>();
                if (!list.Contains(name)) list.Add(name);
            }
        }
        cat.Families.AddRange(merged.Values);
        cat.Families.Sort((a, b) =>
        {
            int s = Array.IndexOf(Schools, a.School).CompareTo(Array.IndexOf(Schools, b.School));
            return s != 0 ? s : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        cat.Cantrips.AddRange(cantrips);
        return cat;
    }

    /// <summary>"Strength Self" -> "Strength" (self true); "Impenetrability" stays.</summary>
    public static string StripTarget(string rest, out bool self)
    {
        self = rest.EndsWith(" Self", StringComparison.OrdinalIgnoreCase);
        if (self) return rest.Substring(0, rest.Length - 5);
        if (rest.EndsWith(" Other", StringComparison.OrdinalIgnoreCase)) return rest.Substring(0, rest.Length - 6);
        return rest;
    }

    private static string? SchoolOf(string family, bool hasSelf)
    {
        foreach (string w in Excluded)
            if (family.Contains(w, StringComparison.OrdinalIgnoreCase)) return null;
        if (family.StartsWith("Aura of ", StringComparison.OrdinalIgnoreCase)
            || family.EndsWith(" Bane", StringComparison.OrdinalIgnoreCase)
            || family.Equals("Impenetrability", StringComparison.OrdinalIgnoreCase)
            || family.Equals("Strengthen Lock", StringComparison.OrdinalIgnoreCase))
            return Item;
        if (family.EndsWith(" Protection", StringComparison.OrdinalIgnoreCase)
            || Array.Exists(LifeFamilies, x => x.Equals(family, StringComparison.OrdinalIgnoreCase)))
            return Life;
        // Attributes, skills and defenses: the ones a character can cast on itself.
        return hasSelf ? Creature : null;
    }
}
