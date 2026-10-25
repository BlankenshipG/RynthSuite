using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// An item as one Mag-Tools ItemInfo line (Mag-Plugins Mag-Tools/ItemInfo/ItemInfo.cs, the text
/// Mag-Tools prints when an item is identified), read from the appraisal:
///   "Gold Heavy Crossbow (Crossbow), Shimmering Shadows Set, AL 300, CS, Tinks 3, 35.25-47, +12,
///    +20%, +5%a, 10%md, Legendary Strength, Wield Lvl 180, Diff 300, Craft 9, Value 12,345, BU 50,
///    [D 3, CD 2]"
/// Same order, abbreviations and spell filter as Mag-Tools. Added for the Gear tab: the item's level
/// after its set ("Lvl 12/50"), the rating cluster with T11 modifier ratings included, and the
/// T11 modifier lines at the end. Mag's "buffed values" (its own weapon damage maths) are left out.
/// </summary>
internal static class MagFormat
{
    // ACE PropertyInt ids (Decal's LongValueKey numbers where Decal has them).
    public const int PropBurden = 5, PropValue = 19, PropArmorLevel = 28, PropResistMagic = 36, PropDamage = 44,
                     PropWorkmanship = 105, PropDifficulty = 109, PropActivationLevel = 115, PropMaterial = 131,
                     PropWieldRequirement = 158, PropWieldSkill = 159, PropWieldDifficulty = 160, PropTinks = 171,
                     PropActivationSkill = 176, PropImbued = 179, PropElementalBonus = 204, PropMastery = 353;

    // ACE PropertyFloat ids.
    public const int FloatSlashProt = 13, FloatPierceProt = 14, FloatBludgeonProt = 15, FloatColdProt = 16,
                     FloatFireProt = 17, FloatAcidProt = 18, FloatLightningProt = 19, FloatVariance = 22,
                     FloatMeleeDefense = 29, FloatOffense = 62, FloatDamageMod = 63, FloatManaConversion = 144,
                     FloatMissileDefense = 149, FloatMagicDefense = 150, FloatElementalVsMonsters = 152;

    /// <summary>The int properties <see cref="GearScanner"/> reads for the line.</summary>
    public static readonly int[] IntProps =
    {
        PropBurden, PropValue, PropArmorLevel, PropResistMagic, PropDamage, PropWorkmanship, PropDifficulty,
        PropActivationLevel, PropMaterial, PropWieldRequirement, PropWieldSkill, PropWieldDifficulty, PropTinks,
        PropActivationSkill, PropImbued, PropElementalBonus, PropMastery,
    };

    /// <summary>The float properties <see cref="GearScanner"/> reads for the line.</summary>
    public static readonly int[] FloatProps =
    {
        FloatSlashProt, FloatPierceProt, FloatBludgeonProt, FloatColdProt, FloatFireProt, FloatAcidProt, FloatLightningProt,
        FloatVariance, FloatMeleeDefense, FloatOffense, FloatDamageMod, FloatManaConversion, FloatMissileDefense,
        FloatMagicDefense, FloatElementalVsMonsters,
    };

    /// <summary>Mag's imbue abbreviations by PropertyInt.ImbuedEffect bit.</summary>
    private static readonly (int Bit, string Text)[] Imbues =
    {
        (1, "CS"), (2, "CB"), (4, "AR"), (8, "SlashRend"), (16, "PierceRend"), (32, "BludgeRend"), (64, "AcidRend"),
        (128, "FrostRend"), (256, "LightRend"), (512, "FireRend"), (1024, "MeleeImbue"), (4096, "MagicImbue"),
        (8192, "Hematited"), (536870912, "MagicAbsorb"),
    };

    /// <summary>The weapon-buff spell families Mag-Tools keeps only at level 7 and up.</summary>
    private static bool IsWeaponBuffFamily(uint family) => family is >= 152 and <= 158 or 195 or 325;

    private static readonly string[] LowLevels = { " I", " II", " III", " IV", " V", " VI" };

    /// <summary>The whole Mag-style line for one piece.</summary>
    public static string Describe(GearPiece p, Func<uint, SpellMeta>? lookup, (int Prop, int Value)[] ratings)
    {
        var sb = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;

        int material = p.Int(PropMaterial);
        if (material > 0)
        {
            string m = GearNames.Material(material);
            sb.Append(m.Length > 0 ? m : "unknown material " + material.ToString(inv)).Append(' ');
        }
        sb.Append(p.Name);
        int mastery = p.Int(PropMastery);
        if (mastery > 0)
        {
            string m = GearNames.Mastery(mastery);
            sb.Append(" (").Append(m.Length > 0 ? m : "Unknown mastery " + mastery.ToString(inv)).Append(')');
        }

        if (p.SetId != 0) sb.Append(", ").Append(GearNames.SetName(p.SetId));
        if (p.MaxLevel > 0 && p.Leveled) sb.Append(", Lvl ").Append(p.Level.ToString(inv)).Append('/').Append(p.MaxLevel.ToString(inv));

        int al = p.Int(PropArmorLevel);
        if (al > 0) sb.Append(", AL ").Append(al.ToString(inv));

        int imbued = p.Int(PropImbued);
        if (imbued > 0)
        {
            sb.Append(',');
            foreach (var (bit, text) in Imbues)
                if ((imbued & bit) == bit) sb.Append(' ').Append(text);
        }

        int tinks = p.Int(PropTinks);
        if (tinks > 0) sb.Append(", Tinks ").Append(tinks.ToString(inv));

        int maxDamage = p.Int(PropDamage);
        double variance = p.Float(FloatVariance, 0);
        if (maxDamage != 0 && variance != 0)
            sb.Append(", ").Append((maxDamage - maxDamage * variance).ToString("N2", inv)).Append('-').Append(maxDamage.ToString(inv));
        else if (maxDamage != 0)
            sb.Append(", ").Append(maxDamage.ToString(inv));

        int elemental = p.Int(PropElementalBonus);
        if (elemental != 0) sb.Append(", +").Append(elemental.ToString(inv));

        AppendPercent(sb, p.Float(FloatDamageMod, 1), "+", "%", 0);
        AppendPercent(sb, p.Float(FloatElementalVsMonsters, 1), "+", "%vs. Monsters", 0);
        AppendPercent(sb, p.Float(FloatOffense, 1), "+", "%a", 0);
        AppendPercent(sb, p.Float(FloatMeleeDefense, 1), "", "%md", 0);
        AppendPercent(sb, p.Float(FloatMagicDefense, 1), "", "%mgc.d", 1);
        AppendPercent(sb, p.Float(FloatMissileDefense, 1), "", "%msl.d", 1);
        double manaC = p.Float(FloatManaConversion, 0);
        if (manaC != 0) sb.Append(", ").Append(Math.Round(manaC * 100).ToString(inv)).Append("%mc");

        if (lookup != null)
            foreach (string spell in ShownSpells(p, lookup)) sb.Append(", ").Append(spell);

        int wieldDiff = p.Int(PropWieldDifficulty);
        if (wieldDiff > 0)
        {
            int req = p.Int(PropWieldRequirement), skill = p.Int(PropWieldSkill);
            if (req == 7 && skill == 1) sb.Append(", Wield Lvl ").Append(wieldDiff.ToString(inv));
            else
            {
                string s = GearNames.Skill(skill);
                sb.Append(", ").Append(s.Length > 0 ? s : "Unknown skill: " + skill.ToString(inv)).Append(' ').Append(wieldDiff.ToString(inv));
            }
        }

        // "Melee Defense 300 to Activate", unless the wield requirement already says as much.
        int actLevel = p.Int(PropActivationLevel), actSkill = p.Int(PropActivationSkill);
        if (actLevel > 0 && (p.Int(PropWieldSkill) != actSkill || wieldDiff < actLevel))
        {
            string s = GearNames.Skill(actSkill);
            sb.Append(", ").Append(s.Length > 0 ? s : "Unknown skill: " + actSkill.ToString(inv))
              .Append(' ').Append(actLevel.ToString(inv)).Append(" to Activate");
        }

        int diff = p.Int(PropDifficulty);
        if (diff > 0) sb.Append(", Diff ").Append(diff.ToString(inv));

        int work = p.Int(PropWorkmanship);
        if (work > 0 && tinks != 10) sb.Append(", Craft ").Append(work.ToString(inv));   // Mag hides it on a 10-tink item

        // Unenchantable armour: its fixed protections, Slash/Pierce/Bludge/Cold/Fire/Acid/Lightning.
        if ((p.ItemType & GearAnalysis.ItemArmor) != 0 && Unenchantable(p))
        {
            sb.Append(", [");
            int[] prots = { FloatSlashProt, FloatPierceProt, FloatBludgeonProt, FloatColdProt, FloatFireProt, FloatAcidProt, FloatLightningProt };
            for (int i = 0; i < prots.Length; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(p.Float(prots[i], 0).ToString("N1", inv));
            }
            sb.Append(']');
        }

        int value = p.Int(PropValue);
        if (value > 0) sb.Append(", Value ").Append(value.ToString("N0", inv));
        int burden = p.Int(PropBurden);
        if (burden > 0) sb.Append(", BU ").Append(burden.ToString(inv));

        var shown = ratings.Where(r => r.Value > 0).ToList();
        if (shown.Count > 0)
            sb.Append(", [").Append(string.Join(", ", shown.Select(r => GearAnalysis.RatingShort(r.Prop) + " " + r.Value.ToString(inv)))).Append(']');

        if (p.Modifiers.Length > 0)
        {
            sb.Append(", T11 [").Append(string.Join(", ", p.Modifiers.Select(m => m.Text))).Append(']');
            if (p.ZoneLocked) sb.Append(" (zone locked)");
        }
        return sb.ToString();
    }

    /// <summary>Mag's "+20%" style: a multiplier shown as the percent it adds (only when it isn't 1).</summary>
    private static void AppendPercent(StringBuilder sb, double mod, string sign, string suffix, int digits)
    {
        if (mod == 1) return;
        sb.Append(", ").Append(sign).Append(Math.Round((mod - 1) * 100, digits).ToString(CultureInfo.InvariantCulture)).Append(suffix);
    }

    /// <summary>ResistMagic 9999: the item can't be enchanted (Mag's "Unenchantable").</summary>
    private static bool Unenchantable(GearPiece p) => p.Int(PropResistMagic) >= 9999;

    /// <summary>
    /// Mag-Tools' spell filter, highest id first: everything on an item that isn't loot (no
    /// material); on loot the cantrips and the notable spells, without the level 1-7 noise.
    /// Banes and Impenetrability show only on unenchantable gear (on the rest they get recast).
    /// </summary>
    internal static List<string> ShownSpells(GearPiece p, Func<uint, SpellMeta> lookup)
    {
        var shown = new List<string>();
        bool loot = p.Int(PropMaterial) > 0;
        bool unenchantable = Unenchantable(p);
        foreach (uint id in p.Inscribed.Distinct().OrderByDescending(i => i))
        {
            SpellMeta s = lookup(id);
            string n = s.Name;
            if (!loot || Keep(n, s, unenchantable)) shown.Add(n);
        }
        return shown;
    }

    private static bool Keep(string n, SpellMeta s, bool unenchantable)
    {
        if (n.Contains("Minor Impenetrability") || n.Contains("Major Impenetrability") || n.Contains("Epic Impenetrability")
            || n.Contains("Legendary Impenetrability") || n.Contains("Augmented"))
            return true;
        bool baneOrImpen = n.Contains(" Bane") || n.Contains("Impen") || n.StartsWith("Brogard", StringComparison.Ordinal);
        if (baneOrImpen) return unenchantable;
        if (IsWeaponBuffFamily(s.Category)) return s.Power == 300 || s.Power >= 400;
        foreach (string level in LowLevels) if (n.EndsWith(level, StringComparison.Ordinal)) return false;
        if (s.Power == 300) return false;
        if (n.Contains("Incantation")) return false;
        return true;
    }
}
