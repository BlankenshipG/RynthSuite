using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using RynthCore.Loot.T11;
using RynthCore.Loot.VTank;

namespace RynthCore.Loot.Editing;

/// <summary>What a rule says about an item known only by its saved facts.</summary>
public enum LootFactsVerdict
{
    Match,
    NoMatch,
    /// <summary>
    /// A condition that needs an identify failed on an item that wasn't identified when it was
    /// read: it might match once identified.
    /// </summary>
    Unknown,
}

/// <summary>
/// Judges a loot rule's conditions on <see cref="LootItemFacts"/> instead of a live object: for
/// items RynthAi can't see (another character's, read back from RynthInventory's files; added
/// 2026-10-24 for "Find in Global Inventory"). The semantics follow RynthAi's
/// VTankLootEvaluator for every condition the "Add to loot profile" checklist makes (class,
/// name, long / double keys including T11's virtual keys, rating total, spells); conditions
/// about the character (skills, level, pack space) and colours can't be judged on saved data
/// and never match. Pure; regexes are cached.
/// </summary>
public static class LootFactsMatcher
{
    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Dictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);
    private static readonly object RegexGate = new();

    /// <summary>
    /// Int keys every item sends with its description (no identify needed): item type, burden,
    /// valid locations, stack, value, material. A failed test on any other key, a float, the
    /// ratings or the spells only means "no" once the item has been identified.
    /// </summary>
    private static readonly HashSet<int> PublicLongKeys = new() { 1, 5, 9, 11, 12, 19, 131, T11Keys.IsT11 };

    /// <summary>
    /// The rule's verdict. <paramref name="identified"/>: the facts include the item's appraisal
    /// (spells, workmanship, ratings...).
    /// </summary>
    public static LootFactsVerdict Match(IReadOnlyList<VTankLootCondition> conditions, LootItemFacts f, bool identified)
    {
        bool unknown = false;
        foreach (VTankLootCondition c in conditions)
        {
            if (c.NodeType == VTankNodeTypes.DisabledRule)
            {
                if (IsDisabled(c)) return LootFactsVerdict.NoMatch;
                continue;
            }
            bool ok;
            try { ok = Test(c, f); }
            catch (FormatException) { return LootFactsVerdict.NoMatch; }
            catch (ArgumentOutOfRangeException) { return LootFactsVerdict.NoMatch; }
            catch (OverflowException) { return LootFactsVerdict.NoMatch; }
            if (ok) continue;
            if (!identified && NeedsIdentify(c)) unknown = true;
            else return LootFactsVerdict.NoMatch;
        }
        return unknown ? LootFactsVerdict.Unknown : LootFactsVerdict.Match;
    }

    /// <summary>Some condition can only be judged on an identified item.</summary>
    public static bool NeedsIdentify(IReadOnlyList<VTankLootCondition> conditions)
    {
        foreach (VTankLootCondition c in conditions)
            if (c.NodeType != VTankNodeTypes.DisabledRule && NeedsIdentify(c)) return true;
        return false;
    }

    private static bool NeedsIdentify(VTankLootCondition c)
    {
        switch (c.NodeType)
        {
            case VTankNodeTypes.ObjectClass:
            case VTankNodeTypes.StringValueMatch:
                return false;
            case VTankNodeTypes.LongValKeyE:
            case VTankNodeTypes.LongValKeyNE:
            case VTankNodeTypes.LongValKeyGE:
            case VTankNodeTypes.LongValKeyLE:
            case VTankNodeTypes.LongValKeyFlagExists:
                return !(c.DataLines.Count > 1
                         && int.TryParse(c.DataLines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int key)
                         && PublicLongKeys.Contains(key));
            default:
                return true;
        }
    }

    private static bool Test(VTankLootCondition c, LootItemFacts f)
    {
        IList<string> d = c.DataLines;
        switch (c.NodeType)
        {
            case VTankNodeTypes.ObjectClass:
                return AcItemClassifier.ClassMatches(f.ObjectClass, I(d, 0));
            case VTankNodeTypes.StringValueMatch:
                // Saved items keep their name only (key 1); any other string reads empty, as a missing one does live.
                return IsMatch(I(d, 1) == 1 ? f.Name : string.Empty, d[0]);
            case VTankNodeTypes.LongValKeyE: return f.Int(I(d, 1)) == I(d, 0);
            case VTankNodeTypes.LongValKeyNE: return f.Int(I(d, 1)) != I(d, 0);
            case VTankNodeTypes.LongValKeyGE: return f.Int(I(d, 1)) >= I(d, 0);
            case VTankNodeTypes.LongValKeyLE: return f.Int(I(d, 1)) <= I(d, 0);
            case VTankNodeTypes.LongValKeyFlagExists: return (f.Int(I(d, 1)) & I(d, 0)) != 0;
            case VTankNodeTypes.DoubleValKeyGE: return f.Double(FloatKey(I(d, 1))) >= D(d, 0);
            case VTankNodeTypes.DoubleValKeyLE: return f.Double(FloatKey(I(d, 1))) <= D(d, 0);
            case VTankNodeTypes.TotalRatingsGE:
            {
                int total = 0;
                foreach (int k in LootItemRules.RatingKeys) total += f.Int(k);
                return total >= D(d, 0);
            }
            case VTankNodeTypes.SpellNameMatch:
            {
                Regex? rx = Get(d[0]);
                if (rx == null) return false;
                foreach (string n in f.Spells)
                    if (!string.IsNullOrEmpty(n) && SafeMatch(rx, n)) return true;
                return false;
            }
            case VTankNodeTypes.SpellMatch:
            {
                Regex? pos = Get(d[0]);
                if (pos == null) return false;
                Regex? neg = string.IsNullOrWhiteSpace(d[1]) ? null : Get(d[1]);
                int want = I(d, 2), n = 0;
                foreach (string name in f.Spells)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (SafeMatch(pos, name) && (neg == null || !SafeMatch(neg, name)) && ++n >= want) return true;
                }
                return false;
            }
            case VTankNodeTypes.SpellCountGE: return f.Spells.Count >= I(d, 0);
            case VTankNodeTypes.MinDamageGE:
            {
                int max = f.Int(LootItemRules.KeyDamage);
                if (max == 0) return false;
                return max - f.Double(22) * max >= D(d, 0);
            }
            case VTankNodeTypes.BuffedLongValKeyGE: return f.Int(I(d, 1)) >= D(d, 0);
            case VTankNodeTypes.BuffedDoubleValKeyGE: return f.Double(FloatKey(I(d, 1))) >= D(d, 0);
            case VTankNodeTypes.CalcdBuffedTinkedDamageGE: return f.Int(LootItemRules.KeyDamage) >= D(d, 0);
            default:
                // Character, colour and palette conditions: nothing saved to judge them on.
                return false;
        }
    }

    private static bool IsDisabled(VTankLootCondition c) =>
        c.DataLines.Count > 0 && string.Equals(c.DataLines[0].Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static int I(IList<string> d, int i) => int.Parse(d[i], NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static double D(IList<string> d, int i) =>
        double.Parse(d[i], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

    /// <summary>VTank's appraisal-profile float keys (Decal 0x0A0000xx) as the property they read (RynthAi's FloatKey).</summary>
    private static int FloatKey(int key) => unchecked((uint)key) switch
    {
        0x0A000000 => 13, 0x0A000001 => 14, 0x0A000002 => 15, 0x0A000003 => 18, 0x0A000004 => 19,
        0x0A000005 => 17, 0x0A000006 => 16, 0x0A000009 => 280, 0x0A00000B => 22, 0x0A00000C => 62, 0x0A00000E => 63,
        _ => key,
    };

    private static bool IsMatch(string value, string pattern)
    {
        Regex? rx = Get(pattern);
        return rx != null && SafeMatch(rx, value ?? string.Empty);
    }

    private static bool SafeMatch(Regex rx, string s)
    {
        try { return rx.IsMatch(s); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static Regex? Get(string pattern)
    {
        lock (RegexGate)
        {
            if (RegexCache.TryGetValue(pattern, out Regex? rx)) return rx;
            try { rx = new Regex(pattern, Rx, RxTimeout); }
            catch (ArgumentException) { rx = null; }
            if (RegexCache.Count > 512) RegexCache.Clear();
            RegexCache[pattern] = rx;
            return rx;
        }
    }
}
