// MagItemDescriber.cs — one-line "Mag-style" item info (Mag-Tools ItemInfo / UtilityBelt ub-IT ItemInfo).
//
//   Gold Ornate Long Sword (Slash Sword), Noble Relic Set, CS, Undead Slayer, Tinks 4,
//   Applied: Steel x2, 152.48-236, 0.35v, +18%a, 12%md, Legendary Blood Thirst, Wield Lvl 180,
//   Heavy Weapons 375, Diff 270, Craft 8, [D 3, CD 2]
//
// Field order and per-class gating follow ub-IT (Tools/ItemInfo.cs ItemDescriptions.ToString,
// Lib/ItemDescribeMagShorthand.cs, Lib/ItemInfoHelper/ItemIdentifyFieldSet.cs); labels and the
// spell filter follow Mag-Tools. All ids are raw ACE properties (RynthCore reads CBaseQualities by
// STypeInt/STypeFloat/STypeString id — not Decal's remapped LongValueKey/DoubleValueKey numbers).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.Loot;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

/// <summary>Property reads for one item. Implemented over the live object cache (see
/// <see cref="CacheItemPropertySource"/>); a missing property returns false / empty.</summary>
internal interface IItemPropertySource
{
    string Name { get; }
    AcObjectClass ObjectClass { get; }
    bool TryGetInt(uint stype, out int value);
    bool TryGetDouble(uint stype, out double value);
    string GetString(uint stype);
    /// <summary>Spell ids on the item (inscribed + cantrips), unsorted.</summary>
    IReadOnlyList<int> SpellIds { get; }
}

/// <summary>Output options (Items → Display → Item Info, or /ra iteminfo value|verbose).</summary>
internal readonly record struct MagItemInfoOptions(bool ShowValueAndBurden, bool VerboseSpells);

/// <summary>Builds the Mag-style item info line.</summary>
internal static class MagItemDescriber
{
    // ── ACE PropertyInt ids ──────────────────────────────────────────────────
    private const uint IntBurden              = 5;    // EncumbranceVal
    private const uint IntValue               = 19;
    private const uint IntArmorLevel          = 28;
    private const uint IntResistMagic         = 36;   // >= 9999 = unenchantable
    private const uint IntMaxDamage           = 44;   // Damage
    private const uint IntDamageType          = 45;
    private const uint IntAttackType          = 47;
    private const uint IntStructure           = 92;   // keyring uses remaining
    private const uint IntWorkmanship         = 105;
    private const uint IntItemDifficulty      = 109;  // arcane lore requirement ("Diff")
    private const uint IntActivationLevel     = 115;  // ItemSkillLevelLimit
    private const uint IntMaterial            = 131;
    private const uint IntWieldReq            = 158;
    private const uint IntWieldSkill          = 159;
    private const uint IntWieldValue          = 160;
    private const uint IntSlayerCreature      = 166;
    private const uint IntNumItemsInMaterial  = 170;  // salvage: workmanship = 105 / 170
    private const uint IntTinks               = 171;
    private const uint IntActivationSkill     = 176;  // AppraisalItemSkill
    private const uint IntImbued              = 179;  // ImbuedEffect (+ 303..306 for 2..5)
    private const uint IntNumKeys             = 193;
    private const uint IntElementalBonus      = 204;
    private const uint IntEquipmentSet        = 265;
    private const uint IntWieldReq2           = 270;
    private const uint IntWieldSkill2         = 271;
    private const uint IntWieldValue2         = 272;
    private const uint IntWeaponType          = 353;  // mastery
    private const uint IntUseRequiresSkill    = 366;
    private const uint IntUseRequiresSkillLvl = 367;
    private const uint IntUseRequiresSpec     = 368;
    private const uint IntUseRequiresLevel    = 369;
    private const uint IntSplitArrows         = 9031; // ACECustom SplitArrowCount

    // ── ACE PropertyFloat ids ────────────────────────────────────────────────
    private const uint FloatArmorVsSlash      = 13;   // 13..19 = Slash/Pierce/Bludgeon/Cold/Fire/Acid/Electric
    private const uint FloatVariance          = 22;
    private const uint FloatMaxVelocity       = 26;
    private const uint FloatMeleeDefense      = 29;   // WeaponDefense
    private const uint FloatAttack            = 62;   // WeaponOffense
    private const uint FloatDamageMod         = 63;
    private const uint FloatCritMultiplier    = 136;
    private const uint FloatManaConversion    = 144;
    private const uint FloatCritFrequency     = 147;
    private const uint FloatMissileDefense    = 149;
    private const uint FloatMagicDefense      = 150;
    private const uint FloatVsMonsters        = 152;  // ElementalDamageMod
    private const uint FloatIgnoreArmor       = 155;  // armor cleaving

    // ── ACE PropertyString ids ───────────────────────────────────────────────
    private const uint StringTinkerLog        = 9007; // ACECustom: CSV of applied material ids

    /// <summary>Gear ratings are small (1..~50). Anything above is a wrong key leaking through.</summary>
    private const int MaxPlausibleRating = 200;

    /// <summary>Class-specific fields (ub-IT ItemIdentifyFieldSet). Generic fields print for every class.</summary>
    [Flags]
    private enum Field
    {
        None           = 0,
        DamageType     = 1 << 0,
        CritStats      = 1 << 1,
        Splits         = 1 << 2,
        Cleaves        = 1 << 3,
        Slayer         = 1 << 4,
        Range          = 1 << 5,
        Damage         = 1 << 6,
        AttackBonus    = 1 << 7,
        MeleeDefense   = 1 << 8,
        MissileDefense = 1 << 9,
        MagicDefense   = 1 << 10,
        ManaConversion = 1 << 11,
        ArmorCleave    = 1 << 12,
        WeaponDefenses = MeleeDefense | MissileDefense | MagicDefense,
    }

    private static Field FieldsFor(AcObjectClass cls) => cls switch
    {
        AcObjectClass.MissileWeapon => Field.DamageType | Field.CritStats | Field.Splits | Field.Cleaves
                                     | Field.Slayer | Field.Range | Field.Damage | Field.WeaponDefenses
                                     | Field.ArmorCleave,
        AcObjectClass.MeleeWeapon   => Field.DamageType | Field.CritStats | Field.Cleaves | Field.Slayer
                                     | Field.Damage | Field.AttackBonus | Field.WeaponDefenses
                                     | Field.ArmorCleave,
        AcObjectClass.WandStaffOrb  => Field.DamageType | Field.CritStats | Field.Slayer | Field.Damage
                                     | Field.WeaponDefenses | Field.ManaConversion,
        AcObjectClass.Jewelry       => Field.None,
        // Armor / clothing / misc: keep Mag's generic bonus lines (shields roll %md etc.).
        _                           => Field.AttackBonus | Field.WeaponDefenses | Field.ManaConversion,
    };

    /// <summary>Full Mag-style line for one item (name first, comma-separated fields).</summary>
    public static string Describe(IItemPropertySource item, MagItemInfoOptions options)
    {
        var sb = new StringBuilder(256);
        AcObjectClass cls = item.ObjectClass;
        Field fields = FieldsFor(cls);
        bool Has(Field f) => (fields & f) == f;

        int Int(uint id) => item.TryGetInt(id, out int v) ? v : 0;
        double Dbl(uint id, double fallback) => item.TryGetDouble(id, out double v) ? v : fallback;

        // ── Name: "[Material ]Name (DamageType Mastery)" ────────────────────────
        int material = Int(IntMaterial);
        if (material > 0)
        {
            string matName = MagItemInfoTables.Materials.TryGetValue(material, out string? m) ? m : $"material {material}";
            // Loot names usually omit the material; don't double it when the name already starts with it.
            if (!item.Name.StartsWith(matName, StringComparison.OrdinalIgnoreCase))
                sb.Append(matName).Append(' ');
        }
        sb.Append(item.Name);

        if (Has(Field.DamageType))
        {
            string dmgType = MagItemInfoTables.DamageTypeName(Int(IntDamageType));
            int mastery = Int(IntWeaponType);
            string masteryName = mastery > 0
                ? (MagItemInfoTables.Masteries.TryGetValue(mastery, out string? mn) ? mn : $"Unknown mastery {mastery}")
                : string.Empty;
            string inner = $"{dmgType} {masteryName}".Trim();
            if (inner.Length > 0) sb.Append(" (").Append(inner).Append(')');
        }

        // ── Set ─────────────────────────────────────────────────────────────────
        int set = Int(IntEquipmentSet);
        if (set > 0)
            sb.Append(", ").Append(MagItemInfoTables.EquipmentSets.TryGetValue(set, out string? setName) ? setName : $"Unknown set {set}");

        // ── Armor level ─────────────────────────────────────────────────────────
        int al = Int(IntArmorLevel);
        if (al > 0) sb.Append(", AL ").Append(al);

        // ── Imbues (ImbuedEffect 179 plus ImbuedEffect2..5 = 303..306) ─────────
        int imbued = Int(IntImbued) | Int(303) | Int(304) | Int(305) | Int(306);
        AppendImbues(sb, imbued);

        if (Has(Field.ArmorCleave) && Dbl(FloatIgnoreArmor, 0) != 0)
            sb.Append(", AC");

        if (Has(Field.CritStats))
        {
            double critMult = Dbl(FloatCritMultiplier, 0);
            if (critMult > 0) sb.Append(", CritMult (").Append(Fmt(critMult)).Append(')');
            double critFreq = Dbl(FloatCritFrequency, 0);
            if (critFreq > 0) sb.Append(", CritFreq (").Append(Fmt(critFreq)).Append(')');
        }

        if (Has(Field.Splits))
        {
            int splits = Int(IntSplitArrows);
            if (splits > 0) sb.Append(", Splits (").Append(splits).Append(')');
        }

        if (Has(Field.Range))
        {
            int yards = MissileRangeYards(Dbl(FloatMaxVelocity, 0));
            if (yards > 0) sb.Append(", Range ").Append(yards).Append("yd");
        }

        if (Has(Field.Cleaves))
        {
            int attackType = Int(IntAttackType);
            int mastery = Int(IntWeaponType);
            bool cleaving = (attackType == 4 && mastery == 11) || (attackType == 1 && mastery != 11 && mastery != 0);
            if (cleaving) sb.Append(", Cleaving");
            else if (attackType is 160 or 166) sb.Append(", Multi-Strike");
            else if (attackType == 486) sb.Append(", Multi-Strike (3)");
        }

        // ── Slayer ──────────────────────────────────────────────────────────────
        if (Has(Field.Slayer))
        {
            string? slayer = MagItemInfoTables.CreatureTypeName(Int(IntSlayerCreature));
            if (slayer != null) sb.Append(", ").Append(slayer).Append(" Slayer");
        }

        // ── Tinks + applied materials (TinkerLog) ──────────────────────────────
        int tinks = Int(IntTinks);
        if (tinks > 0) sb.Append(", Tinks ").Append(tinks);
        string applied = FormatTinkerLog(item.GetString(StringTinkerLog));
        if (applied.Length > 0) sb.Append(", Applied: ").Append(applied);

        // ── Damage ──────────────────────────────────────────────────────────────
        if (Has(Field.Damage))
            AppendDamage(sb, cls, Int(IntMaxDamage), Dbl(FloatVariance, 0), Int(IntElementalBonus),
                Dbl(FloatDamageMod, 1), Dbl(FloatVsMonsters, 1));

        // ── Attack / defense / mana conversion percents ───────────────────────
        if (Has(Field.AttackBonus))    AppendPercent(sb, Dbl(FloatAttack, 1), "%a", 0);
        if (Has(Field.MeleeDefense))   AppendPercent(sb, Dbl(FloatMeleeDefense, 1), "%md", 0);
        if (Has(Field.MagicDefense))   AppendPercent(sb, Dbl(FloatMagicDefense, 1), "%mgc.d", 1);
        if (Has(Field.MissileDefense)) AppendPercent(sb, Dbl(FloatMissileDefense, 1), "%msl.d", 1);
        if (Has(Field.ManaConversion))
        {
            double mc = Dbl(FloatManaConversion, 0);
            if (mc != 0) sb.Append(", ").Append(Math.Round(mc * 100).ToString(CultureInfo.InvariantCulture)).Append("%mc");
        }

        // ── Spells ──────────────────────────────────────────────────────────────
        bool unenchantable = Int(IntResistMagic) >= 9999;
        AppendSpells(sb, item.SpellIds, unenchantable, options.VerboseSpells);

        // ── Requirements ────────────────────────────────────────────────────────
        AppendWieldRequirement(sb, Int(IntWieldReq), Int(IntWieldSkill), Int(IntWieldValue));
        AppendWieldRequirement(sb, Int(IntWieldReq2), Int(IntWieldSkill2), Int(IntWieldValue2));

        int useLevel = Int(IntUseRequiresLevel);
        if (useLevel > 0) sb.Append(", Lvl ").Append(useLevel);

        // "Melee Defense 300 to Activate" — hidden when the wield requirement already covers it.
        int actSkill = Int(IntActivationSkill), actLevel = Int(IntActivationLevel);
        if (actLevel > 0 && (Int(IntWieldSkill) != actSkill || Int(IntWieldValue) < actLevel))
            sb.Append(", ").Append(SkillName(actSkill)).Append(' ').Append(actLevel).Append(" to Activate");

        // Summoning essences / gems.
        int useSkill = Int(IntUseRequiresSkill), useSkillLvl = Int(IntUseRequiresSkillLvl), useSpec = Int(IntUseRequiresSpec);
        if (useSkill > 0 && useSkillLvl > 0) sb.Append(", ").Append(SkillName(useSkill)).Append(' ').Append(useSkillLvl);
        if (useSpec > 0 && useSkillLvl > 0) sb.Append(", Spec ").Append(SkillName(useSpec)).Append(' ').Append(useSkillLvl);

        int diff = Int(IntItemDifficulty);
        if (diff > 0) sb.Append(", Diff ").Append(diff);

        // ── Workmanship ─────────────────────────────────────────────────────────
        int work = Int(IntWorkmanship);
        if (cls == AcObjectClass.Salvage)
        {
            int items = Int(IntNumItemsInMaterial);
            if (work > 0)
            {
                double avg = items > 0 ? (double)work / items : work;
                sb.Append(", Work ").Append(avg.ToString("N2", CultureInfo.InvariantCulture));
            }
        }
        else if (work > 0 && tinks != 10) // Mag: hide craft once fully tinked
        {
            sb.Append(", Craft ").Append(work);
        }

        // ── Unenchantable armor: base protections [S/P/B/C/F/A/L] ─────────────
        if (cls == AcObjectClass.Armor && unenchantable)
        {
            sb.Append(", [");
            for (uint i = 0; i < 7; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(Dbl(FloatArmorVsSlash + i, 0).ToString("N1", CultureInfo.InvariantCulture));
            }
            sb.Append(']');
        }

        if (options.ShowValueAndBurden)
        {
            int value = Int(IntValue);
            if (value > 0) sb.Append(", Value ").Append(value.ToString("N0", CultureInfo.InvariantCulture));
            int burden = Int(IntBurden);
            if (burden > 0) sb.Append(", BU ").Append(burden);
        }

        // ── Ratings ─────────────────────────────────────────────────────────────
        AppendRatings(sb, item);

        if (cls == AcObjectClass.Misc && item.Name.Contains("Keyring", StringComparison.OrdinalIgnoreCase))
            sb.Append(", Keys: ").Append(Int(IntNumKeys)).Append(", Uses: ").Append(Int(IntStructure));

        return sb.ToString();
    }

    /// <summary>Mag imbue labels, plus the ACE flags Mag predates (missile imbue, nether rend).</summary>
    private static void AppendImbues(StringBuilder sb, int mask)
    {
        if (mask == 0) return;
        int start = sb.Length;
        void Flag(int bit, string label) { if ((mask & bit) != 0) sb.Append(' ').Append(label); }
        sb.Append(',');
        Flag(0x0001, "CS");
        Flag(0x0002, "CB");
        Flag(0x0004, "AR");
        Flag(0x0008, "SlashRend");
        Flag(0x0010, "PierceRend");
        Flag(0x0020, "BludgeRend");
        Flag(0x0040, "AcidRend");
        Flag(0x0080, "FrostRend");
        Flag(0x0100, "LightRend");
        Flag(0x0200, "FireRend");
        Flag(0x4000, "NetherRend");
        Flag(0x0400, "MeleeImbue");
        Flag(0x0800, "MissileImbue");
        Flag(0x1000, "MagicImbue");
        Flag(0x2000, "Hematited");
        Flag(0x20000000, "MagicAbsorb");
        if (sb.Length == start + 1) sb.Length = start; // only unknown bits — drop the lone comma
    }

    /// <summary>Melee "min-max", variance, "+elem", "+mod%", wand "+N% vs. Monsters".</summary>
    private static void AppendDamage(StringBuilder sb, AcObjectClass cls, int maxDmg, double variance,
        int elemBonus, double dmgMod, double vsMonsters)
    {
        if (cls != AcObjectClass.MissileWeapon && maxDmg > 0)
        {
            sb.Append(", ");
            if (variance > 0)
                sb.Append((maxDmg - maxDmg * variance).ToString("N2", CultureInfo.InvariantCulture)).Append('-');
            sb.Append(maxDmg);
        }
        if (variance > 0)
            sb.Append(", ").Append(Math.Round(variance, 2).ToString(CultureInfo.InvariantCulture)).Append('v');
        if (elemBonus != 0)
            sb.Append(", +").Append(elemBonus);
        // A missing/zero multiplier means "no bonus" (0 would print as -100%).
        if (dmgMod > 0 && dmgMod != 1)
            sb.Append(", +").Append(Math.Round((dmgMod - 1) * 100).ToString(CultureInfo.InvariantCulture)).Append('%');
        if (vsMonsters > 0 && vsMonsters != 1)
            sb.Append(", +").Append(Math.Round((vsMonsters - 1) * 100).ToString(CultureInfo.InvariantCulture)).Append("% vs. Monsters");
    }

    /// <summary>Mag "18%a" from a 1.0-based multiplier; skips missing (≤0) and neutral 1.0.</summary>
    private static void AppendPercent(StringBuilder sb, double multiplier, string suffix, int decimals)
    {
        if (multiplier <= 0 || multiplier == 1) return;
        double pct = Math.Round((multiplier - 1) * 100, decimals);
        sb.Append(", ").Append(pct.ToString(CultureInfo.InvariantCulture)).Append(suffix);
    }

    /// <summary>"Wield Lvl 180" for a level requirement, else "Heavy Weapons 375".</summary>
    private static void AppendWieldRequirement(StringBuilder sb, int reqType, int skillOrAttr, int value)
    {
        if (value <= 0) return;
        if (reqType == 7) // WieldRequirement.Level
            sb.Append(", Wield Lvl ").Append(value);
        else
            sb.Append(", ").Append(SkillName(skillOrAttr)).Append(' ').Append(value);
    }

    private static string SkillName(int skill) =>
        MagItemInfoTables.Skills.TryGetValue(skill, out string? name) ? name : $"Skill {skill}";

    /// <summary>
    /// Mag-Tools spell filter (ub-IT applies it to every item): keep cantrip Impen, Augmented
    /// (trinket) spells, banes/Impen only on unenchantable gear, level 7+ weapon auras, and
    /// named high-tier spells (cantrips); drop I–VI, level-7 lore buffs and Incantations.
    /// Verbose lists every spell (capped) for troubleshooting.
    /// </summary>
    private static void AppendSpells(StringBuilder sb, IReadOnlyList<int> spellIds, bool unenchantable, bool verbose)
    {
        if (spellIds.Count == 0) return;
        var ids = new List<int>(spellIds);
        ids.Sort();
        ids.Reverse(); // Mag lists highest spell id first

        var seen = new HashSet<int>();
        const int maxVerbose = 26;
        int shown = 0, skippedVerbose = 0;
        foreach (int id in ids)
        {
            if (!seen.Add(id)) continue;
            bool known = SpellDatabase.HasSpell(id);
            if (verbose)
            {
                // Verbose is the troubleshooting view: list ids SpellData.txt doesn't know too.
                if (shown >= maxVerbose) { skippedVerbose++; continue; }
                sb.Append(shown == 0 ? ", Spells: " : "; ")
                  .Append(known ? SpellDatabase.GetSpellName(id) : $"Spell {id}");
                shown++;
                continue;
            }
            if (!known) continue;
            string name = SpellDatabase.GetSpellName(id);
            SpellDatabase.TryGetSpellMeta(id, out int family, out int difficulty);
            if (ShouldShowSpell(name, family, difficulty, unenchantable))
                sb.Append(", ").Append(name);
        }
        if (skippedVerbose > 0) sb.Append("; (+").Append(skippedVerbose).Append(" more spells)");
    }

    private static bool ShouldShowSpell(string name, int family, int difficulty, bool unenchantable)
    {
        if (name.Contains("Minor Impenetrability") || name.Contains("Major Impenetrability")
            || name.Contains("Epic Impenetrability") || name.Contains("Legendary Impenetrability"))
            return true;
        if (name.Contains("Augmented"))
            return true;

        // Banes / Impen / Brogard's: only meaningful on unenchantable gear (loot armor
        // already reflects them in AL and protections).
        bool baneLike = name.Contains(" Bane") || name.Contains("Impen") || name.StartsWith("Brogard", StringComparison.Ordinal);
        if (baneLike) return unenchantable;

        // Weapon auras (Elysa's Sight .. Swift Killer, Hermetic Link, Vision of the Hunter):
        // level 7 (300) and 8+ (400+) only.
        if ((family >= 152 && family <= 158) || family == 195 || family == 325)
            return difficulty == 300 || difficulty >= 400;

        if (name.EndsWith(" I") || name.EndsWith(" II") || name.EndsWith(" III")
            || name.EndsWith(" IV") || name.EndsWith(" V") || name.EndsWith(" VI"))
            return false;
        if (difficulty == 300) return false;       // level-7 lore buffs (Might of the Lugians …)
        if (name.Contains("Incantation")) return false;
        return true;                                // cantrips: Legendary Strength, Epic Defender …
    }

    /// <summary>Rating cluster "[D 11, C 8, CD 4]" from ACE Gear* ratings 370–389.</summary>
    private static void AppendRatings(StringBuilder sb, IItemPropertySource item)
    {
        int start = sb.Length;
        bool first = true;
        void Part(uint id, string label)
        {
            if (!item.TryGetInt(id, out int v) || v <= 0 || v > MaxPlausibleRating) return;
            sb.Append(first ? ", [" : ", ").Append(label).Append(' ').Append(v);
            first = false;
        }
        Part(370, "D");     // GearDamage
        Part(371, "DR");    // GearDamageResist
        Part(372, "C");     // GearCrit
        Part(374, "CD");    // GearCritDamage
        Part(373, "CR");    // GearCritResist
        Part(375, "CDR");   // GearCritDamageResist
        Part(376, "HB");    // GearHealingBoost
        Part(379, "V");     // GearMaxHealth (vitality)
        Part(377, "NR");    // GearNetherResist
        Part(378, "LR");    // GearLifeResist
        Part(383, "PKD");   // GearPKDamageRating
        Part(384, "PKDR");  // GearPKDamageResistRating
        Part(388, "OP");    // GearOverpower
        Part(389, "OPR");   // GearOverpowerResist
        if (sb.Length > start) sb.Append(']');
    }

    /// <summary>ACECustom TinkerLog CSV ("64,64,46") → "Steel x2, White Quartz".</summary>
    private static string FormatTinkerLog(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return string.Empty;
        var order = new List<int>();
        var counts = new Dictionary<int, int>();
        foreach (string raw in csv.Split(','))
        {
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
                continue;
            if (counts.TryGetValue(id, out int c)) counts[id] = c + 1;
            else { counts[id] = 1; order.Add(id); }
        }
        var sb = new StringBuilder();
        foreach (int id in order)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(MagItemInfoTables.Materials.TryGetValue(id, out string? name) ? name : $"material {id}");
            if (counts[id] > 1) sb.Append(" x").Append(counts[id]);
        }
        return sb.ToString();
    }

    /// <summary>Max ballistic range (45° arc, v²/g) in yards; 0 when velocity is missing.</summary>
    private static int MissileRangeYards(double velocity)
    {
        if (double.IsNaN(velocity) || velocity <= 0) return 0;
        double meters = velocity * velocity / 9.8;
        return (int)Math.Round(meters * 1.0936133);
    }

    private static string Fmt(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
}

/// <summary><see cref="IItemPropertySource"/> over the live client object (RynthCore host reads).</summary>
internal sealed class CacheItemPropertySource : IItemPropertySource
{
    private readonly RynthCore.PluginSdk.RynthCoreHost _host;
    private readonly uint _id;
    private IReadOnlyList<int>? _spells;

    public CacheItemPropertySource(RynthCore.PluginSdk.RynthCoreHost host, WorldObject wo)
    {
        _host = host;
        _id = unchecked((uint)wo.Id);
        Name = wo.Name;
        ObjectClass = wo.ObjectClass;
    }

    public string Name { get; }
    public AcObjectClass ObjectClass { get; }

    public bool TryGetInt(uint stype, out int value)
    {
        value = 0;
        return _host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(_id, stype, out value);
    }

    public bool TryGetDouble(uint stype, out double value)
    {
        value = 0;
        return _host.HasGetObjectDoubleProperty && _host.TryGetObjectDoubleProperty(_id, stype, out value);
    }

    public string GetString(uint stype) =>
        _host.HasGetObjectStringProperty && _host.TryGetObjectStringProperty(_id, stype, out string? v) && v != null
            ? v
            : string.Empty;

    public IReadOnlyList<int> SpellIds => _spells ??= ReadSpells();

    private IReadOnlyList<int> ReadSpells()
    {
        if (!_host.HasGetObjectSpellIds) return Array.Empty<int>();
        uint[] buf = new uint[128];
        int n = _host.GetObjectSpellIds(_id, buf, buf.Length);
        if (n <= 0) return Array.Empty<int>();
        int take = Math.Min(n, buf.Length);
        var list = new List<int>(take);
        for (int i = 0; i < take; i++) list.Add(unchecked((int)buf[i]));
        return list;
    }
}
