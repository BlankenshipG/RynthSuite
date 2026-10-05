using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.Loot.VTank;

namespace RynthCore.Loot.Editing;

/// <summary>
/// Words for loot rules, in plain ASCII (the in-game font has no ≥): action
/// labels, node type names, one-line condition summaries, and the vocabulary the
/// editor face's pickers show (LootEditVocab).
/// </summary>
public static class LootRuleText
{
    /// <summary>The actions in picker order: Keep, Keep #, Salvage, Sell, Read.</summary>
    public static readonly VTankLootAction[] Actions =
    {
        VTankLootAction.Keep, VTankLootAction.KeepUpTo, VTankLootAction.Salvage, VTankLootAction.Sell, VTankLootAction.Read,
    };

    public static bool IsKnownAction(int code) => Array.IndexOf(Actions, (VTankLootAction)code) >= 0;

    public static string ActionName(VTankLootAction a) => a switch
    {
        VTankLootAction.Keep => "Keep",
        VTankLootAction.KeepUpTo => "Keep #",
        VTankLootAction.Salvage => "Salvage",
        VTankLootAction.Sell => "Sell",
        VTankLootAction.Read => "Read",
        _ => $"Action {(int)a}",
    };

    /// <summary>A node type's name, ASCII.</summary>
    public static string NodeName(int type) => Ascii(VTankNodeTypes.DisplayName(type));

    private static string Ascii(string s) => s.Replace("≥", ">=").Replace("≤", "<=").Replace("≠", "!=");

    // ── Key tables (the common ones; unknown ids show as "key N") ──────────

    public static readonly (int Id, string Name)[] LongKeys =
    {
        (5, "EncumbVal"), (9, "Locations"), (19, "Value"), (25, "Level"), (28, "ArmorLevel"), (44, "Damage"), (45, "DamageType"),
        (48, "WeaponSkill"), (54, "MaxDamage"),
        (87, "MaxStructure"), (88, "Structure"), (105, "ItemWorkmanship"), (107, "ItemMaxMana"),
        (131, "MaterialType"), (158, "WieldRequirements"), (159, "WieldSkilltype"), (160, "WieldDifficulty"),
        (218, "EquippedSlots"), (353, "ImbuedEffect"), (370, "DamageRating"), (371, "DamageResistRating"),
        (372, "CritRating"), (373, "CritResistRating"), (374, "CritDamageRating"),
        (375, "CritDamageResistRating"), (376, "HealBoostRating"), (379, "VitalityRating"),
        (0x0D000000, "Type (WCID)"),   // Decal's synthetic key: the weenie class id
    };

    public static readonly (int Id, string Name)[] DoubleKeys =
    {
        (5, "ApproachDistance"), (22, "DamageVariance"), (29, "WeaponLength"), (62, "WeaponDefense"),
        (63, "WeaponOffense"), (152, "ElementalDamageVsMonsters"), (167, "ManaRate"),
    };

    public static readonly (int Id, string Name)[] StringKeys =
    {
        (1, "Name"), (5, "Inscription"), (7, "Title"),
    };

    /// <summary>
    /// AC's skill ids (ACE's Skill enum, the retail client's numbering; the same ids
    /// AcStubs and RynthLua use), alphabetical so a picker reads easily. Used for the
    /// CharacterSkillGE / CharacterBaseSkill key and as the value names of
    /// WieldSkilltype (159) and WeaponSkill (48). Retired and unused skills are kept
    /// so old items still show a name.
    /// </summary>
    public static readonly (int Id, string Name)[] Skills =
    {
        (38, "Alchemy"), (14, "Arcane Lore"), (29, "Armor Tinkering"), (26, "Arms and Armor Repair"),
        (27, "Assess Creature"), (19, "Assess Person"), (25, "Awareness"), (1, "Axe"), (2, "Bow"),
        (53, "Challenge"), (39, "Cooking"), (31, "Creature Enchantment"), (3, "Crossbow"), (4, "Dagger"),
        (20, "Deception"), (52, "Dirty Fighting"), (49, "Dual Wield"), (46, "Finesse Weapons"), (37, "Fletching"),
        (42, "Gearcraft"), (21, "Healing"), (44, "Heavy Weapons"), (32, "Item Enchantment"), (18, "Item Tinkering"),
        (22, "Jump"), (35, "Leadership"), (33, "Life Magic"), (45, "Light Weapons"), (23, "Lockpick"),
        (36, "Loyalty"), (5, "Mace"), (15, "Magic Defense"), (30, "Magic Item Tinkering"), (16, "Mana Conversion"),
        (6, "Melee Defense"), (7, "Missile Defense"), (47, "Missile Weapons"), (50, "Recklessness"), (24, "Run"),
        (40, "Salvaging"), (48, "Shield"), (8, "Sling"), (51, "Sneak Attack"), (9, "Spear"), (17, "Spellcraft"),
        (10, "Staff"), (54, "Summoning"), (11, "Sword"), (12, "Thrown Weapon"), (41, "Two Handed"),
        (13, "Unarmed Combat"), (43, "Void Magic"), (34, "War Magic"), (28, "Weapon Tinkering"),
    };

    // ── Value tables (what a long key's number means) ──────────────────────

    /// <summary>WieldRequirements (158): what WieldDifficulty is checked against (ACE's WieldRequirement enum).</summary>
    public static readonly (int Id, string Name)[] WieldRequirements =
    {
        (0, "Invalid"), (1, "Skill"), (2, "Base Skill"), (3, "Attribute"), (4, "Base Attribute"),
        (5, "Vital"), (6, "Base Vital"), (7, "Level"), (8, "Training"), (9, "Int Property"),
        (10, "Bool Property"), (11, "Creature Type"), (12, "Heritage"),
    };

    /// <summary>MaterialType (131): ACE's MaterialType enum, in id order (grouped cloth, gems, hides, metals, stone, wood).</summary>
    public static readonly (int Id, string Name)[] Materials =
    {
        (0, "Unknown"), (1, "Ceramic"), (2, "Porcelain"), (3, "Cloth"), (4, "Linen"), (5, "Satin"), (6, "Silk"),
        (7, "Velvet"), (8, "Wool"), (9, "Gem"), (10, "Agate"), (11, "Amber"), (12, "Amethyst"), (13, "Aquamarine"),
        (14, "Azurite"), (15, "Black Garnet"), (16, "Black Opal"), (17, "Bloodstone"), (18, "Carnelian"),
        (19, "Citrine"), (20, "Diamond"), (21, "Emerald"), (22, "Fire Opal"), (23, "Green Garnet"),
        (24, "Green Jade"), (25, "Hematite"), (26, "Imperial Topaz"), (27, "Jet"), (28, "Lapis Lazuli"),
        (29, "Lavender Jade"), (30, "Malachite"), (31, "Moonstone"), (32, "Onyx"), (33, "Opal"), (34, "Peridot"),
        (35, "Red Garnet"), (36, "Red Jade"), (37, "Rose Quartz"), (38, "Ruby"), (39, "Sapphire"),
        (40, "Smokey Quartz"), (41, "Sunstone"), (42, "Tiger Eye"), (43, "Tourmaline"), (44, "Turquoise"),
        (45, "White Jade"), (46, "White Quartz"), (47, "White Sapphire"), (48, "Yellow Garnet"),
        (49, "Yellow Topaz"), (50, "Zircon"), (51, "Ivory"), (52, "Leather"), (53, "Armoredillo Hide"),
        (54, "Gromnie Hide"), (55, "Reed Shark Hide"), (56, "Metal"), (57, "Brass"), (58, "Bronze"),
        (59, "Copper"), (60, "Gold"), (61, "Iron"), (62, "Pyreal"), (63, "Silver"), (64, "Steel"), (65, "Stone"),
        (66, "Alabaster"), (67, "Granite"), (68, "Marble"), (69, "Obsidian"), (70, "Sandstone"),
        (71, "Serpentine"), (72, "Wood"), (73, "Ebony"), (74, "Mahogany"), (75, "Oak"), (76, "Pine"), (77, "Teak"),
    };

    /// <summary>
    /// DamageType (45): ACE's DamageType flags, the single flags plus the common
    /// slashing/piercing pair. Other combinations stay numbers ("value N").
    /// </summary>
    public static readonly (int Id, string Name)[] DamageTypes =
    {
        (0, "Undefined"), (1, "Slashing"), (2, "Piercing"), (3, "Slashing/Piercing"), (4, "Bludgeoning"),
        (8, "Cold"), (16, "Fire"), (32, "Acid"), (64, "Electric"), (128, "Health"), (256, "Stamina"),
        (512, "Mana"), (1024, "Nether"),
    };

    /// <summary>
    /// The names for a long key's value, or null when the key's value is a plain
    /// number. Unknown values are still valid: show them as "value N".
    /// </summary>
    public static (int Id, string Name)[]? ValueTableFor(int longKey) => longKey switch
    {
        45 => DamageTypes,
        48 => Skills,
        131 => Materials,
        158 => WieldRequirements,
        159 => Skills,
        _ => null,
    };

    /// <summary>True when the long key's value is a set of flags (a named value is one flag or a common pair).</summary>
    public static bool ValueTableIsFlags(int longKey) => longKey == 45;

    /// <summary>The long keys that have a value table, for the vocabulary.</summary>
    public static readonly int[] KeysWithValueTables = { 45, 48, 131, 158, 159 };

    /// <summary>
    /// True for the conditions whose value is picked by name when the key has a
    /// table: LongValKey GE/LE/E/NE and BuffedLongValKeyGE (not "has flag").
    /// </summary>
    public static bool UsesValueNames(int nodeType) => nodeType is VTankNodeTypes.LongValKeyGE
        or VTankNodeTypes.LongValKeyLE or VTankNodeTypes.LongValKeyE or VTankNodeTypes.LongValKeyNE
        or VTankNodeTypes.BuffedLongValKeyGE;

    /// <summary>
    /// A value line as a whole number: "45", " 45 " and "45.0" (a buffed-long value
    /// written as a double) all read as 45. False for anything else.
    /// </summary>
    public static bool TryParseValue(string? raw, out int value)
    {
        string s = (raw ?? string.Empty).Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
        {
            value = (int)d;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>The name of a long key's value, or null when the key has no table or the value isn't in it.</summary>
    public static string? ValueName(int longKey, string? raw)
    {
        var table = ValueTableFor(longKey);
        if (table == null || !TryParseValue(raw, out int v)) return null;
        foreach (var t in table)
            if (t.Id == v) return t.Name;
        return null;
    }

    /// <summary>
    /// How a picker shows a value: "Light Weapons (45)" when named, "value 45" when
    /// not (or the raw text, "value abc", when it isn't a number).
    /// </summary>
    public static string ValueLabel(int longKey, string? raw)
    {
        string? name = ValueName(longKey, raw);
        if (name != null && TryParseValue(raw, out int v)) return PickerLabel(v, name);
        return "value " + (raw ?? string.Empty).Trim();
    }

    /// <summary>"Name (id)", the way the pickers list a named id.</summary>
    public static string PickerLabel(int id, string name) => name + " (" + id.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>Object classes (Decal/VTank numbering, AcObjectClass).</summary>
    public static (int Id, string Name)[] ObjectClasses { get; } = BuildObjectClasses();

    private static (int, string)[] BuildObjectClasses()
    {
        var list = new List<(int, string)>();
        foreach (AcObjectClass c in Enum.GetValues<AcObjectClass>())
            list.Add(((int)c, c.ToString()));
        return list.ToArray();
    }

    private static string KeyName((int Id, string Name)[] table, string raw)
    {
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            foreach (var t in table)
                if (t.Id == n) return t.Name;
        return "key " + raw.Trim();
    }

    // ── Editors per node type (what the face shows for a condition) ─────────

    /// <summary>The face's editor for a node type; see LootEditNodeType.Editor.</summary>
    public static (string Editor, string KeyTable) EditorFor(int type) => type switch
    {
        VTankNodeTypes.ObjectClass => ("class", ""),
        VTankNodeTypes.LongValKeyGE or VTankNodeTypes.LongValKeyLE or VTankNodeTypes.LongValKeyE
            or VTankNodeTypes.LongValKeyNE or VTankNodeTypes.LongValKeyFlagExists
            or VTankNodeTypes.BuffedLongValKeyGE => ("keyval", "long"),
        VTankNodeTypes.DoubleValKeyGE or VTankNodeTypes.DoubleValKeyLE
            or VTankNodeTypes.BuffedDoubleValKeyGE => ("keyval", "double"),
        VTankNodeTypes.CharacterSkillGE => ("keyval", "skill"),
        VTankNodeTypes.StringValueMatch => ("keypattern", "string"),
        VTankNodeTypes.SpellNameMatch => ("text", ""),
        VTankNodeTypes.DamagePercentGE or VTankNodeTypes.SpellCountGE or VTankNodeTypes.MinDamageGE
            or VTankNodeTypes.CharacterMainPackEmptySlotsGE or VTankNodeTypes.CharacterLevelGE
            or VTankNodeTypes.CharacterLevelLE or VTankNodeTypes.BuffedMedianDamageGE
            or VTankNodeTypes.BuffedMissileDamageGE or VTankNodeTypes.CalcdBuffedTinkedDamageGE
            or VTankNodeTypes.TotalRatingsGE => ("value", ""),
        _ => ("raw", ""),
    };

    /// <summary>What each data line holds, in file order.</summary>
    public static string[] LineLabels(int type) => type switch
    {
        VTankNodeTypes.SpellNameMatch => new[] { "Pattern" },
        VTankNodeTypes.StringValueMatch => new[] { "Pattern", "Key" },
        VTankNodeTypes.LongValKeyLE or VTankNodeTypes.LongValKeyGE or VTankNodeTypes.LongValKeyE
            or VTankNodeTypes.LongValKeyNE or VTankNodeTypes.DoubleValKeyLE or VTankNodeTypes.DoubleValKeyGE
            or VTankNodeTypes.BuffedLongValKeyGE or VTankNodeTypes.BuffedDoubleValKeyGE => new[] { "Value", "Key" },
        VTankNodeTypes.LongValKeyFlagExists => new[] { "Flag", "Key" },
        VTankNodeTypes.ObjectClass => new[] { "Class" },
        VTankNodeTypes.SpellCountGE => new[] { "Count" },
        VTankNodeTypes.DamagePercentGE => new[] { "Percent" },
        VTankNodeTypes.MinDamageGE or VTankNodeTypes.BuffedMedianDamageGE or VTankNodeTypes.BuffedMissileDamageGE
            or VTankNodeTypes.CalcdBuffedTinkedDamageGE => new[] { "Damage" },
        VTankNodeTypes.CharacterLevelGE or VTankNodeTypes.CharacterLevelLE => new[] { "Level" },
        VTankNodeTypes.TotalRatingsGE => new[] { "Ratings" },
        VTankNodeTypes.SpellMatch => new[] { "Match", "No match", "Count" },
        VTankNodeTypes.AnySimilarColor => new[] { "R", "G", "B", "Max diff H", "Max diff SV" },
        VTankNodeTypes.SimilarColorArmorType => new[] { "R", "G", "B", "Max diff H", "Max diff SV", "Armor group" },
        VTankNodeTypes.SlotSimilarColor => new[] { "R", "G", "B", "Max diff H", "Max diff SV", "Slot" },
        VTankNodeTypes.SlotExactPalette => new[] { "Slot", "Palette" },
        VTankNodeTypes.CharacterSkillGE => new[] { "Value", "Skill" },
        VTankNodeTypes.CharacterMainPackEmptySlotsGE => new[] { "Slots" },
        VTankNodeTypes.CharacterBaseSkill => new[] { "Skill", "Min", "Max" },
        VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE => new[] { "DoT", "Def", "Atk" },
        VTankNodeTypes.DisabledRule => new[] { "Disabled" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Data lines for a new condition of this type.</summary>
    public static List<string> DefaultLines(int type)
    {
        int n = Math.Max(0, VTankNodeTypes.GetDataLineCount(type));
        var lines = new List<string>(n);
        for (int i = 0; i < n; i++) lines.Add(IsTextLine(type, i) ? string.Empty : "0");
        if (type == VTankNodeTypes.DisabledRule && n == 1) lines[0] = "true";
        if (type == VTankNodeTypes.StringValueMatch) lines[1] = "1";           // Name
        if (type == VTankNodeTypes.ObjectClass) lines[0] = ((int)AcObjectClass.MeleeWeapon).ToString(CultureInfo.InvariantCulture);
        return lines;
    }

    /// <summary>True for a line that holds text (a pattern), not a number.</summary>
    public static bool IsTextLine(int type, int line) => type switch
    {
        VTankNodeTypes.SpellNameMatch => line == 0,
        VTankNodeTypes.StringValueMatch => line == 0,
        VTankNodeTypes.SpellMatch => line <= 1,
        VTankNodeTypes.DisabledRule => true,
        _ => false,
    };

    /// <summary>True for a number line that must hold a whole number (else a decimal).</summary>
    public static bool IsIntLine(int type, int line) => type switch
    {
        VTankNodeTypes.DoubleValKeyGE or VTankNodeTypes.DoubleValKeyLE or VTankNodeTypes.BuffedDoubleValKeyGE => line == 1,
        VTankNodeTypes.DamagePercentGE or VTankNodeTypes.MinDamageGE or VTankNodeTypes.BuffedMedianDamageGE
            or VTankNodeTypes.BuffedMissileDamageGE or VTankNodeTypes.CalcdBuffedTinkedDamageGE
            or VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE => false,
        VTankNodeTypes.AnySimilarColor or VTankNodeTypes.SimilarColorArmorType or VTankNodeTypes.SlotSimilarColor => line <= 2 || line == 5,
        _ => true,
    };

    // ── Summaries ────────────────────────────────────────────────────────

    /// <summary>A rule's conditions in words, joined with AND (DisabledRule nodes left out).</summary>
    public static string Summary(VTankLootRule rule, int maxLength = 160)
    {
        var sb = new StringBuilder();
        foreach (VTankLootCondition c in rule.Conditions)
        {
            if (c.NodeType == VTankNodeTypes.DisabledRule) continue;
            if (sb.Length > 0) sb.Append(" AND ");
            sb.Append(Condition(c));
            if (sb.Length > maxLength) break;
        }
        if (sb.Length == 0) return "Matches every item";
        return sb.Length > maxLength ? sb.ToString(0, maxLength - 3) + "..." : sb.ToString();
    }

    /// <summary>One condition in words.</summary>
    public static string Condition(VTankLootCondition c)
    {
        IList<string> d = c.DataLines;
        string G(int i) => i < d.Count ? d[i] : "?";
        string Lk(int i) => KeyName(LongKeys, G(i));
        string Dk(int i) => KeyName(DoubleKeys, G(i));
        string Sk(int i) => KeyName(StringKeys, G(i));
        string Skl(int i) => KeyName(Skills, G(i));
        string Oc(int i)
        {
            if (int.TryParse(G(i).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                foreach (var t in ObjectClasses)
                    if (t.Id == n) return t.Name;
            return "class " + G(i);
        }
        // A long key's value by name when the key has a table ("WieldSkilltype = Light Weapons").
        string Lv()
        {
            if (TryParseValue(G(1), out int key) && ValueName(key, G(0)) is string name) return name;
            return G(0);
        }
        static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) + "..." : s;

        return c.NodeType switch
        {
            VTankNodeTypes.ObjectClass => $"Class is {Oc(0)}",
            VTankNodeTypes.LongValKeyGE => $"{Lk(1)} >= {Lv()}",
            VTankNodeTypes.LongValKeyLE => $"{Lk(1)} <= {Lv()}",
            VTankNodeTypes.LongValKeyE => $"{Lk(1)} = {Lv()}",
            VTankNodeTypes.LongValKeyNE => $"{Lk(1)} != {Lv()}",
            VTankNodeTypes.LongValKeyFlagExists => $"{Lk(1)} has flag {G(0)}",
            VTankNodeTypes.DoubleValKeyGE => $"{Dk(1)} >= {G(0)}",
            VTankNodeTypes.DoubleValKeyLE => $"{Dk(1)} <= {G(0)}",
            VTankNodeTypes.BuffedLongValKeyGE => $"buffed {Lk(1)} >= {Lv()}",
            VTankNodeTypes.BuffedDoubleValKeyGE => $"buffed {Dk(1)} >= {G(0)}",
            VTankNodeTypes.StringValueMatch => $"{Sk(1)} matches \"{Trunc(G(0), 30)}\"",
            VTankNodeTypes.SpellNameMatch => $"spell matches \"{Trunc(G(0), 30)}\"",
            VTankNodeTypes.SpellCountGE => $"spell count >= {G(0)}",
            VTankNodeTypes.SpellMatch => $">= {G(2)} spells matching \"{Trunc(G(0), 20)}\"",
            VTankNodeTypes.MinDamageGE => $"min dmg >= {G(0)}",
            VTankNodeTypes.DamagePercentGE => $"dmg% >= {G(0)}",
            VTankNodeTypes.TotalRatingsGE => $"total ratings >= {G(0)}",
            VTankNodeTypes.BuffedMedianDamageGE => $"median dmg >= {G(0)}",
            VTankNodeTypes.BuffedMissileDamageGE => $"missile dmg >= {G(0)}",
            VTankNodeTypes.CalcdBuffedTinkedDamageGE => $"tinked dmg >= {G(0)}",
            VTankNodeTypes.CharacterLevelGE => $"char level >= {G(0)}",
            VTankNodeTypes.CharacterLevelLE => $"char level <= {G(0)}",
            VTankNodeTypes.CharacterMainPackEmptySlotsGE => $"pack slots >= {G(0)}",
            VTankNodeTypes.CharacterSkillGE => $"{Skl(1)} skill >= {G(0)}",
            VTankNodeTypes.CharacterBaseSkill => $"{Skl(0)} base in [{G(1)}, {G(2)}]",
            VTankNodeTypes.SlotExactPalette => $"palette slot {G(0)} = {G(1)}",
            VTankNodeTypes.AnySimilarColor => "color (any similar)",
            VTankNodeTypes.SimilarColorArmorType => "color (armor type)",
            VTankNodeTypes.SlotSimilarColor => "color (slot)",
            VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE => "buffed tinked target melee",
            VTankNodeTypes.DisabledRule => "(rule disabled)",
            _ => $"node {c.NodeType}",
        };
    }

    // ── Vocabulary for the face ──────────────────────────────────────────

    public static LootEditVocab BuildVocab()
    {
        var v = new LootEditVocab();
        foreach (VTankLootAction a in Actions) v.Actions.Add(new LootEditName { Id = (int)a, Name = ActionName(a) });
        foreach (int t in VTankNodeTypes.All)
        {
            (string editor, string table) = EditorFor(t);
            v.NodeTypes.Add(new LootEditNodeType
            {
                Id = t, Name = NodeName(t), Lines = Math.Max(0, VTankNodeTypes.GetDataLineCount(t)),
                Labels = new List<string>(LineLabels(t)), Defaults = DefaultLines(t), Editor = editor, KeyTable = table,
                NamedValues = UsesValueNames(t),
            });
        }
        Fill(v.ObjectClasses, ObjectClasses);
        Fill(v.LongKeys, LongKeys);
        Fill(v.DoubleKeys, DoubleKeys);
        Fill(v.StringKeys, StringKeys);
        Fill(v.Skills, Skills);
        foreach (int key in KeysWithValueTables)
        {
            var vt = new LootEditValueTable { Key = key, Flags = ValueTableIsFlags(key) };
            Fill(vt.Values, ValueTableFor(key)!);
            v.LongValueTables.Add(vt);
        }
        return v;

        static void Fill(List<LootEditName> into, (int Id, string Name)[] from)
        {
            foreach (var (id, name) in from) into.Add(new LootEditName { Id = id, Name = name });
        }
    }
}
