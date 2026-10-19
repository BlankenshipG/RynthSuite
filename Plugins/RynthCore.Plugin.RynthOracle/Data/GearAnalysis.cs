using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// One item read from the game for the Gear tab: what it is, where it sits, its spells (from the
/// appraisal) and the properties the ratings view shows. Filled by <see cref="GearScanner"/>.
/// </summary>
internal sealed class GearPiece
{
    public uint Id;
    public string Name = "";
    /// <summary>ItemType flags (MeleeWeapon 0x1, Armor 0x2, Clothing 0x4, Jewelry 0x8, MissileWeapon 0x100, Caster 0x8000).</summary>
    public uint ItemType;
    /// <summary>EquipMask it is worn in; 0 in a pack.</summary>
    public uint Location;
    public bool Equipped;
    /// <summary>The appraisal reached the client (or it was asked for and answered with no spells).</summary>
    public bool Appraised;
    /// <summary>The item's own spells (cantrips, banes, procs): Decal's Spell list.</summary>
    public uint[] Inscribed = Array.Empty<uint>();
    /// <summary>Item enchantments active on it right now (appraisal ids with 0x80000000): Decal's ActiveSpell.</summary>
    public uint[] OnItem = Array.Empty<uint>();
    public int SetId;
    public int MaxLevel;
    public int XpStyle;
    public long BaseXp;
    public long TotalXp;
    /// <summary>PropertyInt.DamageType (45): the weapon's element (a caster's too).</summary>
    public int DamageType;
    /// <summary>The gear ratings it carries (PropertyInt 370-389), non-zero only.</summary>
    public (int Prop, int Value)[] Ratings = Array.Empty<(int, int)>();

    /// <summary>Gains levels from experience (ItemBaseXp, ItemMaxLevel and ItemXpStyle all set).</summary>
    public bool Leveled => BaseXp > 0 && MaxLevel > 0 && XpStyle > 0;

    /// <summary>The item's level from its experience (0 for an item that doesn't level).</summary>
    public int Level => Leveled ? GearMath.ItemLevel(TotalXp, BaseXp, MaxLevel, XpStyle) : 0;
}

/// <summary>A spell as the Gear tab knows it: from portal.dat, else SpellData.txt, else only its id.</summary>
internal readonly record struct SpellMeta(uint Id, string Name, uint Category, uint Power, bool Enchantment, bool ItemTarget, bool Known)
{
    public static SpellMeta Unknown(uint id) => new(id, "Spell " + id.ToString(CultureInfo.InvariantCulture), 0, 0, true, false, false);
}

/// <summary>Where a spell on the character comes from.</summary>
internal enum SpellLayer
{
    /// <summary>One of the item's own spells.</summary>
    Inscribed,
    /// <summary>An item enchantment active on the item (a bane cast on it, Blood Drinker).</summary>
    OnItem,
    /// <summary>An equipment set's bonus for the pieces worn.</summary>
    SetBonus,
    /// <summary>An enchantment on the character (spells cast on you, and worn items' cantrips).</summary>
    Buff,
}

/// <summary>One spell from one source.</summary>
internal sealed class SpellSource
{
    /// <summary>The item (0 for a set bonus or a buff on the character).</summary>
    public uint ItemId;
    public string ItemName = "";
    public SpellLayer Layer;
    public SpellMeta Spell;
    /// <summary>The equipment set of a set bonus, else 0.</summary>
    public int SetId;

    public string LayerLabel => GearAnalysis.LayerLabel(Layer);
}

/// <summary>What a family's spells do together.</summary>
internal enum OverlapKind
{
    /// <summary>Different spells of one enchantment family: only the highest power works, the rest are wasted.</summary>
    Overlap,
    /// <summary>The same spell from more than one item: one copy works, the others are wasted.</summary>
    Duplicate,
    /// <summary>Not enchantments (procs, heals): each works on its own.</summary>
    Stacks,
    /// <summary>One spell, one source.</summary>
    Ok,
}

/// <summary>One family on one target (the character, or one item for item spells).</summary>
internal sealed class OverlapRow
{
    public OverlapKind Kind;
    public uint Category;
    public string Family = "";
    /// <summary>The item the spells are on (item spells), or "" for the character.</summary>
    public string Scope = "";
    public SpellSource Winner = null!;
    public List<SpellSource> Sources = new();
    /// <summary>"item/layer/power" per source, highest first; "(beat)" on the ones that lose.</summary>
    public string Summary = "";
}

/// <summary>Ward and bane tiers, lowest to highest: a higher tier in the family wins.</summary>
internal enum ProtTier { Missing = 0, Lower = 1, Minor = 2, Major = 3, Epic = 4, Legendary = 5 }

/// <summary>One element of the life wards or banes: the spell that wins and what it beats.</summary>
internal sealed class ProtRow
{
    public string Element = "";
    public uint Category;
    public ProtTier Tier;
    public string Winner = "";
    public string Source = "";
    public string Beaten = "";
    public bool Missing => Tier == ProtTier.Missing;
}

/// <summary>Life wards (on the character) and banes (on the armour), seven elements each.</summary>
internal sealed class ProtSummary
{
    public List<ProtRow> Life = new();
    public List<ProtRow> Crit = new();
    public int LifeLegendary => Life.Count(r => r.Tier == ProtTier.Legendary);
    public int CritLegendary => Crit.Count(r => r.Tier == ProtTier.Legendary);
    public string LifeMissing => string.Join(", ", Life.Where(r => r.Missing).Select(r => r.Element));
    public string CritMissing => string.Join(", ", Crit.Where(r => r.Missing).Select(r => r.Element));
}

/// <summary>An equipment set worn, its level and the bonus spells it gives.</summary>
internal sealed class SetBonusGroup
{
    public int SetId;
    public string Name = "";
    public int Pieces;
    public int Level;
    public uint Tier;
    public bool LeveledSet;
    public string Label = "";
    public string PieceNames = "";
    public List<SpellSource> Spells = new();
}

/// <summary>The Overlap view's whole picture at one moment.</summary>
internal sealed class GearReport
{
    public DateTime CheckedLocal;
    public int Equipped;
    public int NotAppraised;
    public int SpellSources;
    public int UnknownSpells;
    public List<GearPiece> Pieces = new();
    public Dictionary<uint, List<SpellSource>> PieceSpells = new();
    public List<SetBonusGroup> Sets = new();
    public List<SpellSource> Buffs = new();
    public List<SpellSource> Paragon = new();
    public List<OverlapRow> Rows = new();
    public ProtSummary Protection = new();
    public int Count(OverlapKind kind) => Rows.Count(r => r.Kind == kind);
}

/// <summary>One item of the Ratings view (sets, item levels, gear ratings; UB-ILT's Paragon gear, for every server).</summary>
internal sealed class RatingRow
{
    public GearPiece Piece = null!;
    public string Category = "";
    public string TypeLabel = "";
    public string SetName = "-";
    public bool Paragon;
    public string Stats = "";
}

internal sealed class RatingsReport
{
    public DateTime CheckedLocal;
    public bool IncludePack;
    public List<RatingRow> Rows = new();
    /// <summary>Every rating summed over the worn items.</summary>
    public List<(int Prop, int Value)> WornTotals = new();
    public int EquippedRows => Rows.Count(r => r.Piece.Equipped);
}

/// <summary>Item experience to level (ACE ExperienceSystem.ItemTotalXPToLevel).</summary>
internal static class GearMath
{
    public static int ItemLevel(long totalXp, long baseXp, int maxLevel, int xpStyle)
    {
        if (baseXp <= 0 || maxLevel <= 0) return 0;
        ulong gained = (ulong)Math.Max(0, totalXp), b = (ulong)baseXp;
        int level = 0;
        switch (xpStyle)
        {
            case 1:   // Fixed
                level = (int)(gained / b);
                break;
            case 2:   // ScalesWithLevel: each level costs twice the last
            {
                ulong cost = b, left = gained;
                while (left >= cost && level < maxLevel)
                {
                    level++;
                    left -= cost;
                    if (cost > ulong.MaxValue / 2) break;
                    cost *= 2;
                }
                break;
            }
            case 3:   // FixedPlusBase
                if (gained >= b && gained < b * 3UL) level = 1;
                else if (gained >= b) level = (int)((gained - b) / b);
                break;
        }
        return Math.Min(level, maxLevel);
    }
}

/// <summary>
/// The Gear tab's analysis, all of it pure (the tests build pieces by hand). Ported from UB-ILT's
/// EquippedSpellOverlapAnalyzer, EquippedProtectionCoverage, EquippedGearSetBonus and
/// ParagonGearAnalyzer, with the spell facts taken from the client's own spell table instead of
/// a catalog exported from one server, so it reads any server's gear.
/// </summary>
internal static class GearAnalysis
{
    public const uint ItemMelee = 0x1, ItemArmor = 0x2, ItemClothing = 0x4, ItemJewelry = 0x8,
                      ItemMissile = 0x100, ItemContainer = 0x200, ItemCaster = 0x8000;
    public const uint ItemWeapons = ItemMelee | ItemMissile | ItemCaster;
    public const uint ItemWearables = ItemWeapons | ItemArmor | ItemClothing | ItemJewelry;

    /// <summary>ACE's paragon weapon sets (ACECustom EquipmentSet 138-140); harmless where they don't exist.</summary>
    public const int ParagonSetFirst = 138, ParagonSetLast = 140;
    /// <summary>Luminous Amber puts ItemBaseXp at 2,000,000,000 on a paragon weapon.</summary>
    public const long ParagonWeaponBaseXp = 2_000_000_000L;

    /// <summary>The life ward families, in UB-ILT's order (ACE SpellCategory).</summary>
    public static readonly (uint Category, string Element)[] LifeWards =
    {
        (403, "Slashing"), (405, "Piercing"), (401, "Bludgeon"), (287, "Fire"), (289, "Cold"), (285, "Acid"), (291, "Storm"),
    };

    /// <summary>The bane families (ExtraXResistanceRaising: armour against an element).</summary>
    public static readonly (uint Category, string Element)[] CritBanes =
    {
        (395, "Slashing"), (393, "Piercing"), (383, "Bludgeon"), (385, "Fire"), (387, "Cold"), (381, "Acid"), (397, "Storm"),
    };

    /// <summary>The gear ratings (ACE PropertyInt), short and long names.</summary>
    public static readonly (int Prop, string Short, string Long)[] RatingProps =
    {
        (370, "D", "Damage"), (371, "DR", "Damage resist"), (372, "C", "Crit"), (373, "CR", "Crit resist"),
        (374, "CD", "Crit damage"), (375, "CDR", "Crit damage resist"), (376, "H", "Healing boost"),
        (377, "NR", "Nether resist"), (378, "LR", "Life resist"), (379, "MH", "Max health"),
        (383, "PKD", "PK damage"), (384, "PKDR", "PK damage resist"), (388, "OP", "Overpower"), (389, "OPR", "Overpower resist"),
    };

    public static string LayerLabel(SpellLayer layer) => layer switch
    {
        SpellLayer.Inscribed => "Inscribed",
        SpellLayer.OnItem => "Active",
        SpellLayer.SetBonus => "Set bonus",
        _ => "Active buff",
    };

    private static string LayerShort(SpellLayer layer) => layer switch
    {
        SpellLayer.Inscribed => "Ins",
        SpellLayer.OnItem => "Act",
        SpellLayer.SetBonus => "Set",
        _ => "Buff",
    };

    // ── Overlap ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything on the worn gear and the character at once: each piece's spells, the set bonuses,
    /// the character's enchantments, the overlap rows and the ward / bane coverage.
    /// </summary>
    public static GearReport Analyze(IReadOnlyList<GearPiece> equipped, IReadOnlyList<uint> buffSpellIds,
                                     Func<uint, SpellMeta> lookup, Func<int, SpellSetDef?> setOf,
                                     string characterName, DateTime nowLocal)
    {
        var report = new GearReport { CheckedLocal = nowLocal, Equipped = equipped.Count };
        var all = new List<SpellSource>();
        var unknown = new HashSet<uint>();
        SpellMeta Look(uint id)
        {
            SpellMeta m = lookup(id);
            if (!m.Known) unknown.Add(id);
            return m;
        }

        foreach (GearPiece p in equipped.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            report.Pieces.Add(p);
            if (!p.Appraised) report.NotAppraised++;
            var list = new List<SpellSource>();
            foreach (uint id in p.Inscribed.Distinct())
                list.Add(new SpellSource { ItemId = p.Id, ItemName = p.Name, Layer = SpellLayer.Inscribed, Spell = Look(id) });
            foreach (uint id in p.OnItem.Distinct())
                list.Add(new SpellSource { ItemId = p.Id, ItemName = p.Name, Layer = SpellLayer.OnItem, Spell = Look(id) });
            report.PieceSpells[p.Id] = list;
            all.AddRange(list);
        }

        report.Sets = SetBonuses(equipped, setOf, Look);
        foreach (SetBonusGroup g in report.Sets) all.AddRange(g.Spells);

        string who = characterName.Length > 0 ? characterName : "(character)";
        foreach (uint id in buffSpellIds.Distinct())
            report.Buffs.Add(new SpellSource { ItemName = who, Layer = SpellLayer.Buff, Spell = Look(id) });
        report.Buffs.Sort((a, b) => string.Compare(a.Spell.Name, b.Spell.Name, StringComparison.OrdinalIgnoreCase));
        all.AddRange(report.Buffs);

        report.SpellSources = all.Count;
        report.UnknownSpells = unknown.Count;
        report.Rows = BuildRows(all);
        report.Protection = Protection(all);
        report.Paragon = all.Where(IsParagonSource)
                            .OrderByDescending(s => s.Spell.Power).ThenBy(s => s.Spell.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return report;
    }

    /// <summary>Spells cast on an item are per item (each piece is its own target); the rest land on the character.</summary>
    private static bool ItemScoped(SpellSource s) =>
        s.Spell.ItemTarget && s.ItemId != 0 && (s.Layer == SpellLayer.Inscribed || s.Layer == SpellLayer.OnItem);

    /// <summary>A family key that keeps spells with no known family apart (each its own row).</summary>
    private static long FamilyKey(SpellMeta m) => m.Category != 0 ? m.Category : (1L << 32) | m.Id;

    internal static List<OverlapRow> BuildRows(List<SpellSource> sources)
    {
        var rows = new List<OverlapRow>();
        foreach (var g in sources.GroupBy(s => (Family: FamilyKey(s.Spell), Item: ItemScoped(s) ? s.ItemId : 0u)))
            rows.Add(MakeRow(g.ToList(), g.Key.Item != 0));
        return rows
            .OrderBy(r => (int)r.Kind)
            .ThenBy(r => r.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static OverlapRow MakeRow(List<SpellSource> group, bool itemScope)
    {
        var ordered = group.OrderByDescending(s => s.Spell.Power).ThenBy(s => s.Spell.Id)
                           .ThenBy(s => s.ItemName, StringComparer.OrdinalIgnoreCase).ToList();
        SpellSource winner = ordered[0];
        int distinctSpells = group.Select(s => s.Spell.Id).Distinct().Count();
        // The same spell on two pieces (a worn cantrip also shows as the character's buff: that one
        // doesn't count, it is the same copy).
        int itemsWithWinner = group.Where(s => s.Spell.Id == winner.Spell.Id && s.Layer != SpellLayer.Buff)
                                   .Select(s => s.Layer == SpellLayer.SetBonus ? 0x8000_0000u | (uint)s.SetId : s.ItemId)
                                   .Distinct().Count();
        bool exclusive = group.All(s => s.Spell.Enchantment);
        OverlapKind kind = distinctSpells > 1
            ? (exclusive ? OverlapKind.Overlap : OverlapKind.Stacks)
            : (itemsWithWinner > 1 && exclusive ? OverlapKind.Duplicate : OverlapKind.Ok);

        return new OverlapRow
        {
            Kind = kind,
            Category = winner.Spell.Category,
            Family = GearNames.FamilyLabel(winner.Spell.Category),
            Scope = itemScope ? winner.ItemName : "",
            Winner = winner,
            Sources = ordered,
            Summary = Summary(ordered, winner, kind),
        };
    }

    private static string Summary(List<SpellSource> ordered, SpellSource winner, OverlapKind kind)
    {
        var sb = new StringBuilder();
        bool winnerCopySeen = false;
        foreach (SpellSource s in ordered)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(Cut(s.ItemName, 18)).Append('/').Append(LayerShort(s.Layer)).Append("/P").Append(s.Spell.Power.ToString(CultureInfo.InvariantCulture));
            if (kind == OverlapKind.Overlap && s.Spell.Id != winner.Spell.Id) sb.Append(" (beat)");
            else if (kind == OverlapKind.Duplicate && s.Layer != SpellLayer.Buff)
            {
                if (winnerCopySeen) sb.Append(" (extra)");
                winnerCopySeen = true;
            }
        }
        return sb.ToString();
    }

    // ── Life wards and banes ─────────────────────────────────────────────────

    internal static ProtSummary Protection(IReadOnlyList<SpellSource> sources) => new()
    {
        Life = LifeWards.Select(s => ProtSlot(s.Element, s.Category, sources, life: true)).ToList(),
        Crit = CritBanes.Select(s => ProtSlot(s.Element, s.Category, sources, life: false)).ToList(),
    };

    private static ProtRow ProtSlot(string element, uint category, IReadOnlyList<SpellSource> sources, bool life)
    {
        var mine = sources.Where(s => InSlot(s, element, category, life)).ToList();
        if (mine.Count == 0) return new ProtRow { Element = element, Category = category, Tier = ProtTier.Missing, Winner = "MISSING" };

        var distinct = mine.GroupBy(s => s.Spell.Id).Select(g => g.First())
                           .OrderByDescending(s => (int)TierOf(s.Spell.Name)).ThenByDescending(s => s.Spell.Power).ThenByDescending(s => s.Spell.Id)
                           .ToList();
        SpellSource winner = distinct[0];
        ProtTier tier = TierOf(winner.Spell.Name);
        var beaten = distinct.Skip(1).Select(s => Cut(s.Spell.Name, 20)).ToList();
        return new ProtRow
        {
            Element = element,
            Category = category,
            Tier = tier,
            Winner = winner.Spell.Name,
            Source = winner.Layer == SpellLayer.SetBonus ? winner.ItemName + " [set]" : winner.ItemName,
            Beaten = beaten.Count == 0 ? "" : string.Join(", ", beaten.Take(3)) + (beaten.Count > 3 ? "..." : ""),
        };
    }

    /// <summary>
    /// A source fills a ward slot (on the character) or a bane slot (on an item) by its family; a spell
    /// the tables don't know is placed by its name ("... Fire Ward", "... Flame Bane").
    /// </summary>
    private static bool InSlot(SpellSource s, string element, uint category, bool life)
    {
        if (s.Spell.Known && s.Spell.Category != 0)
            return s.Spell.Category == category && s.Spell.ItemTarget != life;
        string n = s.Spell.Name;
        if (n.IndexOf(life ? " Ward" : " Bane", StringComparison.OrdinalIgnoreCase) < 0) return false;
        foreach (string word in ElementWords(element))
            if (n.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string[] ElementWords(string element) => element switch
    {
        "Slashing" => new[] { "Slashing" },
        "Piercing" => new[] { "Piercing" },
        "Bludgeon" => new[] { "Bludgeon" },
        "Fire" => new[] { "Fire", "Flame" },
        "Cold" => new[] { "Cold", "Frost" },
        "Acid" => new[] { "Acid" },
        "Storm" => new[] { "Storm", "Lightning", "Electric" },
        _ => Array.Empty<string>(),
    };

    /// <summary>A ward or bane's tier from its name ("Legendary ...", "Epic ...").</summary>
    public static ProtTier TierOf(string name)
    {
        if (name.StartsWith("Legendary", StringComparison.OrdinalIgnoreCase)) return ProtTier.Legendary;
        if (name.StartsWith("Epic", StringComparison.OrdinalIgnoreCase)) return ProtTier.Epic;
        if (name.StartsWith("Major", StringComparison.OrdinalIgnoreCase)) return ProtTier.Major;
        if (name.StartsWith("Minor", StringComparison.OrdinalIgnoreCase)) return ProtTier.Minor;
        return ProtTier.Lower;
    }

    public static string TierLabel(ProtTier tier) => tier switch
    {
        ProtTier.Legendary => "Legendary",
        ProtTier.Epic => "Epic",
        ProtTier.Major => "Major",
        ProtTier.Minor => "Minor",
        ProtTier.Lower => "Other",
        _ => "MISSING",
    };

    // ── Equipment sets ───────────────────────────────────────────────────────

    /// <summary>
    /// The worn pieces grouped by set, as ACE's GetSpellSet: a leveled set's level is the sum of its
    /// pieces' item levels, any other set's the number of pieces worn; the set's spells for that level.
    /// </summary>
    internal static List<SetBonusGroup> SetBonuses(IReadOnlyList<GearPiece> equipped, Func<int, SpellSetDef?> setOf, Func<uint, SpellMeta> look)
    {
        var groups = new List<SetBonusGroup>();
        foreach (var bySet in equipped.Where(p => p.SetId > 0).GroupBy(p => p.SetId))
        {
            SpellSetDef? def = setOf(bySet.Key);
            if (def == null) continue;
            var pieces = bySet.ToList();
            bool leveled = pieces[0].XpStyle > 0;
            int level = leveled ? pieces.Sum(p => p.Level) : pieces.Count;
            uint tier = def.TierAt((uint)Math.Max(0, level));
            uint[] spells = def.SpellsAt((uint)Math.Max(0, level));
            if (spells.Length == 0) continue;
            string name = GearNames.SetName(bySet.Key);
            string label = leveled ? $"{name} ({pieces.Count} pc, level {level})" : $"{name} ({pieces.Count} pc)";
            var g = new SetBonusGroup
            {
                SetId = bySet.Key,
                Name = name,
                Pieces = pieces.Count,
                Level = level,
                Tier = tier,
                LeveledSet = leveled,
                Label = label,
                PieceNames = string.Join(", ", pieces.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
            };
            foreach (uint id in spells.Where(id => id != 0).Distinct())
                g.Spells.Add(new SpellSource { ItemName = label, Layer = SpellLayer.SetBonus, Spell = look(id), SetId = bySet.Key });
            groups.Add(g);
        }
        groups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return groups;
    }

    public static bool IsParagonSet(int setId) => setId >= ParagonSetFirst && setId <= ParagonSetLast;

    /// <summary>A paragon mastery spell (family "Paragon...") or a paragon set's bonus.</summary>
    private static bool IsParagonSource(SpellSource s) =>
        GearNames.FamilyRaw(s.Spell.Category).StartsWith("Paragon", StringComparison.Ordinal)
        || (s.Layer == SpellLayer.SetBonus && IsParagonSet(s.SetId));

    // ── Ratings (sets, levels, gear ratings) ─────────────────────────────────

    /// <summary>
    /// Items with a set, an item level or gear ratings, worn first; and the ratings summed over
    /// what is worn. UB-ILT's Paragon gear view, without the server's paragon gate: paragon items
    /// are marked where they exist.
    /// </summary>
    public static RatingsReport Ratings(IReadOnlyList<GearPiece> items, bool includePack, DateTime nowLocal)
    {
        var report = new RatingsReport { CheckedLocal = nowLocal, IncludePack = includePack };
        foreach (GearPiece p in items)
        {
            if (!includePack && !p.Equipped) continue;
            bool candidate = p.SetId > 0 || p.Ratings.Length > 0 || (p.MaxLevel > 0 && (p.ItemType & ItemWearables) != 0);
            if (!candidate) continue;
            string category = CategoryOf(p.ItemType);
            string element = (p.ItemType & ItemWeapons) != 0 ? ElementShort(p.DamageType) : "";
            report.Rows.Add(new RatingRow
            {
                Piece = p,
                Category = category,
                TypeLabel = element.Length > 0 ? category + " " + element : category,
                SetName = GearNames.SetName(p.SetId),
                Paragon = IsParagonSet(p.SetId) || ((p.ItemType & ItemWeapons) != 0 && p.MaxLevel > 0 && p.BaseXp >= ParagonWeaponBaseXp),
                Stats = string.Join(", ", p.Ratings.Select(r => RatingLong(r.Prop) + " " + r.Value.ToString(CultureInfo.InvariantCulture))),
            });
        }
        report.Rows = report.Rows
            .OrderByDescending(r => r.Piece.Equipped)
            .ThenBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Piece.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totals = new Dictionary<int, int>();
        foreach (GearPiece p in items.Where(p => p.Equipped))
            foreach (var (prop, value) in p.Ratings)
                totals[prop] = totals.TryGetValue(prop, out int t) ? t + value : value;
        foreach (var (prop, _, _) in RatingProps)
            if (totals.TryGetValue(prop, out int v) && v != 0) report.WornTotals.Add((prop, v));
        return report;
    }

    public static string RatingShort(int prop)
    {
        foreach (var r in RatingProps) if (r.Prop == prop) return r.Short;
        return prop.ToString(CultureInfo.InvariantCulture);
    }

    public static string RatingLong(int prop)
    {
        foreach (var r in RatingProps) if (r.Prop == prop) return r.Long;
        return "Rating " + prop.ToString(CultureInfo.InvariantCulture);
    }

    public static string CategoryOf(uint itemType)
    {
        if ((itemType & ItemWeapons) != 0) return "Weapon";
        if ((itemType & ItemArmor) != 0) return "Armor";
        if ((itemType & ItemJewelry) != 0) return "Jewelry";
        if ((itemType & ItemClothing) != 0) return "Clothing";
        return "Other";
    }

    /// <summary>A weapon's element, short (PropertyInt.DamageType flags; Nether wins where it is set).</summary>
    public static string ElementShort(int damageType)
    {
        if ((damageType & 0x400) != 0) return "Neth";
        return damageType switch
        {
            0 => "",
            0x1 => "Slash",
            0x2 => "Pierce",
            0x3 => "Sl+Pi",
            0x4 => "Bludg",
            0x8 => "Cold",
            0x10 => "Fire",
            0x20 => "Acid",
            0x40 => "Storm",
            _ => "0x" + damageType.ToString("X", CultureInfo.InvariantCulture),
        };
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 3)) + "...";
}
