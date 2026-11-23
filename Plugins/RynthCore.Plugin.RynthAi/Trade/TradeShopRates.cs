// UB-IT InventoryControl conversion table (ConvBuyEc* / PyrealPerMmd) for Trade Shop pricing.
// Pure math — no host / WorldObject deps — so RynthAiTests can link this file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>Which currency a payment item (or price alternative) is denominated in.</summary>
public enum TradeShopCurrency
{
    Unknown = 0,
    Pyreals = 1,
    Mmd = 2,
    Luminance = 3,
    LegendaryKeys = 4,
    MythicalKeys = 5,
    EnlightenedCoins = 6,
    WeaklyEnlightenedCoins = 7,
}

/// <summary>
/// Item-conversion rates ported from UB-IT's Trade Bot / Banking shop defaults.
/// Listings stay priced in pyreals; buyers may pay in any accepted currency at these rates.
/// </summary>
public sealed class TradeShopRates
{
    /// <summary>Face value of one MMD trade note in pyreals (UB default 250,000).</summary>
    public long PyrealPerMmd = 250_000;

    // EC conversion table: how many units of each currency per 1 EC (buy side = what we charge).
    public long ConvBuyEcMmd = 4;
    public long ConvBuyEcLum = 1000;
    public long ConvBuyEcLk = 2;
    public long ConvBuyEcMk = 1;
    public long ConvBuyEcWec = 10;

    // Sell-side (optional; bank-shop / WTB display). Same defaults as UB-IT.
    public long ConvSellEcMmd = 4;
    public long ConvSellEcLum = 900;
    public long ConvSellEcLk = 1;
    public long ConvSellEcMk = 1;
    public long ConvSellEcWec = 8;

    public bool AcceptPyreals = true;
    public bool AcceptMmd = true;
    public bool AcceptLuminance = true;
    public bool AcceptLegendaryKeys = true;
    public bool AcceptMythicalKeys = true;
    public bool AcceptEnlightenedCoins = true;
    public bool AcceptWeaklyEnlightenedCoins = true;

    /// <summary>Pyreals per 1 EC at the buy rate (MMDs-per-EC × pyreals-per-MMD).</summary>
    public long PyrealsPerEcBuy()
    {
        long mmd = Math.Max(1, ConvBuyEcMmd);
        long py = Math.Max(1, PyrealPerMmd <= 0 ? 250_000 : PyrealPerMmd);
        return mmd * py;
    }

    /// <summary>EC units charged for a listing priced at <paramref name="basePy"/> (ceil, min 1).</summary>
    public long EcUnitsForPyreals(long basePy)
    {
        if (basePy <= 0) return 0;
        long perEc = PyrealsPerEcBuy();
        long units = (long)Math.Ceiling(basePy / (double)perEc);
        return units <= 0 ? 1 : units;
    }

    /// <summary>How many units of <paramref name="currency"/> equal one EC on the buy side.</summary>
    public long UnitsPerEcBuy(TradeShopCurrency currency) => currency switch
    {
        TradeShopCurrency.EnlightenedCoins => 1,
        TradeShopCurrency.Mmd => Math.Max(1, ConvBuyEcMmd),
        TradeShopCurrency.Luminance => Math.Max(1, ConvBuyEcLum),
        TradeShopCurrency.LegendaryKeys => Math.Max(1, ConvBuyEcLk),
        TradeShopCurrency.MythicalKeys => Math.Max(1, ConvBuyEcMk),
        TradeShopCurrency.WeaklyEnlightenedCoins => Math.Max(1, ConvBuyEcWec),
        _ => 0,
    };

    /// <summary>True when this shop accepts that currency for payment.</summary>
    public bool Accepts(TradeShopCurrency currency) => currency switch
    {
        TradeShopCurrency.Pyreals => AcceptPyreals,
        TradeShopCurrency.Mmd => AcceptMmd,
        TradeShopCurrency.Luminance => AcceptLuminance,
        TradeShopCurrency.LegendaryKeys => AcceptLegendaryKeys,
        TradeShopCurrency.MythicalKeys => AcceptMythicalKeys,
        TradeShopCurrency.EnlightenedCoins => AcceptEnlightenedCoins,
        TradeShopCurrency.WeaklyEnlightenedCoins => AcceptWeaklyEnlightenedCoins,
        _ => false,
    };

    /// <summary>
    /// Required payment amount in <paramref name="currency"/> for a listing at <paramref name="basePy"/>.
    /// Pyreals = face price; others = EC units × units-per-EC. Returns 0 when not accepted / unknown.
    /// </summary>
    public long RequiredAmount(long basePy, TradeShopCurrency currency)
    {
        if (basePy <= 0 || !Accepts(currency)) return 0;
        if (currency == TradeShopCurrency.Pyreals) return basePy;
        long perEc = UnitsPerEcBuy(currency);
        if (perEc <= 0) return 0;
        return EcUnitsForPyreals(basePy) * perEc;
    }

    /// <summary>
    /// Pyreal-equivalent value of <paramref name="units"/> of <paramref name="currency"/> at buy rates.
    /// Used when tallying mixed payments in a trade window.
    /// </summary>
    public long ToPyreals(TradeShopCurrency currency, long units)
    {
        if (units <= 0) return 0;
        if (currency == TradeShopCurrency.Pyreals) return units;
        if (currency == TradeShopCurrency.Mmd)
            return units * Math.Max(1, PyrealPerMmd <= 0 ? 250_000 : PyrealPerMmd);

        long perEc = UnitsPerEcBuy(currency);
        if (perEc <= 0) return 0;
        // Floor: each full EC-worth of this currency counts as PyrealsPerEcBuy.
        long fullEc = units / perEc;
        return fullEc * PyrealsPerEcBuy();
    }

    /// <summary>
    /// True when the tallied payment covers <paramref name="basePy"/> via any single accepted currency
    /// (UB-IT style alternatives) or via combined pyreal-equivalent.
    /// </summary>
    public bool PaymentMeetsPrice(long basePy, IReadOnlyDictionary<TradeShopCurrency, long> tallies)
    {
        if (basePy <= 0 || tallies == null || tallies.Count == 0) return false;

        foreach (KeyValuePair<TradeShopCurrency, long> kv in tallies)
        {
            if (kv.Value <= 0 || !Accepts(kv.Key)) continue;
            long need = RequiredAmount(basePy, kv.Key);
            if (need > 0 && kv.Value >= need) return true;
        }

        // Mixed bag: sum pyreal-equivalent of accepted currencies.
        long eq = 0;
        foreach (KeyValuePair<TradeShopCurrency, long> kv in tallies)
        {
            if (kv.Value <= 0 || !Accepts(kv.Key)) continue;
            eq += ToPyreals(kv.Key, kv.Value);
            if (eq >= basePy) return true;
        }
        return false;
    }

    /// <summary>Human-readable price alternatives for a listing (UB BuildConversionForItem).</summary>
    public string FormatAlternatives(long basePy)
    {
        if (basePy <= 0) return "";
        var parts = new List<string>();
        long ec = EcUnitsForPyreals(basePy);

        void Add(TradeShopCurrency c, string label)
        {
            long n = RequiredAmount(basePy, c);
            if (n > 0) parts.Add(n.ToString("N0", CultureInfo.InvariantCulture) + " " + label);
        }

        Add(TradeShopCurrency.Pyreals, "Py");
        Add(TradeShopCurrency.Luminance, "Lum");
        Add(TradeShopCurrency.LegendaryKeys, "LK");
        Add(TradeShopCurrency.MythicalKeys, "MK");
        Add(TradeShopCurrency.EnlightenedCoins, "EC");
        Add(TradeShopCurrency.WeaklyEnlightenedCoins, "WEC");
        Add(TradeShopCurrency.Mmd, "MMD");

        if (parts.Count == 0 && ec > 0)
            parts.Add(basePy.ToString("N0", CultureInfo.InvariantCulture) + " Py");
        return string.Join(" / ", parts);
    }

    /// <summary>One-line rates summary for !help / UI.</summary>
    public string RatesHelpLine()
    {
        long py = Math.Max(1, PyrealPerMmd <= 0 ? 250_000 : PyrealPerMmd);
        var sb = new StringBuilder();
        sb.Append("1 EC = ").Append(ConvBuyEcMmd.ToString("N0", CultureInfo.InvariantCulture)).Append(" MMD");
        sb.Append(" = ").Append(ConvBuyEcLum.ToString("N0", CultureInfo.InvariantCulture)).Append(" Lum");
        sb.Append(" = ").Append(ConvBuyEcLk.ToString("N0", CultureInfo.InvariantCulture)).Append(" LK");
        sb.Append(" = ").Append(ConvBuyEcMk.ToString("N0", CultureInfo.InvariantCulture)).Append(" MK");
        sb.Append(" = ").Append(ConvBuyEcWec.ToString("N0", CultureInfo.InvariantCulture)).Append(" WEC");
        sb.Append(". 1 MMD = ").Append(py.ToString("N0", CultureInfo.InvariantCulture)).Append(" Py.");
        return sb.ToString();
    }

    /// <summary>Copy field values onto <paramref name="dst"/> (settings clone / UI apply).</summary>
    public void CopyTo(TradeShopRates dst)
    {
        if (dst == null) return;
        dst.PyrealPerMmd = PyrealPerMmd;
        dst.ConvBuyEcMmd = ConvBuyEcMmd;
        dst.ConvBuyEcLum = ConvBuyEcLum;
        dst.ConvBuyEcLk = ConvBuyEcLk;
        dst.ConvBuyEcMk = ConvBuyEcMk;
        dst.ConvBuyEcWec = ConvBuyEcWec;
        dst.ConvSellEcMmd = ConvSellEcMmd;
        dst.ConvSellEcLum = ConvSellEcLum;
        dst.ConvSellEcLk = ConvSellEcLk;
        dst.ConvSellEcMk = ConvSellEcMk;
        dst.ConvSellEcWec = ConvSellEcWec;
        dst.AcceptPyreals = AcceptPyreals;
        dst.AcceptMmd = AcceptMmd;
        dst.AcceptLuminance = AcceptLuminance;
        dst.AcceptLegendaryKeys = AcceptLegendaryKeys;
        dst.AcceptMythicalKeys = AcceptMythicalKeys;
        dst.AcceptEnlightenedCoins = AcceptEnlightenedCoins;
        dst.AcceptWeaklyEnlightenedCoins = AcceptWeaklyEnlightenedCoins;
    }
}
