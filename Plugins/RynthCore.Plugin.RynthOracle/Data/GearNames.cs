using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// Names for the numbers the Gear tab reads: spell families (portal.dat SpellBase.Category, ACE
/// SpellCategory) and equipment sets (PropertyInt.EquipmentSetId 265, ACE EquipmentSet), from
/// Resources\spellcategories.csv and equipmentsets.csv (Tools\RynthOracle.QuestPackGen\Make-GearNames.ps1).
/// A number missing from a table still shows ("Family 812", "Set 301").
/// </summary>
internal static class GearNames
{
    private static Dictionary<int, string>? _families, _sets;

    /// <summary>ACE's name for a spell family ("SlashWardProtection"), or "".</summary>
    public static string FamilyRaw(uint category)
    {
        _families ??= Load("spellcategories.csv");
        return _families.TryGetValue((int)category, out string? n) ? n : "";
    }

    /// <summary>
    /// A family for a narrow column: "Slash Ward", "Extra Fire Resistance" as "Fire Resistance",
    /// "Strength" for StrengthRaising (UB-ILT's FormatSpellFamilyShort, with the spaces put in).
    /// </summary>
    public static string FamilyLabel(uint category)
    {
        if (category == 0) return "?";   // no family known (ACE's 0 is "Undef")
        string fam = FamilyRaw(category);
        if (fam.Length == 0) return "Family " + category.ToString(CultureInfo.InvariantCulture);
        if (fam.EndsWith("Protection", StringComparison.Ordinal) && fam.Length > "Protection".Length)
            fam = fam[..^"Protection".Length];
        else if (fam.StartsWith("Extra", StringComparison.Ordinal) && fam.EndsWith("ResistanceRaising", StringComparison.Ordinal)
                 && fam.Length > "Extra".Length + "ResistanceRaising".Length)
            fam = fam["Extra".Length..^"Raising".Length];
        else if (fam.EndsWith("Raising", StringComparison.Ordinal) && fam.Length > "Raising".Length)
            fam = fam[..^"Raising".Length];
        return Pretty(fam);
    }

    /// <summary>An equipment set's name ("Shimmering Shadows Set", "Paragon Melee"); "-" for none.</summary>
    public static string SetName(int setId)
    {
        if (setId <= 0) return "-";
        _sets ??= Load("equipmentsets.csv");
        return _sets.TryGetValue(setId, out string? n) ? Pretty(n) : "Set " + setId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>CamelCase to words: "ShimmeringShadowsSet" as "Shimmering Shadows Set", "PKDamage" as "PK Damage".</summary>
    public static string Pretty(string camel)
    {
        if (string.IsNullOrEmpty(camel)) return "";
        var sb = new StringBuilder(camel.Length + 8);
        for (int i = 0; i < camel.Length; i++)
        {
            char c = camel[i];
            if (i > 0 && c != '_')
            {
                char p = camel[i - 1];
                bool next = i + 1 < camel.Length && char.IsLower(camel[i + 1]);
                // A capital after a lower-case letter or a digit, or the last capital of a run before lower case.
                if (char.IsUpper(c) && (char.IsLower(p) || char.IsDigit(p) || (char.IsUpper(p) && next))) sb.Append(' ');
                else if (char.IsDigit(c) && char.IsLetter(p)) sb.Append(' ');
            }
            sb.Append(c == '_' ? ' ' : c);
        }
        return sb.ToString();
    }

    private static Dictionary<int, string> Load(string file)
    {
        var map = new Dictionary<int, string>();
        try
        {
            using StreamReader r = Csv.OpenEmbedded(file);
            r.ReadLine();   // header
            while (r.ReadLine() is string line)
            {
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (int.TryParse(line.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                    map[id] = line[(comma + 1)..].Trim();
            }
        }
        catch
        {
            // A missing table only costs names ("Family 287").
        }
        return map;
    }
}
