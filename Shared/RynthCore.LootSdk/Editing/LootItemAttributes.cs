using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.Loot.T11;
using RynthCore.Loot.VTank;

namespace RynthCore.Loot.Editing;

// "Add to loot profile"'s attribute checklist (2026-10-18): every attribute a clicked item
// has that a loot rule can require, grouped for the popup, each with the item's value, the
// comparisons it offers and whether it is ticked by default. LootItemRules.Build turns the
// ticked rows into conditions, in row order. With no choices from the popup (an older engine,
// /ra loot add) the defaults reproduce the rules "items like this" and "Include T11
// attributes" always made.

/// <summary>How a checklist row is required (LootEditItemAttribute.Op).</summary>
public enum LootItemAttributeOp
{
    AtLeast = 0,
    Exactly = 1,
    AtMost = 2,
    Not = 3,
    /// <summary>
    /// A flags value (imbues, damage types) has any of the picked flags (2026-11-07): "has Fire Rending",
    /// "has Any elemental rend". VTank's Long Key Has Flag, (value AND flags) != 0.
    /// </summary>
    HasAny = 4,
    /// <summary>Any value at all (2026-11-07): "any slayer", "any imbue". Long Key >= 1.</summary>
    Any = 5,
    /// <summary>A flags value has every picked flag (2026-11-07, multi imbues): one Long Key Has Flag per flag.</summary>
    HasAll = 6,
    /// <summary>A spell by its exact name.</summary>
    SpellExact = 10,
    /// <summary>This spell at this tier or a higher one (Major Strength: Major, Epic or Legendary).</summary>
    SpellOrBetter = 11,
    /// <summary>This spell at any tier.</summary>
    SpellAnyTier = 12,
}

/// <summary>One checklist row while a rule is built (the wire form is LootEditItemAttribute).</summary>
public sealed class LootItemAttribute
{
    internal enum Shape { Long, Double, TotalRatings, Spell, CantripTier, SpellCount, SpellFamily, ObjectClass }

    public string Id { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Tip { get; init; } = string.Empty;
    internal Shape Kind { get; init; }
    /// <summary>The property key (long or double rows), or the cantrip tier (CantripTier rows).</summary>
    internal int Key { get; init; }
    internal double ItemValue { get; init; }
    public string ItemText { get; init; } = string.Empty;
    public LootItemAttributeOp[] Ops { get; set; } = Array.Empty<LootItemAttributeOp>();
    /// <summary>The item's value can be replaced by a typed number.</summary>
    public bool Editable { get; init; }
    /// <summary>Spell rows: the spell's name (SpellFamily rows: the family, Key its level).</summary>
    internal string Spell { get; init; } = string.Empty;
    /// <summary>The values a named row can take (material names, skills, classes...), or null: the value is typed or fixed.</summary>
    internal (int Id, string Name)[]? Choices { get; set; }
    /// <summary>The value is a set of flags (imbues, damage types): several can be picked at once (2026-11-07).</summary>
    public bool Flags { get; set; }
    /// <summary>SpellFamily rows: the family's spells by level (the catalog's), or null: a name pattern.</summary>
    internal LootSpellFamily? Family { get; init; }

    // Where the row stands now (defaults, then the player's choice).
    public bool On { get; set; }
    public LootItemAttributeOp Op { get; set; }
    internal double Value { get; set; }
    /// <summary>The value as the player typed it (empty: the item's).</summary>
    internal string Typed { get; set; } = string.Empty;

    public LootEditItemAttribute ToWire() => new()
    {
        Id = Id, Group = Group, Label = Label, ItemValue = ItemText, Tip = Tip, Editable = Editable, On = On,
        Op = (int)Op, Ops = new List<int>(Array.ConvertAll(Ops, o => (int)o)),
        Value = Editable || Choices != null ? LootItemAttributes.Number(Value) : ItemText,
        Choices = Choices == null ? null : new List<LootEditName>(Array.ConvertAll(Choices, c => new LootEditName { Id = c.Id, Name = c.Name })),
        Flags = Flags,
    };
}

public static class LootItemAttributes
{
    public const string GroupItem = "Item", GroupGear = "Weapon / Armor", GroupCards = "Weapon cards", GroupRatings = "Ratings", GroupSpells = "Spells",
        GroupPet = "Pet essence", GroupT11 = "T11", GroupRequirements = "Requirements";

    /// <summary>Cantrip tiers, lowest first: "Major Strength" is tier 2.</summary>
    public static readonly string[] CantripTiers = { "Minor", "Major", "Epic", "Legendary", "Paragon" };

    /// <summary>
    /// The regex alternation for cantrip tiers <paramref name="from"/> and up. Paragon is also
    /// written "Paragon's" by some servers, so it takes an optional "'s".
    /// </summary>
    public static string TierAlternation(int from)
    {
        var parts = new List<string>();
        for (int i = Math.Max(0, from); i < CantripTiers.Length; i++)
            parts.Add(CantripTiers[i] == "Paragon" ? "Paragon(?:'s)?" : CantripTiers[i]);
        return string.Join("|", parts);
    }
    private static readonly string[] Roman = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII" };

    // Pet essence property ids (ACECustom; the Mini Remote's PetStats reads the same).
    public const int KeyBond = 9053, KeyCapturedDamageType = 9054, KeyPotencyStored = 9056, KeyBreedingCharges = 9057,
        KeyMutations = 9075, KeySummoningMastery = 362, KeyUseLevelRequirement = 364, KeyUseRequiresSkill = 366,
        KeyUseRequiresSkillSpec = 368, KeyUseRequiresLevel = 369;
    private const int SkillSummoning = 54;

    private enum Cmp { Min, Max, Named }

    private readonly record struct Spec(string Group, int Key, bool IsDouble, string Label, Cmp Cmp, string Tip = "");

    /// <summary>The property rows, in display (and condition) order. Spells, T11 and the rating total are built separately.</summary>
    private static readonly Spec[] Specs =
    {
        new(GroupItem, LootItemRules.KeyMaterial, false, "Material", Cmp.Named),
        new(GroupItem, LootItemRules.KeyWorkmanship, false, "Workmanship", Cmp.Min),
        new(GroupItem, 19, false, "Value", Cmp.Min),
        new(GroupItem, 5, false, "Burden", Cmp.Max),
        new(GroupItem, 106, false, "Spellcraft", Cmp.Min),
        new(GroupItem, 108, false, "Max mana", Cmp.Min),
        new(GroupItem, 109, false, "Arcane Lore to use", Cmp.Max, "The Arcane Lore the item's spells need (lower is easier)."),
        new(GroupItem, 92, false, "Uses left", Cmp.Min),
        new(GroupItem, 91, false, "Max uses", Cmp.Min),
        new(GroupItem, 265, false, "Armor set", Cmp.Named, "The set id (the same set's pieces share it)."),
        new(GroupItem, 179, false, "Imbue", Cmp.Named,
            "The item's imbues (ImbuedEffect, with ImbuedEffect2-5). \"has any\" matches an item with any of the picked ones: "
            + "\"Any elemental rend\" is every element, one rend is that element only. A caster's spell armor rend is the "
            + "Armor Rending imbue on a wand, staff or orb."),
        new(GroupItem, 166, false, "Slayer", Cmp.Named),
        new(GroupItem, 45, false, "Damage type", Cmp.Named),
        // ACECustom appraisal properties (2026-11-07).
        new(GroupItem, 171, false, "Times tinkered", Cmp.Max, "How many tinks it has had (fewer leaves more room)."),
        new(GroupItem, 17, false, "Rare", Cmp.Min, "A retail rare (its rare id); any rare: >= 1."),
        new(GroupItem, 114, false, "Attuned", Cmp.Min, "1 when attuned (can't be traded)."),
        new(GroupItem, 33, false, "Bonded", Cmp.Min, "1 when bonded (stays on death)."),
        new(GroupItem, 319, false, "Max item level", Cmp.Min, "Items that level up (Aetheria, legendary keys...)."),

        new(GroupGear, 353, false, "Weapon type", Cmp.Named, "The weapon's mastery: sword, axe, bow, crossbow..."),
        new(GroupGear, LootItemRules.KeyWieldSkill, false, "Wield skill", Cmp.Named),
        new(GroupGear, LootItemRules.KeyWeaponSkill, false, "Weapon skill", Cmp.Named),
        new(GroupGear, LootItemRules.KeyDamage, false, "Max damage", Cmp.Min),
        new(GroupGear, 22, true, "Damage variance", Cmp.Max, "Lower is steadier damage."),
        new(GroupGear, 63, true, "Damage modifier", Cmp.Min, "Missile weapons' damage multiplier."),
        new(GroupGear, 62, true, "Attack modifier", Cmp.Min),
        new(GroupGear, 29, true, "Melee defense modifier", Cmp.Min),
        new(GroupGear, 149, true, "Missile defense modifier", Cmp.Min),
        new(GroupGear, 150, true, "Magic defense modifier", Cmp.Min),
        new(GroupGear, 144, true, "Mana conversion modifier", Cmp.Min),
        new(GroupGear, LootItemRules.KeyElementalDamageMod, true, "Elemental damage vs monsters", Cmp.Min),
        new(GroupGear, 204, false, "Elemental damage bonus", Cmp.Min, "A caster's or launcher's flat elemental damage bonus."),
        new(GroupGear, 159, true, "Absorb magic damage", Cmp.Min, "Fraction of magic damage absorbed (0.25 = 25%)."),
        new(GroupGear, LootItemRules.KeyEquipSlots, false, "Equip slots", Cmp.Named, "Where it is worn or wielded."),
        new(GroupGear, LootItemRules.KeyArmorLevel, false, "Armor level", Cmp.Min),

        // ACECustom's weapon cards: the real properties, then the ones only the appraisal text carries
        // (RynthAi's virtual keys, read from "Property Details:").
        new(GroupCards, 147, true, "Biting Strike", Cmp.Min, "Crit chance (0.4 = 40%)."),
        new(GroupCards, 136, true, "Crushing Blow", Cmp.Min, "Stored as the crit damage multiplier minus 1 (4.0 = 5x crit damage)."),
        new(GroupCards, 155, true, "Armor Cleaving", Cmp.Min, "Any armor cleaving: >= 0.01 (the server only checks that it is set)."),
        new(GroupCards, 157, true, "Resistance Cleaving", Cmp.Min, "The vuln it applies (0.5 = +50% damage)."),
        new(GroupCards, 263, false, "Resistance Cleaving element", Cmp.Named),
        new(GroupCards, 292, false, "Cleaving targets", Cmp.Min, "Total targets it hits (the extra targets plus one)."),
        new(GroupCards, 9031, false, "Split arrows", Cmp.Min, "Extra arrows a launcher fires."),
        new(GroupCards, T11Keys.SlayerDamage, false, "Slayer damage %", Cmp.Min, "The slayer card's damage, 2.1x = 210 (from the appraisal text)."),
        new(GroupCards, T11Keys.AllCreaturesSlayer, false, "All Creatures slayer", Cmp.Min, "1 for an all-creatures slayer (from the appraisal text; Slayer Type is 0 on these)."),
        new(GroupCards, T11Keys.RendPower, false, "Rend power %", Cmp.Min, "The rend card's bonus, +213% = 213 (from the appraisal text)."),
        new(GroupCards, T11Keys.SpellArmorRend, false, "Spell Armor Rend %", Cmp.Min, "Casters: the Spell Armor the spells ignore, 35% = 35 (from the appraisal text; there is no imbue bit for it)."),
        new(GroupCards, T11Keys.Phantom, false, "Phantom", Cmp.Min, "1 when it ignores magic protections (from the appraisal text)."),

        // The rating total comes first in this group (built separately); then each rating.
        new(GroupRatings, 370, false, "Damage rating", Cmp.Min),
        new(GroupRatings, 371, false, "Damage resist rating", Cmp.Min),
        new(GroupRatings, 372, false, "Crit rating", Cmp.Min),
        new(GroupRatings, 373, false, "Crit resist rating", Cmp.Min),
        new(GroupRatings, 374, false, "Crit damage rating", Cmp.Min),
        new(GroupRatings, 375, false, "Crit damage resist rating", Cmp.Min),
        new(GroupRatings, 376, false, "Heal boost rating", Cmp.Min),
        new(GroupRatings, 377, false, "Nether resist rating", Cmp.Min),
        new(GroupRatings, 379, false, "Vitality rating", Cmp.Min),
        new(GroupRatings, 378, false, "Life resist rating", Cmp.Min),
        new(GroupRatings, 388, false, "Overpower rating", Cmp.Min),
        new(GroupRatings, 389, false, "Overpower resist rating", Cmp.Min),

        new(GroupPet, KeyBond, false, "Bond level", Cmp.Min),
        new(GroupPet, KeyUseRequiresLevel, false, "Level", Cmp.Min),
        new(GroupPet, KeyUseLevelRequirement, false, "Level requirement", Cmp.Min),
        new(GroupPet, KeyPotencyStored, false, "Potency stored", Cmp.Min),
        new(GroupPet, KeySummoningMastery, false, "Summoning mastery", Cmp.Named),
        new(GroupPet, KeyMutations, false, "Mutations", Cmp.Min),
        new(GroupPet, 9070, false, "Damage mutations", Cmp.Min),
        new(GroupPet, 9071, false, "Damage resist mutations", Cmp.Min),
        new(GroupPet, 9072, false, "Crit mutations", Cmp.Min),
        new(GroupPet, 9073, false, "Vitality mutations", Cmp.Min),
        new(GroupPet, 9074, false, "Potency mutations", Cmp.Min),
        new(GroupPet, KeyCapturedDamageType, false, "Captured damage type", Cmp.Named),
        new(GroupPet, 307, false, "Pet damage rating", Cmp.Min),
        new(GroupPet, 308, false, "Pet damage resist rating", Cmp.Min),
        new(GroupPet, 313, false, "Pet crit rating", Cmp.Min),
        new(GroupPet, 314, false, "Pet crit damage rating", Cmp.Min),
        new(GroupPet, 315, false, "Pet crit resist rating", Cmp.Min),
        new(GroupPet, 316, false, "Pet crit damage resist rating", Cmp.Min),
        new(GroupPet, KeyBreedingCharges, false, "Breeding charges", Cmp.Min),

        new(GroupRequirements, 158, false, "Wield requirement", Cmp.Named),
        new(GroupRequirements, 160, false, "Wield difficulty", Cmp.Max, "Lower is easier to wield."),
    };

    /// <summary>Every int property a checklist can use: what RynthAi reads into LootItemFacts.</summary>
    public static readonly int[] IntKeys = BuildKeys(isDouble: false);
    /// <summary>Every float property a checklist can use.</summary>
    public static readonly int[] DoubleKeys = BuildKeys(isDouble: true);

    private static int[] BuildKeys(bool isDouble)
    {
        var keys = new List<int>();
        foreach (Spec s in Specs)
            if (s.IsDouble == isDouble && !keys.Contains(s.Key)) keys.Add(s.Key);
        if (!isDouble)
            foreach (int k in new[] { KeyUseRequiresSkill, KeyUseRequiresSkillSpec, 303, 304, 305, 306, 51100, 9060 })
                if (!keys.Contains(k)) keys.Add(k);
        return keys.ToArray();
    }

    /// <summary>
    /// A combat / summoning essence: the plugin says so, or the item carries ACECustom's bond or
    /// potency, or using it needs Summoning.
    /// </summary>
    public static bool IsPetEssence(LootItemFacts f)
        => f.IsPetEssence || f.Int(KeyBond) > 0 || f.Ints.ContainsKey(KeyPotencyStored)
           || f.Int(KeyUseRequiresSkill) == SkillSummoning || f.Int(KeyUseRequiresSkillSpec) == SkillSummoning
           || f.ObjectClass == (int)AcObjectClass.CombatPet;

    /// <summary>
    /// The item's rows with their defaults: ticked for "items like this" (<paramref name="like"/>)
    /// what that match always required, the T11 rows when <paramref name="includeT11"/>.
    /// </summary>
    /// <summary>An item's checklist; off Infinite Leaftide (<paramref name="ilt"/> false) without the T11, card and pet rows.</summary>
    public static List<LootItemAttribute> For(LootItemFacts f, bool like, bool includeT11, bool ilt)
    {
        List<LootItemAttribute> rows = For(f, like, includeT11 && ilt);
        if (!ilt) rows.RemoveAll(r => r.Group == GroupT11 || r.Group == GroupPet || (r.Kind == LootItemAttribute.Shape.Long && IsCustomServerKey(r.Key)));
        return rows;
    }

    public static List<LootItemAttribute> For(LootItemFacts f, bool like, bool includeT11)
    {
        var rows = new List<LootItemAttribute>();
        bool pet = IsPetEssence(f);
        HashSet<int> likeKeys = like ? LikeKeys(f, pet) : new HashSet<int>();

        foreach (Spec s in Specs)
        {
            if (s.Group == GroupPet && !pet) continue;
            if (s.Group == GroupRatings && s.Key == 370) AddTotalRatings(rows, f, like);
            double v = s.IsDouble ? f.Double(s.Key) : f.Int(s.Key);
            if (s.Cmp == Cmp.Named ? v == 0 : v <= 0) continue;
            // A pet's level comes from 369 when it has one (PetStats does the same).
            if (s.Key == KeyUseLevelRequirement && f.Int(KeyUseRequiresLevel) > 0) continue;
            LootItemAttribute pr = PropertyRow(s, v, likeKeys.Contains(s.IsDouble ? -s.Key : s.Key));
            if (!s.IsDouble && s.Key == LootItemFacts.ImbuedEffectKey)
            {
                // Two or more imbues (Armor Rending + Cold Rending): the item's set is "has all", not "has any"
                // (2026-11-08: a rule that took any one of them).
                long iv = (long)v;
                if ((iv & (iv - 1)) != 0) pr.Op = LootItemAttributeOp.HasAll;
                rows.Add(pr);
                rows.Add(RendRow(on: false));
                continue;
            }
            rows.Add(pr);
        }
        // Spells and T11 go after the ratings and before Requirements.
        int req = rows.FindIndex(r => r.Group == GroupRequirements);
        var tail = req >= 0 ? rows.GetRange(req, rows.Count - req) : new List<LootItemAttribute>();
        if (req >= 0) rows.RemoveRange(req, rows.Count - req);
        AddSpellRows(rows, f, like);
        AddT11Rows(rows, f, includeT11);
        AddT11ItemRows(rows, f);
        rows.AddRange(tail);
        return rows;
    }

    /// <summary>
    /// The keys "items like this" required before the checklist (doubles negated), plus a
    /// pet essence's bond, level, potency, mastery and mutations.
    /// </summary>
    private static HashSet<int> LikeKeys(LootItemFacts f, bool pet)
    {
        var k = new HashSet<int> { LootItemRules.KeyMaterial, LootItemRules.KeyWorkmanship };
        int wieldSkill = f.Int(LootItemRules.KeyWieldSkill);
        switch ((AcObjectClass)f.ObjectClass)
        {
            case AcObjectClass.MeleeWeapon:
                k.Add(wieldSkill != 0 ? LootItemRules.KeyWieldSkill : LootItemRules.KeyWeaponSkill);
                k.Add(LootItemRules.KeyDamage);
                break;
            case AcObjectClass.MissileWeapon:
                k.Add(wieldSkill != 0 ? LootItemRules.KeyWieldSkill : LootItemRules.KeyWeaponSkill);
                break;
            case AcObjectClass.WandStaffOrb:
                k.Add(LootItemRules.KeyWieldSkill);
                k.Add(-LootItemRules.KeyElementalDamageMod);
                break;
            case AcObjectClass.Armor:
            case AcObjectClass.Clothing:
                k.Add(LootItemRules.KeyEquipSlots);
                k.Add(LootItemRules.KeyArmorLevel);
                break;
            case AcObjectClass.Jewelry:
                k.Add(LootItemRules.KeyEquipSlots);
                break;
        }
        if (pet)
            foreach (int key in new[] { KeyBond, KeyUseRequiresLevel, KeyUseLevelRequirement, KeyPotencyStored, KeySummoningMastery, KeyMutations })
                k.Add(key);
        return k;
    }

    // ── "Plus a rend" (2026-11-08, Silentkelpie: "armor rend is one imbue, and then a elemental rend also can be on
    // a item ... it should be able to find items with armor rend + bs + cs, but also with any elemental on it") ──
    // A second row on the imbue key: "has any" of the rends, besides the Imbue row's own test. "Imbue has all
    // Armor Rending" with "Plus a rend: Any elemental rend" is an Armor Rending weapon with a rend of any element.

    /// <summary>The rend row's id (the Imbue row is "L:179").</summary>
    public const string RendRowId = "L:179:rend";
    private const int RendBits = Physical | Elements | Nether;

    private static LootItemAttribute RendRow(bool on)
    {
        var list = new List<(int, string)>();
        foreach (var c in LootRuleText.ImbuedEffects)
            if ((c.Id & ~RendBits) == 0) list.Add((c.Id, c.Name));
        // The "any" picks first: the row is for one rend of several.
        list.Sort((a, b) => (IsSingle(b.Item1) ? 0 : 1).CompareTo(IsSingle(a.Item1) ? 0 : 1));
        var ops = new[] { LootItemAttributeOp.HasAny };
        return new LootItemAttribute
        {
            Id = RendRowId, Group = GroupItem, Label = "Plus a rend",
            Tip = "Besides the Imbue row: the item also has one of these rends (\"Any elemental rend\": any element). "
                + "Imbue \"has all\" Armor Rending with this row on: Armor Rending and a rend of any element.",
            Kind = LootItemAttribute.Shape.Long, Key = LootItemFacts.ImbuedEffectKey, ItemText = "any",
            Ops = ops, Op = ops[0], Editable = false, On = on, Choices = list.ToArray(), Flags = true,
            Value = list.Count > 0 ? list[0].Item1 : 0,
        };
    }

    private static LootItemAttribute PropertyRow(Spec s, double v, bool on)
    {
        LootItemAttributeOp[] ops = s.Cmp switch
        {
            // Flags (imbues, damage types): "has any" / "has all" of several picks; for imbues they come first
            // (an item can carry two). Every named row can ask for "any" value: any slayer, any imbue.
            Cmp.Named when LootRuleText.ValueTableIsFlags(s.Key) && s.Key == LootItemFacts.ImbuedEffectKey
                => new[] { LootItemAttributeOp.HasAny, LootItemAttributeOp.HasAll, LootItemAttributeOp.Exactly, LootItemAttributeOp.Not, LootItemAttributeOp.Any },
            Cmp.Named when LootRuleText.ValueTableIsFlags(s.Key)
                => new[] { LootItemAttributeOp.Exactly, LootItemAttributeOp.HasAny, LootItemAttributeOp.HasAll, LootItemAttributeOp.Not, LootItemAttributeOp.Any },
            Cmp.Named => new[] { LootItemAttributeOp.Exactly, LootItemAttributeOp.Not, LootItemAttributeOp.Any },
            Cmp.Max => new[] { LootItemAttributeOp.AtMost, LootItemAttributeOp.Exactly, LootItemAttributeOp.AtLeast },
            _ => new[] { LootItemAttributeOp.AtLeast, LootItemAttributeOp.Exactly, LootItemAttributeOp.AtMost },
        };
        string text = s.IsDouble ? Number(v) : NamedOrNumber(s.Key, (int)v);
        (int Id, string Name)[]? choices = s.Cmp == Cmp.Named && !s.IsDouble ? LootRuleText.ValueTableFor(s.Key) : null;
        return new LootItemAttribute
        {
            Id = (s.IsDouble ? "D:" : "L:") + s.Key.ToString(CultureInfo.InvariantCulture),
            Group = s.Group, Label = s.Label, Tip = s.Tip,
            Kind = s.IsDouble ? LootItemAttribute.Shape.Double : LootItemAttribute.Shape.Long,
            Key = s.Key, ItemValue = v, ItemText = text, Ops = ops, Editable = s.Cmp != Cmp.Named,
            On = on, Op = ops[0], Value = v, Choices = choices, Flags = choices != null && LootRuleText.ValueTableIsFlags(s.Key),
        };
    }

    private static void AddTotalRatings(List<LootItemAttribute> rows, LootItemFacts f, bool like)
    {
        int total = 0;
        foreach (int k in LootItemRules.RatingKeys) total += Math.Max(0, f.Int(k));
        if (total <= 0 || rows.Exists(r => r.Kind == LootItemAttribute.Shape.TotalRatings)) return;
        rows.Add(new LootItemAttribute
        {
            Id = "ratings", Group = GroupRatings, Label = "Total ratings", Kind = LootItemAttribute.Shape.TotalRatings,
            Tip = "Damage, damage resist, crit, crit resist, crit damage, crit damage resist, heal boost and vitality ratings added up.",
            ItemValue = total, ItemText = Number(total), Ops = new[] { LootItemAttributeOp.AtLeast }, Editable = true,
            On = like, Op = LootItemAttributeOp.AtLeast, Value = total,
        });
    }

    // ── Spells ──────────────────────────────────────────────────────────────

    /// <summary>"Major Strength": tier 1 (CantripTiers index) and "Strength".</summary>
    public static bool TryCantrip(string spell, out int tier, out string rest)
    {
        for (int i = 0; i < CantripTiers.Length; i++)
        {
            string p = CantripTiers[i] + " ";
            if (spell.StartsWith(p, StringComparison.OrdinalIgnoreCase) && spell.Length > p.Length)
            {
                tier = i;
                rest = spell.Substring(p.Length);
                return true;
            }
        }
        tier = -1;
        rest = string.Empty;
        return false;
    }

    /// <summary>"Strength Self VI": level 6 and "Strength Self".</summary>
    public static bool TryLevel(string spell, out int level, out string rest)
    {
        int sp = spell.LastIndexOf(' ');
        if (sp > 0)
        {
            int idx = Array.IndexOf(Roman, spell.Substring(sp + 1).ToUpperInvariant());
            if (idx >= 0)
            {
                level = idx + 1;
                rest = spell.Substring(0, sp);
                return true;
            }
        }
        level = 0;
        rest = string.Empty;
        return false;
    }

    /// <summary>The spell condition's pattern for a spell row's comparison.</summary>
    public static string SpellPattern(string spell, LootItemAttributeOp op)
    {
        if (op == LootItemAttributeOp.SpellOrBetter || op == LootItemAttributeOp.SpellAnyTier)
        {
            int from;
            if (TryCantrip(spell, out int tier, out string rest))
            {
                from = op == LootItemAttributeOp.SpellAnyTier ? 0 : tier;
                return "^(" + TierAlternation(from) + ") " + LootItemRules.EscapeText(rest) + "$";
            }
            if (TryLevel(spell, out int level, out rest))
            {
                from = op == LootItemAttributeOp.SpellAnyTier ? 0 : level - 1;
                return "^" + LootItemRules.EscapeText(rest) + " (" + string.Join("|", Roman, from, Roman.Length - from) + ")$";
            }
        }
        return LootItemRules.ExactPattern(spell);
    }

    /// <summary>
    /// A row per spell (cantrips default to "or better", leveled spells to the exact spell, as
    /// "items like this" always did; the first <see cref="LootItemRules.MaxSpells"/> ticked
    /// for it), then "N cantrips of a tier or better" and the spell count.
    /// </summary>
    private static void AddSpellRows(List<LootItemAttribute> rows, LootItemFacts f, bool like)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tiers = new List<int>();
        int ticked = 0, count = 0;
        foreach (string raw in f.Spells)
        {
            string s = LootItemRules.OneLine(raw).Trim();
            if (s.Length == 0 || !seen.Add(s)) continue;
            count++;
            bool cantrip = TryCantrip(s, out int tier, out _);
            int level = 0;
            bool leveled = !cantrip && TryLevel(s, out level, out _);
            if (cantrip) tiers.Add(tier);
            LootItemAttributeOp[] ops = cantrip
                ? new[] { LootItemAttributeOp.SpellOrBetter, LootItemAttributeOp.SpellExact, LootItemAttributeOp.SpellAnyTier }
                : leveled
                    ? new[] { LootItemAttributeOp.SpellExact, LootItemAttributeOp.SpellOrBetter, LootItemAttributeOp.SpellAnyTier }
                    : new[] { LootItemAttributeOp.SpellExact };
            bool on = like && ticked < LootItemRules.MaxSpells;
            if (on) ticked++;
            rows.Add(new LootItemAttribute
            {
                Id = "spell:" + s, Group = GroupSpells, Label = s, Kind = LootItemAttribute.Shape.Spell, Spell = s,
                ItemText = cantrip ? CantripTiers[tier] + " cantrip" : leveled ? "level " + Roman[level - 1] : "spell",
                Tip = cantrip ? "Or better: this cantrip at this tier or higher. Any tier: Minor to Paragon."
                    : leveled ? "Or better: this spell at this level or higher. Any tier: any level."
                    : "The item has this spell.",
                Ops = ops, On = on, Op = ops[0],
            });
        }

        // "At least N Epic-or-better cantrips": one row per tier the item has, highest first.
        tiers.Sort();
        for (int t = CantripTiers.Length - 1; t >= 0; t--)
        {
            if (!tiers.Contains(t)) continue;
            int n = tiers.FindAll(x => x >= t).Count;
            string label = t == CantripTiers.Length - 1 ? CantripTiers[t] + " cantrips" : CantripTiers[t] + "-or-better cantrips";
            rows.Add(new LootItemAttribute
            {
                Id = "cantrips:" + CantripTiers[t], Group = GroupSpells, Label = label, Kind = LootItemAttribute.Shape.CantripTier,
                Key = t, ItemValue = n, ItemText = Number(n), Ops = new[] { LootItemAttributeOp.AtLeast }, Editable = true,
                Tip = "How many of the item's cantrips are this tier or higher.", Op = LootItemAttributeOp.AtLeast, Value = n,
            });
        }
        if (count > 0)
            rows.Add(new LootItemAttribute
            {
                Id = "spellcount", Group = GroupSpells, Label = "Spell count", Kind = LootItemAttribute.Shape.SpellCount,
                ItemValue = count, ItemText = Number(count), Ops = new[] { LootItemAttributeOp.AtLeast }, Editable = true,
                Op = LootItemAttributeOp.AtLeast, Value = count,
            });
    }

    /// <summary>How many spell rows there are (the "more than 4 spells" note).</summary>
    internal static int SpellRowCount(List<LootItemAttribute> rows) => rows.FindAll(r => r.Kind == LootItemAttribute.Shape.Spell).Count;

    /// <summary>True for the T11 group's rows.</summary>
    internal static bool IsT11Row(LootItemAttribute r) => r.Group == GroupT11;

    // ── T11 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The T11 rows on RynthAi's virtual T11 keys, in the order "Include T11 attributes" always
    /// added them (Is T11, tier, weapon grade, damage %, gear grade, each catalogued modifier,
    /// Has Slot Special for a value-less one, Cast on Strike count), then the summary keys,
    /// off by default. Left out on purpose: Can Wield (the character), Zone Locked (where it
    /// was appraised), property slots and Tainted (bag state, not quality).
    /// </summary>
    /// <summary>
    /// ACECustom's newer loot items (2026-11-11, <see cref="T11Items"/>): a clicked item bounty, school gem, Charm Infusion
    /// Gem, levelled charm or lum token gets "T11 item is (kind)", ticked, and its tier / coins, not ticked.
    /// </summary>
    private static void AddT11ItemRows(List<LootItemAttribute> rows, LootItemFacts f)
    {
        int kind = f.Int(T11Keys.ItemKind);
        if (kind <= 0) return;
        void Row(string id, string label, int key, int v, bool named, bool on)
        {
            LootItemAttributeOp[] ops = named ? new[] { LootItemAttributeOp.Exactly, LootItemAttributeOp.Not }
                : new[] { LootItemAttributeOp.AtLeast, LootItemAttributeOp.Exactly, LootItemAttributeOp.AtMost };
            rows.Add(new LootItemAttribute
            {
                Id = "t11:" + id, Group = GroupT11, Label = label, Kind = LootItemAttribute.Shape.Long, Key = key,
                ItemValue = v, ItemText = named ? NamedOrNumber(key, v) : Number(v), Ops = ops, Choices = named ? T11Items.Kinds : null,
                Editable = !named, On = on, Op = ops[0], Value = v,
            });
        }
        Row("itemkind", "T11 item", T11Keys.ItemKind, kind, named: true, on: true);
        int tier = f.Int(T11Keys.ItemTier);
        if (tier > 0) Row("itemtier", kind == T11Items.SchoolGem ? "Earned at tier" : "Bounty tier", T11Keys.ItemTier, tier, named: false, on: false);
        int coins = f.Int(T11Keys.InfusionCoins);
        if (coins > 0) Row("coins", "Infusion gem coins", T11Keys.InfusionCoins, coins, named: false, on: false);
    }

    private static void AddT11Rows(List<LootItemAttribute> rows, LootItemFacts f, bool on)
    {
        T11ItemInfo? t = f.T11;
        if (t == null || !t.IsT11) return;
        void Row(string id, string label, int key, int v, bool named, bool def = true, bool equal = false, string tip = "")
        {
            LootItemAttributeOp[] ops = equal ? new[] { LootItemAttributeOp.Exactly }
                : new[] { LootItemAttributeOp.AtLeast, LootItemAttributeOp.Exactly, LootItemAttributeOp.AtMost };
            rows.Add(new LootItemAttribute
            {
                Id = "t11:" + id, Group = GroupT11, Label = label, Kind = LootItemAttribute.Shape.Long, Key = key, Tip = tip,
                ItemValue = v, ItemText = named ? NamedOrNumber(key, v) : equal ? "yes" : Number(v), Ops = ops,
                Editable = !named && !equal, On = on && def, Op = ops[0], Value = v,
            });
        }

        Row("is", "Is T11", T11Keys.IsT11, 1, named: false, equal: true);
        if (!t.HasText) return;
        if (t.Tier > 0) Row("tier", t.TierIsExact ? "Tier" : "Tier (estimated)", T11Keys.EstimatedTier, t.Tier, named: false);
        if (t.GradeRank > 0) Row("grade", "Weapon grade", T11Keys.WeaponGrade, t.GradeRank, named: true);
        if (t.DamagePercent > 0) Row("dmgpct", "Damage %", T11Keys.DamagePercent, t.DamagePercent, named: false);
        if (t.GearGradeRank > 0) Row("geargrade", "Gear grade", T11Keys.GearGrade, t.GearGradeRank, named: true);
        bool valuelessSpecial = false;
        foreach (T11Modifier m in t.Modifiers)
        {
            if (T11Catalog.ModifierByKey(m.Key) == null) continue;
            // A modifier's key reads 0 when it is absent, so a value-less one can't be required by value.
            if (m.Value > 0) Row("mod:" + m.Key.ToString(CultureInfo.InvariantCulture), "Mod: " + m.Name, T11Keys.ForModifier(m.Key), m.Value, named: false);
            else if (m.IsSlotSpecial) valuelessSpecial = true;
        }
        if (valuelessSpecial) Row("special", "Has slot special", T11Keys.HasSlotSpecial, 1, named: false, equal: true);
        if (t.Procs.Count > 0) Row("cos", "Cast on Strike count", T11Keys.CastOnStrikeCount, t.Procs.Count, named: false);
        if (f.Int(51100) > 0) Row("rare", "T11 rare", 51100, f.Int(51100), named: true, def: false);
        if (t.WeaponQuality > 0) Row("quality", "Weapon quality (0-1000)", 9060, t.WeaponQuality, named: false, def: false);

        foreach (var (key, label) in new[] { (T11Keys.ModifierCount, "Modifier count"), (T11Keys.ModifierRatingTotal, "Modifier rating total"), (T11Keys.BestRollPercent, "Best roll %") })
            if (T11Keys.TryGetValue(t, key, null, out int v) && v > 0)
                Row("sum:" + key.ToString(CultureInfo.InvariantCulture), label, key, v, named: false, def: false);
    }

    /// <summary>The catalogued-but-unknown T11 modifiers (the rule can't require them).</summary>
    internal static List<string> UnknownT11Modifiers(LootItemFacts f)
    {
        var unknown = new List<string>();
        if (f.T11 is { IsT11: true } t)
            foreach (T11Modifier m in t.Modifiers)
                if (T11Catalog.ModifierByKey(m.Key) == null) unknown.Add(m.Name);
        return unknown;
    }

    // ── Choices and conditions ──────────────────────────────────────────────

    /// <summary>
    /// The player's choices onto the rows (unknown ids are ignored: the item may have changed).
    /// A typed value that isn't a number keeps the item's and says so in <paramref name="notes"/>.
    /// </summary>
    public static void Apply(List<LootItemAttribute> rows, IReadOnlyList<LootEditItemChoice>? choices, List<string> notes)
    {
        if (choices == null) return;
        foreach (LootEditItemChoice c in choices)
        {
            LootItemAttribute? r = rows.Find(x => x.Id == c.Id);
            if (r == null) continue;
            r.On = c.On;
            if (c.Op >= 0 && Array.IndexOf(r.Ops, (LootItemAttributeOp)c.Op) >= 0) r.Op = (LootItemAttributeOp)c.Op;
            string typed = (c.Value ?? string.Empty).Trim();
            if (typed.Length == 0) continue;
            if (r.Choices != null)
            {
                // A named value picked from the row's list (another material, skill, class...).
                if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pick)
                    && (Array.Exists(r.Choices, x => x.Id == pick) || (r.Flags && pick != 0 && (pick & ~AllBits(r.Choices)) == 0)))
                {
                    r.Value = pick;
                    r.Typed = typed;
                }
                continue;
            }
            if (!r.Editable) continue;
            bool whole = r.Kind != LootItemAttribute.Shape.Double;
            if (double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0 && (!whole || v == Math.Floor(v)))
            {
                r.Value = v;
                r.Typed = typed;
            }
            else
                notes.Add($"{r.Label}: '{typed}' isn't a{(whole ? " whole" : "")} number of 0 or more; the item's {r.ItemText} is used.");
        }
    }

    /// <summary>The ticked rows' conditions onto <paramref name="rule"/>, in row order; how many rows added one.</summary>
    public static int AddConditions(VTankLootRule rule, List<LootItemAttribute> rows)
    {
        int n = 0;
        foreach (LootItemAttribute r in rows)
        {
            if (!r.On) continue;
            n++;
            string key = r.Key.ToString(CultureInfo.InvariantCulture);
            switch (r.Kind)
            {
                case LootItemAttribute.Shape.Long when r.Op == LootItemAttributeOp.Any:
                    rule.Conditions.Add(Cond(VTankNodeTypes.LongValKeyGE, "1", key));
                    break;
                case LootItemAttribute.Shape.Long when r.Op == LootItemAttributeOp.HasAll:
                {
                    // One "has flag" per picked flag: the item needs every one of them.
                    long mask = (long)r.Value;
                    bool any = false;
                    for (int bit = 0; bit < 31; bit++)
                    {
                        long f = 1L << bit;
                        if ((mask & f) == 0) continue;
                        rule.Conditions.Add(Cond(VTankNodeTypes.LongValKeyFlagExists, f.ToString(CultureInfo.InvariantCulture), key));
                        any = true;
                    }
                    if (!any) n--;
                    break;
                }
                case LootItemAttribute.Shape.Long:
                    string iv = ((long)r.Value).ToString(CultureInfo.InvariantCulture);
                    int type = r.Op switch
                    {
                        LootItemAttributeOp.Exactly => VTankNodeTypes.LongValKeyE,
                        LootItemAttributeOp.AtMost => VTankNodeTypes.LongValKeyLE,
                        LootItemAttributeOp.Not => VTankNodeTypes.LongValKeyNE,
                        LootItemAttributeOp.HasAny => VTankNodeTypes.LongValKeyFlagExists,
                        _ => VTankNodeTypes.LongValKeyGE,
                    };
                    rule.Conditions.Add(Cond(type, iv, key));
                    break;
                case LootItemAttribute.Shape.Double:
                    string dv = Number(r.Value);
                    if (r.Op != LootItemAttributeOp.AtMost) rule.Conditions.Add(Cond(VTankNodeTypes.DoubleValKeyGE, dv, key));
                    if (r.Op != LootItemAttributeOp.AtLeast) rule.Conditions.Add(Cond(VTankNodeTypes.DoubleValKeyLE, dv, key));
                    break;
                case LootItemAttribute.Shape.TotalRatings:
                    rule.Conditions.Add(Cond(VTankNodeTypes.TotalRatingsGE, Number(r.Value)));
                    break;
                case LootItemAttribute.Shape.Spell:
                    rule.Conditions.Add(Cond(VTankNodeTypes.SpellNameMatch, SpellPattern(r.Spell, r.Op)));
                    break;
                case LootItemAttribute.Shape.CantripTier:
                    rule.Conditions.Add(Cond(VTankNodeTypes.SpellMatch, "^(" + TierAlternation(r.Key) + ") ", string.Empty, Number(r.Value)));
                    break;
                case LootItemAttribute.Shape.SpellFamily:
                    rule.Conditions.Add(Cond(VTankNodeTypes.SpellNameMatch,
                        r.Family != null ? r.Family.Pattern(r.Key, r.Op) : FamilyPattern(r.Spell, r.Key, r.Op)));
                    break;
                case LootItemAttribute.Shape.ObjectClass:
                    rule.Conditions.Add(Cond(VTankNodeTypes.ObjectClass, ((long)r.Value).ToString(CultureInfo.InvariantCulture)));
                    break;
                case LootItemAttribute.Shape.SpellCount:
                    rule.Conditions.Add(Cond(VTankNodeTypes.SpellCountGE, Number(r.Value)));
                    break;
            }
        }
        return n;
    }

    private static VTankLootCondition Cond(int type, params string[] lines) => new(type, "0", lines);

    // ── Spells picked from the standard lists (2026-10-24) ──────────────────

    /// <summary>
    /// The spell condition for a family at a level: "Strength" level 6, or better: the Self or
    /// Other spell at VI, VII or VIII (exact: VI only; any tier: I to VIII).
    /// </summary>
    public static string FamilyPattern(string family, int level, LootItemAttributeOp op)
    {
        int from = op == LootItemAttributeOp.SpellAnyTier ? 0 : Math.Clamp(level, 1, Roman.Length) - 1;
        string levels = op == LootItemAttributeOp.SpellExact ? Roman[from] : string.Join("|", Roman, from, Roman.Length - from);
        return "^" + LootItemRules.EscapeText(family) + "( Self| Other)? (" + levels + ")$";
    }

    /// <summary>The roman numeral for a spell level (1-8).</summary>
    public static string RomanLevel(int level) => Roman[Math.Clamp(level, 1, Roman.Length) - 1];

    /// <summary>
    /// Rows for the spells the player added from the lists: a leveled family ("spell", Name the
    /// family, Level 1-8) or a cantrip ("cantrip", Name without tier, Level the tier index).
    /// Ticked; "or better" unless the extra says otherwise. They go after the item's spells.
    /// </summary>
    public static void AddExtras(List<LootItemAttribute> rows, IReadOnlyList<LootEditItemExtra>? extras, LootSpellCatalog? spells = null)
    {
        if (extras == null || extras.Count == 0) return;
        int at = rows.FindIndex(r => r.Group == GroupPet || r.Group == GroupT11 || r.Group == GroupRequirements);
        if (at < 0) at = rows.Count;
        var ops = new[] { LootItemAttributeOp.SpellOrBetter, LootItemAttributeOp.SpellExact, LootItemAttributeOp.SpellAnyTier };
        var add = new List<LootItemAttribute>();
        foreach (LootEditItemExtra x in extras)
        {
            string name = LootItemRules.OneLine(x.Name).Trim();
            if (name.Length == 0) continue;
            string id = ExtraId(x);
            if (rows.Exists(r => r.Id == id) || add.Exists(r => r.Id == id)) continue;
            LootItemAttributeOp op = Array.IndexOf(ops, (LootItemAttributeOp)x.Op) >= 0 ? (LootItemAttributeOp)x.Op : ops[0];
            if (x.Kind == LootEditItemExtra.Cantrip)
            {
                string spell = CantripTiers[Math.Clamp(x.Level, 0, CantripTiers.Length - 1)] + " " + name;
                add.Add(new LootItemAttribute
                {
                    Id = id, Group = GroupSpells, Label = spell, Kind = LootItemAttribute.Shape.Spell, Spell = spell,
                    ItemText = "added", Tip = "Added from the cantrip list. Or better: this tier or higher; any tier: Minor to Paragon.",
                    Ops = ops, On = true, Op = op,
                });
            }
            else
            {
                int level = Math.Clamp(x.Level, 1, Roman.Length);
                add.Add(new LootItemAttribute
                {
                    Id = id, Group = GroupSpells, Label = name + " " + Roman[level - 1], Kind = LootItemAttribute.Shape.SpellFamily,
                    Spell = name, Key = level, ItemText = "added", Family = spells?.Find(name),
                    Tip = "Added from the spell list (the Self or Other spell). Or better: this level or higher; any tier: any level.",
                    Ops = ops, On = true, Op = op,
                });
            }
        }
        rows.InsertRange(at, add);
    }

    /// <summary>The row id an extra spell gets ("x:spell:Strength:6", "x:cantrip:3:Strength").</summary>
    public static string ExtraId(LootEditItemExtra x) =>
        x.Kind == LootEditItemExtra.Cantrip
            ? "x:cantrip:" + x.Level.ToString(CultureInfo.InvariantCulture) + ":" + LootItemRules.OneLine(x.Name).Trim()
            : "x:spell:" + LootItemRules.OneLine(x.Name).Trim() + ":" + x.Level.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The checklist without an item (the Inventory panel's and Global Inventory's own filter,
    /// 2026-10-24): every attribute a rule can test, unticked and with a value to fill in; a
    /// Class row first; named values pick from their lists; a row per cantrip tier; T11's
    /// summary keys. The spells come from the lists (<see cref="AddExtras"/>).
    /// </summary>
    /// <summary>
    /// An ACECustom / Infinite Leaftide-only key (2026-11-07, Silentkelpie: "make sure all t11 attributes
    /// stuff is gated behind infinite leaftide"): RynthAi's virtual T11 and weapon-card keys, ACECustom's
    /// own property range (9000+: pets, split arrows, T11 weapon quality) and its Zc keys (50000+).
    /// Decal's and VTank's synthetic keys (0x0A..., 0x0D...) are retail.
    /// </summary>
    public static bool IsCustomServerKey(int key) => T11Keys.IsVirtual(key) || (key >= 9000 && key < 0x0A000000);

    /// <summary>The blank checklist (every attribute); off Infinite Leaftide without the T11, card and pet rows.</summary>
    public static List<LootItemAttribute> ForBlank(bool ilt = true)
    {
        List<LootItemAttribute> all = ForBlankAll();
        if (!ilt) all.RemoveAll(r => r.Group == GroupT11 || r.Group == GroupPet || (r.Kind == LootItemAttribute.Shape.Long && IsCustomServerKey(r.Key)));
        return all;
    }

    private static List<LootItemAttribute> ForBlankAll()
    {
        var rows = new List<LootItemAttribute>();
        var classes = Array.FindAll(LootRuleText.ObjectClasses, c => c.Id != (int)AcObjectClass.Unknown && !NotAnItem((AcObjectClass)c.Id));
        Array.Sort(classes, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        rows.Add(new LootItemAttribute
        {
            Id = "class", Group = GroupItem, Label = "Class", Kind = LootItemAttribute.Shape.ObjectClass,
            Tip = "The kind of item (Decal / VTank object class).", ItemText = "any",
            Ops = new[] { LootItemAttributeOp.Exactly }, Op = LootItemAttributeOp.Exactly, Choices = classes,
            Value = classes.Length > 0 ? classes[0].Id : 0,
        });
        foreach (Spec s in Specs)
        {
            if (s.Group == GroupRatings && s.Key == 370)
                rows.Add(new LootItemAttribute
                {
                    Id = "ratings", Group = GroupRatings, Label = "Total ratings", Kind = LootItemAttribute.Shape.TotalRatings,
                    Tip = "Damage, damage resist, crit, crit resist, crit damage, crit damage resist, heal boost and vitality ratings added up.",
                    ItemText = "any", Ops = new[] { LootItemAttributeOp.AtLeast }, Editable = true, Op = LootItemAttributeOp.AtLeast,
                });
            if (s.Group == GroupRequirements && !rows.Exists(r => r.Group == GroupSpells)) AddBlankSpellRows(rows);
            LootItemAttribute r = PropertyRow(s, 0, false);
            (int Id, string Name)[]? table = r.Choices;
            rows.Add(new LootItemAttribute
            {
                Id = r.Id, Group = r.Group, Label = r.Label, Tip = r.Tip, Kind = r.Kind, Key = r.Key, Ops = r.Ops, Op = r.Op,
                // A named value without a list (equip slots, armor set) is typed as its number here.
                Editable = r.Editable || table == null, Choices = table, Flags = r.Flags, ItemText = "any",
                Value = table != null && table.Length > 0 ? table[0].Id : 0,
            });
            if (!s.IsDouble && s.Key == LootItemFacts.ImbuedEffectKey) rows.Add(RendRow(on: false));
        }
        if (!rows.Exists(r => r.Group == GroupSpells)) AddBlankSpellRows(rows);

        // T11: the keys that need no particular item, before Requirements.
        int t11At = rows.FindIndex(r => r.Group == GroupRequirements);
        if (t11At < 0) t11At = rows.Count;
        var t11 = new List<LootItemAttribute>();
        void T11Row(string id, string label, int key, bool named, bool equal = false)
        {
            LootItemAttributeOp[] ops = equal ? new[] { LootItemAttributeOp.Exactly }
                : new[] { LootItemAttributeOp.AtLeast, LootItemAttributeOp.Exactly, LootItemAttributeOp.AtMost };
            (int Id, string Name)[]? choices = named ? LootRuleText.ValueTableFor(key) : null;
            t11.Add(new LootItemAttribute
            {
                Id = "t11:" + id, Group = GroupT11, Label = label, Kind = LootItemAttribute.Shape.Long, Key = key,
                ItemText = equal ? "yes" : "any", Ops = ops, Op = ops[0], Editable = !named && !equal, Choices = choices,
                Value = equal ? 1 : choices != null && choices.Length > 0 ? choices[0].Id : 0,
            });
        }
        T11Row("is", "Is T11", T11Keys.IsT11, named: false, equal: true);
        T11Row("tier", "Tier", T11Keys.EstimatedTier, named: false);
        T11Row("grade", "Weapon grade", T11Keys.WeaponGrade, named: true);
        T11Row("dmgpct", "Damage %", T11Keys.DamagePercent, named: false);
        T11Row("geargrade", "Gear grade", T11Keys.GearGrade, named: true);
        T11Row("cos", "Cast on Strike count", T11Keys.CastOnStrikeCount, named: false);
        T11Row("rare", "T11 rare", 51100, named: true);
        T11Row("quality", "Weapon quality (0-1000)", 9060, named: false);
        T11Row("sum:" + T11Keys.ModifierCount.ToString(CultureInfo.InvariantCulture), "Modifier count", T11Keys.ModifierCount, named: false);
        T11Row("sum:" + T11Keys.ModifierRatingTotal.ToString(CultureInfo.InvariantCulture), "Modifier rating total", T11Keys.ModifierRatingTotal, named: false);
        // ACECustom's newer loot items (2026-11-11, T11Items): "T11 item is School gem", "Item tier at least 15".
        T11Row("itemkind", "T11 item (bounty, gem, token...)", T11Keys.ItemKind, named: true);
        T11Row("itemtier", "T11 item tier (bounty, school gem)", T11Keys.ItemTier, named: false);
        T11Row("coins", "Infusion gem coins", T11Keys.InfusionCoins, named: false);
        rows.InsertRange(t11At, t11);
        return rows;
    }

    // ── A rule back onto the checklist (2026-11-07: edit a rule with the same checklist) ──────

    /// <summary>
    /// The checklist choices that make <paramref name="conditions"/> again, on the blank checklist's
    /// <paramref name="rows"/>; what no row can say (spells by name, string matches, the Disabled
    /// marker, a second condition on the same key...) goes to <paramref name="leftovers"/>, kept as
    /// written. Rebuilding with the choices plus the leftovers gives the same tests back.
    /// </summary>
    public static List<LootEditItemChoice> ChoicesFromRule(List<LootItemAttribute> rows, IReadOnlyList<VTankLootCondition> conditions,
        out List<VTankLootCondition> leftovers)
    {
        var picked = new Dictionary<string, LootEditItemChoice>();
        leftovers = new List<VTankLootCondition>();
        static bool Int(IList<string> d, int i, out long v)
        {
            v = 0;
            return i < d.Count && long.TryParse(d[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }
        static bool Dbl(IList<string> d, int i, out double v)
        {
            v = 0;
            return i < d.Count && double.TryParse(d[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
        bool Take(LootItemAttribute? r, LootItemAttributeOp op, string value)
        {
            if (r == null || picked.ContainsKey(r.Id) || Array.IndexOf(r.Ops, op) < 0) return false;
            // A named value the row's list doesn't have would be dropped by Apply: keep it as written.
            if (r.Choices != null && op != LootItemAttributeOp.Any
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pv)
                && !Array.Exists(r.Choices, x => x.Id == pv)
                && !(r.Flags && pv > 0 && (pv & ~(long)AllBits(r.Choices)) == 0)) return false;
            picked[r.Id] = new LootEditItemChoice { Id = r.Id, On = true, Op = (int)op, Value = value };
            return true;
        }
        string N(double v) => Number(v);

        foreach (VTankLootCondition c in conditions)
        {
            IList<string> d = c.DataLines;
            bool ok = false;
            switch (c.NodeType)
            {
                case VTankNodeTypes.ObjectClass when Int(d, 0, out long cls):
                    ok = Take(rows.Find(r => r.Kind == LootItemAttribute.Shape.ObjectClass), LootItemAttributeOp.Exactly, N(cls));
                    break;
                case VTankNodeTypes.LongValKeyGE or VTankNodeTypes.LongValKeyLE or VTankNodeTypes.LongValKeyE
                    or VTankNodeTypes.LongValKeyNE or VTankNodeTypes.LongValKeyFlagExists when Int(d, 0, out long val) && Int(d, 1, out long key):
                {
                    LootItemAttribute? r = rows.Find(x => x.Kind == LootItemAttribute.Shape.Long && x.Key == key && x.Id != RendRowId);
                    if (r == null) break;
                    // "Has flag" of several rends (any elemental rend) on the imbue key: the "Plus a rend" row, when
                    // the Imbue row has its own test already or the rule tests the imbue key more than once.
                    LootItemAttribute? rend = rows.Find(x => x.Id == RendRowId);
                    if (c.NodeType == VTankNodeTypes.LongValKeyFlagExists && rend != null && key == LootItemFacts.ImbuedEffectKey
                        && !IsSingle(val) && (val & ~RendBits) == 0 && !picked.ContainsKey(rend.Id)
                        && (picked.ContainsKey(r.Id) || ImbueTests(conditions) > 1))
                    {
                        ok = Take(rend, LootItemAttributeOp.HasAny, N(val));
                        break;
                    }
                    if (c.NodeType == VTankNodeTypes.LongValKeyFlagExists && picked.TryGetValue(r.Id, out LootEditItemChoice? had)
                        && (had.Op == (int)LootItemAttributeOp.HasAny || had.Op == (int)LootItemAttributeOp.HasAll)
                        && Array.IndexOf(r.Ops, LootItemAttributeOp.HasAll) >= 0
                        && long.TryParse(had.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long hv)
                        && hv > 0 && IsSingle(val)
                        && (had.Op == (int)LootItemAttributeOp.HasAll || IsSingle(hv)))
                    {
                        // A second "has flag" on the same key: the item needs both (has all).
                        had.Op = (int)LootItemAttributeOp.HasAll;
                        had.Value = N(hv | val);
                        ok = true;
                        break;
                    }
                    LootItemAttributeOp op = c.NodeType switch
                    {
                        VTankNodeTypes.LongValKeyLE => LootItemAttributeOp.AtMost,
                        VTankNodeTypes.LongValKeyE => LootItemAttributeOp.Exactly,
                        VTankNodeTypes.LongValKeyNE => LootItemAttributeOp.Not,
                        VTankNodeTypes.LongValKeyFlagExists => LootItemAttributeOp.HasAny,
                        _ => r.Choices != null && val == 1 && Array.IndexOf(r.Ops, LootItemAttributeOp.Any) >= 0
                            ? LootItemAttributeOp.Any : LootItemAttributeOp.AtLeast,
                    };
                    // A named row has no >= / <=: those stay as written.
                    ok = Take(r, op, N(val));
                    break;
                }
                case VTankNodeTypes.DoubleValKeyGE or VTankNodeTypes.DoubleValKeyLE when Dbl(d, 0, out double dv) && Int(d, 1, out long dkey):
                {
                    LootItemAttribute? r = rows.Find(x => x.Kind == LootItemAttribute.Shape.Double && x.Key == dkey);
                    if (r == null) break;
                    bool ge = c.NodeType == VTankNodeTypes.DoubleValKeyGE;
                    if (picked.TryGetValue(r.Id, out LootEditItemChoice? had) && had.Value == N(dv)
                        && had.Op == (int)(ge ? LootItemAttributeOp.AtMost : LootItemAttributeOp.AtLeast))
                    {
                        had.Op = (int)LootItemAttributeOp.Exactly;   // >= and <= the same value
                        ok = true;
                        break;
                    }
                    ok = Take(r, ge ? LootItemAttributeOp.AtLeast : LootItemAttributeOp.AtMost, N(dv));
                    break;
                }
                case VTankNodeTypes.TotalRatingsGE when Dbl(d, 0, out double tr):
                    ok = Take(rows.Find(r => r.Kind == LootItemAttribute.Shape.TotalRatings), LootItemAttributeOp.AtLeast, N(tr));
                    break;
                case VTankNodeTypes.SpellCountGE when Dbl(d, 0, out double sc):
                    ok = Take(rows.Find(r => r.Kind == LootItemAttribute.Shape.SpellCount), LootItemAttributeOp.AtLeast, N(sc));
                    break;
                case VTankNodeTypes.SpellMatch when d.Count >= 3 && (d[1] ?? string.Empty).Length == 0 && Dbl(d, 2, out double cn):
                {
                    LootItemAttribute? r = rows.Find(x => x.Kind == LootItemAttribute.Shape.CantripTier
                        && d[0] == "^(" + TierAlternation(x.Key) + ") ");
                    ok = Take(r, LootItemAttributeOp.AtLeast, N(cn));
                    break;
                }
            }
            if (!ok) leftovers.Add(c);
        }
        return new List<LootEditItemChoice>(picked.Values);
    }

    private static bool IsSingle(long v) => v > 0 && (v & (v - 1)) == 0;

    /// <summary>How many of the conditions test the imbue key (179).</summary>
    private static int ImbueTests(IReadOnlyList<VTankLootCondition> conditions)
    {
        int n = 0;
        foreach (VTankLootCondition c in conditions)
            if (c.NodeType is VTankNodeTypes.LongValKeyGE or VTankNodeTypes.LongValKeyLE or VTankNodeTypes.LongValKeyE
                    or VTankNodeTypes.LongValKeyNE or VTankNodeTypes.LongValKeyFlagExists
                && c.DataLines.Count > 1 && c.DataLines[1]?.Trim() == "179") n++;
        return n;
    }

    /// <summary>Every flag a flags row's list names (single flags and the "any of" picks).</summary>
    internal static int AllBits((int Id, string Name)[] choices)
    {
        int all = 0;
        foreach (var c in choices) all |= c.Id;
        return all;
    }

    // ── Imbues by class (2026-11-07, from ACECustom's loot and recipes) ──────────────────────
    // Melee and missile weapons: Critical Strike, Crippling Blow, Armor Rending and the physical and
    // elemental rends; ACECustom's drops OR a rend matching the weapon's element with Armor Rending, so
    // they can carry several. Casters: the same, with Nether Rending (ACECustom casters carry Armor
    // Rending too, besides the Spell Armor Rend card). Armor, clothing and jewelry: the defense imbues,
    // one at a time.
    private const int Crit = 1, Cripple = 2, ArmorRend = 4, Slash = 8, Pierce = 16, Bludge = 32, Acid = 64, Cold = 128,
        Elec = 256, Fire = 512, MeleeDef = 1024, MissileDef = 2048, MagicDef = 4096, Nether = 16384;
    private const int Physical = Slash | Pierce | Bludge, Elements = Acid | Cold | Elec | Fire;

    /// <summary>The imbue flags a class can carry (null: no class, every imbue), and whether it can carry several.</summary>
    public static int? ImbuesFor(int objectClass, out bool several)
    {
        several = true;
        switch ((AcObjectClass)objectClass)
        {
            case AcObjectClass.MeleeWeapon:
            case AcObjectClass.MissileWeapon:
                return Crit | Cripple | ArmorRend | Physical | Elements;
            case AcObjectClass.WandStaffOrb:
                // Armor Rending too: ACECustom casters carry it (rules like "Caster Slayer + Nether Rend + AR").
                return Crit | Cripple | ArmorRend | Physical | Elements | Nether;
            case AcObjectClass.Armor:
            case AcObjectClass.Clothing:
            case AcObjectClass.Jewelry:
                several = false;
                return MeleeDef | MissileDef | MagicDef;
            default:
                return null;
        }
    }

    /// <summary>
    /// The Imbue row's list for the class (<paramref name="objectClass"/>; 0 or unknown: every imbue):
    /// only the imbues it can carry, the "any of" picks cut down to them, and "has all" only where a
    /// class carries several.
    /// </summary>
    public static void FilterImbues(List<LootItemAttribute> rows, int objectClass)
    {
        LootItemAttribute? r = rows.Find(x => x.Kind == LootItemAttribute.Shape.Long && x.Key == LootItemFacts.ImbuedEffectKey && x.Choices != null && x.Id != RendRowId);
        if (r == null) return;
        int? allowed = ImbuesFor(objectClass, out bool several);
        if (allowed is not int ok) return;
        // "Plus a rend": the class's rends only; none (armor, jewelry): no row (2026-11-08).
        LootItemAttribute? rend = rows.Find(x => x.Id == RendRowId);
        if (rend?.Choices != null)
        {
            // One rend: the class must carry it. An "any" pick: kept while two or more of its rends fit the class
            // ("Any elemental rend" on a sword: nether never turns up there, the rest do).
            var keep = Array.FindAll(rend.Choices, c => IsSingle(c.Id) ? (c.Id & ok) == c.Id : !IsSingle(c.Id & ok) && (c.Id & ok) != 0);
            if (keep.Length == 0 || (ok & RendBits) == 0) { if (!rend.On) rows.Remove(rend); }
            else
            {
                rend.Choices = keep;
                if (!Array.Exists(keep, c => c.Id == (int)rend.Value)) rend.Value = keep[0].Id;
            }
        }
        var list = new List<(int, string)>();
        var seen = new HashSet<int>();
        foreach (var c in LootRuleText.ImbuedEffects)
        {
            int bits = c.Id & ok;
            if (bits == 0) continue;
            bool single = (c.Id & (c.Id - 1)) == 0;
            // An "any of" pick keeps its name only while every flag it names fits the class.
            if (!single && (bits != c.Id || (bits & (bits - 1)) == 0)) continue;
            if (seen.Add(c.Id)) list.Add((c.Id, c.Name));
        }
        r.Choices = list.ToArray();
        if (!several)
        {
            r.Ops = Array.FindAll(r.Ops, o => o != LootItemAttributeOp.HasAll);
            if (r.Op == LootItemAttributeOp.HasAll) r.Op = r.Ops[0];
        }
    }

    /// <summary>The class the checklist is for: the ticked Class row's (blank), else 0.</summary>
    internal static int TickedClass(List<LootItemAttribute> rows)
    {
        LootItemAttribute? c = rows.Find(x => x.Kind == LootItemAttribute.Shape.ObjectClass);
        return c != null && c.On ? (int)c.Value : 0;
    }

    /// <summary>The blank checklist's spell group: a cantrip count per tier and the spell count.</summary>
    private static void AddBlankSpellRows(List<LootItemAttribute> rows)
    {
        for (int t = 0; t < CantripTiers.Length; t++)
            rows.Add(new LootItemAttribute
            {
                Id = "cantrips:" + CantripTiers[t], Group = GroupSpells,
                Label = t == CantripTiers.Length - 1 ? CantripTiers[t] + " cantrips" : CantripTiers[t] + "-or-better cantrips",
                Kind = LootItemAttribute.Shape.CantripTier, Key = t, ItemText = "any", Ops = new[] { LootItemAttributeOp.AtLeast },
                Editable = true, Op = LootItemAttributeOp.AtLeast, Value = 1, Tip = "How many cantrips of this tier or higher.",
            });
        rows.Add(new LootItemAttribute
        {
            Id = "spellcount", Group = GroupSpells, Label = "Spell count", Kind = LootItemAttribute.Shape.SpellCount,
            ItemText = "any", Ops = new[] { LootItemAttributeOp.AtLeast }, Editable = true, Op = LootItemAttributeOp.AtLeast, Value = 1,
        });
    }

    private static bool NotAnItem(AcObjectClass c) => c is AcObjectClass.Monster or AcObjectClass.Player or AcObjectClass.Vendor
        or AcObjectClass.Door or AcObjectClass.Corpse or AcObjectClass.Lifestone or AcObjectClass.Portal or AcObjectClass.Npc
        or AcObjectClass.Sign or AcObjectClass.Housing;

    /// <summary>A number as rules write it: whole numbers plain, decimals to 4 places.</summary>
    public static string Number(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A long key's value by name when it has one ("Gold", "Light Weapons"), else the number.</summary>
    private static string NamedOrNumber(int key, int v)
        => LootRuleText.ValueName(key, v.ToString(CultureInfo.InvariantCulture)) ?? v.ToString(CultureInfo.InvariantCulture);
}
