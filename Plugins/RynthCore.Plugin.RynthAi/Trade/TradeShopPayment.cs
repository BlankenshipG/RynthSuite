// Sum / classify payment items on the partner's trade side (UB-IT conversion-aware).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using RynthCore.Loot;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>Values trade-side items as pyreals / currency tallies for Trade Shop payment checks.</summary>
internal static class TradeShopPayment
{
    // "Trade Note (250,000)" / "Trade Note (250000)" when Value is missing.
    private static readonly Regex NoteNameRx = new(
        @"Trade\s+Note\s*\(\s*([\d,]+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Total pyreal value of <paramref name="itemIds"/> using the object cache (notes + pyreals only).
    /// Prefer <see cref="Tally"/> + <see cref="TradeShopRates.PaymentMeetsPrice"/> for multi-currency shops.
    /// </summary>
    public static long SumPyreals(WorldObjectCache? cache, IEnumerable<uint> itemIds)
    {
        if (cache == null) return 0;
        long total = 0;
        foreach (uint id in itemIds)
        {
            WorldObject? wo = cache[(int)id];
            if (wo == null) continue;
            total += ItemPyreals(wo);
        }
        return total;
    }

    /// <summary>
    /// Classify and tally accepted payment currencies on the partner's side.
    /// Trade notes count as MMD stacks (Value / PyrealPerMmd) plus leftover pyreals from odd face values.
    /// </summary>
    public static Dictionary<TradeShopCurrency, long> Tally(
        WorldObjectCache? cache, IEnumerable<uint> itemIds, TradeShopRates rates)
    {
        var tallies = new Dictionary<TradeShopCurrency, long>();
        if (cache == null || rates == null) return tallies;

        long pyPerMmd = Math.Max(1, rates.PyrealPerMmd <= 0 ? 250_000 : rates.PyrealPerMmd);

        foreach (uint id in itemIds)
        {
            WorldObject? wo = cache[(int)id];
            if (wo == null) continue;
            ClassifyAdd(wo, pyPerMmd, tallies);
        }
        return tallies;
    }

    /// <summary>True when partner payment covers <paramref name="pricePyreals"/> at the shop rates.</summary>
    public static bool MeetsPrice(
        WorldObjectCache? cache, IEnumerable<uint> partnerItemIds, long pricePyreals, TradeShopRates rates)
    {
        if (pricePyreals <= 0 || rates == null) return false;
        var tallies = Tally(cache, partnerItemIds, rates);
        return rates.PaymentMeetsPrice(pricePyreals, tallies);
    }

    /// <summary>Pyreal worth of one world object (stack-aware; notes by Value / name).</summary>
    public static long ItemPyreals(WorldObject wo)
    {
        string name = wo.Name ?? "";
        int stack = Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
        if (IsPyrealName(name))
            return stack;

        // IntValueKey.Value = 19 (face value on trade notes / valued currency).
        long value = wo.Values((LongValueKey)19, 0);
        if (value > 0) return value * (LooksLikeTradeNote(name) ? stack : 1);

        Match m = NoteNameRx.Match(name);
        if (m.Success
            && long.TryParse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long face))
            return face * stack;

        return 0;
    }

    /// <summary>Classify one inventory / trade item into a currency bucket (Unknown = not payment).</summary>
    public static TradeShopCurrency Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return TradeShopCurrency.Unknown;
        string n = name.Trim();

        if (IsPyrealName(n)) return TradeShopCurrency.Pyreals;
        if (LooksLikeTradeNote(n)) return TradeShopCurrency.Mmd;

        if (EqualsCI(n, "Enlightened Coin") || EqualsCI(n, "Enlightened Coins"))
            return TradeShopCurrency.EnlightenedCoins;
        if (EqualsCI(n, "Weakly Enlightened Coin") || EqualsCI(n, "Weakly Enlightened Coins"))
            return TradeShopCurrency.WeaklyEnlightenedCoins;

        if (ContainsCI(n, "Legendary Key")) return TradeShopCurrency.LegendaryKeys;
        if (ContainsCI(n, "Mythical Key")) return TradeShopCurrency.MythicalKeys;

        if (EqualsCI(n, "Luminance") || EqualsCI(n, "Luminance Certificate")
            || EqualsCI(n, "Pack of Luminance"))
            return TradeShopCurrency.Luminance;

        return TradeShopCurrency.Unknown;
    }

    private static void ClassifyAdd(WorldObject wo, long pyPerMmd, Dictionary<TradeShopCurrency, long> tallies)
    {
        string name = wo.Name ?? "";
        int stack = Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
        TradeShopCurrency kind = Classify(name);

        if (kind == TradeShopCurrency.Pyreals)
        {
            Add(tallies, TradeShopCurrency.Pyreals, stack);
            return;
        }

        if (kind == TradeShopCurrency.Mmd)
        {
            long face = wo.Values((LongValueKey)19, 0);
            if (face <= 0)
            {
                Match m = NoteNameRx.Match(name);
                if (m.Success)
                    long.TryParse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out face);
            }
            if (face <= 0) return;

            long totalPy = face * stack;
            long mmd = totalPy / pyPerMmd;
            long rem = totalPy % pyPerMmd;
            if (mmd > 0) Add(tallies, TradeShopCurrency.Mmd, mmd);
            if (rem > 0) Add(tallies, TradeShopCurrency.Pyreals, rem);
            return;
        }

        if (kind == TradeShopCurrency.Unknown) return;
        Add(tallies, kind, stack);
    }

    private static void Add(Dictionary<TradeShopCurrency, long> tallies, TradeShopCurrency c, long n)
    {
        if (n <= 0) return;
        tallies.TryGetValue(c, out long cur);
        tallies[c] = cur + n;
    }

    private static bool IsPyrealName(string name)
        => EqualsCI(name, "Pyreal") || EqualsCI(name, "Pyreals");

    private static bool LooksLikeTradeNote(string name)
        => name.IndexOf("Trade Note", StringComparison.OrdinalIgnoreCase) >= 0
           || NoteNameRx.IsMatch(name);

    private static bool EqualsCI(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCI(string hay, string needle)
        => hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
