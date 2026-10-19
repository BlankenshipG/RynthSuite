// IltSigilChain.cs - The Enlightenment Sigil craft chain for /enl 301-325 (ACECustom tier 5:
// Originally SilentKelpie's ILT Hub (RynthAi IltHub/); moved to RynthILT 2026-10-13.
// T - 5 sigils and T x 2B luminance per target level T). Every craft is one plus one makes one:
//   Enlightened Coin + Ancient Yalani Trinket = Ascension Coin
//   Life Splitter + Plant                     = Lifebloom Resin
//   Lifebloom Resin + Necrolytic Extract      = Lifeblood Elixir
//   Lifeblood Elixir + Ascension Coin         = Enlightenment Sigil
// Pure math (no host access) so the planner and the host tests share it. Mirrors UB-IT's
// EnlightenmentSigilChainPolicy.
using System;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>Stack counts of every item in the sigil chain.</summary>
internal sealed class IltSigilStock
{
    public long Sigils, AscensionCoins, LifebloodElixirs, LifebloomResins;
    public long EnlightenedCoins, YalaniTrinkets, NecrolyticExtracts, LifeSplitters, Plants;

    public IltSigilStock Clone() => (IltSigilStock)MemberwiseClone();
}

/// <summary>
/// What a sigil demand still needs after using stock from the top of the chain down. "ToCraft" are
/// crafts left to do (intermediates); "Short" are raw materials left to get.
/// </summary>
internal sealed class IltSigilShortfall
{
    public long SigilsNeeded, SigilsToCraft, AscensionToCraft, ElixirsToCraft, ResinsToCraft;
    /// <summary>E-coins the Ascension Coin crafts use (before stock).</summary>
    public long CoinsForCraft;
    public long CoinsShort, TrinketsShort, ExtractsShort, SplittersShort, PlantsShort;

    /// <summary>Stock (with the whole chain) covers every sigil.</summary>
    public bool Covered => CoinsShort == 0 && TrinketsShort == 0 && ExtractsShort == 0 && SplittersShort == 0 && PlantsShort == 0;
}

/// <summary>The "How to get" text of each chain row (IltSigilChain.HowTo).</summary>
internal sealed class IltSigilHowTo
{
    public string Ascension = "", Elixir = "", Resin = "", Coins = "", Trinkets = "", Extracts = "", Splitters = "", Plants = "";
}

internal static class IltSigilChain
{
    public const int FirstTarget = 301;
    public const int LastTarget = 325;

    // Item names (ACECustom ships no WCIDs for these yet, so they are counted by name).
    public const string AscensionCoin = "Ascension Coin";
    public const string YalaniTrinket = "Ancient Yalani Trinket";
    public const string LifeSplitter = "Life Splitter";
    public const string Plant = "Plant";
    public const string LifebloomResin = "Lifebloom Resin";
    public const string NecrolyticExtract = "Necrolytic Extract";
    public const string LifebloodElixir = "Lifeblood Elixir";
    /// <summary>What a vendor trades for Necrolytic Extracts (not part of the craft chain).</summary>
    public const string Trophy = "Trophy";

    public static bool IsSigilTarget(int t) => t >= FirstTarget && t <= LastTarget;

    /// <summary>Sigils for target levels current+1 .. target that fall inside 301-325 (T - 5 each).</summary>
    public static long SigilsForRange(int current, int target)
    {
        long total = 0;
        for (int t = Math.Max(current + 1, FirstTarget); t <= Math.Min(target, LastTarget); t++)
            total += Math.Max(0, t - 5);
        return total;
    }

    /// <summary>Shortfall for <paramref name="sigilsNeeded"/>: sigils first, then coins / elixirs, then resins, then raws.</summary>
    public static IltSigilShortfall Shortfall(long sigilsNeeded, IltSigilStock stock)
    {
        var s = new IltSigilShortfall { SigilsNeeded = Math.Max(0, sigilsNeeded) };
        if (s.SigilsNeeded == 0) return s;
        stock ??= new IltSigilStock();
        s.SigilsToCraft = Short(s.SigilsNeeded, stock.Sigils);
        s.AscensionToCraft = Short(s.SigilsToCraft, stock.AscensionCoins);
        s.ElixirsToCraft = Short(s.SigilsToCraft, stock.LifebloodElixirs);
        s.CoinsForCraft = s.AscensionToCraft;
        s.CoinsShort = Short(s.AscensionToCraft, stock.EnlightenedCoins);
        s.TrinketsShort = Short(s.AscensionToCraft, stock.YalaniTrinkets);
        s.ResinsToCraft = Short(s.ElixirsToCraft, stock.LifebloomResins);
        s.ExtractsShort = Short(s.ElixirsToCraft, stock.NecrolyticExtracts);
        s.SplittersShort = Short(s.ResinsToCraft, stock.LifeSplitters);
        s.PlantsShort = Short(s.ResinsToCraft, stock.Plants);
        return s;
    }

    /// <summary>Sigils the stock can make: finished sigils plus the scarcer of the two craft paths.</summary>
    public static long Fulfillable(IltSigilStock stock)
    {
        if (stock == null) return 0;
        long coins = stock.AscensionCoins + Math.Min(stock.EnlightenedCoins, stock.YalaniTrinkets);
        long resins = stock.LifebloomResins + Math.Min(stock.LifeSplitters, stock.Plants);
        long elixirs = stock.LifebloodElixirs + Math.Min(resins, stock.NecrolyticExtracts);
        return stock.Sigils + Math.Min(coins, elixirs);
    }

    /// <summary>
    /// Splits the e-coin pool between Ascension Coin crafts (served first) and token conversion.
    /// <paramref name="coinsNeeded"/> is what is still to get for both.
    /// </summary>
    public static void EcoinDemand(long coinsHave, long coinsForSigils, long tokensShort, int coinsPerToken,
        out long tokensFromCoins, out long tokensStill, out long coinsNeeded)
    {
        long rate = Math.Max(1, coinsPerToken);
        long have = Math.Max(0, coinsHave), sigil = Math.Max(0, coinsForSigils), tok = Math.Max(0, tokensShort);
        tokensFromCoins = Math.Min(tok, Math.Max(0, have - sigil) / rate);
        tokensStill = tok - tokensFromCoins;
        coinsNeeded = Math.Max(0, sigil - have) + tokensStill * rate;
    }

    /// <summary>True for a chain item name (stack names may be plural; "Plant" must be the whole name).</summary>
    public static bool NameIs(string itemName, string chainName)
    {
        string n = (itemName ?? string.Empty).Trim();
        return n.Equals(chainName, StringComparison.OrdinalIgnoreCase)
            || n.Equals(chainName + "s", StringComparison.OrdinalIgnoreCase)
            // "Trophy" stacks as "Trophies".
            || (chainName.EndsWith('y') && n.Equals(chainName[..^1] + "ies", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// How to get each chain item, for the planner's "How to get" column: the recipe of a crafted
    /// item (with the amounts still to craft), or a vendor trade. Rates of 0 hide a trade.
    /// </summary>
    /// <param name="lumPerCoin">Luminance price of one e-coin (0 = not priced).</param>
    public static IltSigilHowTo HowTo(IltSigilShortfall s, int extractsPerAscension, int extractsPerTrophyTrade,
                                      int trophiesPerTrade, long lumPerCoin, long trophiesHave)
    {
        string N(long v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var h = new IltSigilHowTo
        {
            Ascension = $"craft: E-coin + {YalaniTrinket}"
                        + (s.AscensionToCraft > 0 ? $" ({N(s.AscensionToCraft)} each)" : "")
                        + (extractsPerAscension > 0
                            ? $"; or buy: {N(extractsPerAscension)} {NecrolyticExtract}s each"
                              + (s.AscensionToCraft > 0 ? $" ({N(s.AscensionToCraft * extractsPerAscension)} for all)" : "")
                            : ""),
            Elixir = $"craft: {LifebloomResin} + {NecrolyticExtract}" + (s.ElixirsToCraft > 0 ? $" ({N(s.ElixirsToCraft)} each)" : ""),
            Resin = $"craft: {LifeSplitter} + {Plant}" + (s.ResinsToCraft > 0 ? $" ({N(s.ResinsToCraft)} each)" : ""),
            Coins = lumPerCoin > 0
                ? $"buy: ~{IltParse.Compact(lumPerCoin)} lum each"
                  + (s.CoinsShort > 0 ? $" (~{IltParse.Compact((double)s.CoinsShort * lumPerCoin)} for the short)" : "")
                : "",
            Extracts = extractsPerTrophyTrade > 0 && trophiesPerTrade > 0
                ? $"vendor: {N(extractsPerTrophyTrade)} extracts trade for {N(trophiesPerTrade)} Trophies (have {N(trophiesHave)})"
                : "",
        };
        return h;
    }

    private static long Short(long demand, long have) => Math.Max(0, demand - Math.Max(0, have));
}
